using System.Security.Cryptography;

namespace ChatVerse.API.Services.Games;

// ============================================================
//  LocalJokesBank — fallback when icanhazdadjoke is unreachable.
//
//  Same architectural pattern as LocalQuizBank: a hand-curated
//  set that ships inside the binary, ensuring the show goes on
//  if the upstream API hiccups or rate-limits us.
//
//  Content guidelines for additions:
//   • PG / family-friendly — Jokes mode is the casual, low-stakes
//     gateway to ChatVerse gaming, can't risk an offensive joke
//     surfacing on a school's network the moment WiFi flickers.
//   • Short — long jokes don't fit the 30s reaction window;
//     readers should finish in 10s and have 20s to react.
//   • Universal — avoid US-only references; ChatVerse skews
//     India + global, so dad jokes & wordplay travel better than
//     pop-culture references.
// ============================================================

public static class LocalJokesBank
{
    private static JokeItem J(string text) => new JokeItem
    {
        Id = Guid.NewGuid().ToString("N")[..12],
        Text = text,
    };

    public static readonly IReadOnlyList<JokeItem> All = new List<JokeItem>
    {
        J("I told my wife she was drawing her eyebrows too high. She looked surprised."),
        J("Why don't scientists trust atoms? Because they make up everything."),
        J("I'm reading a book about anti-gravity. It's impossible to put down."),
        J("Did you hear about the mathematician who's afraid of negative numbers? He'll stop at nothing to avoid them."),
        J("Why did the scarecrow win an award? He was outstanding in his field."),
        J("I would tell you a joke about an elevator, but it's an uplifting experience."),
        J("Parallel lines have so much in common. It's a shame they'll never meet."),
        J("I used to play piano by ear. Now I use my hands."),
        J("Why don't skeletons fight each other? They don't have the guts."),
        J("What do you call a fake noodle? An impasta."),
        J("I'm on a seafood diet. I see food and I eat it."),
        J("Why did the bicycle fall over? Because it was two-tired."),
        J("How do you organize a space party? You planet."),
        J("I told my computer I needed a break. Now it won't stop sending me Kit Kat ads."),
        J("Why was the math book sad? It had too many problems."),
        J("What's the best thing about Switzerland? I don't know, but the flag is a big plus."),
        J("Why don't eggs tell jokes? They'd crack each other up."),
        J("I'm terrified of elevators. I'm going to start taking steps to avoid them."),
        J("What do you call a sleeping bull? A bulldozer."),
        J("Why did the coffee file a police report? It got mugged."),
        J("I asked my dog what's two minus two. He said nothing."),
        J("Why did the gym close down? It just didn't work out."),
        J("I'm reading a book about teleportation. It's bound to take me places."),
        J("What did the ocean say to the shore? Nothing, it just waved."),
        J("Why did the cookie cry? Because its mom was a wafer so long."),
        J("I tried to catch fog yesterday. Mist."),
        J("Why don't scientists trust stairs? They're always up to something."),
        J("What do you call cheese that isn't yours? Nacho cheese."),
        J("I told my suitcases there'd be no vacation this year. Now I'm dealing with emotional baggage."),
        J("Why did the picture go to jail? Because it was framed."),
    };

    /// <summary>Pick <paramref name="count"/> distinct jokes, randomised.</summary>
    public static List<JokeItem> Pick(int count)
    {
        var indices = Enumerable.Range(0, All.Count).ToList();
        var picked = new List<JokeItem>(count);
        for (int i = 0; i < count && indices.Count > 0; i++)
        {
            int idx = RandomNumberGenerator.GetInt32(indices.Count);
            picked.Add(All[indices[idx]]);
            indices.RemoveAt(idx);
        }
        // Re-id so two rooms drawing from the same bank don't collide.
        return picked.Select(j => new JokeItem
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Text = j.Text,
        }).ToList();
    }
}

/// <summary>
/// Server-side joke item. Persisted to Redis as part of session state.
/// Public so JokesSession + JokesProvider + LocalJokesBank can share.
/// </summary>
public sealed class JokeItem
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
}
