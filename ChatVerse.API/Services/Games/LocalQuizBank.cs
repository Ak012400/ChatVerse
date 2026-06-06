using ChatVerse.API.Models.Games;
using System.Security.Cryptography;

namespace ChatVerse.API.Services.Games;

// ============================================================
//  LocalQuizBank — graceful degradation source.
//
//  OpenTriviaDB is free and reliable BUT it has rate limits (5 req/IP/5s)
//  and occasional downtime. Without a fallback, the first time the
//  API hiccups every quiz in production would silently fail to start.
//
//  This bank covers that case. 30 hand-curated questions spanning the
//  same categories OpenTriviaDB exposes. Not enough for sustained play,
//  but enough that one or two rounds always work even if external
//  trivia is unavailable.
//
//  NOTE: questions are tagged with their original difficulty/category
//  so we can still filter when the caller requests "Hard / Science".
//  If a filter doesn't have enough matches, we expand to any matching
//  category at the requested difficulty, then any at any difficulty —
//  so the fallback never returns fewer questions than asked for.
// ============================================================

public static class LocalQuizBank
{
    private static QuizQuestion Q(
        string category, string difficulty, string question,
        string correct, string a, string b, string c)
    {
        var opts = new List<string> { correct, a, b, c };
        // Fisher-Yates with CSPRNG so the correct answer doesn't always
        // sit at index 0 — important because clients see the shuffled list.
        for (int i = opts.Count - 1; i > 0; i--)
        {
            int j = RandomNumberGenerator.GetInt32(i + 1);
            (opts[i], opts[j]) = (opts[j], opts[i]);
        }
        return new QuizQuestion(
            Id: Guid.NewGuid().ToString("N").Substring(0, 12),
            Category: category,
            Difficulty: difficulty,
            Question: question,
            Options: opts,
            CorrectIndex: opts.IndexOf(correct));
    }

    public static readonly IReadOnlyList<QuizQuestion> All = new List<QuizQuestion>
    {
        // ── General Knowledge ─────────────────────────────────────
        Q("General Knowledge", "easy",
            "What is the largest planet in our solar system?",
            correct: "Jupiter", a: "Saturn", b: "Earth", c: "Neptune"),
        Q("General Knowledge", "easy",
            "How many continents are there?",
            correct: "7", a: "5", b: "6", c: "8"),
        Q("General Knowledge", "medium",
            "Which country has the longest coastline in the world?",
            correct: "Canada", a: "Russia", b: "Indonesia", c: "Australia"),

        // ── Science ───────────────────────────────────────────────
        Q("Science", "easy",
            "What is the chemical symbol for gold?",
            correct: "Au", a: "Ag", b: "Gd", c: "Go"),
        Q("Science", "medium",
            "What is the speed of light in a vacuum (approx)?",
            correct: "300,000 km/s", a: "150,000 km/s", b: "500,000 km/s", c: "30,000 km/s"),
        Q("Science", "hard",
            "Which subatomic particle has no electric charge?",
            correct: "Neutron", a: "Proton", b: "Electron", c: "Positron"),

        // ── Geography ─────────────────────────────────────────────
        Q("Geography", "easy",
            "What is the capital of Australia?",
            correct: "Canberra", a: "Sydney", b: "Melbourne", c: "Perth"),
        Q("Geography", "easy",
            "Which river flows through Paris?",
            correct: "Seine", a: "Thames", b: "Rhine", c: "Danube"),
        Q("Geography", "medium",
            "Mount Kilimanjaro is located in which country?",
            correct: "Tanzania", a: "Kenya", b: "Ethiopia", c: "Uganda"),

        // ── History ───────────────────────────────────────────────
        Q("History", "easy",
            "In which year did World War II end?",
            correct: "1945", a: "1944", b: "1946", c: "1943"),
        Q("History", "medium",
            "Who was the first President of the United States?",
            correct: "George Washington", a: "Thomas Jefferson", b: "John Adams", c: "Benjamin Franklin"),
        Q("History", "hard",
            "Which empire was ruled by Genghis Khan?",
            correct: "Mongol Empire", a: "Ottoman Empire", b: "Roman Empire", c: "Persian Empire"),

        // ── Film ──────────────────────────────────────────────────
        Q("Film", "easy",
            "Who directed the movie 'Jurassic Park' (1993)?",
            correct: "Steven Spielberg", a: "George Lucas", b: "James Cameron", c: "Ridley Scott"),
        Q("Film", "medium",
            "Which actor played Iron Man in the Marvel Cinematic Universe?",
            correct: "Robert Downey Jr.", a: "Chris Evans", b: "Mark Ruffalo", c: "Chris Hemsworth"),

        // ── Music ─────────────────────────────────────────────────
        Q("Music", "easy",
            "How many strings does a standard guitar have?",
            correct: "6", a: "4", b: "5", c: "7"),
        Q("Music", "medium",
            "Which band released the album 'The Dark Side of the Moon'?",
            correct: "Pink Floyd", a: "Led Zeppelin", b: "The Beatles", c: "The Rolling Stones"),

        // ── Sports ────────────────────────────────────────────────
        Q("Sports", "easy",
            "How many players are there in a football (soccer) team on the field?",
            correct: "11", a: "10", b: "12", c: "9"),
        Q("Sports", "medium",
            "In cricket, how many balls are bowled in a standard over?",
            correct: "6", a: "5", b: "7", c: "8"),
        Q("Sports", "hard",
            "Which country has won the most FIFA World Cup titles (as of 2022)?",
            correct: "Brazil", a: "Germany", b: "Italy", c: "Argentina"),

        // ── Computers / Tech ──────────────────────────────────────
        Q("Computers", "easy",
            "What does 'HTTP' stand for?",
            correct: "HyperText Transfer Protocol",
            a: "High Transfer Text Protocol", b: "HyperText Transit Protocol", c: "Home Text Transfer Protocol"),
        Q("Computers", "medium",
            "Who is widely regarded as the inventor of the World Wide Web?",
            correct: "Tim Berners-Lee", a: "Bill Gates", b: "Steve Jobs", c: "Linus Torvalds"),
        Q("Computers", "medium",
            "Which company developed the C# programming language?",
            correct: "Microsoft", a: "Oracle", b: "Sun Microsystems", c: "IBM"),
        Q("Computers", "hard",
            "What is the time complexity of binary search on a sorted array?",
            correct: "O(log n)", a: "O(n)", b: "O(n log n)", c: "O(1)"),

        // ── Books ─────────────────────────────────────────────────
        Q("Books", "easy",
            "Who wrote the Harry Potter series?",
            correct: "J. K. Rowling", a: "J. R. R. Tolkien", b: "George R. R. Martin", c: "Roald Dahl"),
        Q("Books", "medium",
            "Which novel begins with the line 'Call me Ishmael'?",
            correct: "Moby-Dick", a: "The Great Gatsby", b: "Catch-22", c: "Of Mice and Men"),

        // ── Mythology ─────────────────────────────────────────────
        Q("Mythology", "easy",
            "Who is the Greek god of the sea?",
            correct: "Poseidon", a: "Zeus", b: "Hades", c: "Apollo"),
        Q("Mythology", "medium",
            "In Hindu mythology, who is known as the 'destroyer of evil'?",
            correct: "Shiva", a: "Vishnu", b: "Brahma", c: "Ganesha"),

        // ── Animals ───────────────────────────────────────────────
        Q("Animals", "easy",
            "What is the largest mammal on Earth?",
            correct: "Blue whale", a: "African elephant", b: "Sperm whale", c: "Giraffe"),
        Q("Animals", "medium",
            "How many hearts does an octopus have?",
            correct: "3", a: "1", b: "2", c: "4"),

        // ── Politics ──────────────────────────────────────────────
        Q("Politics", "medium",
            "How many permanent members are there in the United Nations Security Council?",
            correct: "5", a: "10", b: "7", c: "15"),
    };

    /// <summary>
    /// Pick <paramref name="count"/> distinct questions, honouring
    /// category/difficulty when there are enough matches. Falls back to
    /// broader pools so we never return fewer than asked.
    /// </summary>
    public static List<QuizQuestion> Pick(QuizCategory category, QuizDifficulty difficulty, int count)
    {
        // Tier 1: exact match
        var diffStr = difficulty == QuizDifficulty.Any ? null : difficulty.ToString().ToLowerInvariant();
        var catStr = category == QuizCategory.Any ? null : CategoryToString(category);

        bool MatchesExact(QuizQuestion q) =>
            (catStr == null || string.Equals(q.Category, catStr, StringComparison.OrdinalIgnoreCase)) &&
            (diffStr == null || string.Equals(q.Difficulty, diffStr, StringComparison.OrdinalIgnoreCase));

        var pool = All.Where(MatchesExact).ToList();

        // Tier 2: relax difficulty if we don't have enough
        if (pool.Count < count && catStr != null)
            pool = All.Where(q => string.Equals(q.Category, catStr, StringComparison.OrdinalIgnoreCase)).ToList();

        // Tier 3: any question at requested difficulty
        if (pool.Count < count && diffStr != null)
            pool = All.Where(q => string.Equals(q.Difficulty, diffStr, StringComparison.OrdinalIgnoreCase)).ToList();

        // Tier 4: any question, period
        if (pool.Count < count)
            pool = All.ToList();

        // Reservoir-pick without replacement using CSPRNG to avoid the
        // predictable order System.Random gives.
        var picked = new List<QuizQuestion>();
        var indices = Enumerable.Range(0, pool.Count).ToList();
        for (int i = 0; i < count && indices.Count > 0; i++)
        {
            int idx = RandomNumberGenerator.GetInt32(indices.Count);
            picked.Add(pool[indices[idx]]);
            indices.RemoveAt(idx);
        }

        // Each picked question gets a fresh Id so callers can use it as
        // a Redis key without two rounds colliding on the same question.
        return picked.Select(q => q with { Id = Guid.NewGuid().ToString("N").Substring(0, 12) }).ToList();
    }

    private static string CategoryToString(QuizCategory c) => c switch
    {
        QuizCategory.General => "General Knowledge",
        QuizCategory.Books => "Books",
        QuizCategory.Film => "Film",
        QuizCategory.Music => "Music",
        QuizCategory.Sports => "Sports",
        QuizCategory.Geography => "Geography",
        QuizCategory.History => "History",
        QuizCategory.Politics => "Politics",
        QuizCategory.Science => "Science",
        QuizCategory.Computers => "Computers",
        QuizCategory.Mythology => "Mythology",
        QuizCategory.Animals => "Animals",
        _ => "",
    };
}
