using System.Security.Cryptography;
using System.Text;

namespace ChatVerse.API.Services.Cipher;

// ============================================================
//  CipherPhraseBank — curated poetic phrases for The Cipher.
//
//  Each phrase is 6-8 words so the per-Member fragment is exactly
//  one word and the puzzle stays decipherable but not trivial.
//  Mix of Hindi/Urdu/English poetic registers per VISION ("Hindi/
//  English/Urdu poetic phrases"). MVP ships 30; the locked roadmap
//  calls for 1000 hand-curated — that's a future content pass.
//
//  Selection is deterministic per ISO week label so retries are
//  idempotent (FNV-1a hash → index).
// ============================================================

public static class CipherPhraseBank
{
    public static readonly string[] Phrases = new[]
    {
        "The river remembers what the sky forgets",
        "Every silence carries the weight of an unsent letter",
        "Moonlight folds itself into the shape of patience",
        "The lantern knows the way the wind doesn’t",
        "We are the rumours we tell ourselves at night",
        "Old songs return like postcards from former cities",
        "A wound is a door that closes both ways",
        "The stranger’s laugh sounds like someone we knew once",
        "Every morning the sea rewrites the shoreline",
        "Some names are heavier on the tongue than others",
        "The map outlives the road it once described",
        "Smoke remembers the shape of the house",
        "Footprints last longer than the people they belonged to",
        "Hope is the slowest weather in any country",
        "The radio plays the song you almost forgot",
        "Cities are libraries of unfinished conversations",
        "Telegrams arrive late but the news arrives early",
        "The lamp burns longer for those who watched it dim",
        "A tea stain on the page outlived the writer",
        "We carry borrowed weather wherever we travel",
        "Every train station is a chapel for almosts",
        "The garden knows your name in three languages",
        "Forgiveness is the longest road home from anywhere",
        "Some windows only open at the wrong hour",
        "The river writes its own forgetting in the sand",
        "A photograph remembers everyone except the one holding it",
        "Even the moon takes a different shape in foreign weather",
        "The bell stops but the echo finds a new house",
        "Letters travel slowly in countries that have stopped writing them",
        "The clock ticks louder in rooms that miss someone",
    };

    /// <summary>FNV-1a hash → deterministic per-week picker. Retries
    /// for the same week label get the same phrase.</summary>
    public static string PickForWeek(string weekLabel)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var c in weekLabel)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return Phrases[(int)(hash % (uint)Phrases.Length)];
        }
    }

    /// <summary>Stable lowercase hash of the canonical phrase — used
    /// for the cheap "exact match?" check before doing similarity.</summary>
    public static string HashPhrase(string phrase)
    {
        var normalised = NormalisePhrase(phrase);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Lower-case, strip punctuation, collapse whitespace.
    /// Same routine used on submission scoring so a Hunter who
    /// got the words right but capitalised differently still wins.</summary>
    public static string NormalisePhrase(string phrase)
    {
        var sb = new StringBuilder(phrase.Length);
        bool lastWasSpace = true;
        foreach (var ch in phrase.ToLowerInvariant())
        {
            if (char.IsLetter(ch) || char.IsDigit(ch))
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true;
            }
            // punctuation skipped
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Word-level Jaccard similarity, 0-100. Cheap, fair
    /// for short phrases. "river remembers sky" vs "the river remembers
    /// what the sky forgets" → high overlap.</summary>
    public static int PhraseSimilarity(string guess, string canonical)
    {
        var g = NormalisePhrase(guess).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var c = NormalisePhrase(canonical).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (g.Count == 0 || c.Count == 0) return 0;
        var inter = g.Intersect(c).Count();
        var union = g.Union(c).Count();
        return union == 0 ? 0 : (int)Math.Round(100.0 * inter / union);
    }

    /// <summary>Symmetric overlap of two user-id sets, 0-100. Returns
    /// the *recall* against the true member set so a Hunter who named
    /// all 7 correctly + 3 extras scores higher than one who named just
    /// 4 correctly with no extras.</summary>
    public static int MemberSetOverlap(IEnumerable<string> named, IEnumerable<string> truth)
    {
        var n = new HashSet<string>(named);
        var t = new HashSet<string>(truth);
        if (t.Count == 0) return 0;
        var matched = n.Intersect(t).Count();
        // Recall is (matched / truth) — % of the actual members the
        // Hunter caught. We don't penalise extra names heavily because
        // the phrase-similarity term already grounds the score.
        return (int)Math.Round(100.0 * matched / t.Count);
    }
}
