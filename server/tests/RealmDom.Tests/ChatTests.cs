using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using RealmDom.Server.Api;
using RealmDom.World;

namespace RealmDom.Tests;

public class ChatTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"realmdom-chat-{Guid.NewGuid():N}.db");
    public void Dispose() { try { File.Delete(_db); } catch { } }

    private static async Task<HubConnection> Connect(ServerFixture s)
    {
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(s.Server.BaseAddress, "hubs/world"), o => { o.HttpMessageHandlerFactory = _ => s.Server.CreateHandler(); o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling; })
            .Build();
        await hub.StartAsync();
        return hub;
    }

    [Fact]
    public async Task A_tagged_call_for_help_reaches_only_your_faction()
    {
        await using var server = new ServerFixture(_db, new FakeMapSource());
        var area = (await server.CreateClient().GetFromJsonAsync<AreaDto>($"/api/world/area?lat={Spots.Lat}&lon={Spots.Lon}&realm=forest&ring=0"))!;
        var place = area.Landmarks.First();

        await using var me = await Connect(server);
        await using var ally = await Connect(server);
        await using var rival = await Connect(server);
        await me.InvokeAsync("JoinFactionChat", "forest", "tide");
        await ally.InvokeAsync("JoinFactionChat", "forest", "tide");
        await rival.InvokeAsync("JoinFactionChat", "forest", "ember");

        var allyGot = new TaskCompletionSource<ChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rivalGot = false;
        ally.On<string, ChatMessage>("chat", (_, m) => allyGot.TrySetResult(m));
        rival.On<string, ChatMessage>("chat", (_, _) => rivalGot = true);

        await me.InvokeAsync("SendPhrase", "forest", "faction", "Elad", "conquer", place.Id);
        var m = await allyGot.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("conquer", m.PhraseId);
        Assert.Equal(place.Name, m.PlaceName);
        await Task.Delay(500);
        Assert.False(rivalGot);
    }

    [Fact]
    public async Task Anything_but_a_known_phrase_and_a_real_landmark_is_rejected()
    {
        await using var server = new ServerFixture(_db, new FakeMapSource());
        await using var me = await Connect(server);
        await me.InvokeAsync("JoinFactionChat", "forest", "tide");
        await Assert.ThrowsAsync<HubException>(() => me.InvokeAsync("SendPhrase", "forest", "faction", "Elad", "call me at 050-1234567", null));
        await Assert.ThrowsAsync<HubException>(() => me.InvokeAsync("SendPhrase", "forest", "faction", "Elad", "conquer", null));
        await Assert.ThrowsAsync<HubException>(() => me.InvokeAsync("SendPhrase", "forest", "faction", "Elad", "conquer", "osm:n0-my-house"));
        await Assert.ThrowsAsync<HubException>(() => me.InvokeAsync("SendPhrase", "forest", "faction", "x@y.com", "hi", null));
    }

    [Fact]
    public async Task Sending_too_fast_is_blocked()
    {
        await using var server = new ServerFixture(_db, new FakeMapSource());
        await using var me = await Connect(server);
        await me.InvokeAsync("JoinFactionChat", "forest", "tide");
        await me.InvokeAsync("SendPhrase", "forest", "faction", "Elad", "hi", null);
        var ex = await Assert.ThrowsAsync<HubException>(() => me.InvokeAsync("SendPhrase", "forest", "faction", "Elad", "gg", null));
        Assert.Contains("Slow down", ex.Message);
    }
}
