using System.Collections.Concurrent;

namespace Jellyfin.Plugin.JuddHome.Services;

/// <summary>
/// Tracks plugin health: startup state, degraded status and per-section refresh timestamps.
/// Exposed via GET /JuddHome/Health.
/// </summary>
public class HealthService
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sectionRefreshes = new();
    private volatile string? _startupError;
    private volatile bool _scriptInjected;

    /// <summary>
    /// Gets the plugin status string: "Healthy" or "Degraded".
    /// </summary>
    public string Status => _startupError is null ? "Healthy" : "Degraded";

    /// <summary>
    /// Gets the startup error message, if startup failed.
    /// </summary>
    public string? StartupError => _startupError;

    /// <summary>
    /// Gets a value indicating whether the home screen script was injected into index.html.
    /// </summary>
    public bool ScriptInjected => _scriptInjected;

    /// <summary>
    /// Gets a snapshot of per-section last refresh timestamps.
    /// </summary>
    public IReadOnlyDictionary<string, DateTimeOffset> SectionRefreshes =>
        new Dictionary<string, DateTimeOffset>(_sectionRefreshes);

    /// <summary>
    /// Records that a section was refreshed (served fresh data) just now.
    /// </summary>
    /// <param name="sectionType">The section type key.</param>
    public void ReportSectionRefresh(string sectionType) =>
        _sectionRefreshes[sectionType] = DateTimeOffset.UtcNow;

    /// <summary>
    /// Records a startup failure. The plugin keeps running in degraded mode.
    /// </summary>
    /// <param name="message">A human-readable error message.</param>
    public void ReportStartupFailure(string message) => _startupError = message;

    /// <summary>
    /// Records whether the home screen script was injected into the web client.
    /// </summary>
    /// <param name="injected">True when injection succeeded.</param>
    public void ReportScriptInjection(bool injected) => _scriptInjected = injected;
}
