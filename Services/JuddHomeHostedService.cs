using System.Text.RegularExpressions;
using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JuddHome.Services;

/// <summary>
/// Background service that builds recommendation profiles on startup, refreshes
/// them on the configured interval, and injects the home screen script into the
/// Jellyfin web client. Startup never throws — failures are logged and surfaced
/// via <see cref="HealthService"/> so Jellyfin always starts.
/// </summary>
public sealed class JuddHomeHostedService : IHostedService, IDisposable
{
    // Matches any previously-injected JuddHome script tag, with or without the
    // ?v= cache-busting marker added from 1.0.7.0 onward, so upgrading from an
    // older version still finds and replaces it instead of leaving a stale tag.
    private static readonly Regex ExistingScriptTag =
        new(@"<script src=""\.\./JuddHome/Web/home\.js(\?v=[^""]*)?"" defer></script>", RegexOptions.Compiled);

    private readonly IServerApplicationPaths _appPaths;
    private readonly RecommendationEngine _engine;
    private readonly HealthService _health;
    private readonly ILogger<JuddHomeHostedService> _logger;

    private Timer? _refreshTimer;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Initializes a new instance of the <see cref="JuddHomeHostedService"/> class.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IServerApplicationPaths"/> interface.</param>
    /// <param name="engine">The recommendation engine.</param>
    /// <param name="health">The health service.</param>
    /// <param name="logger">The logger.</param>
    public JuddHomeHostedService(
        IServerApplicationPaths appPaths,
        RecommendationEngine engine,
        HealthService health,
        ILogger<JuddHomeHostedService> logger)
    {
        _appPaths = appPaths;
        _engine = engine;
        _health = health;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _cts = new CancellationTokenSource();

            InjectHomeScript();

            var hours = Plugin.Instance?.Configuration.RecommendationRefreshHours ?? 6;
            if (hours < 1)
            {
                hours = 6;
            }

            var interval = TimeSpan.FromHours(hours);
            _refreshTimer = new Timer(_ => RefreshProfiles(), null, TimeSpan.Zero, interval);
            _logger.LogInformation("JuddHome: started; profile refresh every {Hours}h", hours);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JuddHome: startup failed; running in degraded mode");
            _health.ReportStartupFailure(ex.Message);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        _refreshTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _refreshTimer?.Dispose();
        _cts?.Dispose();
    }

    private void RefreshProfiles()
    {
        var token = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await _engine.RefreshAllProfilesAsync(token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "JuddHome: scheduled profile refresh failed");
                }
            },
            token);
    }

    /// <summary>
    /// Injects a script tag into jellyfin-web's index.html so the JuddHome UI loads
    /// on every page. Uses a relative src so reverse-proxy sub-paths keep working.
    /// The tag carries a ?v=&lt;plugin version&gt; marker so every update changes the
    /// actual URL — without this, browsers and TV apps cache home.js/home.css/
    /// home.html against the unversioned URL and silently keep serving whatever
    /// was first loaded, no matter how many versions ship after that (this bit
    /// every release before 1.0.7.0). If the web directory is read-only (common in
    /// Docker), logs instructions for manual injection instead of failing.
    /// </summary>
    private void InjectHomeScript()
    {
        var version = Plugin.Instance?.Version.ToString() ?? "0";
        var scriptTag = $"<script src=\"../JuddHome/Web/home.js?v={version}\" defer></script>";

        try
        {
            var indexPath = Path.Combine(_appPaths.WebPath, "index.html");
            if (!File.Exists(indexPath))
            {
                _logger.LogWarning(
                    "JuddHome: index.html not found at {Path}; add {Tag} to your web client's index.html manually",
                    indexPath,
                    scriptTag);
                _health.ReportScriptInjection(false);
                return;
            }

            var html = File.ReadAllText(indexPath);
            if (html.Contains(scriptTag, StringComparison.Ordinal))
            {
                // Already injected for this exact version — nothing to do.
                _health.ReportScriptInjection(true);
                return;
            }

            // Strip any previous version's tag first, so an update actually changes
            // the URL browsers/TVs see instead of leaving a second, stale tag behind.
            html = ExistingScriptTag.Replace(html, string.Empty);

            var closingBody = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (closingBody < 0)
            {
                _logger.LogWarning("JuddHome: could not find </body> in index.html; script not injected");
                _health.ReportScriptInjection(false);
                return;
            }

            File.WriteAllText(indexPath, html.Insert(closingBody, scriptTag));
            _health.ReportScriptInjection(true);
            _logger.LogInformation("JuddHome: injected home screen script into {Path} ({Tag})", indexPath, scriptTag);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "JuddHome: could not inject home screen script (web directory may be read-only). "
                + "Add {Tag} before </body> in the web client's index.html manually",
                scriptTag);
            _health.ReportScriptInjection(false);
        }
    }
}
