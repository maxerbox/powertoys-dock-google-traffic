using System.Globalization;
using System.Text.RegularExpressions;

namespace GoogleTrafficDock;

/// <summary>
/// Keyless travel-time provider that uses only Google data, exactly like the
/// maps.google.com web client does (no API key, no cookies required):
///   - Geocoding: the internal <c>tbm=map</c> search RPC
///     (<c>www.google.com/search?tbm=map&amp;pb=...</c>)
///   - Routing + live traffic: the internal directions RPC
///     (<c>www.google.com/maps/preview/directions?pb=...</c>), which returns
///     the in-traffic duration, the free-flow duration and a congestion level
///     for driving routes.
/// Both endpoints are unofficial and can change shape at any time; parsing is
/// defensive and fails with a readable error instead of a wrong answer.
/// </summary>
internal sealed partial class GooglePreviewDirectionsClient : IDisposable
{
    private static readonly HttpClient Http = CreateClient();

    // Matches the best place in the search RPC response, in either of the two
    // observed shapes (the hex id may sit inside or right after the
    // coordinate array):
    //   `[null,null,51.501364,-0.14189],"0x4876...:0xa26a...","Buckingham Palace"`
    //   `[null,null,51.501364,-0.14189,"0x4876...:0xa26a...","Buckingham Palace"]`
    [GeneratedRegex(@"\[null,null,(-?\d+(?:\.\d+)?),(-?\d+(?:\.\d+)?),?\]?,?""0x[0-9a-fA-F]+:0x[0-9a-fA-F]+"",""((?:[^""\\]|\\.)*)""")]
    private static partial Regex PlaceRegex();

    // A plain "lat,lng" pair is used as-is instead of being geocoded.
    [GeneratedRegex(@"^\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*$")]
    private static partial Regex CoordinateRegex();

    // The search RPC pb template (captured from a live maps.google.com request;
    // the session token slot is not validated by Google, so a dummy is used).
    private const string SearchPbTemplate =
        "!1s{QUERY}!7i20!10b1!12m58!1m5!18b1!30b1!31m1!1b1!34e1!2m4!5m1!6e2!20e3!39b1!6m31!32i1!49b1!63m0!66b1!85b1!114b1!149b1!206b1!209b1!212b1!215b1!216b1!222b1!223b1!232b1!234b1!235b1!239b1!246b1!253b1!260b1!262b1!266b1!270b1!273b1!280b1!281b1!286b1!291m0!302i300!303i100!10b1!12b1!13b1!14b1!16b1!17m1!3e1!20m3!5e2!6b1!14b1!46m1!1b0!96b1!99b1!19m4!2m3!1i360!2i120!4i8!20m57!2m2!1i203!2i100!3m2!2i4!5b1!6m6!1m2!1i86!2i86!1m2!1i408!2i240!7m33!1m3!1e1!2b0!3e3!1m3!1e2!2b1!3e2!1m3!1e2!2b0!3e3!1m3!1e8!2b0!3e3!1m3!1e10!2b0!3e3!1m3!1e10!2b1!3e2!1m3!1e10!2b0!3e4!1m3!1e9!2b1!3e2!2b1!9b0!15m8!1m7!1m2!1m1!1e2!2m2!1i195!2i195!3i20!22m5!1sAAAAAAAAAAAAAAAAAAAAAA!7e81!14m1!3sAAAAAAAAAAAAAAAAAAAAAA!15i9937!24m107!1m25!13m9!2b1!3b1!4b1!6i1!8b1!9b1!14b1!20b1!25b1!18m14!3b1!4b1!5b1!6b1!13b1!14b1!17b1!21b1!22b1!32b1!33m1!1b1!34b1!36e2!10m1!8e3!11m1!3e1!17b1!20m2!1e3!1e6!24b1!25b1!26b1!27b1!29b1!30m1!2b1!36b1!37b1!39m3!2m2!2i1!3i1!43b1!52b1!54m1!1b1!55b1!56m1!1b1!61m2!1m1!1e1!65m5!3m4!1m3!1m2!1i224!2i298!72m22!1m8!2b1!5b1!7b1!12m4!1b1!2b1!4m1!1e1!4b1!8m10!1m6!4m1!1e1!4m1!1e3!4m1!1e4!3sother_user_google_review_posts__and__hotel_and_vr_partner_review_posts!6m1!1e1!9b1!89b1!90m2!1m1!1e2!98m3!1b1!2b1!3b1!103b1!113b1!114m3!1b1!2m1!1b1!117b1!122m1!1b1!126b1!127b1!128m1!1b1!26m4!2m3!1i80!2i92!4i8!30m28!1m6!1m2!1i0!2i0!2m2!1i530!2i768!1m6!1m2!1i974!2i0!2m2!1i1024!2i768!1m6!1m2!1i0!2i0!2m2!1i1024!2i20!1m6!1m2!1i0!2i748!2m2!1i1024!2i768!34m19!2b1!3b1!4b1!6b1!8m6!1b1!3b1!4b1!5b1!6b1!7b1!9b1!12b1!14b1!20b1!23b1!25b1!26b1!31b1!37m1!1e81!42b1!49m10!3b1!6m2!1b1!2b1!7m2!1e3!2b1!8b1!9b1!10e2!50m3!2e2!3m1!3b1!61b1!67m5!7b1!10b1!14b1!15m1!1b0!69i791!77b1";

    private readonly Dictionary<string, (double Lat, double Lon)> _geocodeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _geocodeGate = new(1, 1);
    private DateTime _lastGeocodeUtc = DateTime.MinValue;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Referrer = new Uri("https://www.google.com/maps/");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return client;
    }

    public async Task<RouteStatus> GetRouteStatusAsync(SavedRoute route, CancellationToken cancellationToken)
    {
        try
        {
            var from = await ResolvePointAsync(route.From, cancellationToken).ConfigureAwait(false);
            var to = await ResolvePointAsync(route.To, cancellationToken).ConfigureAwait(false);

            var pb = BuildDirectionsPb(from.Lat, from.Lon, to.Lat, to.Lon);
            var url = $"https://www.google.com/maps/preview/directions?authuser=0&hl=en&gl=us&pb={Uri.EscapeDataString(pb)}";

            using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new RouteStatus
                {
                    Route = route,
                    Error = $"Google Maps {(int)response.StatusCode}: {response.ReasonPhrase}",
                };
            }

            return ParseDirectionsResponse(body, route);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new RouteStatus
            {
                Route = route,
                Error = ex.Message,
            };
        }
    }

    /// <summary>
    /// Parses the directions RPC response. Body is the anti-hijack prefix
    /// <c>)]}'\n</c> followed by plain JSON: routes live at
    /// <c>data[0][1]</c>; each route head is <c>[modeEcho, via, [distM, distText],
    /// [durS, durText]]</c>; the traffic block for the first route lives at
    /// <c>route[1][0][0][10]</c> as <c>[[inTrafficS, text], null, level,
    /// [freeFlowS, text], [best, worst, rangeText]]</c>.
    /// </summary>
    private static RouteStatus ParseDirectionsResponse(string body, SavedRoute route)
    {
        var newline = body.IndexOf('\n');
        if (newline == -1 || !body.StartsWith(")]}'", StringComparison.Ordinal))
        {
            return new RouteStatus
            {
                Route = route,
                Error = "Unexpected Google Maps directions response (missing anti-hijack prefix)",
            };
        }

        using var doc = System.Text.Json.JsonDocument.Parse(body[(newline + 1)..]);
        var root = doc.RootElement;

        if (!TryGet(root, 0, out var container) || !TryGet(container, 1, out var routes) || routes.ValueKind != System.Text.Json.JsonValueKind.Array || routes.GetArrayLength() == 0)
        {
            return new RouteStatus
            {
                Route = route,
                Error = "No route found between these addresses",
            };
        }

        var first = routes[0];

        // Route head: [modeEcho, via, [distM, distText], [durS, durText]].
        if (!TryGet(first, 0, out var head) || head.ValueKind != System.Text.Json.JsonValueKind.Array || head.GetArrayLength() < 4)
        {
            return new RouteStatus
            {
                Route = route,
                Error = "Unexpected Google Maps directions response (no route head)",
            };
        }

        // Google silently ignores malformed pb options and answers with a
        // driving route, so the echoed travel mode is verified instead of
        // assumed (0 = driving).
        if (!head[0].TryGetInt32(out var modeEcho) || modeEcho != 0)
        {
            return new RouteStatus
            {
                Route = route,
                Error = $"Google Maps answered with an unexpected travel mode ({modeEcho})",
            };
        }

        var headDuration = ReadSeconds(head, 3);

        // Traffic block: [[inTrafficS, text], null, level, [freeFlowS, text],
        // [usualMinS, usualMaxS, "14–20 min"], [typicalNowS]].
        TimeSpan? inTraffic = null;
        TimeSpan? freeFlow = null;
        TimeSpan? usualMin = null;
        TimeSpan? usualMax = null;
        int googleLevel = 0;
        if (TryGet(first, 1, out var routeDetail)
            && TryGet(routeDetail, 0, out var detailArray)
            && TryGet(detailArray, 0, out var trafficList)
            && trafficList.ValueKind == System.Text.Json.JsonValueKind.Array
            && trafficList.GetArrayLength() > 10)
        {
            var block = trafficList[10];
            if (block.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                inTraffic = ReadSeconds(block, 0);
                freeFlow = ReadSeconds(block, 3);

                // Google's own server-computed traffic grade for the whole
                // route — the exact value the Maps web UI maps to the color of
                // the travel time: 3 = green (#2b7f3f), 2 = orange (#c54105),
                // 1 = red (#d32f28, "Heavier traffic than usual"). Verified
                // against the rendered DOM + stylesheet.
                if (block.GetArrayLength() > 2 && block[2].ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    block[2].TryGetInt32(out googleLevel);
                }

                // Google's "usual" range for the route (e.g. 14–20 min).
                if (block.GetArrayLength() > 4 && block[4].ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    usualMin = ReadSeconds(block[4], 0);
                    usualMax = ReadSeconds(block[4], 1);
                }

                // Traffic can never make a trip faster than free-flow; when it
                // appears to, the two numbers describe different journeys
                // (e.g. a ferry leg) and the block cannot be trusted.
                if (inTraffic is { } it && freeFlow is { } ff && it < ff * 0.98)
                {
                    inTraffic = null;
                    freeFlow = null;
                }
            }
        }

        var duration = inTraffic ?? headDuration;
        var staticDuration = freeFlow ?? headDuration;

        // Prefer Google's own grade; only fall back to a heuristic when the
        // response did not include one.
        var level = googleLevel switch
        {
            3 => TrafficLevel.Green,
            2 => TrafficLevel.Orange,
            1 => TrafficLevel.Red,
            _ => TrafficLevel.Unknown,
        };

        if (level == TrafficLevel.Unknown && duration is { } dur)
        {
            if (usualMax is { } max)
            {
                level = dur > max * 1.25 ? TrafficLevel.Red
                    : dur > max ? TrafficLevel.Orange
                    : TrafficLevel.Green;
            }
            else if (staticDuration is { } stat && stat > TimeSpan.Zero)
            {
                var ratio = dur / stat;
                level = ratio >= 1.30 ? TrafficLevel.Red : ratio >= 1.15 ? TrafficLevel.Orange : TrafficLevel.Green;
            }
        }

        return new RouteStatus
        {
            Route = route,
            Duration = duration,
            StaticDuration = staticDuration,
            UsualMin = usualMin,
            UsualMax = usualMax,
            Level = level,
        };
    }

    /// <summary>Resolves a settings value that is either a "lat,lng" pair or an address to geocode via Google.</summary>
    private async Task<(double Lat, double Lon)> ResolvePointAsync(string value, CancellationToken cancellationToken)
    {
        if (CoordinateRegex().Match(value) is { Success: true } coordMatch)
        {
            return (
                double.Parse(coordMatch.Groups[1].Value, CultureInfo.InvariantCulture),
                double.Parse(coordMatch.Groups[2].Value, CultureInfo.InvariantCulture));
        }

        lock (_geocodeCache)
        {
            if (_geocodeCache.TryGetValue(value, out var cached))
            {
                return cached;
            }
        }

        await _geocodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_geocodeCache)
            {
                if (_geocodeCache.TryGetValue(value, out var cached))
                {
                    return cached;
                }
            }

            // Be polite to an internal endpoint with no published limit.
            var sinceLast = DateTime.UtcNow - _lastGeocodeUtc;
            if (sinceLast < TimeSpan.FromSeconds(1.1))
            {
                await Task.Delay(TimeSpan.FromSeconds(1.1) - sinceLast, cancellationToken).ConfigureAwait(false);
            }

            var pb = SearchPbTemplate.Replace("{QUERY}", Uri.EscapeDataString(value));
            var url = $"https://www.google.com/search?tbm=map&authuser=0&hl=en&gl=us&q={Uri.EscapeDataString(value)}&pb={Uri.EscapeDataString(pb)}";

            using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            _lastGeocodeUtc = DateTime.UtcNow;
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var match = PlaceRegex().Match(body);
            if (!match.Success)
            {
                throw new InvalidOperationException($"Address not found: {value}");
            }

            var result = (
                Lat: double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                Lon: double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));

            lock (_geocodeCache)
            {
                _geocodeCache[value] = result;
            }

            return result;
        }
        finally
        {
            _geocodeGate.Release();
        }
    }

    /// <summary>
    /// The directions pb: two waypoints (<c>!1m4!3m2!3d{lat}!4d{lng}!6e2</c>),
    /// a viewport block around the midpoint, and the options block whose
    /// <c>!20m5!1e0</c> selects driving.
    /// </summary>
    private static string BuildDirectionsPb(double oLat, double oLng, double dLat, double dLng)
    {
        var midLat = (oLat + dLat) / 2;
        var midLng = (oLng + dLng) / 2;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"!1m4!3m2!3d{oLat:0.######}!4d{oLng:0.######}!6e2!1m4!3m2!3d{dLat:0.######}!4d{dLng:0.######}!6e2!3m12!1m3!1d20000!2d{midLng:0.######}!3d{midLat:0.######}!2m3!1f0.0!2f0.0!3f0.0!3m2!1i1024!2i768!4f13.1!6m6!20m5!1e0!2e3!5e2!6b1!14b1");
    }

    private static bool TryGet(System.Text.Json.JsonElement element, int index, out System.Text.Json.JsonElement value)
    {
        if (element.ValueKind == System.Text.Json.JsonValueKind.Array && element.GetArrayLength() > index)
        {
            value = element[index];
            return value.ValueKind != System.Text.Json.JsonValueKind.Null;
        }

        value = default;
        return false;
    }

    /// <summary>Reads a <c>[seconds, "text"]</c> pair at the given array index.</summary>
    private static TimeSpan? ReadSeconds(System.Text.Json.JsonElement element, int index)
    {
        if (!TryGet(element, index, out var pair)
            || pair.ValueKind != System.Text.Json.JsonValueKind.Array
            || pair.GetArrayLength() == 0)
        {
            return null;
        }

        var secondsValue = pair[0];
        return secondsValue.ValueKind == System.Text.Json.JsonValueKind.Number && secondsValue.TryGetDouble(out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    public void Dispose() => _geocodeGate.Dispose();
}
