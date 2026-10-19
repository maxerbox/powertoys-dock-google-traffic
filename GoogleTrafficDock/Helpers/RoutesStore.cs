using System.Text.Json;

namespace GoogleTrafficDock;

/// <summary>
/// Persistent, ordered list of saved routes, stored as JSON under
/// <c>%USERPROFILE%\.googletrafficdock\routes.json</c>.
/// The file deliberately lives OUTSIDE <c>%LOCALAPPDATA%</c>: inside an MSIX
/// package, AppData writes are virtualized into the package's private
/// LocalCache folder, which is deleted whenever the package is uninstalled
/// (Remove-AppxPackage before reinstalling an update) — that silently wiped
/// the routes on every version bump. The user-profile root is not
/// virtualized, so the store survives reinstalls. A one-time migration from
/// the old virtualized location (and before that, the semicolon-separated
/// "routes" text setting) keeps existing data.
/// </summary>
internal sealed class RoutesStore
{
    private static readonly string StoreDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".googletrafficdock");

    private static readonly string StorePath = Path.Combine(StoreDir, "routes.json");

    // Pre-2.0 location — inside the MSIX container. Kept only as a migration
    // source; when running packaged, this resolves to the virtualized copy of
    // the currently installed package.
    private static readonly string LegacyStorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GoogleTrafficDock",
        "routes.json");

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly object _lock = new();
    private List<SavedRoute> _routes = [];

    /// <summary>Raised whenever a route is added, edited or removed.</summary>
    public event EventHandler? RoutesChanged;

    public RoutesStore(SettingsManager settings)
    {
        Load(settings);
    }

    public IReadOnlyList<SavedRoute> Routes
    {
        get
        {
            lock (_lock)
            {
                return [.. _routes];
            }
        }
    }

    /// <summary>Adds a route, or replaces the existing route with the same id.</summary>
    public void Upsert(SavedRoute route)
    {
        lock (_lock)
        {
            var index = _routes.FindIndex(r => r.Id == route.Id);
            if (index >= 0)
            {
                _routes[index] = route;
            }
            else
            {
                _routes.Add(route);
            }

            Save();
        }

        RoutesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string id)
    {
        lock (_lock)
        {
            if (_routes.RemoveAll(r => r.Id == id) == 0)
            {
                return;
            }

            Save();
        }

        RoutesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Load(SettingsManager settings)
    {
        lock (_lock)
        {
            if (File.Exists(StorePath))
            {
                try
                {
                    var loaded = JsonSerializer.Deserialize<List<SavedRoute>>(File.ReadAllText(StorePath), JsonOpts);
                    if (loaded is not null)
                    {
                        _routes = loaded;
                        return;
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    // fall through to the legacy migration / empty list
                }
            }

            // Migration from the old MSIX-virtualized AppData location.
            if (File.Exists(LegacyStorePath))
            {
                try
                {
                    var loaded = JsonSerializer.Deserialize<List<SavedRoute>>(File.ReadAllText(LegacyStorePath), JsonOpts);
                    if (loaded is not null && loaded.Count > 0)
                    {
                        _routes = loaded;
                        Save();
                        return;
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    // fall through to the legacy setting migration
                }
            }

            // One-time migration from the old semicolon-separated text setting.
            // Never let a settings read failure kill the extension process.
            try
            {
                _routes = ParseLegacyRoutes(settings.RoutesRaw);
            }
            catch
            {
                _routes = [];
            }

            Save();
        }
    }

    private static List<SavedRoute> ParseLegacyRoutes(string raw)
    {
        var result = new List<SavedRoute>();

        foreach (var entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split('|');
            if (parts.Length < 3)
            {
                continue;
            }

            var name = parts[0].Trim();
            var from = parts[1].Trim();
            var to = parts[2].Trim();

            if (name.Length == 0 || from.Length == 0 || to.Length == 0)
            {
                continue;
            }

            result.Add(new SavedRoute { Name = name, From = from, To = to });
        }

        return result;
    }

    private void Save()
    {
        Directory.CreateDirectory(StoreDir);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(_routes, JsonOpts));
    }
}
