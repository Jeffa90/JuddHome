using System.Net.Mime;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JuddHome.Configuration;
using Jellyfin.Plugin.JuddHome.Services;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Playlists;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JuddHome.Controllers;

/// <summary>
/// JuddHome REST API: section layout, section items, per-user and admin
/// configuration, health, and embedded web assets.
/// </summary>
[ApiController]
[Route("JuddHome")]
public class HomeController : ControllerBase
{
    private const int MaxItemsPerRow = 40;

    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ITVSeriesManager _tvSeriesManager;
    private readonly IPlaylistManager _playlistManager;
    private readonly WatchHistoryAnalyser _analyser;
    private readonly RecommendationEngine _engine;
    private readonly UserPreferencesStore _prefsStore;
    private readonly HealthService _health;
    private readonly ILogger<HomeController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HomeController"/> class.
    /// </summary>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="tvSeriesManager">Instance of the <see cref="ITVSeriesManager"/> interface.</param>
    /// <param name="playlistManager">Instance of the <see cref="IPlaylistManager"/> interface.</param>
    /// <param name="analyser">The watch history analyser.</param>
    /// <param name="engine">The recommendation engine.</param>
    /// <param name="prefsStore">The user preferences store.</param>
    /// <param name="health">The health service.</param>
    /// <param name="logger">The logger.</param>
    public HomeController(
        IUserManager userManager,
        ILibraryManager libraryManager,
        ITVSeriesManager tvSeriesManager,
        IPlaylistManager playlistManager,
        WatchHistoryAnalyser analyser,
        RecommendationEngine engine,
        UserPreferencesStore prefsStore,
        HealthService health,
        ILogger<HomeController> logger)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _tvSeriesManager = tvSeriesManager;
        _playlistManager = playlistManager;
        _analyser = analyser;
        _engine = engine;
        _prefsStore = prefsStore;
        _health = health;
        _logger = logger;
    }

    /// <summary>
    /// Gets the ordered list of enabled sections for the current user, including
    /// server-wide display settings and new-user fallback flags.
    /// </summary>
    /// <returns>The section layout.</returns>
    [HttpGet("Sections")]
    [Authorize]
    public ActionResult<SectionsResponseDto> GetSections()
    {
        var user = GetCurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var prefs = _prefsStore.GetEffective(user.Id);
        var disabled = new HashSet<string>(prefs.DisabledSections, StringComparer.OrdinalIgnoreCase);
        var watchedCount = _analyser.GetWatchedCount(user);
        var newUser = watchedCount < 3;

        var sections = new List<SectionDescriptorDto>();
        foreach (var sectionType in prefs.SectionOrder)
        {
            var enabled = !disabled.Contains(sectionType);
            if (sectionType.Equals(SectionTypes.HeroBanner, StringComparison.OrdinalIgnoreCase))
            {
                enabled &= config.HeroBannerEnabled;
            }

            // New-user fallback: no watch history means no Because You Watched rows.
            if (newUser && sectionType.Equals(SectionTypes.BecauseYouWatched, StringComparison.OrdinalIgnoreCase))
            {
                enabled = false;
            }

            sections.Add(new SectionDescriptorDto
            {
                SectionType = sectionType,
                Title = SectionTitles.For(sectionType),
                Enabled = enabled
            });
        }

        return new SectionsResponseDto
        {
            Sections = sections,
            HeroBannerEnabled = config.HeroBannerEnabled,
            HeroRotationSeconds = config.HeroRotationSeconds,
            ItemsPerRow = Math.Clamp(config.ItemsPerRow, 1, MaxItemsPerRow),
            WatchedCount = watchedCount,
            NewUserFallback = newUser
        };
    }

    /// <summary>
    /// Gets the items for a single section for the current user. Failures return
    /// an empty section rather than an error so one bad section never breaks the
    /// home screen.
    /// </summary>
    /// <param name="sectionType">The section type key.</param>
    /// <returns>The section payload.</returns>
    [HttpGet("Section/{sectionType}")]
    [Authorize]
    public ActionResult<SectionDto> GetSection([FromRoute] string sectionType)
    {
        var user = GetCurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var limit = Math.Clamp(config.ItemsPerRow, 1, MaxItemsPerRow);
        var section = new SectionDto
        {
            SectionType = sectionType,
            Title = SectionTitles.For(sectionType)
        };

        try
        {
            var newUser = _analyser.GetWatchedCount(user) < 3;

            switch (sectionType.ToUpperInvariant())
            {
                case "CONTINUEWATCHING":
                    section.Items = _analyser.GetContinueWatching(user, limit)
                        .Select(x => HomeItemDto.FromItem(x.Item, x.ProgressPercent))
                        .ToList();
                    break;

                case "NEXTUP":
                    section.Items = GetNextUpItems(user, limit);
                    break;

                case "BECAUSEYOUWATCHED":
                    section.Rows = _engine.GetBecauseYouWatched(user, 5, limit)
                        .Select(row => new SectionRowDto
                        {
                            Title = $"Because you watched {row.Seed.Name}",
                            Items = row.Items.Select(i => HomeItemDto.FromItem(i)).ToList()
                        })
                        .ToList();
                    break;

                case "RECOMMENDEDFORYOU":
                    // New-user fallback: expanded Popular In Your Library (top 40).
                    section.Items = newUser
                        ? _analyser.GetPopularUnwatched(user, 40).Select(i => HomeItemDto.FromItem(i)).ToList()
                        : _engine.GetRecommendations(user, 20)
                            .Select(s => HomeItemDto.FromItem(s.Item, score: s.Score))
                            .ToList();
                    if (newUser)
                    {
                        section.Title = SectionTitles.For(SectionTypes.PopularInLibrary);
                    }

                    break;

                case "LATESTMOVIES":
                    section.Items = _analyser.GetLatest(user, BaseItemKind.Movie, limit)
                        .Select(i => HomeItemDto.FromItem(i))
                        .ToList();
                    break;

                case "LATESTTVSHOWS":
                    section.Items = _analyser.GetLatest(user, BaseItemKind.Series, limit)
                        .Select(i => HomeItemDto.FromItem(i))
                        .ToList();
                    break;

                case "WATCHAGAIN":
                    section.Items = _analyser.GetWatchAgain(user, limit)
                        .Select(i => HomeItemDto.FromItem(i))
                        .ToList();
                    break;

                case "POPULARINLIBRARY":
                    section.Items = _analyser.GetPopularUnwatched(user, limit)
                        .Select(i => HomeItemDto.FromItem(i))
                        .ToList();
                    break;

                case "GENREROWS":
                    section.Rows = _engine.GetGenreRows(user, 8, limit)
                        .Select(row => new SectionRowDto
                        {
                            Title = row.Label,
                            Items = row.Items.Select(i => HomeItemDto.FromItem(i)).ToList()
                        })
                        .ToList();
                    break;

                case "DECADEROWS":
                    section.Rows = _engine.GetDecadeRows(user, 6, limit)
                        .Select(row => new SectionRowDto
                        {
                            Title = row.Label,
                            Items = row.Items.Select(i => HomeItemDto.FromItem(i)).ToList()
                        })
                        .ToList();
                    break;

                case "MYLIST":
                    var playlist = _analyser.GetMyListPlaylist(user);
                    if (playlist is null)
                    {
                        section.EmptyAction = "CreateMyList";
                    }
                    else
                    {
                        section.Items = _analyser.GetPlaylistItems(user, playlist, limit)
                            .Select(i => HomeItemDto.FromItem(i))
                            .ToList();
                    }

                    break;

                default:
                    return NotFound();
            }

            _health.ReportSectionRefresh(section.SectionType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JuddHome: section {SectionType} failed for user {UserId}", sectionType, user.Id);
            section.Items = new List<HomeItemDto>();
            section.Rows = null;
        }

        return section;
    }

    /// <summary>
    /// Creates the "My List" playlist for the current user.
    /// </summary>
    /// <returns>The new playlist id.</returns>
    [HttpPost("MyList/Create")]
    [Authorize]
    public async Task<ActionResult<object>> CreateMyList()
    {
        var user = GetCurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        var existing = _analyser.GetMyListPlaylist(user);
        if (existing is not null)
        {
            return new { PlaylistId = existing.Id.ToString("N") };
        }

        var result = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
        {
            Name = "My List",
            UserId = user.Id
        }).ConfigureAwait(false);

        return new { PlaylistId = result.Id };
    }

    /// <summary>
    /// Resolves what should actually play for an item: movies and episodes play
    /// themselves; series play the next unwatched episode, or the first episode
    /// when everything is watched.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>The id of the item to play.</returns>
    [HttpGet("PlayTarget/{itemId}")]
    [Authorize]
    public ActionResult<object> GetPlayTarget([FromRoute] Guid itemId)
    {
        var user = GetCurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound();
        }

        if (item is not Series)
        {
            return new { PlayItemId = item.Id.ToString("N") };
        }

        var episodes = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            AncestorIds = new[] { item.Id },
            Recursive = true
        }).OfType<Episode>()
        .OrderBy(e => e.ParentIndexNumber == 0 ? int.MaxValue : e.ParentIndexNumber ?? int.MaxValue)
        .ThenBy(e => e.IndexNumber ?? int.MaxValue)
        .ToList();

        if (episodes.Count == 0)
        {
            return new { PlayItemId = item.Id.ToString("N") };
        }

        var next = episodes.FirstOrDefault(e => _analyser.GetProgressPercent(user, e) < 90) ?? episodes[0];
        return new { PlayItemId = next.Id.ToString("N") };
    }

    /// <summary>
    /// Gets the current user's stored (or default) section preferences.
    /// </summary>
    /// <returns>The user's preferences.</returns>
    [HttpGet("Config/User")]
    [Authorize]
    public ActionResult<UserPreferences> GetUserConfig()
    {
        var user = GetCurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        return _prefsStore.GetEffective(user.Id);
    }

    /// <summary>
    /// Saves the current user's section preferences (order + visibility).
    /// </summary>
    /// <param name="prefs">The preferences to save.</param>
    /// <returns>No content.</returns>
    [HttpPost("Config/User")]
    [Authorize]
    public ActionResult SaveUserConfig([FromBody] UserPreferences prefs)
    {
        var user = GetCurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        _prefsStore.Save(user.Id, prefs);
        return NoContent();
    }

    /// <summary>
    /// Saves the server-wide default configuration. Admin only.
    /// </summary>
    /// <param name="dto">The admin configuration.</param>
    /// <returns>No content.</returns>
    [HttpPost("Config/Admin")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult SaveAdminConfig([FromBody] AdminConfigDto dto)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(503);
        }

        var config = plugin.Configuration;
        config.HeroBannerEnabled = dto.HeroBannerEnabled;
        config.HeroRotationSeconds = dto.HeroRotationSeconds is 5 or 8 or 12 ? dto.HeroRotationSeconds : 8;
        config.ItemsPerRow = Math.Clamp(dto.ItemsPerRow, 1, MaxItemsPerRow);
        config.RecommendationRefreshHours = dto.RecommendationRefreshHours is 1 or 3 or 6 or 12 or 24
            ? dto.RecommendationRefreshHours
            : 6;

        if (dto.DefaultSectionOrder.Length > 0)
        {
            config.DefaultSectionOrder = dto.DefaultSectionOrder
                .Where(s => PluginConfiguration.AllSections.Contains(s, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        config.DefaultDisabledSections = dto.DefaultDisabledSections
            .Where(s => PluginConfiguration.AllSections.Contains(s, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        plugin.SaveConfiguration();
        return NoContent();
    }

    /// <summary>
    /// Lists users who have customised their layout. Admin only.
    /// </summary>
    /// <returns>The user overrides.</returns>
    [HttpGet("Config/Users")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<List<UserOverrideDto>> GetUserOverrides()
    {
        var result = new List<UserOverrideDto>();
        foreach (var (userId, prefs) in _prefsStore.GetAll())
        {
            string? username = null;
            if (Guid.TryParse(userId, out var guid))
            {
                username = _userManager.GetUserById(guid)?.Username;
            }

            result.Add(new UserOverrideDto
            {
                UserId = userId,
                Username = username,
                SectionOrder = prefs.SectionOrder,
                DisabledSections = prefs.DisabledSections
            });
        }

        return result;
    }

    /// <summary>
    /// Resets a user's preferences back to server-wide defaults. Admin only.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>No content.</returns>
    [HttpPost("Config/ResetUser/{userId}")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult ResetUser([FromRoute] Guid userId)
    {
        _prefsStore.Reset(userId);
        return NoContent();
    }

    /// <summary>
    /// Gets plugin health: version, status and per-section refresh timestamps.
    /// </summary>
    /// <returns>The health payload.</returns>
    [HttpGet("Health")]
    [Authorize]
    public ActionResult<HealthDto> GetHealth()
    {
        return new HealthDto
        {
            Version = Plugin.Instance?.Version.ToString() ?? "unknown",
            Status = _health.Status,
            StartupError = _health.StartupError,
            ScriptInjected = _health.ScriptInjected,
            SectionRefreshes = new Dictionary<string, DateTimeOffset>(_health.SectionRefreshes)
        };
    }

    /// <summary>
    /// Serves the embedded web assets (home.html / home.css / home.js).
    /// Anonymous: these are static UI files with no data; all data endpoints
    /// require authentication.
    /// </summary>
    /// <param name="fileName">The asset file name.</param>
    /// <returns>The asset.</returns>
    [HttpGet("Web/{fileName}")]
    [AllowAnonymous]
    public ActionResult GetWebAsset([FromRoute] string fileName)
    {
        var contentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["home.html"] = MediaTypeNames.Text.Html,
            ["home.css"] = "text/css",
            ["home.js"] = "text/javascript"
        };

        if (!contentTypes.TryGetValue(fileName, out var contentType))
        {
            return NotFound();
        }

        var stream = typeof(Plugin).Assembly
            .GetManifestResourceStream($"Jellyfin.Plugin.JuddHome.Web.{fileName}");
        if (stream is null)
        {
            return NotFound();
        }

        return File(stream, contentType);
    }

    private List<HomeItemDto> GetNextUpItems(User user, int limit)
    {
        try
        {
            var result = _tvSeriesManager.GetNextUp(
                new NextUpQuery { User = user, Limit = limit },
                new DtoOptions());

            return result.Items
                .Select(i => HomeItemDto.FromItem(i))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JuddHome: Next Up query failed for user {UserId}", user.Id);
            return new List<HomeItemDto>();
        }
    }

    private User? GetCurrentUser()
    {
        var claim = User.FindFirst("Jellyfin-UserId")?.Value;
        if (!Guid.TryParse(claim, out var userId) || userId == Guid.Empty)
        {
            return null;
        }

        return _userManager.GetUserById(userId);
    }
}
