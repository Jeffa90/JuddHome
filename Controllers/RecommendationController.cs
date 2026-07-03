using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JuddHome.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JuddHome.Controllers;

/// <summary>
/// Recommendation endpoints: hero banner items and the weighted personalised
/// recommendation list.
/// </summary>
[ApiController]
[Route("JuddHome")]
public class RecommendationController : ControllerBase
{
    private readonly IUserManager _userManager;
    private readonly RecommendationEngine _engine;
    private readonly HealthService _health;
    private readonly ILogger<RecommendationController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecommendationController"/> class.
    /// </summary>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="engine">The recommendation engine.</param>
    /// <param name="health">The health service.</param>
    /// <param name="logger">The logger.</param>
    public RecommendationController(
        IUserManager userManager,
        RecommendationEngine engine,
        HealthService health,
        ILogger<RecommendationController> logger)
    {
        _userManager = userManager;
        _engine = engine;
        _health = health;
        _logger = logger;
    }

    /// <summary>
    /// Gets 5 hero banner items: a mix of continue-watching and highly rated
    /// unwatched content, falling back to top community-rated for new users.
    /// </summary>
    /// <returns>The hero items.</returns>
    [HttpGet("Hero")]
    [Authorize]
    public ActionResult<List<HomeItemDto>> GetHero()
    {
        var user = GetCurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        try
        {
            var items = _engine.GetHeroItems(user, 5)
                .Select(x => HomeItemDto.FromItem(x.Item, x.ProgressPercent))
                .ToList();
            _health.ReportSectionRefresh("HeroBanner");
            return items;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JuddHome: hero endpoint failed for user {UserId}", user.Id);
            return new List<HomeItemDto>();
        }
    }

    /// <summary>
    /// Gets the weighted personalised recommendation list (top 20) with scores.
    /// </summary>
    /// <param name="limit">Optional item limit (default 20, max 40).</param>
    /// <returns>The scored recommendations.</returns>
    [HttpGet("Recommendations")]
    [Authorize]
    public ActionResult<List<HomeItemDto>> GetRecommendations([FromQuery] int limit = 20)
    {
        var user = GetCurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        try
        {
            var items = _engine.GetRecommendations(user, Math.Clamp(limit, 1, 40))
                .Select(s => HomeItemDto.FromItem(s.Item, score: s.Score))
                .ToList();
            _health.ReportSectionRefresh("RecommendedForYou");
            return items;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JuddHome: recommendations endpoint failed for user {UserId}", user.Id);
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
