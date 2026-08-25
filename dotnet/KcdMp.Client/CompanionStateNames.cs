namespace KcdMp.Client;

public static class CompanionStateNames
{
    public static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "health", "stamina",
        "strength", "agility", "vitality", "warfare",
        "sword", "axe", "mace", "bow", "marksmanship",
        "unarmed", "thievery", "stealth", "survival",
        "craftsmanship", "scholarship", "speech"
    };
}
