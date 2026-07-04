using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JuddHome.Configuration;

/// <summary>
/// Plugin configuration for JuddHome.
/// Per-user preferences are stored as a JSON string because the plugin
/// configuration is XML-serialised and dictionaries do not round-trip.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Canonical list of every section type JuddHome knows about, in default order.
    /// </summary>
    public static readonly string[] AllSections =
    {
        SectionTypes.HeroBanner,
        SectionTypes.ContinueWatching,
        SectionTypes.NextUp,
        SectionTypes.BecauseYouWatched,
        SectionTypes.RecommendedForYou,
        SectionTypes.LatestMovies,
        SectionTypes.LatestTvShows,
        SectionTypes.JustAddedMovies,
        SectionTypes.JustAddedTvShows,
        SectionTypes.WatchAgain,
        SectionTypes.PopularInLibrary,
        SectionTypes.MyList
    };

    /// <summary>
    /// Gets or sets a value indicating whether the hero banner is enabled server-wide.
    /// </summary>
    public bool HeroBannerEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the hero banner auto-advance interval in seconds (5 / 8 / 12).
    /// </summary>
    public int HeroRotationSeconds { get; set; } = 8;

    /// <summary>
    /// Gets or sets the number of items per row (default 20, max 40).
    /// </summary>
    public int ItemsPerRow { get; set; } = 20;

    /// <summary>
    /// Gets or sets the recommendation profile refresh interval in hours (1 / 3 / 6 / 12 / 24).
    /// </summary>
    public int RecommendationRefreshHours { get; set; } = 6;

    /// <summary>
    /// Gets or sets the recommendation weight for genre match (default 0.40).
    /// </summary>
    public double WeightGenreMatch { get; set; } = 0.40;

    /// <summary>
    /// Gets or sets the recommendation weight for high community rating (default 0.25).
    /// </summary>
    public double WeightCommunityRating { get; set; } = 0.25;

    /// <summary>
    /// Gets or sets the recommendation weight for recently added items (default 0.20).
    /// </summary>
    public double WeightRecentlyAdded { get; set; } = 0.20;

    /// <summary>
    /// Gets or sets the recommendation weight for actor match (default 0.15).
    /// </summary>
    public double WeightActorMatch { get; set; } = 0.15;

    /// <summary>
    /// Gets or sets the server-wide default section order (section type keys).
    /// </summary>
    public string[] DefaultSectionOrder { get; set; } = (string[])AllSections.Clone();

    /// <summary>
    /// Gets or sets the section types disabled server-wide by default.
    /// </summary>
    public string[] DefaultDisabledSections { get; set; } = System.Array.Empty<string>();

    /// <summary>
    /// Gets or sets the per-user preferences serialised as JSON:
    /// a dictionary keyed by userId (Guid as string) of <see cref="UserPreferences"/>.
    /// </summary>
    public string UserPreferencesJson { get; set; } = "{}";
}

/// <summary>
/// Per-user home screen preferences.
/// </summary>
public class UserPreferences
{
    /// <summary>
    /// Gets or sets the user's section order (section type keys).
    /// </summary>
    public string[] SectionOrder { get; set; } = System.Array.Empty<string>();

    /// <summary>
    /// Gets or sets the section types this user has hidden.
    /// </summary>
    public string[] DisabledSections { get; set; } = System.Array.Empty<string>();
}

/// <summary>
/// String keys for every JuddHome section type.
/// </summary>
public static class SectionTypes
{
    /// <summary>Hero banner section.</summary>
    public const string HeroBanner = "HeroBanner";

    /// <summary>Continue watching section.</summary>
    public const string ContinueWatching = "ContinueWatching";

    /// <summary>Next up section.</summary>
    public const string NextUp = "NextUp";

    /// <summary>Because you watched rows.</summary>
    public const string BecauseYouWatched = "BecauseYouWatched";

    /// <summary>Recommended for you section.</summary>
    public const string RecommendedForYou = "RecommendedForYou";

    /// <summary>Latest movies section.</summary>
    public const string LatestMovies = "LatestMovies";

    /// <summary>Latest TV shows section.</summary>
    public const string LatestTvShows = "LatestTvShows";

    /// <summary>Just added movies section (recently added to the server, not recently released).</summary>
    public const string JustAddedMovies = "JustAddedMovies";

    /// <summary>Just added TV shows section (recently added to the server, not recently released).</summary>
    public const string JustAddedTvShows = "JustAddedTvShows";

    /// <summary>Watch again section.</summary>
    public const string WatchAgain = "WatchAgain";

    /// <summary>Popular in your library section.</summary>
    public const string PopularInLibrary = "PopularInLibrary";

    /// <summary>My List playlist section.</summary>
    public const string MyList = "MyList";
}
