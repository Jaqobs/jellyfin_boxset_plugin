using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.TmdbBoxSets.Configuration;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TmdbBoxSets;

/// <summary>
/// Keeps Jellyfin box sets in sync with the TMDB collections that core metadata
/// refresh has already stamped onto each movie.
/// </summary>
public sealed partial class BoxSetSyncManager : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly TmdbCollectionClient _tmdbClient;
    private readonly ILogger<BoxSetSyncManager> _logger;

    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly object _timerLock = new();

    private Timer? _debounceTimer;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="BoxSetSyncManager"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="collectionManager">Instance of the <see cref="ICollectionManager"/> interface.</param>
    /// <param name="providerManager">Instance of the <see cref="IProviderManager"/> interface.</param>
    /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
    /// <param name="tmdbClient">Client used to look up collection metadata directly.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{TCategoryName}"/> interface.</param>
    public BoxSetSyncManager(
        ILibraryManager libraryManager,
        ICollectionManager collectionManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        TmdbCollectionClient tmdbClient,
        ILogger<BoxSetSyncManager> logger)
    {
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _tmdbClient = tmdbClient;
        _logger = logger;
    }

    private static PluginConfiguration Configuration =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Matches a trailing collection suffix so the placeholder name reads as the
    /// franchise name until core's TMDB provider supplies the real one.
    /// </summary>
    [GeneratedRegex(@"\s+Collection$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CollectionSuffixRegex();

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemUpdated += OnItemChanged;
        _libraryManager.ItemAdded += OnItemChanged;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemUpdated -= OnItemChanged;
        _libraryManager.ItemAdded -= OnItemChanged;

        lock (_timerLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Groups every owned movie by its TMDB collection ID and creates, fills or removes
    /// the matching box sets according to the plugin configuration.
    /// </summary>
    /// <param name="progress">Progress reporter, in percent.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task SyncAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SyncInternalAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task SyncInternalAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Configuration;
        var excluded = ParseExcludedCollectionIds(config.ExcludedTmdbCollectionIds);

        var moviesByCollection = GetMoviesByTmdbCollection(excluded);
        var boxSetsByCollection = GetBoxSetsByTmdbCollection();

        _logger.LogInformation(
            "Found {MovieCollectionCount} TMDB collection(s) across the library and {BoxSetCount} existing TMDB box set(s)",
            moviesByCollection.Count,
            boxSetsByCollection.Count);

        var processed = 0;
        var total = moviesByCollection.Count;

        foreach (var (collectionId, movies) in moviesByCollection)
        {
            cancellationToken.ThrowIfCancellationRequested();

            boxSetsByCollection.TryGetValue(collectionId, out var boxSet);

            if (movies.Count < config.MinimumMoviesInCollection)
            {
                if (boxSet is not null && config.RemoveOrphanedBoxSets)
                {
                    RemoveBoxSet(boxSet, movies.Count, config.MinimumMoviesInCollection);
                }

                processed++;
                progress.Report(total == 0 ? 100d : processed * 100d / total);
                continue;
            }

            boxSet ??= await CreateBoxSetAsync(collectionId, movies, config).ConfigureAwait(false);

            if (boxSet is not null)
            {
                await AddMissingMoviesAsync(boxSet, movies).ConfigureAwait(false);
                await EnrichBoxSetAsync(boxSet, collectionId, config, cancellationToken).ConfigureAwait(false);
            }

            processed++;
            progress.Report(total == 0 ? 100d : processed * 100d / total);
        }

        if (config.RemoveOrphanedBoxSets)
        {
            foreach (var (collectionId, boxSet) in boxSetsByCollection)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!moviesByCollection.ContainsKey(collectionId))
                {
                    RemoveBoxSet(boxSet, 0, config.MinimumMoviesInCollection);
                }
            }
        }

        progress.Report(100d);
    }

    private Dictionary<string, List<Movie>> GetMoviesByTmdbCollection(IReadOnlySet<string> excluded)
    {
        var movies = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            IsVirtualItem = false,
            Recursive = true
        });

        var result = new Dictionary<string, List<Movie>>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in movies)
        {
            if (item is not Movie movie)
            {
                continue;
            }

            var collectionId = movie.GetProviderId(MetadataProvider.TmdbCollection);
            if (string.IsNullOrWhiteSpace(collectionId) || excluded.Contains(collectionId))
            {
                continue;
            }

            if (!result.TryGetValue(collectionId, out var bucket))
            {
                bucket = new List<Movie>();
                result[collectionId] = bucket;
            }

            bucket.Add(movie);
        }

        return result;
    }

    private Dictionary<string, BoxSet> GetBoxSetsByTmdbCollection()
    {
        var boxSets = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.BoxSet },
            CollapseBoxSetItems = false,
            Recursive = true
        });

        var result = new Dictionary<string, BoxSet>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in boxSets)
        {
            if (item is not BoxSet boxSet)
            {
                continue;
            }

            // Only box sets carrying a TMDB collection ID are ours to manage; hand-made
            // collections are left untouched.
            var collectionId = boxSet.GetProviderId(MetadataProvider.Tmdb);
            if (!string.IsNullOrWhiteSpace(collectionId))
            {
                result[collectionId] = boxSet;
            }
        }

        return result;
    }

    private async Task<BoxSet?> CreateBoxSetAsync(
        string collectionId,
        IReadOnlyList<Movie> movies,
        PluginConfiguration config)
    {
        var name = BuildPlaceholderName(collectionId, movies, config.StripCollectionSuffix);

        _logger.LogInformation(
            "Creating box set {Name} for TMDB collection {CollectionId} with {Count} movie(s)",
            name,
            collectionId,
            movies.Count);

        var options = new CollectionCreationOptions
        {
            Name = name,
            IsLocked = false
        };
        options.ProviderIds[MetadataProvider.Tmdb.ToString()] = collectionId;

        var boxSet = await _collectionManager.CreateCollectionAsync(options).ConfigureAwait(false);

        // Core ships its own TMDB provider for BoxSet items; queueing a refresh lets it
        // replace the placeholder name with the real title, overview and artwork.
        _providerManager.QueueRefresh(
            boxSet.Id,
            new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = true
            },
            RefreshPriority.High);

        return boxSet;
    }

    private async Task AddMissingMoviesAsync(BoxSet boxSet, IReadOnlyList<Movie> movies)
    {
        var missing = movies
            .Where(movie => !boxSet.ContainsLinkedChildByItemId(movie.Id))
            .Select(movie => movie.Id)
            .ToList();

        if (missing.Count == 0)
        {
            return;
        }

        _logger.LogInformation(
            "Adding {Count} movie(s) to box set {Name}",
            missing.Count,
            boxSet.Name);

        await _collectionManager.AddToCollectionAsync(boxSet.Id, missing).ConfigureAwait(false);
    }

    private void RemoveBoxSet(BoxSet boxSet, int ownedMovies, int minimum)
    {
        _logger.LogInformation(
            "Removing box set {Name}: {Owned} owned movie(s) is below the configured minimum of {Minimum}",
            boxSet.Name,
            ownedMovies,
            minimum);

        _libraryManager.DeleteItem(
            boxSet,
            new DeleteOptions { DeleteFileLocation = true },
            true);
    }

    /// <summary>
    /// Fills in name, overview and artwork straight from TMDB. This exists because
    /// core's own TMDB box set provider cannot be pointed at a proxy, so on networks
    /// that block api.themoviedb.org the placeholder name would stick forever.
    /// Only runs when something is actually missing.
    /// </summary>
    private async Task EnrichBoxSetAsync(
        BoxSet boxSet,
        string collectionId,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        if (!TmdbCollectionClient.IsConfigured || !NeedsMetadata(boxSet, collectionId))
        {
            return;
        }

        var collection = await _tmdbClient.GetCollectionAsync(collectionId, cancellationToken).ConfigureAwait(false);
        if (collection is null)
        {
            return;
        }

        var updated = false;

        if (!string.IsNullOrWhiteSpace(collection.Name) && IsPlaceholderName(boxSet.Name, collectionId))
        {
            boxSet.Name = config.StripCollectionSuffix
                ? CollectionSuffixRegex().Replace(collection.Name, string.Empty).Trim()
                : collection.Name;
            updated = true;
        }

        if (!string.IsNullOrWhiteSpace(collection.Overview) && string.IsNullOrWhiteSpace(boxSet.Overview))
        {
            boxSet.Overview = collection.Overview;
            updated = true;
        }

        if (updated)
        {
            _logger.LogInformation(
                "Updated box set {Name} from TMDB collection {CollectionId}",
                boxSet.Name,
                collectionId);
            await boxSet.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }

        await SaveImageAsync(boxSet, ImageType.Primary, collection.PosterPath, cancellationToken).ConfigureAwait(false);
        await SaveImageAsync(boxSet, ImageType.Backdrop, collection.BackdropPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveImageAsync(
        BoxSet boxSet,
        ImageType imageType,
        string? imagePath,
        CancellationToken cancellationToken)
    {
        if (boxSet.HasImage(imageType, 0))
        {
            return;
        }

        var url = TmdbCollectionClient.BuildImageUrl(imagePath);
        if (url is null)
        {
            return;
        }

        try
        {
            await _providerManager
                .SaveImage(boxSet, url, imageType, null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Artwork is best-effort; a CDN failure must not abort the sync.
            _logger.LogWarning(
                ex,
                "Could not save {ImageType} artwork for box set {Name}",
                imageType,
                boxSet.Name);
        }
    }

    private static bool NeedsMetadata(BoxSet boxSet, string collectionId)
        => IsPlaceholderName(boxSet.Name, collectionId)
           || string.IsNullOrWhiteSpace(boxSet.Overview)
           || !boxSet.HasImage(ImageType.Primary, 0)
           || !boxSet.HasImage(ImageType.Backdrop, 0);

    private static bool IsPlaceholderName(string? name, string collectionId)
        => string.IsNullOrWhiteSpace(name)
           || string.Equals(name, FormatPlaceholderName(collectionId), StringComparison.Ordinal);

    private static string FormatPlaceholderName(string collectionId)
        => string.Format(CultureInfo.InvariantCulture, "TMDB Collection {0}", collectionId);

    private static string BuildPlaceholderName(
        string collectionId,
        IReadOnlyList<Movie> movies,
        bool stripSuffix)
    {
        var name = movies
            .Select(movie => movie.TmdbCollectionName)
            .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));

        if (string.IsNullOrWhiteSpace(name))
        {
            // Replaced later by EnrichBoxSetAsync, or by core's own TMDB box set
            // provider on the queued refresh, whichever can reach TMDB.
            return FormatPlaceholderName(collectionId);
        }

        return stripSuffix ? CollectionSuffixRegex().Replace(name, string.Empty).Trim() : name;
    }

    private static IReadOnlySet<string> ParseExcludedCollectionIds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return raw
            .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        if (!Configuration.EnableAutomaticSync)
        {
            return;
        }

        if (e.Item is not Movie movie)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(movie.GetProviderId(MetadataProvider.TmdbCollection)))
        {
            return;
        }

        ScheduleDebouncedSync();
    }

    private void ScheduleDebouncedSync()
    {
        var delay = TimeSpan.FromSeconds(Math.Max(1, Configuration.AutomaticSyncDelaySeconds));

        lock (_timerLock)
        {
            if (_disposed)
            {
                return;
            }

            if (_debounceTimer is null)
            {
                _debounceTimer = new Timer(_ => _ = RunDebouncedSyncAsync(), null, delay, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _debounceTimer.Change(delay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private async Task RunDebouncedSyncAsync()
    {
        try
        {
            _logger.LogDebug("Running automatic TMDB box set sync");
            await SyncAsync(new Progress<double>(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Automatic TMDB box set sync failed");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_timerLock)
        {
            _disposed = true;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        _syncLock.Dispose();
    }
}
