using System.Collections.Concurrent;

namespace RealmDom.World;

/// <summary>Single-process store for tests and local experiments.</summary>
public sealed class InMemoryWorldStore : IWorldStore
{
    private readonly ConcurrentDictionary<string, CellStatus> _cells = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _claimedAt = new();
    private readonly object _claimLock = new();
    public TimeProvider Clock { get; init; } = TimeProvider.System;
    private readonly ConcurrentDictionary<string, Landmark> _landmarks = new();
    private readonly ConcurrentDictionary<(string, string), LandmarkState> _states = new();
    private readonly object _stateLock = new();

    public Task<CellStatus?> GetCellStatusAsync(string cellId, CancellationToken ct) =>
        Task.FromResult(_cells.TryGetValue(cellId, out var s) ? s : (CellStatus?)null);

    public Task<bool> TryClaimCellAsync(string cellId, CancellationToken ct)
    {
        lock (_claimLock)
        {
            var now = Clock.GetUtcNow();
            if (_cells.TryGetValue(cellId, out var s) && (s == CellStatus.Ready || now - _claimedAt[cellId] < WorldService.StaleClaim)) return Task.FromResult(false);
            _cells[cellId] = CellStatus.Generating; _claimedAt[cellId] = now;
            return Task.FromResult(true);
        }
    }

    public Task SaveCellAsync(string cellId, IReadOnlyList<Landmark> landmarks, string mapVersion, CancellationToken ct)
    {
        foreach (var l in landmarks) _landmarks[l.Id] = l;
        _cells[cellId] = CellStatus.Ready;
        return Task.CompletedTask;
    }

    public Task ReleaseCellAsync(string cellId, CancellationToken ct)
    {
        lock (_claimLock) { if (_cells.TryGetValue(cellId, out var s) && s == CellStatus.Generating) { _cells.TryRemove(cellId, out _); _claimedAt.TryRemove(cellId, out _); } }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Landmark>> GetLandmarksAsync(IReadOnlyCollection<string> cellIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Landmark>>(_landmarks.Values.Where(l => cellIds.Contains(l.CellId)).OrderBy(l => l.Id, StringComparer.Ordinal).ToList());

    public Task<Landmark?> GetLandmarkAsync(string landmarkId, CancellationToken ct) =>
        Task.FromResult(_landmarks.TryGetValue(landmarkId, out var l) ? l : null);

    public Task<IReadOnlyDictionary<string, LandmarkState>> GetStatesAsync(IReadOnlyCollection<string> landmarkIds, string realm, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, LandmarkState>>(landmarkIds
            .Where(id => _states.ContainsKey((id, realm)))
            .ToDictionary(id => id, id => _states[(id, realm)]));

    public Task<bool> TrySaveStateAsync(LandmarkState state, long expectedVersion, CancellationToken ct)
    {
        lock (_stateLock)
        {
            var key = (state.LandmarkId, state.Realm);
            var current = _states.TryGetValue(key, out var s) ? s.Version : 0;
            if (current != expectedVersion) return Task.FromResult(false);
            _states[key] = state;
            return Task.FromResult(true);
        }
    }
}
