namespace GoogleTrafficDock;

using System.Globalization;

internal sealed class SavedRoute
{
    /// <summary>Stable identity of the route (survives renaming).</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public required string Name { get; init; }

    public required string From { get; init; }

    public required string To { get; init; }

    public string MapsUrl =>
        $"https://www.google.com/maps/dir/?api=1&origin={Uri.EscapeDataString(From)}&destination={Uri.EscapeDataString(To)}&travelmode=driving";
}

internal enum TrafficLevel
{
    Unknown,
    Green,
    Orange,
    Red,
}

internal sealed record RouteStatus
{
    public required SavedRoute Route { get; init; }

    /// <summary>Travel time including live traffic, if the API call succeeded.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Free-flow travel time (no traffic), if the API call succeeded.</summary>
    public TimeSpan? StaticDuration { get; init; }

    /// <summary>Lower bound of Google's "usual" range for this route, when reported.</summary>
    public TimeSpan? UsualMin { get; init; }

    /// <summary>Upper bound of Google's "usual" range for this route, when reported.</summary>
    public TimeSpan? UsualMax { get; init; }

    public TrafficLevel Level { get; init; }

    public string? Error { get; init; }

    public DateTime UpdatedUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Compact travel time for the Dock band title, e.g. "17m" / "1h05".</summary>
    public string DurationText
    {
        get
        {
            if (Error is not null)
            {
                return "⚠";
            }

            if (Duration is not { } duration)
            {
                return "…";
            }

            // Same rounding as Format() so the dock title ("21m") never
            // disagrees with the hover subtitle ("21 min").
            var totalMinutes = (int)Math.Round(duration.TotalMinutes);
            return totalMinutes >= 60
                ? $"{totalMinutes / 60}h{totalMinutes % 60:D2}"
                : $"{totalMinutes}m";
        }
    }

    public string Subtitle
    {
        get
        {
            if (Error is not null)
            {
                return $"⚠ {Error}";
            }

            if (Duration is not { } duration)
            {
                return "…";
            }

            var level = Level switch
            {
                TrafficLevel.Red => "🔴 heavier traffic than usual",
                TrafficLevel.Orange => "🟠 slower than usual",
                TrafficLevel.Green => "🟢 the usual traffic",
                _ => string.Empty,
            };

            var updated = UpdatedUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

            // Google's own phrasing: "Typically 14–20 min".
            if (UsualMin is { } usualMin && UsualMax is { } usualMax)
            {
                return $"{Format(duration)} · {level} · Typically {Format(usualMin)}–{Format(usualMax)} · {updated}";
            }

            var delay = StaticDuration is { } staticDuration && staticDuration > TimeSpan.Zero
                ? duration - staticDuration
                : (TimeSpan?)null;

            return delay is { } d && d.TotalMinutes >= 1
                ? $"{Format(duration)} · +{Format(d)} vs normal · {level} · {updated}"
                : $"{Format(duration)} · {level} · {updated}";
        }
    }

    private static string Format(TimeSpan t)
    {
        var totalMinutes = (int)Math.Round(t.TotalMinutes);
        return totalMinutes < 60
            ? $"{totalMinutes} min"
            : $"{totalMinutes / 60} h {totalMinutes % 60:D2} min";
    }
}
