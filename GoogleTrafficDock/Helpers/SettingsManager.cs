using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace GoogleTrafficDock;

internal sealed class SettingsManager
{
    private readonly Settings _settings;

    /// <summary>Raised whenever the user edits a setting in the Command Palette settings UI.</summary>
    public event EventHandler? SettingsUpdated;

    public SettingsManager()
    {
        _settings = new Settings();

        var refreshMinutes = new ChoiceSetSetting(
            "refreshMinutes",
            "Refresh rate",
            "How often traffic is refreshed (minutes)",
            [
                new ChoiceSetSetting.Choice("5 minutes", "5"),
                new ChoiceSetSetting.Choice("1 minute", "1"),
                new ChoiceSetSetting.Choice("15 minutes", "15"),
                new ChoiceSetSetting.Choice("30 minutes", "30"),
                new ChoiceSetSetting.Choice("60 minutes", "60"),
            ]);

        var openOnActivate = new ToggleSetting(
            "openOnActivate",
            "Open in Google Maps on click",
            "When enabled, clicking a route in the Dock opens the itinerary in Google Maps. When disabled, clicking refreshes the traffic data.",
            true);

        _settings.Add(refreshMinutes);
        _settings.Add(openOnActivate);

        _settings.SettingsChanged += OnSettingsChanged;
    }

    public ICommandSettings Settings => _settings;

    public ICommand SettingsPage => _settings.SettingsPage;

    /// <summary>
    /// Legacy semicolon-separated routes text (read once for the migration to
    /// RoutesStore). The "routes" setting is no longer registered, so reading
    /// it may throw <see cref="KeyNotFoundException"/> — always return an
    /// empty string instead of crashing the extension process.
    /// </summary>
    public string RoutesRaw
    {
        get
        {
            try
            {
                return _settings.GetSetting<string>("routes") ?? string.Empty;
            }
            catch (KeyNotFoundException)
            {
                return string.Empty;
            }
            catch (InvalidOperationException)
            {
                return string.Empty;
            }
        }
    }

    public int RefreshMinutes
    {
        get
        {
            var raw = _settings.GetSetting<string>("refreshMinutes");
            return int.TryParse(raw, out var minutes) && minutes > 0 ? minutes : 5;
        }
    }

    public bool OpenOnActivate => _settings.GetSetting<bool>("openOnActivate");

    private void OnSettingsChanged(object sender, Settings args)
    {
        SettingsUpdated?.Invoke(this, EventArgs.Empty);
    }
}
