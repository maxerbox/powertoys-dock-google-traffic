using System.Collections.Concurrent;

namespace GoogleTrafficDock;

/// <summary>
/// Reads the saved routes from the <see cref="RoutesStore"/>, polls Google's
/// internal (keyless) endpoints on a timer, caches the latest statuses and
/// notifies listeners when data changes.
/// </summary>
internal sealed class TrafficService : IDisposable
{
    // Keyless client that talks to Google's internal endpoints, exactly like
    // the maps.google.com web client does.
    private readonly GooglePreviewDirectionsClient _client = new();
    private readonly SettingsManager _settings;
    private readonly RoutesStore _store;
    private readonly JourneyHistoryStore _history = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, RouteStatus> _statuses = [];
    private System.Threading.Timer? _timer;
    private int _refreshInProgress;

    public event EventHandler? StatusesChanged;

    public TrafficService(SettingsManager settings, RoutesStore store)
    {
        _settings = settings;
        _store = store;
        _settings.SettingsUpdated += OnSettingsUpdated;
        _store.RoutesChanged += OnRoutesChanged;
        ResetTimer();
        _ = RefreshAsync();
    }

    public IReadOnlyList<RouteStatus> Statuses
    {
        get
        {
            lock (_lock)
            {
                return [.. _store.Routes
                    .Select(r => _statuses.TryGetValue(r.Id, out var status)
                        ? status
                        : new RouteStatus { Route = r, Error = null })];
            }
        }
    }

    public bool HasRoutes => _store.Routes.Count > 0;

    /// <summary>Recorded travel durations, for the details-pane chart.</summary>
    public JourneyHistoryStore History => _history;

    /// <summary>Triggers an immediate refresh (fire and forget).</summary>
    public void RefreshNow()
    {
        _ = RefreshAsync();
    }

    public void Dispose()
    {
        _settings.SettingsUpdated -= OnSettingsUpdated;
        _store.RoutesChanged -= OnRoutesChanged;
        _timer?.Dispose();
        _cts.Cancel();
        _cts.Dispose();
    }

    private void OnRoutesChanged(object? sender, EventArgs e)
    {
        RefreshNow();
    }

    private void OnSettingsUpdated(object? sender, EventArgs e)
    {
        ResetTimer();
        RefreshNow();
    }

    private void ResetTimer()
    {
        lock (_lock)
        {
            var interval = TimeSpan.FromMinutes(Math.Max(1, _settings.RefreshMinutes));
            _timer?.Dispose();
            _timer = new System.Threading.Timer(
                _ => RefreshNow(),
                null,
                interval,
                interval);
        }
    }

    private async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshInProgress, 1) == 1)
        {
            return; // a refresh is already in flight
        }

        try
        {
            var routes = _store.Routes;

            if (routes.Count > 0)
            {
                await Task.WhenAll(routes.Select(async route =>
                {
                    var status = await _client.GetRouteStatusAsync(route, _cts.Token).ConfigureAwait(false);
                    _statuses[route.Id] = status;

                    // Record successful durations so the details pane can
                    // chart "duration by time of day".
                    if (status.Error is null && status.Duration is { } duration)
                    {
                        _history.Record(route.Id, (int)Math.Round(duration.TotalMinutes));
                    }
                })).ConfigureAwait(false);
            }

            StatusesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch
        {
            // never let a polling failure kill the timer
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInProgress, 0);
        }
    }
}
