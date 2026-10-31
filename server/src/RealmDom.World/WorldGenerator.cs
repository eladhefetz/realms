namespace RealmDom.World;

public sealed class GeneratorOptions
{
    public double MinSpacingMeters { get; set; } = 70;
    public int MaxPerCell { get; set; } = 12;
    public int MaxRoadFallbacks { get; set; } = 4;
    public double ExclusionRadiusMeters { get; set; } = 60;
    public int FortressMinLandmarks { get; set; } = 8;
}

/// <summary>
/// Turns real map features into a cell's landmarks. Pure and deterministic: the same features
/// always give the same landmarks, in any order, on any server.
/// </summary>
public sealed class WorldGenerator(GeneratorOptions? options = null)
{
    private readonly GeneratorOptions _o = options ?? new GeneratorOptions();

    private static readonly string[] NameA = ["Mossy", "Elder", "Whispering", "Sunken", "Silver", "Hollow", "Thorn", "Amber", "Misty", "Ancient", "Glimmer", "Old"];
    private static readonly string[] NameB = ["Shrine", "Fountain", "Oak", "Stone Circle", "Well", "Watchtower", "Gate", "Grove", "Altar", "Bridge", "Monolith", "Spring"];

    private static int Priority(MapFeature f) => f.Kind switch
    {
        FeatureKind.Fountain or FeatureKind.Artwork or FeatureKind.Historic or FeatureKind.Attraction or FeatureKind.Viewpoint => f.Name is null ? 1 : 0,
        FeatureKind.Park => f.Name is null ? 3 : 2,
        FeatureKind.Road => 9,
        _ => 99
    };

    public IReadOnlyList<Landmark> Generate(string cellId, IEnumerable<MapFeature> features)
    {
        var all = features.ToList();
        var excluded = all.Where(f => f.Kind == FeatureKind.Excluded).ToList();

        var candidates = all
            .Where(f => f.Kind != FeatureKind.Excluded)
            .Where(f => Cells.Of(f.Lat, f.Lon) == cellId) // a feature belongs to exactly one cell: no duplicates across cells
            .Where(f => !excluded.Any(x => Geo.Meters(x.Lat, x.Lon, f.Lat, f.Lon) < _o.ExclusionRadiusMeters))
            .GroupBy(f => f.OsmId).Select(g => g.First())
            .OrderBy(Priority).ThenBy(f => StableHash.Of(f.OsmId)).ThenBy(f => f.OsmId, StringComparer.Ordinal)
            .ToList();

        var picked = new List<MapFeature>();
        int roads = 0;
        foreach (var f in candidates)
        {
            if (picked.Count >= _o.MaxPerCell) break;
            if (f.Kind == FeatureKind.Road && (roads >= _o.MaxRoadFallbacks || picked.Count >= 5)) continue;
            if (picked.Any(p => Geo.Meters(p.Lat, p.Lon, f.Lat, f.Lon) < _o.MinSpacingMeters)) continue;
            picked.Add(f);
            if (f.Kind == FeatureKind.Road) roads++;
        }

        string? fortressId = picked.Count >= _o.FortressMinLandmarks
            ? picked.Where(f => f.Kind != FeatureKind.Road).OrderByDescending(f => StableHash.Of("fortress:" + f.OsmId)).FirstOrDefault()?.OsmId
            : null;

        return picked
            .Select(f => new Landmark(
                Id: "osm:" + f.OsmId,
                CellId: cellId,
                Lat: Math.Round(f.Lat, 6),
                Lon: Math.Round(f.Lon, 6),
                Name: f.Name is { Length: > 0 } n ? n : GeneratedName(f.OsmId),
                Kind: f.Kind,
                Fortress: f.OsmId == fortressId))
            .OrderBy(l => l.Id, StringComparer.Ordinal)
            .ToList();
    }

    public static string GeneratedName(string seed)
    {
        var h = StableHash.Of(seed);
        return $"{NameA[(int)(h % (ulong)NameA.Length)]} {NameB[(int)(h / 7 % (ulong)NameB.Length)]}";
    }
}

/// <summary>
/// The state a landmark starts with before anyone touches it. Derived from its ID and realm,
/// so it costs no storage and is identical for every player.
/// </summary>
public static class DefaultState
{
    private static readonly string[] KeeperNames = ["Kael", "Mira", "Thorne", "Lyra", "Bram", "Seren", "Oren", "Isolde", "Fenn", "Nyx", "Rowan", "Ash"];
    private static readonly string[] KeeperBuilds = ["wall", "tower", "barracks", "mint"];

    public static LandmarkState For(Landmark l, string realm)
    {
        if (l.Fortress) return new(l.Id, realm, null, null, null, false, true, [], [], 0);
        var h = StableHash.Of(l.Id + "|" + realm);
        if (h % 100 < 30) return new(l.Id, realm, null, null, null, false, false, [], [], 0);
        var name = KeeperNames[(int)(h / 101 % (ulong)KeeperNames.Length)];
        var faction = Factions.All[(int)(h / 13 % (ulong)Factions.All.Length)];
        var builds = KeeperBuilds.Where((_, i) => (h >> (8 + i * 3) & 7) == 0).Take(2).ToList();
        return new(l.Id, realm, "ai:" + name.ToLowerInvariant(), name, faction, false, false, [], builds, 0);
    }
}
