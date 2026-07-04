using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JuddHome.Services;

/// <summary>
/// Queries the Jellyfin library for a user's watch history and the item lists
/// backing each home screen section. All access goes through the injected
/// Jellyfin interfaces — no HTTP loopback calls.
/// </summary>
public class WatchHistoryAnalyser
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<WatchHistoryAnalyser> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchHistoryAnalyser"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="logger">The logger.</param>
    public WatchHistoryAnalyser(
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        ILogger<WatchHistoryAnalyser> logger)
    {
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets the user's most recently completed (fully played) movies and episodes,
    /// newest first.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="limit">Maximum number of items.</param>
    /// <returns>The completed items.</returns>
    public IReadOnlyList<BaseItem> GetCompletedItems(User user, int limit)
    {
        return SafeQuery(nameof(GetCompletedItems), () => _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            IsPlayed = true,
            Recursive = true,
            OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
            Limit = limit
        }));
    }

    /// <summary>
    /// Gets the number of played items for the user, capped at <paramref name="cap"/>.
    /// Used for the new-user fallback check.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="cap">The cap on the count.</param>
    /// <returns>The number of played items, up to the cap.</returns>
    public int GetWatchedCount(User user, int cap = 10) => GetCompletedItems(user, cap).Count;

    /// <summary>
    /// Gets items with partial watch progress (&gt;5% and &lt;90% complete),
    /// most recently played first. Each entry includes the progress percentage.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="limit">Maximum number of items.</param>
    /// <returns>The resumable items with their progress.</returns>
    public IReadOnlyList<(BaseItem Item, double ProgressPercent)> GetContinueWatching(User user, int limit)
    {
        var items = SafeQuery(nameof(GetContinueWatching), () => _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            IsResumable = true,
            Recursive = true,
            OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
            Limit = limit * 3
        }));

        var result = new List<(BaseItem, double)>();
        foreach (var item in items)
        {
            var progress = GetProgressPercent(user, item);
            if (progress > 5 && progress < 90)
            {
                result.Add((item, progress));
                if (result.Count >= limit)
                {
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Gets the watch progress percentage (0–100) for an item, based on the
    /// user's playback position and the item runtime.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="item">The item.</param>
    /// <returns>The progress percentage, or 0 when unknown.</returns>
    public double GetProgressPercent(User user, BaseItem item)
    {
        try
        {
            var userData = _userDataManager.GetUserData(user, item);
            if (userData is null || item.RunTimeTicks is null || item.RunTimeTicks == 0)
            {
                return 0;
            }

            if (userData.Played)
            {
                return 100;
            }

            return Math.Clamp(userData.PlaybackPositionTicks * 100.0 / item.RunTimeTicks.Value, 0, 100);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JuddHome: failed to read user data for item {ItemId}", item.Id);
            return 0;
        }
    }

    /// <summary>
    /// Gets the most recently released movies or series (by premiere date), newest
    /// first — "Latest Movies"/"Latest TV Shows" means new releases, not new to
    /// the server (see <see cref="GetRecentlyAdded"/> for that).
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="kind">Movie or Series.</param>
    /// <param name="limit">Maximum number of items.</param>
    /// <returns>The latest-released items.</returns>
    public IReadOnlyList<BaseItem> GetLatest(User user, BaseItemKind kind, int limit)
    {
        return SafeQuery(nameof(GetLatest), () => _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { kind },
            Recursive = true,
            OrderBy = new[] { (ItemSortBy.PremiereDate, SortOrder.Descending) },
            Limit = limit
        }));
    }

    /// <summary>
    /// Gets the movies or series most recently added to the server (by file/import
    /// date), newest first — powers the "Just Added" sections.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="kind">Movie or Series.</param>
    /// <param name="limit">Maximum number of items.</param>
    /// <returns>The recently-added items.</returns>
    public IReadOnlyList<BaseItem> GetRecentlyAdded(User user, BaseItemKind kind, int limit)
    {
        return SafeQuery(nameof(GetRecentlyAdded), () => _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { kind },
            Recursive = true,
            OrderBy = new[] { (ItemSortBy.DateCreated, SortOrder.Descending) },
            Limit = limit
        }));
    }

    /// <summary>
    /// Gets fully watched movies and series (&gt;90% progress / played) to suggest rewatching,
    /// most recently played first.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="limit">Maximum number of items.</param>
    /// <returns>The watch-again candidates.</returns>
    public IReadOnlyList<BaseItem> GetWatchAgain(User user, int limit)
    {
        var movies = SafeQuery(nameof(GetWatchAgain), () => _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            IsPlayed = true,
            Recursive = true,
            OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
            Limit = limit
        }));

        // A series counts as watched when every episode is played; surface the series
        // (not individual episodes) so the card links to the show detail page.
        var series = SafeQuery(nameof(GetWatchAgain), () => _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Series },
            IsPlayed = true,
            Recursive = true,
            OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
            Limit = limit
        }));

        return movies.Concat(series)
            .OrderByDescending(i => GetLastPlayedDate(user, i))
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Gets unwatched items sorted by community rating descending.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="limit">Maximum number of items.</param>
    /// <returns>The most popular unwatched items.</returns>
    public IReadOnlyList<BaseItem> GetPopularUnwatched(User user, int limit)
    {
        return SafeQuery(nameof(GetPopularUnwatched), () => _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            IsPlayed = false,
            Recursive = true,
            OrderBy = new[] { (ItemSortBy.CommunityRating, SortOrder.Descending) },
            Limit = limit
        }).Where(i => i.CommunityRating.HasValue).ToList());
    }

    /// <summary>
    /// Gets all unwatched movies and series — the candidate pool for recommendations.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns>The unwatched candidate pool.</returns>
    public IReadOnlyList<BaseItem> GetUnwatchedPool(User user)
    {
        return SafeQuery(nameof(GetUnwatchedPool), () => _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            IsPlayed = false,
            Recursive = true
        }));
    }

    /// <summary>
    /// Gets the user's playlist named exactly "My List", or null when it does not exist.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns>The playlist, or null.</returns>
    public BaseItem? GetMyListPlaylist(User user)
    {
        var playlists = SafeQuery(nameof(GetMyListPlaylist), () => _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Playlist },
            Recursive = true,
            Name = "My List"
        }));

        return playlists.FirstOrDefault(p => string.Equals(p.Name, "My List", StringComparison.Ordinal));
    }

    /// <summary>
    /// Gets the children of a playlist visible to the user.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="playlist">The playlist item.</param>
    /// <param name="limit">Maximum number of items.</param>
    /// <returns>The playlist items.</returns>
    public IReadOnlyList<BaseItem> GetPlaylistItems(User user, BaseItem playlist, int limit)
    {
        try
        {
            if (playlist is Folder folder)
            {
                return folder.GetChildren(user, true).Take(limit).ToList();
            }

            return Array.Empty<BaseItem>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JuddHome: failed to read playlist {PlaylistId}", playlist.Id);
            return Array.Empty<BaseItem>();
        }
    }

    /// <summary>
    /// Gets the ids of everything the user has played, plus the parent series of
    /// played episodes — used to exclude watched content from recommendations.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns>A set of watched item ids.</returns>
    public HashSet<Guid> GetWatchedItemIds(User user)
    {
        var played = SafeQuery(nameof(GetWatchedItemIds), () => _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Series },
            IsPlayed = true,
            Recursive = true
        }));

        var ids = new HashSet<Guid>();
        foreach (var item in played)
        {
            ids.Add(item.Id);
            if (item is Episode { SeriesId: var seriesId } && seriesId != Guid.Empty)
            {
                ids.Add(seriesId);
            }
        }

        return ids;
    }

    /// <summary>
    /// Resolves an episode's parent series when the item is an episode; otherwise
    /// returns the item itself. "Because You Watched" seeds use the series so row
    /// titles read naturally.
    /// </summary>
    /// <param name="item">The watched item.</param>
    /// <returns>The seed item.</returns>
    public BaseItem ResolveSeed(BaseItem item)
    {
        if (item is Episode episode && episode.SeriesId != Guid.Empty)
        {
            var series = _libraryManager.GetItemById(episode.SeriesId);
            if (series is not null)
            {
                return series;
            }
        }

        return item;
    }

    private DateTime GetLastPlayedDate(User user, BaseItem item)
    {
        try
        {
            return _userDataManager.GetUserData(user, item)?.LastPlayedDate ?? DateTime.MinValue;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JuddHome: failed to read last played date for item {ItemId}", item.Id);
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// Runs a library query, returning an empty list instead of throwing so a
    /// failing section can never crash the rest of the home screen.
    /// </summary>
    private IReadOnlyList<BaseItem> SafeQuery(string operation, Func<IReadOnlyList<BaseItem>?> query)
    {
        try
        {
            return query() ?? Array.Empty<BaseItem>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JuddHome: library query {Operation} failed", operation);
            return Array.Empty<BaseItem>();
        }
    }
}
