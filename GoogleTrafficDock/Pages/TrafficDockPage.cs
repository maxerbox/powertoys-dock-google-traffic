using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace GoogleTrafficDock;

/// <summary>
/// The page rendered inside the Command Palette Dock band. Unlike the main
/// <see cref="TrafficPage"/> (where the time lives in the subtitle), this page
/// puts the live travel time directly in each item's <c>Title</c> — the dock
/// only renders title + icon, so "Route · 17 min" is what the user glances at.
/// Items are persistent <see cref="ListItem"/> instances mutated in place
/// (same live-update technique as the Clock dock extension): Title/Icon
/// property setters raise change notifications, so the band refreshes
/// whenever a traffic poll completes.
/// </summary>
internal sealed partial class TrafficDockPage : ListPage
{
    private readonly TrafficService _service;
    private readonly SettingsManager _settings;
    private readonly RoutesStore _store;

    private const int MaxNameLength = 10;

    // Cached icons: reusing the same IconInfo instances (instead of building a
    // new one per poll) avoids the dock icon flickering on every refresh.
    private static readonly IconInfo GrayIcon = IconHelpers.FromRelativePath("Assets\\TrafficGray.png");
    private static readonly IconInfo GreenIcon = IconHelpers.FromRelativePath("Assets\\TrafficGreen.png");
    private static readonly IconInfo OrangeIcon = IconHelpers.FromRelativePath("Assets\\TrafficOrange.png");
    private static readonly IconInfo RedIcon = IconHelpers.FromRelativePath("Assets\\TrafficRed.png");

    private readonly Dictionary<string, DockEntry> _dockItems = [];

    /// <summary>Serializes item mutations between the fetch thread and host RPC threads.</summary>
    private readonly object _gate = new();

    /// <summary>Latest rendered items; what GetItems hands to the host.</summary>
    private IListItem[]? _snapshot;

    /// <summary>A dock item plus the last state it was rendered with, so we only
    /// touch Icon/Command when something actually changed.</summary>
    private sealed class DockEntry
    {
        public required ListItem Item { get; init; }

        // true because the ListItem ctor command is already the OpenUrlCommand.
        public bool OpenOnActivate { get; set; } = true;

        public TrafficLevel? Level { get; set; }

        public bool HasError { get; set; }

        public bool Loaded { get; set; }
    }

    public TrafficDockPage(TrafficService service, SettingsManager settings, RoutesStore store)
    {
        _service = service;
        _settings = settings;
        _store = store;
        Title = "Traffic";
        Name = "Open";
        Id = "com.googletraffic.dockpage";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");

        // StatusesChanged fires on the fetch threadpool thread; GetItems
        // arrives on the host's COM RPC threads. All mutations are serialized
        // by _gate and the fetch thread only POSTS updates — GetItems must
        // NEVER block on the page thread: RaiseItemsChanged is a synchronous
        // COM call into the host, and the host re-enters GetItems from that
        // event, so a GetItems that waits for the thread raising the event
        // deadlocks (dock band stuck on "loading...").
        _service.StatusesChanged += (_, _) => UiThread.Post(UpdateItems);
        _store.RoutesChanged += (_, _) => UiThread.Post(UpdateItems);
    }

    public override IListItem[] GetItems()
    {
        lock (_gate)
        {
            _snapshot ??= BuildItems();
            return _snapshot;
        }
    }

    /// <summary>Page-thread entry: rebuild under the lock, then notify (outside it).</summary>
    private void UpdateItems()
    {
        lock (_gate)
        {
            _snapshot = BuildItems();
        }

        RaiseItemsChanged();
    }

    private IListItem[] BuildItems()
    {
        var routes = _store.Routes;

        // Drop items for deleted routes.
        foreach (var staleId in _dockItems.Keys.Where(id => routes.All(r => r.Id != id)).ToArray())
        {
            _dockItems.Remove(staleId);
        }

        var statuses = _service.Statuses;
        var openOnActivate = _settings.OpenOnActivate;

        foreach (var route in routes)
        {
            if (!_dockItems.TryGetValue(route.Id, out var entry))
            {
                entry = new DockEntry
                {
                    Item = new ListItem(new OpenItineraryCommand(route.MapsUrl))
                    {
                        Icon = GrayIcon,
                        // No "Open in Google Maps" here: the palette already
                        // lists the item's main command first in the right-click
                        // menu — duplicating it shows two buttons.
                        MoreCommands =
                        [
                            new CommandContextItem(new RefreshCommand(_service))
                            {
                                Title = "Refresh traffic",
                                Icon = new IconInfo("\uE72C"), // Refresh
                            },
                            new CommandContextItem(new RouteFormPage(_store, route))
                            {
                                Title = "Edit route",
                                Icon = new IconInfo("\uE70F"), // Edit
                            },
                            new CommandContextItem(new DeleteRouteCommand(_store, route))
                            {
                                Title = "Delete route",
                                Icon = new IconInfo("\uE74D"), // Delete
                            },
                            new CommandContextItem(new RoutesPage(_store))
                            {
                                Title = "Manage routes",
                                Icon = new IconInfo("\uE71D"), // List
                            },
                        ],
                    },
                };
                _dockItems[route.Id] = entry;
            }

            var item = entry.Item;
            var status = statuses.FirstOrDefault(s => s.Route.Id == route.Id);
            var hasError = status?.Error is not null;
            var level = status?.Level;

            // The dock clips long labels, so keep the title compact:
            // abbreviated name + short time ("Paris · 17m").
            item.Title = status is null
                ? Abbreviate(route.Name)
                : hasError
                    ? $"{Abbreviate(route.Name)} ⚠"
                    : $"{Abbreviate(route.Name)} · {status.DurationText}";

            item.Subtitle = status?.Subtitle ?? $"{route.From} → {route.To}";

            // Only touch Icon/Command when the state actually changed —
            // reassigning them on every poll makes the dock icon flicker.
            if (entry.Loaded != (status is not null) || entry.HasError != hasError || entry.Level != level)
            {
                entry.Loaded = status is not null;
                entry.HasError = hasError;
                entry.Level = level;

                item.Icon = hasError
                    ? GrayIcon
                    : level switch
                    {
                        TrafficLevel.Green => GreenIcon,
                        TrafficLevel.Orange => OrangeIcon,
                        TrafficLevel.Red => RedIcon,
                        _ => GrayIcon,
                    };
            }

            if (entry.OpenOnActivate != openOnActivate)
            {
                entry.OpenOnActivate = openOnActivate;
                item.Command = openOnActivate
                    ? new OpenItineraryCommand(route.MapsUrl)
                    : new RefreshCommand(_service);

                // When the main command stops being the open command, the
                // right-click menu needs an explicit "Open in Google Maps"
                // entry (and vice versa — never both, or it shows twice).
                item.MoreCommands =
                [
                    .. (!openOnActivate
                        ?
                        [
                            new CommandContextItem(new OpenItineraryCommand(route.MapsUrl))
                            {
                                Title = "Open in Google Maps",
                                Icon = new IconInfo("\uE8A7"), // Map
                            },
                        ]
                        : Array.Empty<CommandContextItem>()),
                    new CommandContextItem(new RefreshCommand(_service))
                    {
                        Title = "Refresh traffic",
                        Icon = new IconInfo("\uE72C"), // Refresh
                    },
                    new CommandContextItem(new RouteFormPage(_store, route))
                    {
                        Title = "Edit route",
                        Icon = new IconInfo("\uE70F"), // Edit
                    },
                    new CommandContextItem(new DeleteRouteCommand(_store, route))
                    {
                        Title = "Delete route",
                        Icon = new IconInfo("\uE74D"), // Delete
                    },
                    new CommandContextItem(new RoutesPage(_store))
                    {
                        Title = "Manage routes",
                        Icon = new IconInfo("\uE71D"), // List
                    },
                ];
            }
        }

        return [.. _dockItems.Values.Select(e => e.Item)];
    }

    /// <summary>Shortens a route name so dock labels don't get ellipsed.</summary>
    private static string Abbreviate(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length <= MaxNameLength ? trimmed : $"{trimmed[..(MaxNameLength - 1)]}…";
    }
}
