namespace KcdMp.Wire;

/// <summary>
/// Companion Co-op full-feature packets. Protocol v8 is intentionally
/// incompatible with the upstream peer model: Henry is immutable campaign
/// authority and guests are companion actors.
/// </summary>
public static partial class Protocol
{
    // 2. Host-following transitions.
    public const byte TransitionUp   = 0x3D;
    public const byte TransitionDown = 0x3E;
    public const int TransitionUpPayloadLen   = 1 + 4 + 4 + 4 + 4; // kind + xyz + rot
    public const int TransitionDownPayloadLen = 1 + TransitionUpPayloadLen;
    public const byte TransitionKindJump = 0;
    public const byte TransitionKindFastTravel = 1;
    public const byte TransitionKindReload = 2;
    public const byte TransitionKindCatchUp = 3;
    public const byte TransitionKindQuestMove = 4;

    // 3. Down / revive / party wipe.
    public const byte PartyStateUp     = 0x3F;
    public const byte PartyStateDown   = 0x40;
    public const byte ReviveUp         = 0x41;
    public const byte ReviveDown       = 0x42;
    public const byte PartyWipeDown    = 0x43;
    public const byte PartyResumeUp    = 0x44;
    public const byte PartyResumeDown  = 0x45;
    public const int PartyStateUpPayloadLen = 1;
    public const int PartyStateDownPayloadLen = 2;
    public const int ReviveUpPayloadLen = 1;
    public const int ReviveDownPayloadLen = 3; // reviver,target,accepted
    public const int PartyWipeDownPayloadLen = 4;
    public const int PartyResumeUpPayloadLen = 4 + 4 + 4 + 4 + 4;
    public const int PartyResumeDownPayloadLen = 4 + 4 + 4 + 4 + 4;
    public const byte PartyAlive = 0;
    public const byte PartyDowned = 1;
    public const byte PartyReloading = 2;

    // 5. Dialogue spectator.
    public const byte DialogueUp   = 0x46;
    public const byte DialogueDown = 0x47;
    public const int MaxDialogueLabelLen = 96;

    // 7. Per-player crime signal.
    public const byte CrimeUp   = 0x48;
    public const byte CrimeDown = 0x49;
    public const int CrimeUpPayloadLen = 2;
    public const int CrimeDownPayloadLen = 3;
    public const byte CrimeGeneric = 0;
    public const byte CrimeViolence = 1;
    public const byte CrimeTheft = 2;
    public const byte CrimeTrespass = 3;

    // 8. Persistent identity.
    public const byte IdentityUp   = 0x4A;
    public const byte IdentityDown = 0x4B;
    public const int MaxCompanionNameLen = 48;
    public const int MaxBackgroundLen = 48;

    // 10 + inventory: two-sided atomic-ish trade session.
    public const byte TradeBeginUp    = 0x4C;
    public const byte TradeStateDown  = 0x4D;
    public const byte TradeOfferUp    = 0x4E;
    public const byte TradeAcceptUp   = 0x4F;
    public const byte TradeCommitDown = 0x50;
    public const byte TradeCancelUp   = 0x51;
    public const byte TradeCancelDown = 0x52;
    public const int TradeBeginUpPayloadLen = 1;
    public const int TradeAcceptUpPayloadLen = 4;
    public const int TradeCancelUpPayloadLen = 4;
    public const int MaxTradeItems = 24;
    public const int TradeItemLen = ItemClassLen + 2 + 4; // class, amount, health
    public const int MaxTradeOfferPayloadLen = 4 + 4 + 1 + MaxTradeItems * TradeItemLen;

    // Small party UI/ping marker.
    public const byte PartyPingUp   = 0x53;
    public const byte PartyPingDown = 0x54;
    public const int PartyPingUpPayloadLen = 4 + 4 + 4;
    public const int PartyPingDownPayloadLen = 1 + PartyPingUpPayloadLen;
}
