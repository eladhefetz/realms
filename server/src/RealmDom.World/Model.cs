namespace RealmDom.World;

/// <summary>What a real map feature is, as far as landmark selection cares.</summary>
public enum FeatureKind { Fountain, Artwork, Viewpoint, Attraction, Historic, Park, Road, Excluded }

/// <summary>A real-world map feature (from OpenStreetMap). <see cref="OsmId"/> is stable, e.g. "n123456" or "w987".</summary>
public sealed record MapFeature(string OsmId, FeatureKind Kind, double Lat, double Lon, string? Name);

/// <summary>A landmark's fixed layout. Generated once per cell and never moved.</summary>
public sealed record Landmark(string Id, string CellId, double Lat, double Lon, string Name, FeatureKind Kind, bool Fortress);

public sealed record TeamShare(string PlayerId, string Name, double Share);

/// <summary>The changing part of a landmark, separate for every realm.</summary>
public sealed record LandmarkState(
    string LandmarkId,
    string Realm,
    string? OwnerId,
    string? OwnerName,
    string? Faction,
    bool Corrupted,
    bool BossAlive,
    IReadOnlyList<TeamShare> Team,
    IReadOnlyList<string> Builds,
    long Version);

public sealed record PlayerRef(string PlayerId, string Name, string Faction);

public enum CellStatus { Generating, Ready }

public static class Realms
{
    public static readonly string[] All = ["forest", "cyber", "kingdom"];
    public static bool IsValid(string realm) => All.Contains(realm);
}

public static class Factions
{
    public static readonly string[] All = ["verdant", "ember", "tide", "veil"];
    public static bool IsValid(string faction) => All.Contains(faction);
}

public static class Structures
{
    public static readonly string[] All = ["tower", "mint", "wall", "barracks", "wardstone"];
    public static int SlotsFor(Landmark l) => l.Fortress ? 5 : 3;
}
