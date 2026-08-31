using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TmdbBoxSets.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TmdbBoxSets;

/// <summary>
/// Fetches TMDB collection metadata directly, optionally through a proxy, for
/// networks where api.themoviedb.org is unreachable and Jellyfin's own TMDB
/// provider therefore cannot fill in box set details.
/// </summary>
public sealed class TmdbCollectionClient
{
    private const string DefaultApiBaseUrl = "https://api.themoviedb.org/3";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TmdbCollectionClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TmdbCollectionClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{TCategoryName}"/> interface.</param>
    public TmdbCollectionClient(IHttpClientFactory httpClientFactory, ILogger<TmdbCollectionClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private static PluginConfiguration Configuration =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Gets a value indicating whether an API key has been configured. Without one
    /// there is nothing to authenticate with and lookups are skipped entirely.
    /// </summary>
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Configuration.TmdbApiKey);

    /// <summary>
    /// Builds an absolute artwork URL from a TMDB relative image path.
    /// </summary>
    /// <param name="imagePath">The relative path returned by the API, e.g. "/abc.jpg".</param>
    /// <returns>The absolute URL, or null when there is no path.</returns>
    public static string? BuildImageUrl(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return null;
        }

        var baseUrl = Configuration.TmdbImageBaseUrl.TrimEnd('/');
        return string.Concat(baseUrl, "/", imagePath.TrimStart('/'));
    }

    /// <summary>
    /// Retrieves a TMDB collection by ID.
    /// </summary>
    /// <param name="collectionId">The TMDB collection ID.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The collection, or null when unconfigured or the request failed.</returns>
    public async Task<TmdbCollection?> GetCollectionAsync(string collectionId, CancellationToken cancellationToken)
    {
        var config = Configuration;
        if (string.IsNullOrWhiteSpace(config.TmdbApiKey))
        {
            return null;
        }

        var baseUrl = string.IsNullOrWhiteSpace(config.TmdbProxyBaseUrl)
            ? DefaultApiBaseUrl
            : config.TmdbProxyBaseUrl.TrimEnd('/');

        var url = string.Format(
            CultureInfo.InvariantCulture,
            "{0}/collection/{1}?language={2}",
            baseUrl,
            Uri.EscapeDataString(collectionId),
            Uri.EscapeDataString(config.MetadataLanguage));

        // A v4 read access token goes in the Authorization header; a v3 key is a
        // query parameter.
        if (!config.TmdbIsV4Token)
        {
            url = string.Concat(url, "&api_key=", Uri.EscapeDataString(config.TmdbApiKey));
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (config.TmdbIsV4Token)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.TmdbApiKey);
            }

            if (!string.IsNullOrWhiteSpace(config.TmdbProxySecret)
                && !string.IsNullOrWhiteSpace(config.TmdbProxySecretHeader))
            {
                request.Headers.TryAddWithoutValidation(config.TmdbProxySecretHeader, config.TmdbProxySecret);
            }

            var httpClient = _httpClientFactory.CreateClient(NamedClient.Default);
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // Deliberately logs only the status and collection ID: the URL carries
                // the API key when using a v3 key.
                var hint = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => " - check the API key, and whether it needs the v4 token option",
                    HttpStatusCode.Forbidden => " - check the proxy shared secret and its header name",
                    _ => string.Empty
                };

                _logger.LogWarning(
                    "TMDB lookup for collection {CollectionId} returned {StatusCode}{Hint}",
                    collectionId,
                    (int)response.StatusCode,
                    hint);
                return null;
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonSerializer
                    .DeserializeAsync<TmdbCollection>(stream, _jsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "TMDB lookup for collection {CollectionId} failed", collectionId);
            return null;
        }
    }
}

/// <summary>
/// The subset of a TMDB collection response this plugin uses.
/// </summary>
public sealed class TmdbCollection
{
    /// <summary>Gets or sets the collection ID.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the collection name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Gets or sets the collection overview.</summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    /// <summary>Gets or sets the relative poster path.</summary>
    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    /// <summary>Gets or sets the relative backdrop path.</summary>
    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; set; }
}
