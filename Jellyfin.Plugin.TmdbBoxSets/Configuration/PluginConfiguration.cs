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
}
