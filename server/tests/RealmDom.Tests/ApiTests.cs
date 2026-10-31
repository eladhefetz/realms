using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RealmDom.Server.Api;
using RealmDom.World;

namespace RealmDom.Tests;

/// <summary>Runs the real server in memory, with a real SQLite file and a fake map.</summary>
public sealed class ServerFixture(string dbPath, FakeMapSource maps) : WebApplicationFactory<Program>
{
    public FakeMapSource Maps { get; } = maps;
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:World", $"Data Source={dbPath}");
        builder.ConfigureServices(s => { s.RemoveAll<IMapSource>(); s.AddSingleton<IMapSource>(Maps); });
    }
}

public class ApiTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"realmdom-test-{Guid.NewGuid():N}.db");
    public void Dispose() { try { File.Delete(_db); } catch { } }

    private static string AreaUrl(string realm = "forest") => $"/api/world/area?lat={Spots.Lat}&lon={Spots.Lon}&realm={realm}&ring=1";

    [Fact]
    public async Task Two_players_see_the_same_world_and_each_others_captures()
    {
        await using var server = new ServerFixture(_db, new FakeMapSource());
        var alice = server.CreateClient();
        var bob = server.CreateClient();

        var a = (await alice.GetFromJsonAsync<AreaDto>(AreaUrl()))!;
        var b = (await bob.GetFromJsonAsync<AreaDto>(AreaUrl()))!;
        Assert.Equal(1, server.Maps.Calls);
        Assert.Equal(a.Landmarks.Select(l => l.Id), b.Landmarks.Select(l => l.Id));

        var target = a.Landmarks.First(l => l.OwnerId is null && !l.Fortress);
        var res = await alice.PostAsJsonAsync($"/api/world/landmarks/{target.Id}/capture",
            new CaptureRequest("forest", new PlayerRef("alice", "Alice", "verdant"), null, target.Version));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var seenByBob = (await bob.GetFromJsonAsync<AreaDto>(AreaUrl()))!.Landmarks.Single(l => l.Id == target.Id);
        Assert.Equal("alice", seenByBob.OwnerId);

        var late = await bob.PostAsJsonAsync($"/api/world/landmarks/{target.Id}/capture",
            new CaptureRequest("forest", new PlayerRef("bob", "Bob", "ember"), null, target.Version));
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
    }

    [Fact]
    public async Task The_world_survives_a_server_restart_unchanged()
    {
        List<string> before;
        string owned;
        await using (var first = new ServerFixture(_db, new FakeMapSource("a")))
        {
            var c = first.CreateClient();
            var area = (await c.GetFromJsonAsync<AreaDto>(AreaUrl()))!;
            before = area.Landmarks.Select(l => l.Id).ToList();
            var t = area.Landmarks.First(l => l.OwnerId is null && !l.Fortress);
            owned = t.Id;
            (await c.PostAsJsonAsync($"/api/world/landmarks/{t.Id}/capture", new CaptureRequest("forest", new PlayerRef("p1", "Elad", "tide"), null, t.Version))).EnsureSuccessStatusCode();
        }
        // new server process, and the map data has changed in the meantime
        await using var second = new ServerFixture(_db, new FakeMapSource("b"));
        var after = (await second.CreateClient().GetFromJsonAsync<AreaDto>(AreaUrl()))!;
        Assert.Equal(0, second.Maps.Calls);
        Assert.Equal(before, after.Landmarks.Select(l => l.Id).ToList());
        Assert.Equal("p1", after.Landmarks.Single(l => l.Id == owned).OwnerId);
    }

    [Fact]
    public async Task Players_watching_a_cell_get_live_updates()
    {
        await using var server = new ServerFixture(_db, new FakeMapSource());
        var http = server.CreateClient();
        var area = (await http.GetFromJsonAsync<AreaDto>(AreaUrl()))!;
        var target = area.Landmarks.First(l => l.OwnerId is null && !l.Fortress);

        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(server.Server.BaseAddress, "hubs/world"), o => { o.HttpMessageHandlerFactory = _ => server.Server.CreateHandler(); o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling; })
            .Build();
        var got = new TaskCompletionSource<LandmarkDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<LandmarkDto>("landmarkChanged", d => got.TrySetResult(d));
        await hub.StartAsync();
        await hub.InvokeAsync("JoinCells", "forest", area.Cells.Select(c => c.CellId).ToArray());

        (await http.PostAsJsonAsync($"/api/world/landmarks/{target.Id}/capture", new CaptureRequest("forest", new PlayerRef("p7", "Noa", "ember"), null, target.Version))).EnsureSuccessStatusCode();

        var update = await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(target.Id, update.Id);
        Assert.Equal("p7", update.OwnerId);
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task Bad_input_is_rejected()
    {
        await using var server = new ServerFixture(_db, new FakeMapSource());
        var c = server.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync($"/api/world/area?lat=1&lon=1&realm=moon")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/world/cells/nonsense")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/world/landmarks/osm:n0/capture", new CaptureRequest("forest", new PlayerRef("p", "P", "tide"), null, 0))).StatusCode);
    }
}
