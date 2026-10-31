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

/// <summary>Live updates: clients join the cells they can see and get every change in them.</summary>
public sealed class WorldHub : Hub
{
    public static string Group(string realm, string cellId) => $"{realm}:{cellId}";

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
