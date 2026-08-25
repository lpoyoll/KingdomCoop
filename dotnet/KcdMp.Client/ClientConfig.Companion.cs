namespace KcdMp.Client;

public partial class ClientConfig
{
    /// <summary>
    /// Stable local identity for the companion profile. When Steam's most-recent
    /// user can be resolved this is replaced by "steam-&lt;Steam64&gt;"; otherwise
    /// the generated GUID remains stable in kcdmp-client.json.
    /// </summary>
    public string CompanionId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Stable host campaign slot. The launcher may replace this with a hash of
    /// the selected KCD2 save. Guests overwrite it in-memory with Henry's value
    /// announced by the relay.
    /// </summary>
    public string CampaignId { get; set; } = Guid.NewGuid().ToString("N");

    public string CompanionName { get; set; } = "Companion";
    public int CompanionFaceIndex { get; set; } = 0;
    public string CompanionSex { get; set; } = "male";
    public string CompanionBackground { get; set; } = "Commoner";

    public bool CompanionFriendlyFire { get; set; } = false;
    public float CompanionCatchUpDistance { get; set; } = 250f;
    public float CompanionDownedHealth { get; set; } = 1.5f;
    public float CompanionReviveHealth { get; set; } = 30f;

    public void NormaliseCompanionSettings()
    {
        if (StableSteamIdentity.TryGetMostRecentSteam64() is string steam)
            CompanionId = "steam-" + steam;
        if (!Guid.TryParseExact(CampaignId, "N", out _)
            && !Guid.TryParse(CampaignId, out _))
            CampaignId = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(CompanionName))
            CompanionName = string.IsNullOrWhiteSpace(PlayerName) ? "Companion" : PlayerName!;
        CompanionFaceIndex = Math.Clamp(CompanionFaceIndex, 0, 47);
        CompanionCatchUpDistance = Math.Clamp(CompanionCatchUpDistance, 30f, 2000f);
        CompanionDownedHealth = Math.Clamp(CompanionDownedHealth, 0.2f, 10f);
        CompanionReviveHealth = Math.Clamp(CompanionReviveHealth, 5f, 100f);
    }
}
