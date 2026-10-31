using Microsoft.EntityFrameworkCore;

namespace RealmDom.Server.Data;

public sealed class CellEntity
{
    public string Id { get; set; } = "";
    public int Status { get; set; }               // 0 = generating, 1 = ready
    public long ClaimedAtMs { get; set; }         // unix ms, for taking over claims left by a crashed server
    public string? MapVersion { get; set; }
    public long? ReadyAtMs { get; set; }
}

public sealed class LandmarkEntity
{
    public string Id { get; set; } = "";
    public string CellId { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    public string Name { get; set; } = "";
    public int Kind { get; set; }
    public bool Fortress { get; set; }
}

public sealed class StateEntity
{
    public string LandmarkId { get; set; } = "";
    public string Realm { get; set; } = "";
    public string? OwnerId { get; set; }
    public string? OwnerName { get; set; }
    public string? Faction { get; set; }
    public bool Corrupted { get; set; }
    public bool BossAlive { get; set; }
    public string TeamJson { get; set; } = "[]";
    public string BuildsJson { get; set; } = "[]";
    public long Version { get; set; }
    public long UpdatedAtMs { get; set; }
}

public sealed class WorldDb(DbContextOptions<WorldDb> options) : DbContext(options)
{
    public DbSet<CellEntity> Cells => Set<CellEntity>();
    public DbSet<LandmarkEntity> Landmarks => Set<LandmarkEntity>();
    public DbSet<StateEntity> States => Set<StateEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<CellEntity>(e => { e.ToTable("cells"); e.HasKey(x => x.Id); e.Property(x => x.Id).HasMaxLength(16); });
        b.Entity<LandmarkEntity>(e => { e.ToTable("landmarks"); e.HasKey(x => x.Id); e.Property(x => x.Id).HasMaxLength(40); e.HasIndex(x => x.CellId); e.Property(x => x.Name).HasMaxLength(120); });
        b.Entity<StateEntity>(e => { e.ToTable("landmark_states"); e.HasKey(x => new { x.LandmarkId, x.Realm }); e.Property(x => x.Realm).HasMaxLength(16); });
    }
}
