namespace KcdMp.Server.Features.ClientHandling;

/// <summary>
/// Canonical source-inventory ledger for Companion Co-op.
///
/// The campaign host periodically publishes a manifest for an authored source.
/// Takes are serialized by the relay and decrement this ledger atomically.
/// Exact KCD2 WUIDs never cross the wire: they are save-local. The stable key is
/// source entity name + ItemClass; health is used to choose the closest
/// canonical lot when a source contains several same-class items.
/// </summary>
public sealed class SharedLootLedger
{
    public readonly record struct TakeDecision(bool Accepted, float CanonicalHealth);

    private sealed class Lot
    {
        public required Guid ItemClass { get; init; }
        public required float Health { get; init; }
        public int Remaining { get; set; }
    }

    private sealed class SourceState
    {
        public uint SnapshotId { get; set; }
        public List<Lot> Lots { get; } = [];
    }

    private sealed class PendingManifest
    {
        public required byte OwnerId { get; init; }
        public required uint SnapshotId { get; init; }
        public List<Lot> Lots { get; } = [];
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, SourceState> _sources =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingManifest> _pending =
        new(StringComparer.Ordinal);

    public void Begin(byte ownerId, uint snapshotId, string source)
    {
        lock (_lock)
        {
            _pending[source] = new PendingManifest
            {
                OwnerId = ownerId,
                SnapshotId = snapshotId,
            };
        }
    }

    public bool Add(byte ownerId, uint snapshotId, string source,
                    Guid itemClass, ushort amount, float health)
    {
        if (amount == 0) return false;
        lock (_lock)
        {
            if (!_pending.TryGetValue(source, out var p)
                || p.OwnerId != ownerId
                || p.SnapshotId != snapshotId)
                return false;

            // Merge byte-for-byte-ish identical lots. Quantizing the health
            // avoids tiny float serialization noise turning one stack into
            // dozens of one-item lots.
            int hb = HealthBucket(health);
            var lot = p.Lots.FirstOrDefault(x =>
                x.ItemClass == itemClass && HealthBucket(x.Health) == hb);
            if (lot is null)
            {
                p.Lots.Add(new Lot
                {
                    ItemClass = itemClass,
                    Health = health,
                    Remaining = amount,
                });
            }
            else
            {
                lot.Remaining += amount;
            }
            return true;
        }
    }

    public bool Commit(byte ownerId, uint snapshotId, string source)
    {
        lock (_lock)
        {
            if (!_pending.TryGetValue(source, out var p)
                || p.OwnerId != ownerId
                || p.SnapshotId != snapshotId)
                return false;

            var state = new SourceState { SnapshotId = snapshotId };
            foreach (var lot in p.Lots)
            {
                state.Lots.Add(new Lot
                {
                    ItemClass = lot.ItemClass,
                    Health = lot.Health,
                    Remaining = lot.Remaining,
                });
            }
            _sources[source] = state;
            _pending.Remove(source);
            return true;
        }
    }

    public TakeDecision TryTake(string source, Guid itemClass, ushort amount,
                                float requestedHealth)
    {
        if (amount == 0) return new(false, requestedHealth);

        lock (_lock)
        {
            if (!_sources.TryGetValue(source, out var state))
                return new(false, requestedHealth);

            Lot? best = null;
            float bestDelta = float.MaxValue;

            foreach (var lot in state.Lots)
            {
                if (lot.ItemClass != itemClass || lot.Remaining < amount)
                    continue;
                float delta = Math.Abs(lot.Health - requestedHealth);
                if (delta < bestDelta)
                {
                    best = lot;
                    bestDelta = delta;
                }
            }

            if (best is null)
                return new(false, requestedHealth);

            best.Remaining -= amount;
            return new(true, best.Health);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _sources.Clear();
            _pending.Clear();
        }
    }

    private static int HealthBucket(float health) =>
        (int)Math.Round(Math.Clamp(health, 0f, 10f) * 1000f);
}
