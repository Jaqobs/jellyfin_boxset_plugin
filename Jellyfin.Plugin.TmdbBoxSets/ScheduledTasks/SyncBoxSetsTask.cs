using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.TmdbBoxSets.ScheduledTasks;

/// <summary>
/// Runs a full TMDB box set sync on a schedule and on demand from the dashboard.
/// </summary>
public class SyncBoxSetsTask : IScheduledTask
{
    private readonly BoxSetSyncManager _syncManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncBoxSetsTask"/> class.
    /// </summary>
    /// <param name="syncManager">The box set sync manager.</param>
    public SyncBoxSetsTask(BoxSetSyncManager syncManager)
    {
        _syncManager = syncManager;
    }

    /// <inheritdoc />
    public string Name => "Sync TMDB Box Sets";

    /// <inheritdoc />
    public string Key => "TmdbBoxSetsSync";

    /// <inheritdoc />
    public string Description =>
        "Groups movies by their TMDB collection and creates or updates the matching box sets.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _syncManager.SyncAsync(progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        };
    }
}
