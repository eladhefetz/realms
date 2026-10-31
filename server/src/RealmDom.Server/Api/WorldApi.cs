using Microsoft.AspNetCore.SignalR;
using RealmDom.World;

namespace RealmDom.Server.Api;

public sealed record LandmarkDto(
    string Id, string CellId, double Lat, double Lon, string Name, string Kind, bool Fortress,
    string Realm, string? OwnerId, string? OwnerName, string? Faction, bool Corrupted, bool BossAlive,
    IReadOnlyList<TeamShare> Team, IReadOnlyList<string> Builds, long Version)
{
    public static LandmarkDto From(LandmarkView v) => new(
        v.Landmark.Id, v.Landmark.CellId, v.Landmark.Lat, v.Landmark.Lon, v.Landmark.Name, v.Landmark.Kind.ToString().ToLowerInvariant(), v.Landmark.Fortress,
        v.State.Realm, v.State.OwnerId, v.State.OwnerName, v.State.Faction, v.State.Corrupted, v.State.BossAlive, v.State.Team, v.State.Builds, v.State.Version);
}

public sealed record CellDto(string CellId, string Status);
public sealed record AreaDto(string CenterCell, string Realm, IReadOnlyList<CellDto> Cells, IReadOnlyList<LandmarkDto> Landmarks);
public sealed record CaptureRequest(string Realm, PlayerRef Leader, IReadOnlyList<PlayerRef>? Allies, long ExpectedVersion);
public sealed record BuildRequest(string Realm, string PlayerId, string Structure, long ExpectedVersion);
public sealed record ProblemDto(string Reason, LandmarkDto? Landmark = null);

/// <summary>Live updates: clients join the cells they can see and get every change in them. Also carries phrase-only chat.</summary>
public sealed class WorldHub(IWorldStore store, TimeProvider clock) : Hub
{
    public static string Group(string realm, string cellId) => $"{realm}:{cellId}";
    public static string FactionChat(string realm, string faction) => $"chat:{realm}:{faction}";
    public static string CellChat(string realm, string cellId) => $"chat:{realm}:cell:{cellId}";
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> LastSent = new();
    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(2);

    /// <summary>Join your faction's channel for a realm (one faction channel per connection).</summary>
    public async Task JoinFactionChat(string realm, string faction)
    {
        if (!Realms.IsValid(realm) || !Factions.IsValid(faction)) throw new HubException("Bad realm or faction.");
        Context.Items["faction:" + realm] = faction;
        await Groups.AddToGroupAsync(Context.ConnectionId, FactionChat(realm, faction));
    }

    /// <summary>Join the "nearby" channel of the cell you are in.</summary>
    public async Task JoinCellChat(string realm, string cellId)
    {
        if (!Realms.IsValid(realm) || !Cells.IsValid(cellId)) throw new HubException("Bad realm or cell.");
        if (Context.Items.TryGetValue("cell:" + realm, out var old) && old is string prev) await Groups.RemoveFromGroupAsync(Context.ConnectionId, CellChat(realm, prev));
        Context.Items["cell:" + realm] = cellId;
        await Groups.AddToGroupAsync(Context.ConnectionId, CellChat(realm, cellId));
    }

    /// <summary>
    /// Send a phrase. <paramref name="channel"/> is "faction" or "nearby". Only phrase IDs are accepted, the tagged
    /// place must be a real landmark, and each connection can send at most one message every two seconds.
    /// </summary>
    public async Task SendPhrase(string realm, string channel, string playerName, string phraseId, string? landmarkId)
    {
        if (!Realms.IsValid(realm)) throw new HubException("Bad realm.");
        if (!ChatPhrases.All.TryGetValue(phraseId ?? "", out var phrase)) throw new HubException("Unknown phrase.");
        if (!ChatPhrases.IsValidPlayerName(playerName)) throw new HubException("Bad player name.");
        if (phrase.NeedsPlace != (landmarkId is not null)) throw new HubException(phrase.NeedsPlace ? "This phrase needs a tagged place." : "This phrase takes no place.");

        var now = clock.GetUtcNow();
        if (LastSent.TryGetValue(Context.ConnectionId, out var last) && now - last < MinGap) throw new HubException("Slow down a little.");
        LastSent[Context.ConnectionId] = now;

        string? faction = Context.Items.TryGetValue("faction:" + realm, out var f) ? f as string : null;
        if (faction is null) throw new HubException("Join your faction chat first.");
        string group = channel switch
        {
            "faction" => FactionChat(realm, faction),
            "nearby" when Context.Items.TryGetValue("cell:" + realm, out var c) && c is string cell => CellChat(realm, cell),
            _ => throw new HubException("Unknown channel, or not in a cell yet.")
        };

        Landmark? place = null;
        if (landmarkId is not null && (place = await store.GetLandmarkAsync(landmarkId, Context.ConnectionAborted)) is null) throw new HubException("Only real landmarks can be tagged.");
        await Clients.Group(group).SendAsync("chat", channel, new ChatMessage(playerName, faction, phrase.Id, place?.Id, place?.Name, place?.Lat, place?.Lon, now));
    }

    public override Task OnDisconnectedAsync(Exception? exception) { LastSent.TryRemove(Context.ConnectionId, out _); return base.OnDisconnectedAsync(exception); }

    public async Task JoinCells(string realm, string[] cellIds)
    {
        if (!Realms.IsValid(realm) || cellIds.Length > 50) throw new HubException("Bad realm or too many cells.");
        foreach (var c in cellIds.Where(Cells.IsValid)) await Groups.AddToGroupAsync(Context.ConnectionId, Group(realm, c));
    }

    public async Task LeaveCells(string realm, string[] cellIds)
    {
        if (!Realms.IsValid(realm)) return;
        foreach (var c in cellIds.Where(Cells.IsValid)) await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(realm, c));
    }
}

public static class WorldApi
{
    private static string StatusName(CellOutcome o) => o switch { CellOutcome.Ready => "ready", CellOutcome.NotLive => "not-live", _ => "unavailable" };

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/world");

        g.MapGet("/area", async (double lat, double lon, string? realm, int? ring, WorldService world, CancellationToken ct) =>
        {
            realm ??= "forest";
            if (!Realms.IsValid(realm)) return Results.BadRequest(new ProblemDto("Unknown realm."));
            if (lat is < -85 or > 85 || lon is < -180 or > 180) return Results.BadRequest(new ProblemDto("Coordinates out of range."));
            var area = await world.GetAreaAsync(lat, lon, realm, Math.Clamp(ring ?? 1, 0, 2), ct);
            return Results.Ok(new AreaDto(area.CenterCell, area.Realm, area.Cells.Select(c => new CellDto(c.CellId, StatusName(c.Outcome))).ToList(), area.Landmarks.Select(LandmarkDto.From).ToList()));
        }).RequireRateLimiting("area");

        g.MapGet("/cells/{cellId}", async (string cellId, string? realm, WorldService world, IWorldStore store, CancellationToken ct) =>
        {
            realm ??= "forest";
            if (!Cells.IsValid(cellId) || !Realms.IsValid(realm)) return Results.BadRequest(new ProblemDto("Unknown cell or realm."));
            var outcome = (await world.EnsureCellsAsync([cellId], ct))[0].Outcome;
            var views = outcome == CellOutcome.Ready ? await world.ViewsAsync(await store.GetLandmarksAsync([cellId], ct), realm, ct) : [];
            return Results.Ok(new AreaDto(cellId, realm, [new CellDto(cellId, StatusName(outcome))], views.Select(LandmarkDto.From).ToList()));
        }).RequireRateLimiting("area");

        g.MapPost("/landmarks/{id}/capture", async (string id, CaptureRequest req, WorldService world, IHubContext<WorldHub> hub, CancellationToken ct) =>
        {
            if (!Realms.IsValid(req.Realm) || !Factions.IsValid(req.Leader.Faction)) return Results.BadRequest(new ProblemDto("Unknown realm or faction."));
            return await Respond(await world.CaptureAsync(id, req.Realm, req.Leader, req.Allies, req.ExpectedVersion, ct), hub, ct);
        });

        g.MapPost("/landmarks/{id}/build", async (string id, BuildRequest req, WorldService world, IHubContext<WorldHub> hub, CancellationToken ct) =>
        {
            if (!Realms.IsValid(req.Realm)) return Results.BadRequest(new ProblemDto("Unknown realm."));
            return await Respond(await world.BuildAsync(id, req.Realm, req.PlayerId, req.Structure, req.ExpectedVersion, ct), hub, ct);
        });
    }

    private static async Task<IResult> Respond(ActionOutcome r, IHubContext<WorldHub> hub, CancellationToken ct)
    {
        var dto = r.View is null ? null : LandmarkDto.From(r.View);
        switch (r.Result)
        {
            case ActionResult.Ok:
                await hub.Clients.Group(WorldHub.Group(dto!.Realm, dto.CellId)).SendAsync("landmarkChanged", dto, ct);
                return Results.Ok(dto);
            case ActionResult.NotFound: return Results.NotFound(new ProblemDto("No such landmark."));
            case ActionResult.Conflict: return Results.Conflict(new ProblemDto(r.Reason ?? "Changed by someone else.", dto));
            default: return Results.Json(new ProblemDto(r.Reason ?? "Not allowed.", dto), statusCode: StatusCodes.Status403Forbidden);
        }
    }
}
