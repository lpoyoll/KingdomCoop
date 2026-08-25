using System.Buffers.Binary;

namespace KcdMp.Server.Features.ClientHandling;

public sealed class CompanionPartyCoordinator
{
    public sealed record TradeItem(Guid ItemClass, ushort Amount, float Health);
    public sealed record TradeOffer(float Money, TradeItem[] Items);
    public sealed record TradeCommit(uint SessionId, byte A, byte B,
                                     TradeOffer AOffer, TradeOffer BOffer);

    private sealed class Trade
    {
        public required uint Id { get; init; }
        public required byte A { get; init; }
        public required byte B { get; init; }
        public TradeOffer AOffer { get; set; } = new(0, []);
        public TradeOffer BOffer { get; set; } = new(0, []);
        public bool AAccepted { get; set; }
        public bool BAccepted { get; set; }
    }

    private readonly object _lock = new();
    private readonly Dictionary<byte, byte> _state = [];
    private readonly Dictionary<uint, Trade> _trades = [];
    private uint _nextTrade = 1;
    private uint _wipeGeneration;

    public byte State(byte id)
    {
        lock (_lock) return _state.TryGetValue(id, out var s) ? s : Protocol.PartyAlive;
    }

    public bool SetState(byte id, byte state, IReadOnlyCollection<ClientSession> ready,
                         out uint wipeGeneration)
    {
        lock (_lock)
        {
            _state[id] = state;
            bool any = false;
            bool allDown = true;
            foreach (var c in ready)
            {
                if (!c.IsReady) continue;
                any = true;
                byte s = _state.TryGetValue(c.Id, out var v) ? v : Protocol.PartyAlive;
                if (s != Protocol.PartyDowned) { allDown = false; break; }
            }

            if (any && allDown)
            {
                _wipeGeneration++;
                wipeGeneration = _wipeGeneration;
                return true;
            }

            wipeGeneration = _wipeGeneration;
            return false;
        }
    }

    public bool TryRevive(byte target)
    {
        lock (_lock)
        {
            if (!_state.TryGetValue(target, out var s) || s != Protocol.PartyDowned)
                return false;
            _state[target] = Protocol.PartyAlive;
            return true;
        }
    }

    public void Remove(byte id)
    {
        lock (_lock)
        {
            _state.Remove(id);
            foreach (uint tid in _trades
                         .Where(kv => kv.Value.A == id || kv.Value.B == id)
                         .Select(kv => kv.Key).ToArray())
                _trades.Remove(tid);
        }
    }

    public uint BeginTrade(byte a, byte b)
    {
        lock (_lock)
        {
            uint id = _nextTrade++;
            if (id == 0) id = _nextTrade++;
            _trades[id] = new Trade { Id = id, A = a, B = b };
            return id;
        }
    }

    public bool SetOffer(uint session, byte who, TradeOffer offer)
    {
        lock (_lock)
        {
            if (!_trades.TryGetValue(session, out var t)) return false;
            if (who == t.A) t.AOffer = offer;
            else if (who == t.B) t.BOffer = offer;
            else return false;
            // Changing an offer invalidates both confirmations.
            t.AAccepted = t.BAccepted = false;
            return true;
        }
    }

    public TradeCommit? Accept(uint session, byte who)
    {
        lock (_lock)
        {
            if (!_trades.TryGetValue(session, out var t)) return null;
            if (who == t.A) t.AAccepted = true;
            else if (who == t.B) t.BAccepted = true;
            else return null;

            if (!t.AAccepted || !t.BAccepted) return null;
            _trades.Remove(session);
            return new TradeCommit(t.Id, t.A, t.B, t.AOffer, t.BOffer);
        }
    }

    public bool Cancel(uint session, byte who, out byte other)
    {
        lock (_lock)
        {
            other = 0;
            if (!_trades.TryGetValue(session, out var t)) return false;
            if (who == t.A) other = t.B;
            else if (who == t.B) other = t.A;
            else return false;
            _trades.Remove(session);
            return true;
        }
    }
}
