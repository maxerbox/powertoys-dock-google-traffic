using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace GoogleTrafficDock;

/// <summary>
/// "Manage routes" page: lists every saved route with edit (Enter), delete
/// (context menu, with confirmation) and an "Add a route" entry. Live-updates
/// when the store changes.
/// </summary>
internal sealed partial class RoutesPage : ListPage
{
    private readonly RoutesStore _store;

    public RoutesPage(RoutesStore store)
    {
        _store = store;
        Name = "Open";
        Title = "Manage routes";
        Id = "com.googletraffic.routespage";
        Icon = new IconInfo("\uE71D"); // AllApps list

        _store.RoutesChanged += (_, _) => UiThread.Post(() => RaiseItemsChanged());
    }

    public override IListItem[] GetItems()
    {
        var routes = _store.Routes;

        var items = new List<IListItem>
        {
            new ListItem(new RouteFormPage(_store))
            {
                Title = "Add a route",
                Subtitle = "Name, from address, to address",
                Icon = new IconInfo("\uE710"), // Add
            },
        };

        items.AddRange(routes.Select(route => new ListItem(new RouteFormPage(_store, route))
        {
            Title = route.Name,
            Subtitle = $"{route.From} → {route.To}",
            Icon = new IconInfo("\uE70F"), // Edit
            MoreCommands =
            [
                new CommandContextItem(new OpenItineraryCommand(route.MapsUrl))
                {
                    Title = "Open in Google Maps",
                    Icon = new IconInfo("\uE8A7"), // Map
                },
                new CommandContextItem(new DeleteRouteCommand(_store, route))
                {
                    Title = "Delete route",
                    Icon = new IconInfo("\uE74D"), // Delete
                },
            ],
        }));

        return [.. items];
    }
}

/// <summary>Deletes a route after an explicit confirmation step.</summary>
internal sealed partial class DeleteRouteCommand : InvokableCommand
{
    private readonly RoutesStore _store;
    private readonly SavedRoute _route;

    public DeleteRouteCommand(RoutesStore store, SavedRoute route)
    {
        _store = store;
        _route = route;
        Name = "Delete route";
        Icon = new IconInfo("\uE74D"); // Delete
    }

    public override CommandResult Invoke()
    {
        return CommandResult.Confirm(new ConfirmationArgs
        {
            Title = $"Delete '{_route.Name}'?",
            Description = "The route will be removed from your list and from the Dock.",
            IsPrimaryCommandCritical = true,
            PrimaryCommand = new AnonymousCommand(() =>
            {
                _store.Remove(_route.Id);
                new ToastStatusMessage($"Deleted route '{_route.Name}'").Show();
            })
            {
                Name = "Delete",
                Result = CommandResult.Dismiss(),
            },
        });
    }
}
