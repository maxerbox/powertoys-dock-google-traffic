using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace GoogleTrafficDock;

public partial class GoogleTrafficDockCommandsProvider : CommandProvider
{
    private readonly SettingsManager _settingsManager = new();
    private readonly RoutesStore _routesStore;
    private readonly TrafficService _trafficService;
    private readonly TrafficPage _trafficPage;
    private readonly TrafficDockPage _trafficDockPage;
    private readonly ICommandItem[] _commands;

    public GoogleTrafficDockCommandsProvider()
    {
        DisplayName = "Google Traffic";
        Id = "com.googletraffic";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");

        _routesStore = new RoutesStore(_settingsManager);
        _trafficService = new TrafficService(_settingsManager, _routesStore);
        _trafficPage = new TrafficPage(_trafficService, _settingsManager, _routesStore);
        _trafficDockPage = new TrafficDockPage(_trafficService, _settingsManager, _routesStore);

        _commands =
        [
            new CommandItem(_trafficPage)
            {
                Title = "Google Traffic",
                Subtitle = "Live traffic for your saved routes",
                MoreCommands =
                [
                    new CommandContextItem(new RoutesPage(_routesStore))
                    {
                        Title = "Manage routes",
                        Icon = new IconInfo("\uE71D"), // List
                    },
                    new CommandContextItem(new RouteFormPage(_routesStore))
                    {
                        Title = "Add a route",
                        Icon = new IconInfo("\uE710"), // Add
                    },
                    new CommandContextItem(_settingsManager.SettingsPage)
                    {
                        Title = "Settings",
                        Icon = new IconInfo("\uE713"), // Settings
                    },
                    new CommandContextItem(new RefreshCommand(_trafficService))
                    {
                        Title = "Refresh now",
                        Icon = new IconInfo("\uE72C"), // Refresh
                    },
                ],
            },
        ];

        Settings = _settingsManager.Settings;
    }

    public override ICommandItem[] TopLevelCommands()
    {
        return _commands;
    }

    /// <summary>
    /// Exposes the saved routes as a dock band whose buttons show the route
    /// name, the traffic-light icon and the live travel time in the title.
    /// </summary>
    public override ICommandItem[]? GetDockBands()
    {
        return [new CommandItem(_trafficDockPage) { Title = "Traffic" }];
    }
}
