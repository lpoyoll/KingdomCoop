using System.Buffers.Binary;

namespace KcdMp.Wire;

/// <summary>
/// Companion Co-op v0.4 full-feature wire foundation.
///
/// 0x36/0x37: host-authored canonical loot-source manifest.
/// 0x38/0x39: transactional loot take request/result.
/// 0x3A/0x3B: immutable campaign-host claim/role.
/// 0x3C: campaign host ended.
///
/// Unlike upstream 0x32-0x35, which covers a runtime item deliberately dropped
/// into the world, these packets cover inventories that already belong to an
/// authored world source: corpse, chest, cupboard, etc.
///
/// The host publishes the contents. The relay owns remaining quantities. A take
/// is never final until the relay accepts it. This is what prevents two local
/// KCD2 simulations from both awarding the same sword.
/// </summary>
public static partial class Protocol
{
    // C -> S
    public const byte LootManifestUp = 0x36;
    public const byte LootTakeUp     = 0x38;
    public const byte HostClaimUp    = 0x3A;

    // S -> C
    public const byte LootManifestDown = 0x37;
    public const byte LootTakeDown     = 0x39;
    public const byte HostRoleDown     = 0x3B;
    public const byte HostEndedDown    = 0x3C;

    public const byte LootManifestPhaseBegin = 0;
    public const byte LootManifestPhaseItem  = 1;
    public const byte LootManifestPhaseEnd   = 2;

    public const int MaxLootSourceNameLen = 64;

    // phase + snapshotId + sourceLen + source + itemClass + amount + health
    public const int LootManifestFixedBytes =
        1 + 4 + 1 + ItemClassLen + 2 + 4;

    // takeId + sourceLen + source + itemClass + amount + requested health
    public const int LootTakeFixedBytes =
        4 + 1 + ItemClassLen + 2 + 4;

    public const int CampaignIdLen = 16;
    public const int HostClaimUpPayloadLen = CampaignIdLen;
    // [isHost:1][hostGhostId:1][campaignId:16]
    public const int HostRoleDownPayloadLen = 2 + CampaignIdLen;
    public const int HostEndedDownPayloadLen = 1;

    public const byte HostEndedReasonDisconnected = 1;

    public static bool IsValidLootManifestLength(int payloadLen) =>
        payloadLen >= LootManifestFixedBytes + 1
        && payloadLen <= LootManifestFixedBytes + MaxLootSourceNameLen;

    public static bool IsValidLootTakeLength(int payloadLen) =>
        payloadLen >= LootTakeFixedBytes + 1
        && payloadLen <= LootTakeFixedBytes + MaxLootSourceNameLen;
}
