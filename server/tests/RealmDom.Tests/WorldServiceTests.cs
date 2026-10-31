using Microsoft.Extensions.Time.Testing;
using RealmDom.World;

namespace RealmDom.Tests;

public class WorldServiceTests
{
    private static WorldService Make(IWorldStore store, IMapSource maps, RegionPolicy? regions = null) =>
        new(store, maps, new WorldGenerator(), regions ?? new RegionPolicy());

    [Fact]
    public async Task Many_players_arriving_at_once_generate_the_area_exactly_once()
    {
        var maps = new FakeMapSource(delay: TimeSpan.FromMilliseconds(150));
        var world = Make(new InMemoryWorldStore(), maps);
        var areas = await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => world.GetAreaAsync(Spots.Lat, Spots.Lon, "forest", 1, default)));
        Assert.Equal(1, maps.Calls);
        var first = areas[0].Landmarks.Select(v => v.Landmark.Id).ToList();
        Assert.NotEmpty(first);
        Assert.All(areas, a => Assert.Equal(first, a.Landmarks.Select(v => v.Landmark.Id).ToList()));
    }

    [Fact]
    public async Task Once_saved_a_cell_is_never_regenerated_even_if_the_map_changes()
    {
        var store = new InMemoryWorldStore();
        var before = await Make(store, new FakeMapSource("a")).GetAreaAsync(Spots.Lat, Spots.Lon, "forest", 1, default);
        var changedMap = new FakeMapSource("b");
        var after = await Make(store, changedMap).GetAreaAsync(Spots.Lat, Spots.Lon, "forest", 1, default);
        Assert.Equal(0, changedMap.Calls);
        Assert.Equal(before.Landmarks.Select(v => v.Landmark), after.Landmarks.Select(v => v.Landmark));
    }

    [Fact]
    public async Task Cells_outside_live_regions_are_not_generated()
    {
        var maps = new FakeMapSource();
        var farAway = new RegionPolicy([new("Elsewhere", [(10, 10), (10, 11), (11, 11), (11, 10)])]);
        var area = await Make(new InMemoryWorldStore(), maps, farAway).GetAreaAsync(Spots.Lat, Spots.Lon, "forest", 1, default);
        Assert.All(area.Cells, c => Assert.Equal(CellOutcome.NotLive, c.Outcome));
        Assert.Empty(area.Landmarks);
        Assert.Equal(0, maps.Calls);
    }

    [Fact]
    public async Task A_live_region_polygon_turns_generation_on()
    {
        var home = new RegionPolicy([new("Rosh HaAyin", [(32.05, 34.90), (32.05, 35.00), (32.13, 35.00), (32.13, 34.90)])]);
        var area = await Make(new InMemoryWorldStore(), new FakeMapSource(), home).GetAreaAsync(Spots.Lat, Spots.Lon, "forest", 0, default);
        Assert.Equal(CellOutcome.Ready, area.Cells.Single().Outcome);
    }

    [Fact]
    public async Task If_the_map_servers_fail_the_cell_is_released_and_a_later_visit_succeeds()
    {
        var maps = new FakeMapSource { Fail = true };
        var world = Make(new InMemoryWorldStore(), maps);
        var failed = await world.GetAreaAsync(Spots.Lat, Spots.Lon, "forest", 0, default);
        Assert.Equal(CellOutcome.Unavailable, failed.Cells.Single().Outcome);
        maps.Fail = false;
        var ok = await world.GetAreaAsync(Spots.Lat, Spots.Lon, "forest", 0, default);
        Assert.Equal(CellOutcome.Ready, ok.Cells.Single().Outcome);
    }

    [Fact]
    public async Task A_claim_left_by_a_crashed_server_is_taken_over_after_two_minutes()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-31T12:00:00Z"));
        var store = new InMemoryWorldStore { Clock = clock };
        var cell = Cells.Of(Spots.Lat, Spots.Lon);
        Assert.True(await store.TryClaimCellAsync(cell, default));   // a server claims it, then dies
        Assert.False(await store.TryClaimCellAsync(cell, default));  // fresh claim is respected
        clock.Advance(TimeSpan.FromMinutes(3));
        var views = await Make(store, new FakeMapSource()).EnsureCellsAsync([cell], default);
        Assert.Equal(CellOutcome.Ready, views.Single().Outcome);
    }

    [Fact]
    public async Task Realms_are_separate_layers_over_the_same_landmarks()
    {
        var world = Make(new InMemoryWorldStore(), new FakeMapSource());
        var forest = await world.GetAreaAsync(Spots.Lat, Spots.Lon, "forest", 0, default);
        var cyber = await world.GetAreaAsync(Spots.Lat, Spots.Lon, "cyber", 0, default);
        Assert.Equal(forest.Landmarks.Select(v => v.Landmark.Id), cyber.Landmarks.Select(v => v.Landmark.Id));
        var free = forest.Landmarks.First(v => v.State.OwnerId is null && !v.Landmark.Fortress);
        var me = new PlayerRef("p1", "Elad", "tide");
        Assert.Equal(ActionResult.Ok, (await world.CaptureAsync(free.Landmark.Id, "forest", me, null, 0, default)).Result);
        var cyberAfter = await world.GetAreaAsync(Spots.Lat, Spots.Lon, "cyber", 0, default);
        Assert.NotEqual("p1", cyberAfter.Landmarks.First(v => v.Landmark.Id == free.Landmark.Id).State.OwnerId);
    }

    [Fact]
    public async Task Capture_and_build_rules_are_enforced_on_the_server()
    {
        var world = Make(new InMemoryWorldStore(), new FakeMapSource());
        var area = await world.GetAreaAsync(Spots.Lat, Spots.Lon, "forest", 1, default);
        var me = new PlayerRef("p1", "Elad", "tide");
        var friend = new PlayerRef("p2", "Noa", "tide");

        var fortress = area.Landmarks.First(v => v.Landmark.Fortress);
        var solo = await world.CaptureAsync(fortress.Landmark.Id, "forest", me, null, fortress.State.Version, default);
        Assert.Equal(ActionResult.NotAllowed, solo.Result);
        var team = await world.CaptureAsync(fortress.Landmark.Id, "forest", me, [friend], fortress.State.Version, default);
        Assert.Equal(ActionResult.Ok, team.Result);
        Assert.Equal(2, team.View!.State.Team.Count);
        Assert.All(team.View.State.Team, t => Assert.Equal(0.5, t.Share));

        var stale = await world.CaptureAsync(fortress.Landmark.Id, "forest", new PlayerRef("p9", "Rival", "ember"), null, 0, default);
        Assert.Equal(ActionResult.Conflict, stale.Result);

        var v = team.View.State.Version;
        Assert.Equal(ActionResult.NotAllowed, (await world.BuildAsync(fortress.Landmark.Id, "forest", "p9", "wall", v, default)).Result);
        var built = await world.BuildAsync(fortress.Landmark.Id, "forest", "p2", "wall", v, default);
        Assert.Equal(ActionResult.Ok, built.Result);
        Assert.Equal(ActionResult.NotAllowed, (await world.BuildAsync(fortress.Landmark.Id, "forest", "p1", "wall", built.View!.State.Version, default)).Result);
    }
}
