using System.Text.RegularExpressions;

namespace RealmDom.World;

/// <summary>
/// Chat is phrase-only: clients send a phrase ID (and optionally a landmark to tag), never text.
/// That makes it impossible to share phone numbers, addresses, links or insults, for players of any age.
/// The client shows the words; the IDs stay the same in every language.
/// </summary>
public static partial class ChatPhrases
{
    public sealed record Phrase(string Id, string Category, string Text, bool NeedsPlace);

    public static readonly IReadOnlyDictionary<string, Phrase> All = new Phrase[]
    {
        new("hi", "social", "Hi!", false), new("gg", "social", "Good game!", false), new("thanks", "social", "Thanks!", false),
        new("sorry", "social", "Sorry!", false), new("nice", "social", "Nice build!", false),
        new("yes", "reply", "Yes", false), new("no", "reply", "No", false), new("omw", "reply", "On my way!", false),
        new("wait", "reply", "Wait for me", false), new("later", "reply", "Can't right now", false),
        new("join", "team", "Join my team?", false), new("teamup", "team", "Let's team up!", false),
        new("canjoin", "team", "Can I join you?", false), new("follow", "team", "Follow me", false),
        new("conquer", "battle", "Help me conquer {place}!", true), new("defend", "battle", "Defend {place}!", true),
        new("raid", "battle", "Raid on {place}, need help!", true), new("fortress", "battle", "Fortress {place}: need a team!", true),
        new("attack", "battle", "Attack {place} in 5 minutes", true), new("meet", "battle", "Meet at {place}", true),
        new("e_up", "emote", "👍", false), new("e_party", "emote", "🎉", false), new("e_shield", "emote", "🛡️", false),
        new("e_swords", "emote", "⚔️", false), new("e_laugh", "emote", "😂", false), new("e_wave", "emote", "👋", false)
    }.ToDictionary(p => p.Id);

    [GeneratedRegex(@"^[\p{L}\p{N} _-]{3,18}$")]
    private static partial Regex NamePattern();

    public static bool IsValidPlayerName(string? name) => name is not null && NamePattern().IsMatch(name);
}

/// <summary>A delivered chat message. Places are public landmarks only; a player's own position is never included.</summary>
public sealed record ChatMessage(string From, string Faction, string PhraseId, string? LandmarkId, string? PlaceName, double? PlaceLat, double? PlaceLon, DateTimeOffset At);
