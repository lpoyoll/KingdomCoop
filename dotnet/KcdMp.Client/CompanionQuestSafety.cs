using System.Text.RegularExpressions;

namespace KcdMp.Client;

/// <summary>
/// Conservative policy for guest-authored canonical violence.
///
/// Generic authored names are shareable. Unknown/named story characters are
/// protected from a guest delivering a canonical killing blow. Henry's own
/// local game remains authoritative and can still kill anything the campaign
/// itself allows.
/// </summary>
public static partial class CompanionQuestSafety
{
    public static bool GuestMayAuthorDamage(string npcName, float healthLoss,
                                            float? knownHealth)
    {
        if (GenericNpcRegex().IsMatch(npcName)) return true;
        if (knownHealth is null) return false;
        // Unknown named NPC: allow scuffling but never guest-authored lethal
        // damage. The host may still legitimately finish the fight.
        return knownHealth.Value - healthLoss > 1f;
    }

    [GeneratedRegex(@"^(?:ttkc_(?:man|woman)_\d+|.*(?:bandit|guard|soldier|mercenary|cumans?|animal|wolf|boar|hare).*)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex GenericNpcRegex();
}
