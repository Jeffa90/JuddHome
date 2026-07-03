using System.Text.Json;
using Jellyfin.Plugin.JuddHome.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JuddHome.Services;

/// <summary>
/// Reads and writes per-user home screen preferences, which are stored inside
/// <see cref="PluginConfiguration.UserPreferencesJson"/> as a JSON dictionary
/// keyed by userId (Guid as string).
/// </summary>
public class UserPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger<UserPreferencesStore> _logger;
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="UserPreferencesStore"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public UserPreferencesStore(ILogger<UserPreferencesStore> logger)
    {
        _logger = logger;
    }

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Gets every stored per-user preference override.
    /// </summary>
    /// <returns>A dictionary keyed by userId string.</returns>
    public Dictionary<string, UserPreferences> GetAll()
    {
        lock (_lock)
        {
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, UserPreferences>>(
                    Config.UserPreferencesJson, JsonOptions) ?? new Dictionary<string, UserPreferences>();
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "JuddHome: stored user preferences JSON is invalid; falling back to defaults");
                return new Dictionary<string, UserPreferences>();
            }
        }
    }

    /// <summary>
    /// Gets the effective preferences for a user: their stored override, or the
    /// server-wide defaults when they have never customised their layout.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The effective preferences.</returns>
    public UserPreferences GetEffective(Guid userId)
    {
        var all = GetAll();
        if (all.TryGetValue(userId.ToString("D"), out var prefs)
            && prefs.SectionOrder.Length > 0)
        {
            return Sanitise(prefs);
        }

        var config = Config;
        return new UserPreferences
        {
            SectionOrder = SanitiseOrder(config.DefaultSectionOrder),
            DisabledSections = config.DefaultDisabledSections
                .Where(s => PluginConfiguration.AllSections.Contains(s, StringComparer.OrdinalIgnoreCase))
                .ToArray()
        };
    }

    /// <summary>
    /// Gets a value indicating whether the user has a stored override.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>True when the user has customised their layout.</returns>
    public bool HasOverride(Guid userId) => GetAll().ContainsKey(userId.ToString("D"));

    /// <summary>
    /// Saves a per-user preference override.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="prefs">The preferences to store.</param>
    public void Save(Guid userId, UserPreferences prefs)
    {
        lock (_lock)
        {
            var all = GetAll();
            all[userId.ToString("D")] = Sanitise(prefs);
            Persist(all);
        }
    }

    /// <summary>
    /// Removes a user's override so they fall back to server-wide defaults.
    /// </summary>
    /// <param name="userId">The user id.</param>
    public void Reset(Guid userId)
    {
        lock (_lock)
        {
            var all = GetAll();
            if (all.Remove(userId.ToString("D")))
            {
                Persist(all);
            }
        }
    }

    private static UserPreferences Sanitise(UserPreferences prefs) => new()
    {
        SectionOrder = SanitiseOrder(prefs.SectionOrder),
        DisabledSections = prefs.DisabledSections
            .Where(s => PluginConfiguration.AllSections.Contains(s, StringComparer.OrdinalIgnoreCase))
            .ToArray()
    };

    /// <summary>
    /// Keeps only known section types, removes duplicates and appends any
    /// missing sections at the end so new sections appear after upgrades.
    /// </summary>
    private static string[] SanitiseOrder(string[] order)
    {
        var result = order
            .Where(s => PluginConfiguration.AllSections.Contains(s, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        result.AddRange(PluginConfiguration.AllSections
            .Where(s => !result.Contains(s, StringComparer.OrdinalIgnoreCase)));
        return result.ToArray();
    }

    private void Persist(Dictionary<string, UserPreferences> all)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            _logger.LogWarning("JuddHome: plugin instance unavailable; preferences not persisted");
            return;
        }

        plugin.Configuration.UserPreferencesJson = JsonSerializer.Serialize(all, JsonOptions);
        plugin.SaveConfiguration();
    }
}
