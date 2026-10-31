using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RealmDom.World;

namespace RealmDom.Server.Data;

/// <summary>World storage on SQLite (development) or PostgreSQL (production). Safe with several server instances.</summary>
public sealed class EfWorldStore(IDbContextFactory<WorldDb> dbs, TimeProvider clock) : IWorldStore
{
    private long NowMs => clock.GetUtcNow().ToUnixTimeMilliseconds();

    public async Task<CellStatus?> GetCellStatusAsync(string cellId, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var s = await db.Cells.AsNoTracking().Where(c => c.Id == cellId).Select(c => (int?)c.Status).FirstOrDefaultAsync(ct);
        return s is null ? null : (CellStatus)s;
    }

    public async Task<bool> TryClaimCellAsync(string cellId, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        db.Cells.Add(new CellEntity { Id = cellId, Status = (int)CellStatus.Generating, ClaimedAtMs = NowMs });
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException)
        {
            // the row exists: take over only a claim left behind by a crashed server
            await using var db2 = await dbs.CreateDbContextAsync(ct);
            long now = NowMs, stale = now - (long)WorldService.StaleClaim.TotalMilliseconds;
            var rows = await db2.Cells
                .Where(c => c.Id == cellId && c.Status == (int)CellStatus.Generating && c.ClaimedAtMs < stale)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.ClaimedAtMs, now), ct);
            return rows == 1;
        }
    }

    public async Task SaveCellAsync(string cellId, IReadOnlyList<Landmark> landmarks, string mapVersion, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ids = landmarks.Select(l => l.Id).ToList();
        var existing = await db.Landmarks.Where(l => ids.Contains(l.Id)).Select(l => l.Id).ToListAsync(ct);
        foreach (var l in landmarks.Where(l => !existing.Contains(l.Id)))
            db.Landmarks.Add(new LandmarkEntity { Id = l.Id, CellId = l.CellId, Lat = l.Lat, Lon = l.Lon, Name = l.Name, Kind = (int)l.Kind, Fortress = l.Fortress });
        var cell = await db.Cells.FirstAsync(c => c.Id == cellId, ct);
        cell.Status = (int)CellStatus.Ready; cell.MapVersion = mapVersion; cell.ReadyAtMs = NowMs;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task ReleaseCellAsync(string cellId, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        await db.Cells.Where(c => c.Id == cellId && c.Status == (int)CellStatus.Generating).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<Landmark>> GetLandmarksAsync(IReadOnlyCollection<string> cellIds, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var ids = cellIds.ToList();
        return (await db.Landmarks.AsNoTracking().Where(l => ids.Contains(l.CellId)).ToListAsync(ct))
            .Select(ToModel).OrderBy(l => l.Id, StringComparer.Ordinal).ToList();
    }

    public async Task<Landmark?> GetLandmarkAsync(string landmarkId, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var e = await db.Landmarks.AsNoTracking().FirstOrDefaultAsync(l => l.Id == landmarkId, ct);
        return e is null ? null : ToModel(e);
    }

    public async Task<IReadOnlyDictionary<string, LandmarkState>> GetStatesAsync(IReadOnlyCollection<string> landmarkIds, string realm, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var ids = landmarkIds.ToList();
        return (await db.States.AsNoTracking().Where(s => s.Realm == realm && ids.Contains(s.LandmarkId)).ToListAsync(ct))
            .ToDictionary(s => s.LandmarkId, ToModel);
    }

    public async Task<bool> TrySaveStateAsync(LandmarkState s, long expectedVersion, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        string team = JsonSerializer.Serialize(s.Team), builds = JsonSerializer.Serialize(s.Builds);
        long now = NowMs;
        if (expectedVersion == 0)
        {
            db.States.Add(new StateEntity { LandmarkId = s.LandmarkId, Realm = s.Realm, OwnerId = s.OwnerId, OwnerName = s.OwnerName, Faction = s.Faction, Corrupted = s.Corrupted, BossAlive = s.BossAlive, TeamJson = team, BuildsJson = builds, Version = s.Version, UpdatedAtMs = now });
            try { await db.SaveChangesAsync(ct); return true; }
            catch (DbUpdateException) { return false; } // someone saved the first change before us
        }
        var rows = await db.States
            .Where(x => x.LandmarkId == s.LandmarkId && x.Realm == s.Realm && x.Version == expectedVersion)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.OwnerId, s.OwnerId).SetProperty(x => x.OwnerName, s.OwnerName).SetProperty(x => x.Faction, s.Faction)
                .SetProperty(x => x.Corrupted, s.Corrupted).SetProperty(x => x.BossAlive, s.BossAlive)
                .SetProperty(x => x.TeamJson, team).SetProperty(x => x.BuildsJson, builds)
                .SetProperty(x => x.Version, s.Version).SetProperty(x => x.UpdatedAtMs, now), ct);
        return rows == 1;
    }

    private static Landmark ToModel(LandmarkEntity e) => new(e.Id, e.CellId, e.Lat, e.Lon, e.Name, (FeatureKind)e.Kind, e.Fortress);

    private static LandmarkState ToModel(StateEntity e) => new(
        e.LandmarkId, e.Realm, e.OwnerId, e.OwnerName, e.Faction, e.Corrupted, e.BossAlive,
        JsonSerializer.Deserialize<List<TeamShare>>(e.TeamJson) ?? [], JsonSerializer.Deserialize<List<string>>(e.BuildsJson) ?? [], e.Version);
}
