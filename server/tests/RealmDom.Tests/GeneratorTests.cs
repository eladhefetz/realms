using RealmDom.World;

namespace RealmDom.Tests;

public class GeneratorTests
{
    private static readonly string Cell = Cells.Of(Spots.Lat, Spots.Lon);
    private static async Task<List<MapFeature>> Features(string salt = "a") =>
        (await new FakeMapSource(salt).GetFeaturesAsync(Cells.BoundingBox(Cells.Around(Cell, 1)), default)).ToList();

    [Fact]
    public async Task Same_features_in_any_order_give_the_same_landmarks()
    {
        var f = await Features();
        var gen = new WorldGenerator();
        var a = gen.Generate(Cell, f);
        var b = gen.Generate(Cell, f.OrderBy(x => Guid.NewGuid()).ToList());
        Assert.NotEmpty(a);
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task Landmarks_only_come_from_features_inside_their_own_cell()
    {
        var f = await Features();
        var gen = new WorldGenerator();
        var all = Cells.Around(Cell, 1).SelectMany(c => gen.Generate(c, f)).ToList();
        Assert.All(all, l => Assert.Equal(l.CellId, Cells.Of(l.Lat, l.Lon)));
        Assert.Equal(all.Count, all.Select(l => l.Id).Distinct().Count()); // no landmark appears in two cells
    }

    [Fact]
    public async Task Rules_spacing_cap_and_road_fallbacks_hold()
    {
        var o = new GeneratorOptions();
        var picks = new WorldGenerator(o).Generate(Cell, await Features());
        Assert.True(picks.Count <= o.MaxPerCell);
        Assert.True(picks.Count(p => p.Kind == FeatureKind.Road) <= o.MaxRoadFallbacks);
        foreach (var a in picks) foreach (var b in picks.Where(b => b != a))
            Assert.True(Geo.Meters(a.Lat, a.Lon, b.Lat, b.Lon) >= o.MinSpacingMeters);
        Assert.Equal(picks.Count >= o.FortressMinLandmarks ? 1 : 0, picks.Count(p => p.Fortress));
    }

    [Fact]
    public void Places_near_schools_hospitals_and_worship_are_never_landmarks()
    {
        var (lat, lon) = Cells.Center(Cell);
        var f = new List<MapFeature>
        {
            new("n1", FeatureKind.Fountain, lat, lon, "Fountain by the school"),
            new("w2", FeatureKind.Excluded, lat + 0.0002, lon, null), // about 22 m away
            new("n3", FeatureKind.Fountain, lat + 0.004, lon, "Far fountain")
        };
        var picks = new WorldGenerator().Generate(Cell, f.Where(x => Cells.Of(x.Lat, x.Lon) == Cell || x.Kind == FeatureKind.Excluded));
        Assert.DoesNotContain(picks, p => p.Id == "osm:n1");
    }

    [Fact]
    public void Default_state_is_the_same_for_everyone_and_fortresses_start_with_a_boss()
    {
        var l = new Landmark("osm:n42", Cell, 0, 0, "X", FeatureKind.Fountain, false);
        Assert.Equal(DefaultState.For(l, "forest"), DefaultState.For(l, "forest"));
        var f = l with { Fortress = true };
        var s = DefaultState.For(f, "cyber");
        Assert.True(s.BossAlive);
        Assert.Null(s.OwnerId);
    }

    [Fact]
    public void Cell_ids_round_trip_and_reject_garbage()
    {
        Assert.True(Cells.IsValid(Cell));
        Assert.False(Cells.IsValid("hello"));
        Assert.False(Cells.IsValid("ffffffffffffffff"));
        Assert.Equal(7, Cells.Around(Cell, 1).Count);
    }
}
