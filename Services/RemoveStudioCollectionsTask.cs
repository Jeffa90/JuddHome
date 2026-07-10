using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.JuddHome.Services;

/// <summary>
/// One-time cleanup task: deletes every JuddHome-managed studio collection.
/// Deleting is idempotent (nothing to do once they're gone), so it's safe to run
/// on every server startup — a task with zero triggers doesn't get a "Run Now"
/// control in Jellyfin's Scheduled Tasks UI, so a startup trigger both fixes that
/// and means the actual cleanup happens automatically on the next restart, no
/// manual click needed.
/// </summary>
public class RemoveStudioCollectionsTask : IScheduledTask
{
    private readonly CollectionOrganizer _organizer;

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoveStudioCollectionsTask"/> class.
    /// </summary>
    /// <param name="organizer">The collection organizer.</param>
    public RemoveStudioCollectionsTask(CollectionOrganizer organizer)
    {
        _organizer = organizer;
    }

    /// <inheritdoc />
    public string Name => "Remove Studio Collections (JuddHome, one-time cleanup)";

    /// <inheritdoc />
    public string Key => "JuddHomeRemoveStudioCollections";

    /// <inheritdoc />
    public string Description =>
        "Deletes every collection JuddHome's studio grouping created. Only touches JuddHome-managed collections — never the underlying movies/shows, or anything you made yourself. Runs automatically once on server startup; safe to run repeatedly (does nothing once they're gone). You can also click Run Now.";

    /// <inheritdoc />
    public string Category => "JuddHome";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        _organizer.RemoveStudioCollectionsAsync(progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger }
    };
}
