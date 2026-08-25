using System.Buffers.Binary;
using System.Text;

namespace KcdMp.Server.Features.ClientHandling;

public partial class ClientSession
{
    private async Task<bool> TryHandlePartyServerPacketAsync(int type, int payloadLen)
    {
        if (type == Protocol.TransitionUp && payloadLen == Protocol.TransitionUpPayloadLen)
        {
            var body = new byte[payloadLen]; await ReadExactAsync(body);
            if (_clientHandler.IsCampaignHost(this))
                _broadcastService.BroadcastTransition(this, body);
            return true;
        }

        if (type == Protocol.PartyStateUp && payloadLen == Protocol.PartyStateUpPayloadLen)
        {
            var body = new byte[1]; await ReadExactAsync(body);
            byte state = body[0];
            if (state > Protocol.PartyReloading) return true;
            bool wipe = _clientHandler.CompanionParty.SetState(
                Id, state, _clientHandler.GetClients(), out uint generation);
            _broadcastService.BroadcastPartyState(this, state);
            if (wipe) _broadcastService.BroadcastPartyWipe(generation);
            return true;
        }

        if (type == Protocol.ReviveUp && payloadLen == Protocol.ReviveUpPayloadLen)
        {
            var body = new byte[1]; await ReadExactAsync(body);
            byte target = body[0];
            bool accepted = _clientHandler.CompanionParty.TryRevive(target);
            _broadcastService.BroadcastRevive(Id, target, accepted);
            if (accepted)
            {
                var targetSession = _clientHandler.FindReady(target);
                if (targetSession is not null)
                    _broadcastService.BroadcastPartyStateAs(targetSession, Protocol.PartyAlive);
            }
            return true;
        }

        if (type == Protocol.PartyResumeUp && payloadLen == Protocol.PartyResumeUpPayloadLen)
        {
            var body = new byte[payloadLen]; await ReadExactAsync(body);
            if (_clientHandler.IsCampaignHost(this))
                _broadcastService.BroadcastPartyResume(body);
            return true;
        }

        if (type == Protocol.DialogueUp
            && payloadLen >= 2 && payloadLen <= 2 + Protocol.MaxDialogueLabelLen)
        {
            var body = new byte[payloadLen]; await ReadExactAsync(body);
            if (_clientHandler.IsCampaignHost(this) && body[1] == payloadLen - 2)
                _broadcastService.BroadcastDialogue(this, body);
            return true;
        }

        if (type == Protocol.CrimeUp && payloadLen == Protocol.CrimeUpPayloadLen)
        {
            var body = new byte[payloadLen]; await ReadExactAsync(body);
            _broadcastService.BroadcastCrime(this, body);
            return true;
        }

        if (type == Protocol.IdentityUp
            && payloadLen >= 4
            && payloadLen <= 4 + Protocol.MaxCompanionNameLen + Protocol.MaxBackgroundLen)
        {
            var body = new byte[payloadLen]; await ReadExactAsync(body);
            int nameLen = body[2];
            if (3 + nameLen >= body.Length) return true;
            int bgLen = body[3 + nameLen];
            if (4 + nameLen + bgLen != body.Length) return true;
            _broadcastService.BroadcastIdentity(this, body);
            return true;
        }

        if (type == Protocol.TradeBeginUp && payloadLen == Protocol.TradeBeginUpPayloadLen)
        {
            var body = new byte[1]; await ReadExactAsync(body);
            byte target = body[0];
            if (target == Id || _clientHandler.FindReady(target) is null) return true;
            uint session = _clientHandler.CompanionParty.BeginTrade(Id, target);
            _broadcastService.SendTradeOpened(session, Id, target);
            return true;
        }

        if (type == Protocol.TradeOfferUp
            && payloadLen >= 9 && payloadLen <= Protocol.MaxTradeOfferPayloadLen)
        {
            var body = new byte[payloadLen]; await ReadExactAsync(body);
            if (!TryParseTradeOffer(body, out uint session, out var offer)) return true;
            if (_clientHandler.CompanionParty.SetOffer(session, Id, offer))
                _broadcastService.SendTradeOffer(session, Id, offer);
            return true;
        }

        if (type == Protocol.TradeAcceptUp && payloadLen == Protocol.TradeAcceptUpPayloadLen)
        {
            var body = new byte[4]; await ReadExactAsync(body);
            uint session = BinaryPrimitives.ReadUInt32LittleEndian(body);
            var commit = _clientHandler.CompanionParty.Accept(session, Id);
            _broadcastService.SendTradeAccepted(session, Id);
            if (commit is not null) _broadcastService.SendTradeCommit(commit);
            return true;
        }

        if (type == Protocol.TradeCancelUp && payloadLen == Protocol.TradeCancelUpPayloadLen)
        {
            var body = new byte[4]; await ReadExactAsync(body);
            uint session = BinaryPrimitives.ReadUInt32LittleEndian(body);
            if (_clientHandler.CompanionParty.Cancel(session, Id, out byte other))
                _broadcastService.SendTradeCancelled(session, Id, other);
            return true;
        }

        if (type == Protocol.PartyPingUp && payloadLen == Protocol.PartyPingUpPayloadLen)
        {
            var body = new byte[payloadLen]; await ReadExactAsync(body);
            _broadcastService.BroadcastPartyPing(this, body);
            return true;
        }

        return false;
    }

    private static bool TryParseTradeOffer(
        byte[] body, out uint session, out CompanionPartyCoordinator.TradeOffer offer)
    {
        session = 0; offer = new(0, []);
        if (body.Length < 9) return false;
        session = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0, 4));
        float money = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(4, 4));
        int count = body[8];
        if (count > Protocol.MaxTradeItems
            || body.Length != 9 + count * Protocol.TradeItemLen
            || money < 0) return false;

        var items = new CompanionPartyCoordinator.TradeItem[count];
        int o = 9;
        for (int i = 0; i < count; i++)
        {
            var cls = new Guid(body.AsSpan(o, Protocol.ItemClassLen)); o += Protocol.ItemClassLen;
            ushort amount = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(o, 2)); o += 2;
            float health = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(o, 4)); o += 4;
            if (amount == 0) return false;
            items[i] = new(cls, amount, health);
        }
        offer = new(money, items);
        return true;
    }

    public void EnqueuePartyState(byte sourceId, byte state) =>
        EnqueueRaw(BuildPacket(Protocol.PartyStateDown, [sourceId, state]));

    public void EnqueueRevive(byte reviver, byte target, bool accepted) =>
        EnqueueRaw(BuildPacket(Protocol.ReviveDown, [reviver, target, accepted ? (byte)1 : (byte)0]));

    public void EnqueuePartyWipe(uint generation)
    {
        var p = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(p, generation);
        EnqueueRaw(BuildPacket(Protocol.PartyWipeDown, p));
    }

    public void EnqueueTransition(byte sourceId, byte[] body)
    {
        var p = new byte[1 + body.Length]; p[0] = sourceId; body.CopyTo(p, 1);
        EnqueueRaw(BuildPacket(Protocol.TransitionDown, p));
    }

    public void EnqueuePartyResume(byte[] body) =>
        EnqueueRaw(BuildPacket(Protocol.PartyResumeDown, body));

    public void EnqueueDialogue(byte sourceId, byte[] body)
    {
        var p = new byte[1 + body.Length]; p[0] = sourceId; body.CopyTo(p, 1);
        EnqueueRaw(BuildPacket(Protocol.DialogueDown, p));
    }

    public void EnqueueCrime(byte offender, byte[] body) =>
        EnqueueRaw(BuildPacket(Protocol.CrimeDown, [offender, body[0], body[1]]));

    public void EnqueueIdentity(byte sourceId, byte[] body)
    {
        var p = new byte[1 + body.Length]; p[0] = sourceId; body.CopyTo(p, 1);
        EnqueueRaw(BuildPacket(Protocol.IdentityDown, p));
    }

    public void EnqueueTradeState(byte[] body) =>
        EnqueueRaw(BuildPacket(Protocol.TradeStateDown, body));

    public void EnqueueTradeCommit(byte[] body) =>
        EnqueueRaw(BuildPacket(Protocol.TradeCommitDown, body));

    public void EnqueueTradeCancel(byte[] body) =>
        EnqueueRaw(BuildPacket(Protocol.TradeCancelDown, body));

    public void EnqueuePartyPing(byte sourceId, byte[] body)
    {
        var p = new byte[1 + body.Length]; p[0] = sourceId; body.CopyTo(p, 1);
        EnqueueRaw(BuildPacket(Protocol.PartyPingDown, p));
    }
}
