using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JuddHome.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JuddHome.Services;

/// <summary>
/// Builds per-user preference profiles from watch history and produces weighted,
/// personalised recommendations. Profiles are held in memory and refreshed
/// periodically by the hosted service.
/// </summary>
public class RecommendationEngine
{
    /// <summary>A genre/decade row with fewer unwatched items than this is skipped rather than shown half-empty.</summary>
    private const int MinimumRowItems = 4;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly WatchHistoryAnalyser _analyser;
    private readonly HealthService _health;
    private readonly ILogger<RecommendationEngine> _logger;

    private readonly ConcurrentDictionary<Guid, UserProfile> _profiles = new();
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<string>> _peopleCache = new();
    private readonly ConcurrentDictionary<Guid, CacheEntry<IReadOnlyList<BaseItem>>> _poolCache = new();
    private readonly ConcurrentDictionary<Guid, CacheEntry<IReadOnlyList<ScoredItem>>> _recommendationCache = new();
    private readonly ConcurrentDictionary<(Guid UserId, int MaxRows, int ItemsPerRow), CacheEntry<IReadOnlyList<(BaseItem Seed, IReadOnlyList<BaseItem> Items)>>> _becauseYouWatchedCache = new();
    private readonly ConcurrentDictionary<Guid, CacheEntry<IReadOnlyList<(string Label, IReadOnlyList<BaseItem> Items)>>> _genreRowsCache = new();
    private readonly ConcurrentDictionary<Guid, CacheEntry<IReadOnlyList<(string Label, IReadOnlyList<BaseItem> Items)>>> _decadeRowsCache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="RecommendationEngine"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="analyser">The watch history analyser.</param>
    /// <param name="health">The health service.</param>
    /// <param name="logger">The logger.</param>
    public RecommendationEngine(
        ILibraryManager libraryManager,
        IUserManager userManager,
        WatchHistoryAnalyser analyser,
        HealthService health,
        ILogger<RecommendationEngine> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _analyser = analyser;
        _health = health;
        _logger = logger;
    }

    /// <summary>
    /// Rebuilds preference profiles for every user and proactively re-warms their
    /// recommendation caches, so the home screen always serves pre-computed data
    /// instead of scoring the library on the user's own request. Never throws.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the refresh.</returns>
    public Task RefreshAllProfilesAsync(CancellationToken cancellationToken)
    {
        try
        {
            _peopleCache.Clear();
            _poolCache.Clear();
            _recommendationCache.Clear();
            _becauseYouWatchedCache.Clear();
            _genreRowsCache.Clear();
            _decadeRowsCache.Clear();

            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var itemsPerRow = Math.Clamp(config.ItemsPerRow, 1, 40);
            var maxBecauseYouWatchedRows = Math.Clamp(config.MaxBecauseYouWatchedRows, 1, 5);

            foreach (var user in _userManager.GetUsers().ToList())
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                BuildProfile(user);

                // Warm the caches now, off the request path, so the first home-screen
                // load after this refresh (or after server start) is never the one
                // that pays for scoring the whole library.
                GetRecommendations(user, 40);
                GetBecauseYouWatched(user, maxBecauseYouWatchedRows, itemsPerRow);
                GetGenreRows(user);
                GetDecadeRows(user);
            }

            _health.ReportSectionRefresh("RecommendationProfiles");
            _logger.LogInformation("JuddHome: refreshed recommendation profiles for {Count} users", _profiles.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JuddHome: profile refresh failed");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Recommendation results are cached at least until the next scheduled profile
    /// refresh (which proactively re-warms them), plus a buffer in case that
    /// refresh is ever delayed or fails for a given user.
    /// </summary>
    private static TimeSpan GetResultCacheTtl()
    {
        var hours = Plugin.Instance?.Configuration.RecommendationRefreshHours ?? 6;
        if (hours < 1)
        {
            hours = 6;
        }

        return TimeSpan.FromHours(hours + 1);
    }

    /// <summary>
    /// Gets the profile for a user, building it on demand when missing.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns>The user's preference profile.</returns>
    public UserProfile GetProfile(User user)
    {
        if (_profiles.TryGetValue(user.Id, out var profile))
        {
            return profile;
        }

        return BuildProfile(user);
    }

    /// <summary>
    /// Scores every unwatched library item against the user's profile using the
    /// configured weights and returns the top <paramref name="limit"/>.
    /// Weights (defaults): genre match 40%, community rating &gt; 7.5 25%,
    /// added within 90 days 20%, actor match 15%.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="limit">Maximum number of recommendations.</param>
    /// <returns>The scored recommendations, best first.</returns>
    public IReadOnlyList<ScoredItem> GetRecommendations(User user, int limit)
    {
        var now = DateTimeOffset.UtcNow;
        if (_recommendationCache.TryGetValue(user.Id, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Value.Take(limit).ToList();
        }

        try
        {
            var profile = GetProfile(user);
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var pool = GetUnwatchedPoolCached(user, now);
            var cutoff = DateTime.UtcNow.AddDays(-90);

            var scored = new List<ScoredItem>(pool.Count);
            foreach (var item in pool)
            {
                var genreScore = GenreMatchScore(item, profile);
                var ratingScore = RatingScore(item);
                var recencyScore = item.DateCreated >= cutoff ? 1.0 : 0.0;

                // People lookups are the expensive part — only pay for them when the
                // item already shows some affinity, otherwise score actors as zero.
                var actorScore = genreScore > 0 || ratingScore > 0
                    ? ActorMatchScore(item, profile)
                    : 0.0;

                var score = (config.WeightGenreMatch * genreScore)
                    + (config.WeightCommunityRating * ratingScore)
                    + (config.WeightRecentlyAdded * recencyScore)
                    + (config.WeightActorMatch * actorScore);

                if (score > 0)
                {
                    scored.Add(new ScoredItem(item, Math.Round(score, 4)));
                }
            }

            IReadOnlyList<ScoredItem> ordered = scored
                .OrderByDescending(s => s.Score)
                .ThenByDescending(s => s.Item.CommunityRating ?? 0)
                .ToList();

            _recommendationCache[user.Id] = new CacheEntry<IReadOnlyList<ScoredItem>>(now.Add(GetResultCacheTtl()), ordered);
            return ordered.Take(limit).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JuddHome: recommendation scoring failed for user {UserId}", user.Id);
            return Array.Empty<ScoredItem>();
        }
    }

    /// <summary>
    /// Builds "Because You Watched" rows: for each of the last completed items,
    /// finds similar unwatched items by genre + studio + people intersection.
    /// Items are deduplicated across rows.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="maxRows">Maximum number of rows (5 per spec).</param>
    /// <param name="itemsPerRow">Maximum items per row.</param>
    /// <returns>The rows: seed item plus similar items.</returns>
    public IReadOnlyList<(BaseItem Seed, IReadOnlyList<BaseItem> Items)> GetBecauseYouWatched(
        User user, int maxRows, int itemsPerRow)
    {
        var now = DateTimeOffset.UtcNow;
        var cacheKey = (user.Id, maxRows, itemsPerRow);
        if (_becauseYouWatchedCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Value;
        }

        var rows = new List<(BaseItem, IReadOnlyList<BaseItem>)>();
        try
        {
            var completed = _analyser.GetCompletedItems(user, 10);
            if (completed.Count == 0)
            {
                return rows;
            }

            var pool = GetUnwatchedPoolCached(user, now);
            var usedSeedIds = new HashSet<Guid>();
            var usedItemIds = new HashSet<Guid>();

            foreach (var watched in completed)
            {
                if (rows.Count >= maxRows)
                {
                    break;
                }

                var seed = _analyser.ResolveSeed(watched);
                if (!usedSeedIds.Add(seed.Id))
                {
                    continue;
                }

                var seedGenres = new HashSet<string>(seed.Genres ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                var seedStudios = new HashSet<string>(seed.Studios ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                var seedPeople = GetPeopleNames(seed);

                var similar = pool
                    .Where(c => c.Id != seed.Id && !usedItemIds.Contains(c.Id))
                    .Select(c => (Item: c, Score: SimilarityScore(c, seedGenres, seedStudios, seedPeople)))
                    .Where(x => x.Score > 0)
                    .OrderByDescending(x => x.Score)
                    .ThenByDescending(x => x.Item.CommunityRating ?? 0)
                    .Take(itemsPerRow)
                    .Select(x => x.Item)
                    .ToList();

                if (similar.Count < 3)
                {
                    continue;
                }

                foreach (var item in similar)
                {
                    usedItemIds.Add(item.Id);
                }

                rows.Add((seed, similar));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JuddHome: Because You Watched failed for user {UserId}", user.Id);
            return rows;
        }

        _becauseYouWatchedCache[cacheKey] = new CacheEntry<IReadOnlyList<(BaseItem, IReadOnlyList<BaseItem>)>>(now.Add(GetResultCacheTtl()), rows);
        return rows;
    }

    /// <summary>
    /// Builds one row per genre in the user's unwatched pool, alphabetical order,
    /// each row holding every matching item (best-rated first). Used by the
    /// dedicated Genres page, not the home screen. Genres below
    /// <see cref="MinimumRowItems"/> items are skipped rather than shown half-empty.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns>The rows: genre name plus every unwatched item in it.</returns>
    public IReadOnlyList<(string Label, IReadOnlyList<BaseItem> Items)> GetGenreRows(User user)
    {
        var now = DateTimeOffset.UtcNow;
        if (_genreRowsCache.TryGetValue(user.Id, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Value;
        }

        IReadOnlyList<(string, IReadOnlyList<BaseItem>)> rows;
        try
        {
            var pool = GetUnwatchedPoolCached(user, now);
            var byGenre = new Dictionary<string, List<BaseItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in pool)
            {
                foreach (var genre in item.Genres ?? Array.Empty<string>())
                {
                    if (!byGenre.TryGetValue(genre, out var list))
                    {
                        list = new List<BaseItem>();
                        byGenre[genre] = list;
                    }

                    list.Add(item);
                }
            }

            rows = byGenre
                .Where(kv => kv.Value.Count >= MinimumRowItems)
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => (kv.Key, (IReadOnlyList<BaseItem>)kv.Value
                    .OrderByDescending(i => i.CommunityRating ?? 0)
                    .ToList()))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JuddHome: genre rows failed for user {UserId}", user.Id);
            return Array.Empty<(string, IReadOnlyList<BaseItem>)>();
        }

        _genreRowsCache[user.Id] = new CacheEntry<IReadOnlyList<(string, IReadOnlyList<BaseItem>)>>(now.Add(GetResultCacheTtl()), rows);
        return rows;
    }

    /// <summary>
    /// Builds one row per decade represented in the user's unwatched pool, newest
    /// decade first, each row holding every matching item (best-rated first). Used
    /// by the dedicated Decades page, not the home screen. Decades below
    /// <see cref="MinimumRowItems"/> items are skipped rather than shown half-empty.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns>The rows: decade label (e.g. "1990s") plus every unwatched item in it.</returns>
    public IReadOnlyList<(string Label, IReadOnlyList<BaseItem> Items)> GetDecadeRows(User user)
    {
        var now = DateTimeOffset.UtcNow;
        if (_decadeRowsCache.TryGetValue(user.Id, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Value;
        }

        IReadOnlyList<(string, IReadOnlyList<BaseItem>)> rows;
        try
        {
            var pool = GetUnwatchedPoolCached(user, now);
            var byDecade = new Dictionary<int, List<BaseItem>>();
            foreach (var item in pool)
            {
                if (item.ProductionYear is not { } year || year <= 0)
                {
                    continue;
                }

                var decade = (year / 10) * 10;
                if (!byDecade.TryGetValue(decade, out var list))
                {
                    list = new List<BaseItem>();
                    byDecade[decade] = list;
                }

                list.Add(item);
            }

            rows = byDecade
                .Where(kv => kv.Value.Count >= MinimumRowItems)
                .OrderByDescending(kv => kv.Key)
                .Select(kv => ($"{kv.Key}s", (IReadOnlyList<BaseItem>)kv.Value
                    .OrderByDescending(i => i.CommunityRating ?? 0)
                    .ToList()))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JuddHome: decade rows failed for user {UserId}", user.Id);
            return Array.Empty<(string, IReadOnlyList<BaseItem>)>();
        }

        _decadeRowsCache[user.Id] = new CacheEntry<IReadOnlyList<(string, IReadOnlyList<BaseItem>)>>(now.Add(GetResultCacheTtl()), rows);
        return rows;
    }

    /// <summary>
    /// Picks hero banner items: a mix of continue-watching and highly rated
    /// unwatched items, falling back to top community-rated when there is no
    /// watch history.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="count">Number of hero items (5 per spec).</param>
    /// <returns>The hero items with progress where applicable.</returns>
    public IReadOnlyList<(BaseItem Item, double ProgressPercent)> GetHeroItems(User user, int count)
    {
        var result = new List<(BaseItem, double)>();
        try
        {
            var resume = _analyser.GetContinueWatching(user, 2);
            result.AddRange(resume.Select(r => (r.Item, r.ProgressPercent)));

            var seen = new HashSet<Guid>(result.Select(r => r.Item1.Id));
            foreach (var item in _analyser.GetPopularUnwatched(user, count * 2))
            {
                if (result.Count >= count)
                {
                    break;
                }

                if (seen.Add(item.Id))
                {
                    result.Add((item, 0));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JuddHome: hero item selection failed for user {UserId}", user.Id);
        }

        return result.Take(count).ToList();
    }

    /// <summary>
    /// Gets the user's unwatched item pool, reusing a short-lived cached copy when
    /// available. The pool query is the most expensive step in both recommendation
    /// paths, so sharing one cached pool between them avoids querying the library
    /// twice per home-screen load.
    /// </summary>
    private IReadOnlyList<BaseItem> GetUnwatchedPoolCached(User user, DateTimeOffset now)
    {
        if (_poolCache.TryGetValue(user.Id, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Value;
        }

        var pool = _analyser.GetUnwatchedPool(user);
        _poolCache[user.Id] = new CacheEntry<IReadOnlyList<BaseItem>>(now.Add(GetResultCacheTtl()), pool);
        return pool;
    }

    private UserProfile BuildProfile(User user)
    {
        var profile = new UserProfile { UserId = user.Id, BuiltAt = DateTimeOffset.UtcNow };
        try
        {
            var watched = _analyser.GetCompletedItems(user, 200);
            profile.WatchedCount = watched.Count;

            var genreCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var actorCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var directorCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var studioCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var ratingCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var runtimes = new List<double>();

            foreach (var item in watched)
            {
                foreach (var genre in item.Genres ?? Array.Empty<string>())
                {
                    genreCounts[genre] = genreCounts.GetValueOrDefault(genre) + 1;
                }

                foreach (var studio in item.Studios ?? Array.Empty<string>())
                {
                    studioCounts[studio] = studioCounts.GetValueOrDefault(studio) + 1;
                }

                if (!string.IsNullOrEmpty(item.OfficialRating))
                {
                    ratingCounts[item.OfficialRating] = ratingCounts.GetValueOrDefault(item.OfficialRating) + 1;
                }

                if (item.RunTimeTicks is > 0)
                {
                    runtimes.Add(TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalMinutes);
                }

                CountPeople(item, actorCounts, directorCounts);
            }

            profile.TopGenres = TopKeys(genreCounts, 5);
            profile.TopActors = TopKeys(actorCounts, 10);
            profile.TopDirectors = TopKeys(directorCounts, 5);
            profile.TopStudios = TopKeys(studioCounts, 5);
            profile.PreferredContentRatings = TopKeys(ratingCounts, 3);
            profile.AverageRuntimeMinutes = runtimes.Count > 0 ? Math.Round(runtimes.Average(), 1) : 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JuddHome: failed to build profile for user {UserId}", user.Id);
        }

        _profiles[user.Id] = profile;
        return profile;
    }

    private void CountPeople(BaseItem item, Dictionary<string, int> actorCounts, Dictionary<string, int> directorCounts)
    {
        try
        {
            foreach (var person in _libraryManager.GetPeople(item))
            {
                if (string.IsNullOrEmpty(person.Name))
                {
                    continue;
                }

                if (person.Type == PersonKind.Actor)
                {
                    actorCounts[person.Name] = actorCounts.GetValueOrDefault(person.Name) + 1;
                }
                else if (person.Type == PersonKind.Director)
                {
                    directorCounts[person.Name] = directorCounts.GetValueOrDefault(person.Name) + 1;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JuddHome: failed to read people for item {ItemId}", item.Id);
        }
    }

    private IReadOnlyList<string> GetPeopleNames(BaseItem item)
    {
        return _peopleCache.GetOrAdd(item.Id, _ =>
        {
            try
            {
                return _libraryManager.GetPeople(item)
                    .Where(p => !string.IsNullOrEmpty(p.Name)
                        && (p.Type == PersonKind.Actor || p.Type == PersonKind.Director))
                    .Select(p => p.Name)
                    .Take(15)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "JuddHome: failed to read people for item {ItemId}", item.Id);
                return (IReadOnlyList<string>)Array.Empty<string>();
            }
        });
    }

    private int SimilarityScore(
        BaseItem candidate,
        HashSet<string> seedGenres,
        HashSet<string> seedStudios,
        IReadOnlyList<string> seedPeople)
    {
        var genreOverlap = (candidate.Genres ?? Array.Empty<string>()).Count(seedGenres.Contains);
        if (genreOverlap == 0)
        {
            return 0;
        }

        var studioOverlap = (candidate.Studios ?? Array.Empty<string>()).Count(seedStudios.Contains);

        var peopleOverlap = 0;
        if (seedPeople.Count > 0)
        {
            var candidatePeople = GetPeopleNames(candidate);
            peopleOverlap = candidatePeople.Count(p => seedPeople.Contains(p, StringComparer.OrdinalIgnoreCase));
        }

        return (genreOverlap * 3) + (studioOverlap * 2) + (peopleOverlap * 2);
    }

    private double GenreMatchScore(BaseItem item, UserProfile profile)
    {
        var genres = item.Genres;
        if (profile.TopGenres.Count == 0 || genres is null || genres.Length == 0)
        {
            return 0;
        }

        var matches = genres.Count(g => profile.TopGenres.Contains(g, StringComparer.OrdinalIgnoreCase));
        return Math.Min(1.0, matches / 2.0);
    }

    private static double RatingScore(BaseItem item)
    {
        if (item.CommunityRating is not { } rating)
        {
            return 0;
        }

        // Full credit above 7.5; partial credit scales up to the threshold.
        return rating > 7.5 ? 1.0 : Math.Max(0, (rating - 5.0) / 2.5) * 0.5;
    }

    private double ActorMatchScore(BaseItem item, UserProfile profile)
    {
        if (profile.TopActors.Count == 0)
        {
            return 0;
        }

        var people = GetPeopleNames(item);
        var matches = people.Count(p => profile.TopActors.Contains(p, StringComparer.OrdinalIgnoreCase));
        return Math.Min(1.0, matches / 2.0);
    }

    private static List<string> TopKeys(Dictionary<string, int> counts, int take) =>
        counts.OrderByDescending(kv => kv.Value).Take(take).Select(kv => kv.Key).ToList();
}

/// <summary>
/// An item with its recommendation score.
/// </summary>
/// <param name="Item">The library item.</param>
/// <param name="Score">The weighted score (0–1).</param>
public record ScoredItem(BaseItem Item, double Score);

/// <summary>
/// A cached value with an absolute expiry time.
/// </summary>
/// <typeparam name="T">The cached value type.</typeparam>
/// <param name="ExpiresAt">When the entry stops being valid.</param>
/// <param name="Value">The cached value.</param>
internal sealed record CacheEntry<T>(DateTimeOffset ExpiresAt, T Value);

/// <summary>
/// An in-memory preference profile for one user.
/// </summary>
public class UserProfile
{
    /// <summary>Gets or sets the user id.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets when the profile was built.</summary>
    public DateTimeOffset BuiltAt { get; set; }

    /// <summary>Gets or sets the number of watched items found.</summary>
    public int WatchedCount { get; set; }

    /// <summary>Gets or sets the top 5 genres by watch count.</summary>
    public List<string> TopGenres { get; set; } = new();

    /// <summary>Gets or sets the top 10 actors by watch count.</summary>
    public List<string> TopActors { get; set; } = new();

    /// <summary>Gets or sets the top 5 directors by watch count.</summary>
    public List<string> TopDirectors { get; set; } = new();

    /// <summary>Gets or sets the top 5 studios by watch count.</summary>
    public List<string> TopStudios { get; set; } = new();

    /// <summary>Gets or sets the preferred content rating buckets.</summary>
    public List<string> PreferredContentRatings { get; set; } = new();

    /// <summary>Gets or sets the average watched runtime in minutes.</summary>
    public double AverageRuntimeMinutes { get; set; }
}
