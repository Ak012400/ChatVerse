namespace ChatVerse.API.Services.StoryChain;

// ============================================================
//  StoryPromptBank — curated opening lines for daily chains.
//
//  Picked to be open-ended (lots of possible directions), grounded
//  in something concrete (a scene, an object, a feeling) and not
//  topic-locked (no "tell me about a politician" type prompts).
//  Length stays under ~140 chars so the prompt + first user's
//  sentence still fits a single phone screen.
// ============================================================

public static class StoryPromptBank
{
    public static readonly string[] Prompts = new[]
    {
        "The lift opened on a floor that wasn't supposed to exist.",
        "She unfolded the postcard for the third time that week.",
        "There were exactly seven keys on the windowsill, and none of them were his.",
        "The coffee was still warm when the lights cut out across the street.",
        "He hadn't expected the letter to arrive in another language.",
        "Somewhere between the third and fourth song, she realised she had lied.",
        "The cat on the staircase was wearing a small silver collar with no name.",
        "It started raining the moment the meeting was supposed to end.",
        "Nobody in the carriage was looking at the man with the violin case.",
        "She found the note tucked inside a library book due in 1994.",
        "The neighbour's wifi was named after a city she had never visited.",
        "His phone died at exactly the wrong second.",
        "The wedding photographer caught something nobody else had noticed.",
        "There was a knock on the door at 3am, and then a second one.",
        "She had two minutes to decide whether to send the message.",
        "He realised the painting in his uncle's house was facing the wrong wall.",
        "The taxi driver hadn't said a word for forty minutes.",
        "She bookmarked the wrong page, and that turned out to matter.",
        "It was the quietest train station he had ever stood in.",
        "The recipe called for one ingredient she could not pronounce.",
        "He woke up wearing a watch he didn't own.",
        "There was a single matchbox on the kitchen counter, and she was sure she had thrown them all away.",
        "The boy on the bicycle had been circling the block for an hour.",
        "She decided to answer the unknown number this time.",
        "He had been writing the same line for three days now.",
        "The waiter slid a folded napkin across the table with the cheque.",
        "Her grandmother left her a sealed envelope and one instruction.",
        "The barista spelled his name correctly for the first time, and somehow that scared him.",
        "She caught the reflection in the mirror of someone who wasn't behind her.",
        "The runner stopped at the bridge because somebody was already standing there.",
    };

    /// <summary>Deterministic per-date pick — same prompt every time
    /// the service retries for the same IST date.</summary>
    public static string PickForDate(string istDate)
    {
        // FNV-1a hash → index. Avoids string.GetHashCode randomisation.
        unchecked
        {
            uint hash = 2166136261;
            foreach (var c in istDate)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return Prompts[(int)(hash % (uint)Prompts.Length)];
        }
    }
}
