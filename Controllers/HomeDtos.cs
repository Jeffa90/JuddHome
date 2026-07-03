using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JuddHome.Controllers;

/// <summary>
/// A single card on the home screen.
/// </summary>
public class HomeItemDto
{
    /// <summary>Gets or sets the item id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the item name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the item kind (Movie, Series, Episode, ...).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the synopsis.</summary>
    public string? Overview { get; set; }

    /// <summary>Gets or sets the production year.</summary>
    public int? ProductionYear { get; set; }

    /// <summary>Gets or sets the genres.</summary>
    public string[] Genres { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the community rating.</summary>
    public float? CommunityRating { get; set; }

    /// <summary>Gets or sets the official (content) rating.</summary>
    public string? OfficialRating { get; set; }

    /// <summary>Gets or sets the runtime in minutes.</summary>
    public int? RuntimeMinutes { get; set; }

    /// <summary>Gets or sets watch progress 0–100, when partially watched.</summary>
    public double? ProgressPercent { get; set; }

    /// <summary>Gets or sets the parent series id for episodes.</summary>
    public string? SeriesId { get; set; }

    /// <summary>Gets or sets the parent series name for episodes.</summary>
    public string? SeriesName { get; set; }

    /// <summary>
    /// Gets or sets the id of the item whose backdrop image should be used
    /// (an episode's series when the episode has no backdrop of its own),
    /// or null when no backdrop exists anywhere.
    /// </summary>
    public string? BackdropItemId { get; set; }

    /// <summary>Gets or sets a value indicating whether a primary (poster) image exists.</summary>
    public bool HasPrimary { get; set; }

    /// <summary>Gets or sets the recommendation score, when scored.</summary>
    public double? Score { get; set; }

    /// <summary>
    /// Maps a library item to a card DTO.
    /// </summary>
    /// <param name="item">The library item.</param>
    /// <param name="progressPercent">Optional watch progress.</param>
    /// <param name="score">Optional recommendation score.</param>
    /// <returns>The DTO.</returns>
    public static HomeItemDto FromItem(BaseItem item, double? progressPercent = null, double? score = null)
    {
        var dto = new HomeItemDto
        {
            Id = item.Id.ToString("N"),
            Name = item.Name ?? string.Empty,
            Type = item.GetBaseItemKind().ToString(),
            Overview = item.Overview,
            ProductionYear = item.ProductionYear,
            Genres = item.Genres ?? Array.Empty<string>(),
            CommunityRating = item.CommunityRating,
            OfficialRating = item.OfficialRating,
            RuntimeMinutes = item.RunTimeTicks is > 0
                ? (int)Math.Round(TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalMinutes)
                : null,
            ProgressPercent = progressPercent is > 0 and < 100 ? Math.Round(progressPercent.Value, 1) : null,
            HasPrimary = item.HasImage(ImageType.Primary),
            Score = score
        };

        if (item is Episode episode)
        {
            dto.SeriesId = episode.SeriesId == Guid.Empty ? null : episode.SeriesId.ToString("N");
            dto.SeriesName = episode.SeriesName;
        }

        if (item.HasImage(ImageType.Backdrop))
        {
            dto.BackdropItemId = dto.Id;
        }
        else if (dto.SeriesId is not null)
        {
            dto.BackdropItemId = dto.SeriesId;
        }

        return dto;
    }
}

/// <summary>
/// One home screen section with its items. "Because You Watched" uses
/// <see cref="Rows"/>; every other section uses <see cref="Items"/>.
/// </summary>
public class SectionDto
{
    /// <summary>Gets or sets the section type key.</summary>
    public string SectionType { get; set; } = string.Empty;

    /// <summary>Gets or sets the display title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the items.</summary>
    public List<HomeItemDto> Items { get; set; } = new();

    /// <summary>Gets or sets sub-rows (Because You Watched).</summary>
    public List<SectionRowDto>? Rows { get; set; }

    /// <summary>
    /// Gets or sets an action hint for empty sections, e.g. "CreateMyList" when
    /// the My List playlist does not exist yet.
    /// </summary>
    public string? EmptyAction { get; set; }
}

/// <summary>
/// A titled sub-row inside a section.
/// </summary>
public class SectionRowDto
{
    /// <summary>Gets or sets the row title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the row items.</summary>
    public List<HomeItemDto> Items { get; set; } = new();
}

/// <summary>
/// A section descriptor in the user's ordered layout.
/// </summary>
public class SectionDescriptorDto
{
    /// <summary>Gets or sets the section type key.</summary>
    public string SectionType { get; set; } = string.Empty;

    /// <summary>Gets or sets the display title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the user sees this section.</summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// Response for GET /JuddHome/Sections.
/// </summary>
public class SectionsResponseDto
{
    /// <summary>Gets or sets the ordered section descriptors.</summary>
    public List<SectionDescriptorDto> Sections { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether the hero banner is enabled.</summary>
    public bool HeroBannerEnabled { get; set; }

    /// <summary>Gets or sets the hero auto-advance interval in seconds.</summary>
    public int HeroRotationSeconds { get; set; }

    /// <summary>Gets or sets the number of items per row.</summary>
    public int ItemsPerRow { get; set; }

    /// <summary>Gets or sets the user's watched item count (capped).</summary>
    public int WatchedCount { get; set; }

    /// <summary>Gets or sets a value indicating whether new-user fallbacks apply (&lt;3 watched items).</summary>
    public bool NewUserFallback { get; set; }
}

/// <summary>
/// Body for POST /JuddHome/Config/Admin.
/// </summary>
public class AdminConfigDto
{
    /// <summary>Gets or sets a value indicating whether the hero banner is enabled.</summary>
    public bool HeroBannerEnabled { get; set; } = true;

    /// <summary>Gets or sets the hero auto-advance interval in seconds.</summary>
    public int HeroRotationSeconds { get; set; } = 8;

    /// <summary>Gets or sets the number of items per row.</summary>
    public int ItemsPerRow { get; set; } = 20;

    /// <summary>Gets or sets the recommendation refresh interval in hours.</summary>
    public int RecommendationRefreshHours { get; set; } = 6;

    /// <summary>Gets or sets the server-wide default section order.</summary>
    public string[] DefaultSectionOrder { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the server-wide default disabled sections.</summary>
    public string[] DefaultDisabledSections { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Response for GET /JuddHome/Health.
/// </summary>
public class HealthDto
{
    /// <summary>Gets or sets the plugin version.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Gets or sets the status: Healthy or Degraded.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the startup error, when degraded.</summary>
    public string? StartupError { get; set; }

    /// <summary>Gets or sets a value indicating whether the web script was injected.</summary>
    public bool ScriptInjected { get; set; }

    /// <summary>Gets or sets per-section last refresh timestamps.</summary>
    public Dictionary<string, DateTimeOffset> SectionRefreshes { get; set; } = new();
}

/// <summary>
/// A user with a stored preference override (admin view).
/// </summary>
public class UserOverrideDto
{
    /// <summary>Gets or sets the user id.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Gets or sets the username, when resolvable.</summary>
    public string? Username { get; set; }

    /// <summary>Gets or sets the user's section order.</summary>
    public string[] SectionOrder { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the sections the user hid.</summary>
    public string[] DisabledSections { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Shared helpers for section metadata.
/// </summary>
public static class SectionTitles
{
    private static readonly Dictionary<string, string> Titles = new(StringComparer.OrdinalIgnoreCase)
    {
        [Configuration.SectionTypes.HeroBanner] = "Featured",
        [Configuration.SectionTypes.ContinueWatching] = "Continue Watching",
        [Configuration.SectionTypes.NextUp] = "Next Up",
        [Configuration.SectionTypes.BecauseYouWatched] = "Because You Watched",
        [Configuration.SectionTypes.RecommendedForYou] = "Recommended For You",
        [Configuration.SectionTypes.LatestMovies] = "Latest Movies",
        [Configuration.SectionTypes.LatestTvShows] = "Latest TV Shows",
        [Configuration.SectionTypes.WatchAgain] = "Watch Again",
        [Configuration.SectionTypes.PopularInLibrary] = "Popular In Your Library",
        [Configuration.SectionTypes.MyList] = "My List"
    };

    /// <summary>
    /// Gets the display title for a section type.
    /// </summary>
    /// <param name="sectionType">The section type key.</param>
    /// <returns>The display title.</returns>
    public static string For(string sectionType) =>
        Titles.TryGetValue(sectionType, out var title) ? title : sectionType;
}
