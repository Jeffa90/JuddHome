using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.JuddHome.Services;

/// <summary>
/// Scheduled task wrapper for <see cref="CollectionOrganizer"/>. Appears under
/// Dashboard &gt; Scheduled Tasks with a "Run Now" button and a default daily
/// trigger; the admin can add or change triggers there too.
/// </summary>
public class OrganizeStudioCollectionsTask : IScheduledTask
{
    private readonly CollectionOrganizer _organizer;

    /// <summary>
    /// Initializes a new instance of the <see cref="OrganizeStudioCollectionsTask"/> class.
    /// </summary>
    /// <param name="organizer">The collection organizer.</param>
    public OrganizeStudioCollectionsTask(CollectionOrganizer organizer)
    {
        _organizer = organizer;
    }

    /// <inheritdoc />
    public string Name => "Organize Studio Collections (JuddHome)";

    /// <inheritdoc />
    public string Key => "JuddHomeOrganizeStudioCollections";

    /// <inheritdoc />
    public string Description =>
        "Groups movies and shows into collections by studio, using metadata Jellyfin already scraped. No manual setup required.";

    /// <inheritdoc />
    public string Category => "JuddHome";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        _organizer.OrganizeStudioCollectionsAsync(progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
        }
    };
}
