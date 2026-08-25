using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace KcdMp.Server.Features.ClientHandling;

public partial class ClientSession
{
    private async Task<bool> TryHandleCompanionPacketAsync(int type, int payloadLen)
    {
        if (await TryHandlePartyServerPacketAsync(type, payloadLen)) return true;
        if (type == Protocol.HostClaimUp
            && payloadLen == Protocol.HostClaimUpPayloadLen)
        {
            var hostBody = new byte[Protocol.HostClaimUpPayloadLen];
            await ReadExactAsync(hostBody);
            var campaignId = new Guid(hostBody);
            bool loopback = _tcp.Client.RemoteEndPoint is IPEndPoint ep
                            && IPAddress.IsLoopback(ep.Address);
            bool accepted = _clientHandler.TryClaimCampaignHost(this, loopback, campaignId);
            _logger.Information(
                "[companion] host claim name={Name} id={Id} loopback={Loopback} accepted={Accepted}",
                Name, Id, loopback, accepted);
            _broadcastService.BroadcastCompanionHostRoles();
            _broadcastService.BroadcastCombatRole();
            return true;
        }

        if (type == Protocol.LootManifestUp
            && Protocol.IsValidLootManifestLength(payloadLen))
        {
            var body = new byte[payloadLen];
            await ReadExactAsync(body);

            // Only Henry's world authors corpse/chest contents.
            if (!_clientHandler.IsCampaignHost(this))
            {
                _logger.Warning("[companion-loot] non-host {Name} tried to publish a manifest.", Name);
                return true;
            }

            if (!TryParseLootManifest(body, out byte phase, out uint snapshotId,
                                      out string source, out Guid cls,
                                      out ushort amount, out float health))
                return true;

            bool ok = phase switch
            {
                Protocol.LootManifestPhaseBegin =>
                    BeginManifest(snapshotId, source),
                Protocol.LootManifestPhaseItem =>
                    _clientHandler.SharedLoot.Add(Id, snapshotId, source, cls, amount, health),
                Protocol.LootManifestPhaseEnd =>
                    _clientHandler.SharedLoot.Commit(Id, snapshotId, source),
                _ => false,
            };

            if (ok)
                _broadcastService.BroadcastLootManifest(this, body);
            return true;
        }

        if (type == Protocol.LootTakeUp
            && Protocol.IsValidLootTakeLength(payloadLen))
        {
            var body = new byte[payloadLen];
            await ReadExactAsync(body);

            if (!TryParseLootTake(body, out uint takeId, out string source,
                                  out Guid cls, out ushort amount,
                                  out float requestedHealth, out int healthOffset))
                return true;

            var decision = _clientHandler.SharedLoot.TryTake(
                source, cls, amount, requestedHealth);

            // The response carries the host-canonical condition, so the winner
            // can persist the exact lot rather than whichever local copy they
            // happened to see.
            if (decision.Accepted)
                BinaryPrimitives.WriteSingleLittleEndian(
                    body.AsSpan(healthOffset, 4), decision.CanonicalHealth);

            _logger.Information(
                "[companion-loot] take {TakeId} {Source} {Class} x{Amount}: {Decision}",
                takeId, source, cls, amount,
                decision.Accepted ? "accepted" : "rejected");

            _broadcastService.BroadcastLootTakeResult(this, decision.Accepted, body);
            return true;
        }

        return false;
    }

    private bool BeginManifest(uint snapshotId, string source)
    {
        _clientHandler.SharedLoot.Begin(Id, snapshotId, source);
        return true;
    }

    private static bool TryParseLootManifest(
        byte[] body, out byte phase, out uint snapshotId, out string source,
        out Guid cls, out ushort amount, out float health)
    {
        phase = 0; snapshotId = 0; source = ""; cls = Guid.Empty; amount = 0; health = 1f;
        if (body.Length < Protocol.LootManifestFixedBytes + 1) return false;

        phase = body[0];
        snapshotId = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(1, 4));
        int sourceLen = body[5];
        if (sourceLen < 1 || sourceLen > Protocol.MaxLootSourceNameLen) return false;
        if (body.Length != Protocol.LootManifestFixedBytes + sourceLen) return false;

        source = Encoding.UTF8.GetString(body, 6, sourceLen);
        if (!IsSafeSourceName(source)) return false;

        int o = 6 + sourceLen;
        cls = new Guid(body.AsSpan(o, Protocol.ItemClassLen)); o += Protocol.ItemClassLen;
        amount = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(o, 2)); o += 2;
        health = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(o, 4));
        return true;
    }

    private static bool TryParseLootTake(
        byte[] body, out uint takeId, out string source, out Guid cls,
        out ushort amount, out float health, out int healthOffset)
    {
        takeId = 0; source = ""; cls = Guid.Empty; amount = 0; health = 1f; healthOffset = 0;
        if (body.Length < Protocol.LootTakeFixedBytes + 1) return false;

        takeId = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0, 4));
        int sourceLen = body[4];
        if (takeId == 0 || sourceLen < 1 || sourceLen > Protocol.MaxLootSourceNameLen) return false;
        if (body.Length != Protocol.LootTakeFixedBytes + sourceLen) return false;

        source = Encoding.UTF8.GetString(body, 5, sourceLen);
        if (!IsSafeSourceName(source)) return false;

        int o = 5 + sourceLen;
        cls = new Guid(body.AsSpan(o, Protocol.ItemClassLen)); o += Protocol.ItemClassLen;
        amount = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(o, 2)); o += 2;
        healthOffset = o;
        health = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(o, 4));
        return true;
    }

    private static bool IsSafeSourceName(string source)
    {
        if (source.Length is < 1 or > Protocol.MaxLootSourceNameLen) return false;
        foreach (char c in source)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
                return false;
        return true;
    }

    public void EnqueueCompanionHostRole(bool isHost)
    {
        var payload = new byte[Protocol.HostRoleDownPayloadLen];
        payload[0] = isHost ? (byte)1 : (byte)0;
        payload[1] = _clientHandler.CampaignHostId;
        _clientHandler.CampaignId.TryWriteBytes(payload.AsSpan(2, Protocol.CampaignIdLen));
        EnqueueRaw(BuildPacket(Protocol.HostRoleDown, payload));
    }

    public void EnqueueCompanionHostEnded(byte reason) =>
        EnqueueRaw(BuildPacket(Protocol.HostEndedDown, [reason]));

    public void EnqueueLootManifest(byte sourceId, byte[] upstream)
    {
        var payload = new byte[1 + upstream.Length];
        payload[0] = sourceId;
        upstream.CopyTo(payload, 1);
        EnqueueRaw(BuildPacket(Protocol.LootManifestDown, payload));
    }

    public void EnqueueLootTakeResult(byte claimerId, bool accepted, byte[] upstream)
    {
        var payload = new byte[2 + upstream.Length];
        payload[0] = claimerId;
        payload[1] = accepted ? (byte)1 : (byte)0;
        upstream.CopyTo(payload, 2);
        EnqueueRaw(BuildPacket(Protocol.LootTakeDown, payload));
    }
}
