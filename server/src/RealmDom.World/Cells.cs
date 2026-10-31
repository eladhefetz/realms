using H3;
using H3.Algorithms;
using H3.Extensions;
using H3.Model;
using NetTopologySuite.Geometries;

namespace RealmDom.World;

/// <summary>
/// The world grid. Every point on Earth falls in exactly one H3 cell (resolution 8, about 0.74 km²),
/// so a cell ID is a permanent address for "this piece of the map".
/// </summary>
public static class Cells
{
    public const int Resolution = 8;

    public static string Of(double lat, double lon) =>
        H3Index.FromLatLng(LatLng.FromCoordinate(new Coordinate(lon, lat)), Resolution).ToString();

    public static bool TryParse(string? cellId, out H3Index index)
    {
        index = H3Index.Invalid;
        if (string.IsNullOrWhiteSpace(cellId) || cellId.Length > 16) return false;
        try { index = new H3Index(Convert.ToUInt64(cellId, 16)); }
        catch (FormatException) { return false; }
        catch (OverflowException) { return false; }
        return index.IsValidCell && index.Resolution == Resolution && index.ToString() == cellId.ToLowerInvariant();
    }

    public static bool IsValid(string? cellId) => TryParse(cellId, out _);

    /// <summary>The cell and every cell within <paramref name="ring"/> steps of it.</summary>
    public static IReadOnlyList<string> Around(string cellId, int ring)
    {
        if (!TryParse(cellId, out var index)) throw new ArgumentException("Not a valid world cell.", nameof(cellId));
        return index.GridDiskDistances(Math.Clamp(ring, 0, 3)).Select(r => r.Index.ToString()).OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    public static (double Lat, double Lon) Center(string cellId)
    {
        if (!TryParse(cellId, out var index)) throw new ArgumentException("Not a valid world cell.", nameof(cellId));
        var c = index.ToLatLng().ToCoordinate();
        return (c.Y, c.X);
    }

    public static IReadOnlyList<(double Lat, double Lon)> Boundary(string cellId)
    {
        if (!TryParse(cellId, out var index)) throw new ArgumentException("Not a valid world cell.", nameof(cellId));
        return index.GetCellBoundaryVertices().Select(v => { var c = v.ToCoordinate(); return (c.Y, c.X); }).ToList();
    }

    /// <summary>Bounding box (south, west, north, east) covering all the given cells, plus a margin in degrees.</summary>
    public static (double S, double W, double N, double E) BoundingBox(IEnumerable<string> cellIds, double margin = 0.0008)
    {
        double s = 90, w = 180, n = -90, e = -180;
        foreach (var id in cellIds)
            foreach (var (lat, lon) in Boundary(id)) { s = Math.Min(s, lat); n = Math.Max(n, lat); w = Math.Min(w, lon); e = Math.Max(e, lon); }
        return (s - margin, w - margin, n + margin, e + margin);
    }
}

public static class Geo
{
    /// <summary>Great-circle distance in meters.</summary>
    public static double Meters(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371000;
        double dLat = (lat2 - lat1) * Math.PI / 180, dLon = (lon2 - lon1) * Math.PI / 180;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Sqrt(a));
    }

    /// <summary>Ray-casting point-in-polygon on lat/lon pairs (fine for city-sized regions).</summary>
    public static bool Contains(IReadOnlyList<(double Lat, double Lon)> polygon, double lat, double lon)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var (yi, xi) = polygon[i]; var (yj, xj) = polygon[j];
            if ((yi > lat) != (yj > lat) && lon < (xj - xi) * (lat - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }
}

/// <summary>Deterministic hashing (unlike string.GetHashCode, identical on every machine and run).</summary>
public static class StableHash
{
    public static ulong Of(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (var ch in s) { h ^= ch; h *= 1099511628211UL; }
        return h;
    }
}
