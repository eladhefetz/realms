using System.Globalization;
using System.Text.Json;
using RealmDom.World;

namespace RealmDom.Server.Maps;

public sealed class OverpassOptions
{
    /// <summary>Tried in order. Public servers are fine for development; production should run its own.</summary>
    public string[] Servers { get; set; } = ["https://overpass-api.de/api/interpreter", "https://overpass.kumi.systems/api/interpreter", "https://overpass.private.coffee/api/interpreter"];
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>Reads OpenStreetMap features for a bounding box from an Overpass API server.</summary>
public sealed class OverpassMapSource(HttpClient http, OverpassOptions options, ILogger<OverpassMapSource> log) : IMapSource
{
    private static readonly string[] ExcludedAmenities = ["school", "kindergarten", "hospital", "clinic", "place_of_worship", "childcare"];

    public static string BuildQuery((double S, double W, double N, double E) b)
    {
        var bbox = string.Join(',', new[] { b.S, b.W, b.N, b.E }.Select(v => v.ToString("0.######", CultureInfo.InvariantCulture)));
        return $"""
            [out:json][timeout:25];
            (
              node["amenity"="fountain"]({bbox});
              node["tourism"~"^(artwork|viewpoint|attraction)$"]({bbox});
              node["historic"]({bbox});
              way["leisure"~"^(park|garden)$"]({bbox});
              way["highway"~"^(residential|tertiary|secondary|living_street|pedestrian)$"]({bbox});
              nwr["amenity"~"^({string.Join('|', ExcludedAmenities)})$"]({bbox});
            );
            out center tags;
            """;
    }

    public async Task<IReadOnlyList<MapFeature>> GetFeaturesAsync((double S, double W, double N, double E) bbox, CancellationToken ct)
    {
        var body = "data=" + Uri.EscapeDataString(BuildQuery(bbox));
        string? last = null;
        foreach (var server in options.Servers)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            try
            {
                using var res = await http.PostAsync(server, new StringContent(body, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"), timeout.Token);
                if (!res.IsSuccessStatusCode) { last = $"{server} answered {(int)res.StatusCode}"; log.LogWarning("Overpass {Server} answered {Status}", server, (int)res.StatusCode); continue; }
                await using var stream = await res.Content.ReadAsStreamAsync(timeout.Token);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
                return Parse(doc.RootElement);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
            {
                last = $"{server}: {ex.Message}"; log.LogWarning(ex, "Overpass {Server} failed", server);
            }
        }
        throw new InvalidOperationException("No map server answered. Last error: " + last);
    }

    public static IReadOnlyList<MapFeature> Parse(JsonElement root)
    {
        var list = new List<MapFeature>();
        if (!root.TryGetProperty("elements", out var elements)) return list;
        foreach (var e in elements.EnumerateArray())
        {
            var type = e.GetProperty("type").GetString();
            var id = e.GetProperty("id").GetInt64();
            double lat, lon;
            if (e.TryGetProperty("lat", out var la) && e.TryGetProperty("lon", out var lo)) { lat = la.GetDouble(); lon = lo.GetDouble(); }
            else if (e.TryGetProperty("center", out var c)) { lat = c.GetProperty("lat").GetDouble(); lon = c.GetProperty("lon").GetDouble(); }
            else continue;
            var tags = e.TryGetProperty("tags", out var t) ? t : default;
            string? Tag(string k) => tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty(k, out var v) ? v.GetString() : null;
            var kind = Classify(Tag("amenity"), Tag("tourism"), Tag("historic"), Tag("leisure"), Tag("highway"));
            if (kind is null) continue;
            var name = kind is FeatureKind.Road or FeatureKind.Excluded ? null : Tag("name");
            list.Add(new MapFeature($"{type![0]}{id}", kind.Value, lat, lon, name));
        }
        return list;
    }

    private static FeatureKind? Classify(string? amenity, string? tourism, string? historic, string? leisure, string? highway)
    {
        if (amenity is not null && ExcludedAmenities.Contains(amenity)) return FeatureKind.Excluded;
        if (amenity == "fountain") return FeatureKind.Fountain;
        if (tourism == "artwork") return FeatureKind.Artwork;
        if (tourism == "viewpoint") return FeatureKind.Viewpoint;
        if (tourism == "attraction") return FeatureKind.Attraction;
        if (historic is not null) return FeatureKind.Historic;
        if (leisure is "park" or "garden") return FeatureKind.Park;
        if (highway is not null) return FeatureKind.Road;
        return null;
    }
}
