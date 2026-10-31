# RealmDom World Service

The shared, persistent game world. Every player asks this service what exists around them; nobody's phone invents landmarks.

## How the world is made

- **Cells.** The planet is divided into [H3](https://h3geo.org/) hexagons at resolution 8 (about 0.74 km² each). A cell ID like `882db056abfffff` is a permanent address.
- **Generated once.** The first request that touches an empty cell claims it (an atomic row insert), downloads OpenStreetMap features for all newly claimed cells in one request, picks landmarks by fixed rules and saves them. Everyone after that reads the saved cell. A claim left by a crashed server is taken over after 2 minutes; a failed download releases the claim so a later visit retries.
- **Deterministic rules** (`WorldGenerator`): public places only (fountains, artworks, viewpoints, historic sites, parks; roads only as a fallback), at least 70 m apart, at most 12 per cell, nothing within 60 m of a school, kindergarten, hospital, clinic or place of worship, and one fortress in cells with 8 or more landmarks. A feature belongs to exactly one cell, so nothing is duplicated across cell borders.
- **Stable IDs.** A landmark's ID is its OpenStreetMap element (`osm:n123456`). Its position and name never change after generation, even if the map data changes later.
- **Layout vs state.** Layout is fixed; the state (owner, team shares, structures, corruption, boss) is stored per landmark **per realm**, so Forest, Cyber and Kingdom share the same spots with separate owners. Untouched landmarks use a deterministic default state, so storage only grows when players act.
- **Live updates.** Clients join SignalR groups for the cells they can see (`/hubs/world`, method `JoinCells(realm, cellIds)`) and receive `landmarkChanged` for every change in them.
- **Optimistic concurrency.** Every change sends the version it was based on; a stale version gets `409 Conflict`.

## Multiple cities

Nothing is configured per city: a city is just a set of cells. `World:Regions` in `appsettings.json` lists the polygons where the game is live (empty = everywhere, for development):

```json
"Regions": [
  { "Name": "Rosh HaAyin", "Polygon": [[32.05, 34.90], [32.05, 35.00], [32.13, 35.00], [32.13, 34.90]] }
]
```

Opening a new city = adding its polygon. Cells outside every region answer `not-live` and are never generated. A 500 km² city is about 700 cells and roughly 14,000 landmarks; one PostgreSQL instance handles many cities, and data can later be split by region.

## API

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/api/world/area?lat=&lon=&realm=forest&ring=1` | The player's cell and its neighbours (generated on first visit) |
| GET | `/api/world/cells/{cellId}?realm=` | One cell |
| POST | `/api/world/landmarks/{id}/capture` | Take a landmark (single player or team) |
| POST | `/api/world/landmarks/{id}/build` | Build a structure on owned land |
| GET | `/health` | Liveness |

Capture and build enforce ownership rules on the server (fortresses need a team, no capturing your own faction's land, owners only build, slot limits). **Prototype limits:** there is no authentication yet and capture trusts the client's result; production must authenticate players and validate battles and duels on the server.

## Run it

```bash
cd server
dotnet test                      # 18 tests: generation, generate-once under concurrency, restart persistence, live updates
dotnet run --project src/RealmDom.Server --urls http://localhost:5080
```

SQLite is the default (`realmdom.db`). For PostgreSQL set `World:Provider` to `postgres` and `ConnectionStrings:World` to a Npgsql connection string. The schema is created on start-up; switch to EF Core migrations before production.

Map data comes from public Overpass servers (three are tried in order). They are fine for development but rate-limited; production should run its own Overpass instance or a PostGIS OpenStreetMap extract behind the same `IMapSource` interface.
