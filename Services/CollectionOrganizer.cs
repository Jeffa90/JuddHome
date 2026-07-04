using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JuddHome.Services;

/// <summary>
/// Groups movies and shows into real Jellyfin Collections (BoxSets) by studio,
/// using the <see cref="BaseItem.Studios"/> metadata Jellyfin already scrapes —
/// no manual rules, no external API calls.
/// </summary>
public class CollectionOrganizer
{
    /// <summary>A studio with fewer items than this does not get its own collection — avoids
    /// a junk collection per one-off production company.</summary>
    private const int MinimumStudioItems = 3;

    /// <summary>Marks a collection as JuddHome-managed and records which studio it represents,
    /// so re-runs find and update it instead of creating duplicates or touching a
    /// user's own manually-created collection of the same name.</summary>
    private const string StudioProviderIdKey = "JuddHomeStudio";

    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly HealthService _health;
    private readonly ILogger<CollectionOrganizer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CollectionOrganizer"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="collectionManager">Instance of the <see cref="ICollectionManager"/> interface.</param>
    /// <param name="health">The health service.</param>
    /// <param name="logger">The logger.</param>
    public CollectionOrganizer(
        ILibraryManager libraryManager,
        ICollectionManager collectionManager,
        HealthService health,
        ILogger<CollectionOrganizer> logger)
    {
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _health = health;
        _logger = logger;
    }

    /// <summary>
    /// Groups every movie and show with a studio into a collection per studio,
    /// creating new collections or adding to existing JuddHome-managed ones as
    /// needed. Never throws — a failure for one studio never stops the rest.
    /// </summary>
    /// <param name="progress">Progress reporter (0–100).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the run.</returns>
    public async Task OrganizeStudioCollectionsAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        try
        {
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                Recursive = true
            });

            var byStudio = new Dictionary<string, List<Guid>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                foreach (var studio in item.Studios ?? Array.Empty<string>())
                {
                    if (string.IsNullOrWhiteSpace(studio))
                    {
                        continue;
                    }

                    if (!byStudio.TryGetValue(studio, out var ids))
                    {
                        ids = new List<Guid>();
                        byStudio[studio] = ids;
                    }

                    ids.Add(item.Id);
                }
            }

            var qualifying = byStudio.Where(kv => kv.Value.Count >= MinimumStudioItems).ToList();

            var existingCollections = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.BoxSet },
                Recursive = true
            }).Where(c => c.ProviderIds.ContainsKey(StudioProviderIdKey)).ToList();

            var processed = 0;
            foreach (var (studio, itemIds) in qualifying)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var existing = existingCollections.FirstOrDefault(c =>
                        string.Equals(c.ProviderIds[StudioProviderIdKey], studio, StringComparison.OrdinalIgnoreCase));

                    if (existing is not null)
                    {
                        // Idempotent: adding an already-present item is a no-op, so this
                        // safely reconciles new matches without needing a diff.
                        await _collectionManager.AddToCollectionAsync(existing.Id, itemIds).ConfigureAwait(false);
                    }
                    else
                    {
                        await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
                        {
                            Name = studio,
                            ItemIdList = itemIds.Select(id => id.ToString("N")).ToList(),
                            ProviderIds = new Dictionary<string, string> { [StudioProviderIdKey] = studio }
                        }).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "JuddHome: failed to organize studio collection {Studio}", studio);
                }

                processed++;
                progress.Report(qualifying.Count == 0 ? 100 : processed * 100.0 / qualifying.Count);
            }

            _health.ReportSectionRefresh("StudioCollections");
            _logger.LogInformation(
                "JuddHome: organized {Count} studio collections ({Skipped} studios skipped, below {Minimum} items)",
                qualifying.Count,
                byStudio.Count - qualifying.Count,
                MinimumStudioItems);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JuddHome: studio collection organizing failed");
        }
    }
}
