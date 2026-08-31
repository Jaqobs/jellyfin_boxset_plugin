using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TmdbBoxSets.Configuration;

/// <summary>
/// Configuration for the TMDB box set plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether box sets are synced automatically when
    /// a movie's metadata changes, in addition to the scheduled task.
    /// </summary>
    public bool EnableAutomaticSync { get; set; } = true;

    /// <summary>
    /// Gets or sets the number of owned movies a TMDB collection must have before a
    /// box set is created for it.
    /// </summary>
    public int MinimumMoviesInCollection { get; set; } = 2;

    /// <summary>
    /// Gets or sets a value indicating whether a trailing "Collection" suffix is stripped
    /// from the placeholder box set name.
    /// </summary>
    public bool StripCollectionSuffix { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether box sets are deleted once their TMDB
    /// collection no longer has enough owned movies.
    /// </summary>
    public bool RemoveOrphanedBoxSets { get; set; }

    /// <summary>
    /// Gets or sets the TMDB collection IDs to ignore, separated by commas or newlines.
    /// </summary>
    public string ExcludedTmdbCollectionIds { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the debounce window, in seconds, between the last observed movie
    /// change and the automatic sync it triggers.
    /// </summary>
    public int AutomaticSyncDelaySeconds { get; set; } = 15;

    /// <summary>
    /// Gets or sets the TMDB v3 API key, or the v4 read access token when
    /// <see cref="TmdbIsV4Token"/> is set. Leave empty to disable direct metadata
    /// lookups and rely solely on Jellyfin's own TMDB provider.
    /// </summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="TmdbApiKey"/> is a v4 read
    /// access token (the long "eyJ..." JWT) and must be sent as a bearer token.
    /// </summary>
    public bool TmdbIsV4Token { get; set; }

    /// <summary>
    /// Gets or sets the base URL used for TMDB API calls, including the trailing
    /// "/3" path segment. Leave empty to call TMDB directly. Set this to a proxy
    /// when the network blocks api.themoviedb.org.
    /// </summary>
    public string TmdbProxyBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional shared secret sent with every proxied request.
    /// </summary>
    public string TmdbProxySecret { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the header name carrying <see cref="TmdbProxySecret"/>. This must
    /// match whatever the proxy expects.
    /// </summary>
    public string TmdbProxySecretHeader { get; set; } = "X-Proxy-Secret";

    /// <summary>
    /// Gets or sets the language requested from TMDB for collection metadata.
    /// </summary>
    public string MetadataLanguage { get; set; } = "en-US";

    /// <summary>
    /// Gets or sets the base URL for TMDB artwork. The image CDN is a separate host
    /// from the API and is often reachable when the API is not.
    /// </summary>
    public string TmdbImageBaseUrl { get; set; } = "https://image.tmdb.org/t/p/original";
}
