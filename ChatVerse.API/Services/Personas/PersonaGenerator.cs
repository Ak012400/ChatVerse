using ChatVerse.Domain.Entities;

namespace ChatVerse.API.Services.Personas;

// ============================================================
//  PersonaGenerator — pure deterministic-ish persona factory.
//
//  Given a real user id + a date string, returns a Persona with
//  display name / avatar seed / bio / mood populated. The reset
//  service calls this once per active user each midnight UTC.
//
//  Why not just System.Random?
//    Same user + same day = same persona, even if we have to
//    re-generate (idempotent retries are cheap). Hash the
//    (userId, date) tuple into an int seed and use that. This
//    also lets us regenerate any past persona for moderation
//    review without needing to have stored every word choice.
// ============================================================

public static class PersonaGenerator
{
    // ── Word lists — kept short on purpose. Curated for "feels
    //   like a person, not a username generator". Easy to extend.
    private static readonly string[] Adjectives = new[]
    {
        "Velvet", "Midnight", "Saffron", "Lantern", "Ember",
        "Glacier", "Marble", "Cobalt", "Origami", "Hazel",
        "Nimbus", "Indigo", "Amber", "Driftwood", "Mosaic",
        "Crimson", "Echo", "Ivory", "Silken", "Vesper",
        "Wandering", "Quiet", "Salt", "Rumour", "Pomegranate",
    };

    private static readonly string[] Nouns = new[]
    {
        "Comet", "Lighthouse", "Pilgrim", "Apricot", "Atlas",
        "Sonnet", "Orbit", "Mirror", "Heron", "Compass",
        "Cinder", "Voyager", "Nightjar", "Cardinal", "Lantern",
        "Postcard", "Footnote", "Telegram", "Sundial", "Folio",
        "Carousel", "Postscript", "Garland", "Almanac", "Refrain",
    };

    // Moods double as a UX accent — the client maps "playful"/"wistful"
    // etc. to a hue on the persona card. Keep the list short.
    private static readonly string[] Moods = new[]
    {
        "playful", "wistful", "curious", "restless", "candid",
        "dreamy", "wry", "tender", "mischievous", "earnest",
    };

    private static readonly string[] Bios = new[]
    {
        "Talks to strangers like they're old friends.",
        "Collects sunsets and bad puns.",
        "Believes every conversation is a small adventure.",
        "Reads the last page first. Sometimes.",
        "Currently writing a letter they'll never send.",
        "Mostly here for the unexpected questions.",
        "Carries a notebook of half-finished thoughts.",
        "Thinks 2am has the best conversations.",
        "Trades stories like postcards.",
        "Looking for the kind of small talk that isn't small.",
        "Half curious, half hiding. Mostly the first one.",
        "Tries to leave every conversation a little kinder.",
    };

    /// <summary>
    /// Build a persona for (userId, dateUtc). Deterministic — calling
    /// twice with the same inputs returns the same fields.
    /// </summary>
    public static Persona Generate(string userId, DateTime dateUtc)
    {
        var date = dateUtc.ToString("yyyy-MM-dd");
        // Seed off (userId + date) so distinct days = distinct personas
        // for the same user, but same-day retries are stable.
        var seed = StableHash($"{userId}|{date}");
        var rng = new Random(seed);

        var adj  = Adjectives[rng.Next(Adjectives.Length)];
        var noun = Nouns[rng.Next(Nouns.Length)];
        var mood = Moods[rng.Next(Moods.Length)];
        var bio  = Bios[rng.Next(Bios.Length)];

        // Avatar seed — dicebear-compatible string. Include date so the
        // image changes daily even though the deterministic seed could
        // technically be reused.
        var avatarSeed = $"{adj}-{noun}-{date}".ToLowerInvariant();

        // ExpiresAt = next 00:00 UTC after `dateUtc`.
        var midnight = new DateTime(dateUtc.Year, dateUtc.Month, dateUtc.Day, 0, 0, 0, DateTimeKind.Utc);
        var expires = midnight.AddDays(1);

        return new Persona
        {
            UserId      = userId,
            Date        = date,
            DisplayName = $"{adj} {noun}",
            AvatarSeed  = avatarSeed,
            Bio         = bio,
            Mood        = mood,
            ExpiresAt   = expires,
            CreatedAt   = DateTime.UtcNow,
        };
    }

    // FNV-1a 32-bit. Deterministic across processes (unlike .NET's
    // randomised string GetHashCode), and cheap. Same input → same int.
    private static int StableHash(string s)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var c in s)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return (int)hash;
        }
    }
}
