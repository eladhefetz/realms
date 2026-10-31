using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using RealmDom.Server.Api;
using RealmDom.Server.Data;
using RealmDom.Server.Maps;
using RealmDom.World;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// storage: SQLite by default, PostgreSQL when World:Provider = "postgres"
var provider = cfg["World:Provider"] ?? "sqlite";
var conn = cfg.GetConnectionString("World") ?? "Data Source=realmdom.db";
builder.Services.AddDbContextFactory<WorldDb>(o => { if (provider == "postgres") o.UseNpgsql(conn); else o.UseSqlite(conn); });
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IWorldStore, EfWorldStore>();

// world rules
builder.Services.AddSingleton(cfg.GetSection("World:Generator").Get<GeneratorOptions>() ?? new GeneratorOptions());
builder.Services.AddSingleton<WorldGenerator>();
builder.Services.AddSingleton(_ =>
{
    var regions = cfg.GetSection("World:Regions").GetChildren().Select(r => new RegionPolicy.Region(
        r["Name"] ?? "region",
        r.GetSection("Polygon").GetChildren().Select(p => (p.GetSection("0").Get<double>(), p.GetSection("1").Get<double>())).ToList()));
    return new RegionPolicy(regions);
});
builder.Services.AddSingleton<WorldService>();

// map data
builder.Services.AddSingleton(cfg.GetSection("World:Overpass").Get<OverpassOptions>() ?? new OverpassOptions());
builder.Services.AddHttpClient<IMapSource, OverpassMapSource>(h =>
{
    h.Timeout = TimeSpan.FromSeconds(90);
    h.DefaultRequestHeaders.UserAgent.ParseAdd("RealmDom-World/0.1 (+https://github.com/eladhefetz/realms)");
});

builder.Services.AddSignalR();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(cfg.GetSection("World:AllowedOrigins").Get<string[]>() ?? ["https://eladhefetz.github.io", "http://localhost:8765"])
    .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("area", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbs = scope.ServiceProvider.GetRequiredService<IDbContextFactory<WorldDb>>();
    await using var db = await dbs.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync(); // switch to EF migrations before production
}

app.UseCors();
app.UseRateLimiter();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
WorldApi.Map(app);
app.MapHub<WorldHub>("/hubs/world");

app.Run();

public partial class Program;
