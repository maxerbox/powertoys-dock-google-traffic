# Live test of the GoogleTrafficDock keyless flow for a given itinerary.
# Extracts SearchPbTemplate + directions pb logic from the real client source,
# so this tests exactly what the installed extension will do.
param(
    [string]$From = "Gare de Lyon, Paris",
    [string]$To = "Versailles"
)

$ErrorActionPreference = "Stop"
$src = Get-Content (Join-Path $PSScriptRoot "..\GoogleTrafficDock\Services\GooglePreviewDirectionsClient.cs") -Raw

# Extract the SearchPbTemplate constant from the source (multi-line C# string concat)
$m = [regex]::Match($src, 'SearchPbTemplate =\s*((?:"[^"]*"\s*)+);')
if (-not $m.Success) { throw "SearchPbTemplate not found in source" }
$searchPbTemplate =(([regex]::Matches($m.Groups[1].Value, '"([^"]*)"') | ForEach-Object { $_.Groups[1].Value }) -join "")

$ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Get-Place([string]$query) {
    $pb = $searchPbTemplate.Replace("{QUERY}", [Uri]::EscapeDataString($query))
    $url = "https://www.google.com/search?tbm=map&authuser=0&hl=en&gl=us&q=$([Uri]::EscapeDataString($query))&pb=$([Uri]::EscapeDataString($pb))"
    $resp = Invoke-WebRequest -Uri $url -UserAgent $ua -Headers @{ Referer = "https://www.google.com/maps/"; "Accept-Language" = "en-US,en;q=0.9" } -UseBasicParsing
    if ($resp.StatusCode -ne 200) { throw "search RPC HTTP $([int]$resp.StatusCode)" }
    $body = $resp.Content
    # Accepts both shapes: [null,null,lat,lng,"0x…","Name"] and [null,null,lat,lng],"0x…","Name"
    $m = [regex]::Match($body, '\[null,null,(-?\d+(?:\.\d+)?),(-?\d+(?:\.\d+)?),?\]?,"?0x[0-9a-fA-F]+:0x[0-9a-fA-F]+","((?:[^"\\]|\\.)*)"')
    if (-not $m.Success) { throw "Address not found: $query" }
    [PSCustomObject]@{
        Name = $m.Groups[3].Value
        Lat  = [double]::Parse($m.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture)
        Lng  = [double]::Parse($m.Groups[2].Value, [Globalization.CultureInfo]::InvariantCulture)
    }
}

function Format-Coord([double]$v) { $v.ToString("0.######", [Globalization.CultureInfo]::InvariantCulture) }

$origin = Get-Place $From
Start-Sleep -Milliseconds 1200
$dest = Get-Place $To
Write-Host "From: $($origin.Name)  ($($origin.Lat), $($origin.Lng))"
Write-Host "To:   $($dest.Name)  ($($dest.Lat), $($dest.Lng))"

$midLat = ($origin.Lat + $dest.Lat) / 2
$midLng = ($origin.Lng + $dest.Lng) / 2
$pb = "!1m4!3m2!3d$(Format-Coord $origin.Lat)!4d$(Format-Coord $origin.Lng)!6e2" +
      "!1m4!3m2!3d$(Format-Coord $dest.Lat)!4d$(Format-Coord $dest.Lng)!6e2" +
      "!3m12!1m3!1d20000!2d$(Format-Coord $midLng)!3d$(Format-Coord $midLat)!2m3!1f0.0!2f0.0!3f0.0!3m2!1i1024!2i768!4f13.1!6m6!20m5!1e0!2e3!5e2!6b1!14b1"
$url = "https://www.google.com/maps/preview/directions?authuser=0&hl=en&gl=us&pb=$([Uri]::EscapeDataString($pb))"
$resp = Invoke-WebRequest -Uri $url -UserAgent $ua -Headers @{ Referer = "https://www.google.com/maps/"; "Accept-Language" = "en-US,en;q=0.9" } -UseBasicParsing
if ($resp.StatusCode -ne 200) { throw "directions RPC HTTP $([int]$resp.StatusCode)" }
$body = $resp.Content
if (-not $body.StartsWith(")]}'")) { throw "missing anti-hijack prefix" }
$json = $body.Substring($body.IndexOf("`n") + 1) | ConvertFrom-Json

$route = $json[0][1][0]
$head = $route[1][0][0]
$distText = $head[2][1]
$durText = $head[3][1]
Write-Host "Route: $distText, $durText (free flow)"

$traffic = $head[10]
if ($null -ne $traffic -and $null -ne $traffic[0]) {
    $inTrafficS = $traffic[0][0]
    $trafficText = $traffic[0][1]
    $level = $traffic[2]
    $freeFlowS = $traffic[3][0]
    $usualMinS = $traffic[4][0]
    $usualMaxS = $traffic[4][1]
    $range = $traffic[4][2]
    # Google's own server-computed grade (same one the web UI colors the
    # travel time with): 3 = green, 2 = orange, 1 = red.
    $light = if ($level -eq 3) { "GREEN" } elseif ($level -eq 2) { "ORANGE" } elseif ($level -eq 1) { "RED" } else { "UNKNOWN" }
    Write-Host ("Traffic: {0} in traffic vs {1} free flow (Google level {2}, usual {3}) -> {4}" -f $trafficText, $traffic[3][1], $level, $range, $light)
} else {
    Write-Host "No traffic block returned"
}
