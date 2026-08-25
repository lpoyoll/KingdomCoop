namespace KcdMp.Server.Features.ClientHandling;

public partial class ClientHandler
{
    private ClientSession? _campaignHost;
    private bool _campaignEnded;
    private Guid _campaignId;

    public SharedLootLedger SharedLoot { get; } = new();

    public ClientSession? CampaignHost
    {
        get { lock (_lock) return _campaignHost; }
    }

    
    public Guid CampaignId
    {
        get { lock (_lock) return _campaignId; }
    }

    public byte CampaignHostId
    {
        get { lock (_lock) return _campaignHost?.Id ?? (byte)0; }
    }

    public bool CampaignEnded
    {
        get { lock (_lock) return _campaignEnded; }
    }

    /// <summary>
    /// HOST GAME launches both relay and local agent on the same PC. Only a
    /// loopback connection may claim campaign authority, so a remote guest
    /// cannot race/spoof the role.
    /// </summary>
    public bool TryClaimCampaignHost(ClientSession client, bool loopback, Guid campaignId)
    {
        lock (_lock)
        {
            if (!loopback || _campaignEnded) return false;
            if (_campaignHost is null) { _campaignHost = client; _campaignId = campaignId; }
            return ReferenceEquals(_campaignHost, client);
        }
    }

    public bool IsCampaignHost(ClientSession client)
    {
        lock (_lock) return ReferenceEquals(_campaignHost, client);
    }

    /// <summary>
    /// Called after the ordinary client list removal. Returns true exactly when
    /// Henry/campaign host left. Authority is deliberately NOT promoted.
    /// </summary>
    public bool CompanionClientRemoved(ClientSession client)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_campaignHost, client)) return false;
            _campaignHost = null;
            _campaignEnded = true;
            SharedLoot.Clear();
            return true;
        }
    }
}
