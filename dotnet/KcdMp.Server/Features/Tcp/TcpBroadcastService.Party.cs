using System.Buffers.Binary;
using KcdMp.Server.Features.ClientHandling;

namespace KcdMp.Server.Features.Tcp;

public partial class TcpBroadcastService
{
    public void BroadcastTransition(ClientSession source, byte[] body)
    {
        foreach (var t in Others(source)) t.EnqueueTransition(source.Id, body);
    }

    public void BroadcastPartyState(ClientSession source, byte state)
    {
        foreach (var t in _clientHandler.GetClients().Where(c => c.IsReady))
            t.EnqueuePartyState(source.Id, state);
    }

    public void BroadcastPartyStateAs(ClientSession source, byte state) =>
        BroadcastPartyState(source, state);

    public void BroadcastRevive(byte reviver, byte target, bool accepted)
    {
        foreach (var t in _clientHandler.GetClients().Where(c => c.IsReady))
            t.EnqueueRevive(reviver, target, accepted);
    }

    public void BroadcastPartyWipe(uint generation)
    {
        foreach (var t in _clientHandler.GetClients().Where(c => c.IsReady))
            t.EnqueuePartyWipe(generation);
    }

    public void BroadcastPartyResume(byte[] body)
    {
        foreach (var t in _clientHandler.GetClients().Where(c => c.IsReady))
            t.EnqueuePartyResume(body);
    }

    public void BroadcastDialogue(ClientSession source, byte[] body)
    {
        foreach (var t in Others(source)) t.EnqueueDialogue(source.Id, body);
    }

    public void BroadcastCrime(ClientSession source, byte[] body)
    {
        foreach (var t in _clientHandler.GetClients().Where(c => c.IsReady))
            t.EnqueueCrime(source.Id, body);
    }

    public void BroadcastIdentity(ClientSession source, byte[] body)
    {
        foreach (var t in Others(source)) t.EnqueueIdentity(source.Id, body);
    }

    public void SendTradeOpened(uint session, byte a, byte b)
    {
        var bodyA = BuildTradeState(0, session, b, null);
        var bodyB = BuildTradeState(0, session, a, null);
        _clientHandler.FindReady(a)?.EnqueueTradeState(bodyA);
        _clientHandler.FindReady(b)?.EnqueueTradeState(bodyB);
    }

    public void SendTradeOffer(uint session, byte source,
                               CompanionPartyCoordinator.TradeOffer offer)
    {
        var body = BuildTradeState(1, session, source, offer);
        foreach (var c in _clientHandler.GetClients().Where(c => c.IsReady))
            c.EnqueueTradeState(body);
    }

    public void SendTradeAccepted(uint session, byte source)
    {
        var body = BuildTradeState(2, session, source, null);
        foreach (var c in _clientHandler.GetClients().Where(c => c.IsReady))
            c.EnqueueTradeState(body);
    }

    public void SendTradeCommit(CompanionPartyCoordinator.TradeCommit c)
    {
        _clientHandler.FindReady(c.A)?.EnqueueTradeCommit(
            BuildTradeCommit(c.SessionId, c.AOffer, c.BOffer));
        _clientHandler.FindReady(c.B)?.EnqueueTradeCommit(
            BuildTradeCommit(c.SessionId, c.BOffer, c.AOffer));
    }

    public void SendTradeCancelled(uint session, byte source, byte other)
    {
        var body = new byte[5];
        BinaryPrimitives.WriteUInt32LittleEndian(body, session);
        body[4] = source;
        _clientHandler.FindReady(source)?.EnqueueTradeCancel(body);
        _clientHandler.FindReady(other)?.EnqueueTradeCancel(body);
    }

    public void BroadcastPartyPing(ClientSession source, byte[] body)
    {
        foreach (var t in Others(source)) t.EnqueuePartyPing(source.Id, body);
    }

    private static byte[] BuildTradeState(byte kind, uint session, byte peer,
        CompanionPartyCoordinator.TradeOffer? offer)
    {
        int len = 1 + 4 + 1;
        if (offer is not null) len += 4 + 1 + offer.Items.Length * Protocol.TradeItemLen;
        var b = new byte[len];
        b[0] = kind;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1,4), session);
        b[5] = peer;
        if (offer is null) return b;
        int o = 6;
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o,4), offer.Money); o += 4;
        b[o++] = (byte)offer.Items.Length;
        foreach (var item in offer.Items)
        {
            item.ItemClass.TryWriteBytes(b.AsSpan(o, Protocol.ItemClassLen)); o += Protocol.ItemClassLen;
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o,2), item.Amount); o += 2;
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o,4), item.Health); o += 4;
        }
        return b;
    }

    // Tailored: own offer first (what this client removes), peer offer second
    // (what this client receives).
    private static byte[] BuildTradeCommit(uint session,
        CompanionPartyCoordinator.TradeOffer own,
        CompanionPartyCoordinator.TradeOffer peer)
    {
        int len = 4 + OfferLen(own) + OfferLen(peer);
        var b = new byte[len];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0,4), session);
        int o = 4;
        o = WriteOffer(b, o, own);
        WriteOffer(b, o, peer);
        return b;
    }

    private static int OfferLen(CompanionPartyCoordinator.TradeOffer x) =>
        4 + 1 + x.Items.Length * Protocol.TradeItemLen;

    private static int WriteOffer(byte[] b, int o, CompanionPartyCoordinator.TradeOffer x)
    {
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o,4), x.Money); o += 4;
        b[o++] = (byte)x.Items.Length;
        foreach (var item in x.Items)
        {
            item.ItemClass.TryWriteBytes(b.AsSpan(o,16)); o += 16;
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o,2), item.Amount); o += 2;
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o,4), item.Health); o += 4;
        }
        return o;
    }
}
