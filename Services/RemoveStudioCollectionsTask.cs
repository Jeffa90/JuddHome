using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.JuddHome.Services;

/// <summary>
/// One-time cleanup task: deletes every JuddHome-managed studio collection. No
/// default trigger — this only ever runs when an admin clicks "Run Now", since it
/// deletes library items. Safe to leave installed indefinitely; running it twice
/// is harmless (nothing to delete the second time).
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
        "Deletes every collection JuddHome's studio grouping created. Only touches JuddHome-managed collections — never the underlying movies/shows, or anything you made yourself. Click Run Now to use it; it has no automatic schedule.";

    /// <inheritdoc />
    public string Category => "JuddHome";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        _organizer.RemoveStudioCollectionsAsync(progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
}
