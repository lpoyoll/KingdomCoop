using KcdMp.Server.Features.ClientHandling;

namespace KcdMp.Server.Features.Tcp;

public partial class TcpBroadcastService
{
    public void BroadcastCompanionHostRoles()
    {
        foreach (var target in _clientHandler.GetClients().Where(c => c.IsReady))
            target.EnqueueCompanionHostRole(_clientHandler.IsCampaignHost(target));
    }

    public void BroadcastCompanionHostEnded()
    {
        foreach (var target in _clientHandler.GetClients().Where(c => c.IsReady))
            target.EnqueueCompanionHostEnded(Protocol.HostEndedReasonDisconnected);
    }

    public void BroadcastLootManifest(ClientSession source, byte[] body)
    {
        foreach (var target in Others(source))
            target.EnqueueLootManifest(source.Id, body);
    }

    /// <summary>
    /// Accepted takes go to everyone so every local copy removes the item.
    /// Rejections only need to reach the claimant, who rolls back the local
    /// inventory gain using the WUID recorded when the take was detected.
    /// </summary>
    public void BroadcastLootTakeResult(ClientSession source, bool accepted, byte[] body)
    {
        if (!accepted)
        {
            source.EnqueueLootTakeResult(source.Id, false, body);
            return;
        }

        foreach (var target in _clientHandler.GetClients().Where(c => c.IsReady))
            target.EnqueueLootTakeResult(source.Id, true, body);
    }
}
