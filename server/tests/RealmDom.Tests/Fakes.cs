using RealmDom.World;

namespace RealmDom.Tests;

/// <summary>
/// A deterministic fake map: a grid of fountains, parks and roads over whatever box is asked for.
/// <see cref="Salt"/> changes every feature ID, which would change the landmarks if a cell were ever regenerated.
/// </summary>
public sealed class FakeMapSource(string salt = "a", TimeSpan? delay = null) : IMapSource
{
    public string Salt { get; } = salt;
    public int Calls;
    public bool Fail { get; set; }

    public async Task<IReadOnlyList<MapFeature>> GetFeaturesAsync((double S, double W, double N, double E) b, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        if (delay is { } d) await Task.Delay(d, ct);
        if (Fail) throw new InvalidOperationException("map servers down");
        var list = new List<MapFeature>();
        const double step = 0.0011; // about 120 m
        for (double lat = Math.Floor(b.S / step) * step; lat <= b.N; lat += step)
            for (double lon = Math.Floor(b.W / step) * step; lon <= b.E; lon += step)
            {
                var key = $"{Math.Round(lat / step)}_{Math.Round(lon / step)}_{Salt}";
                var h = StableHash.Of(key) % 10;
                var kind = h < 4 ? FeatureKind.Fountain : h < 7 ? FeatureKind.Park : FeatureKind.Road;
                list.Add(new MapFeature("n" + (StableHash.Of(key) % 1_000_000_000), kind, lat + 0.0002, lon + 0.0003, h % 2 == 0 ? $"Place {key}" : null));
            }
        return list;
    }
}

public static class Spots
{
    // Rosh HaAyin area
    public const double Lat = 32.0950, Lon = 34.9560;
}
