using ChatVerse.API.Models.Games;
using System.Security.Cryptography;

namespace ChatVerse.API.Services.Games;

// ============================================================
//  AmbientQuestionProvider — supplies the "background trivia"
//  questions that AmbientQuestionService pushes into gameable
//  chat rooms every few minutes.
//
//  Two modes per emission:
//   • MCQ        — reuses QuizQuestionProvider (real trivia + options)
//   • Discussion — pulls from a curated bank of open-ended prompts
//                  designed to spark conversation. No "right answer";
//                  the goal is to seed the chat with something
//                  interesting to riff on.
//
//  Why a hardcoded discussion bank instead of LLM-generated?
//    LLM calls have cost + latency and we'd be spending tokens on
//    every dyno every few minutes regardless of chat activity.
//    Hand-curated prompts ship instantly and read well. We can
//    swap in an AI fallback later if the bank feels stale.
// ============================================================

public sealed class AmbientQuestionProvider
{
    private readonly QuizQuestionProvider _quiz;
    private readonly ILogger<AmbientQuestionProvider> _logger;

    public AmbientQuestionProvider(QuizQuestionProvider quiz, ILogger<AmbientQuestionProvider> logger)
    {
        _quiz = quiz;
        _logger = logger;
    }

    /// <summary>
    /// Returns the next ambient question. The flavour split is
    /// roughly 60/40 toward MCQ — visual variety beats pure prose,
    /// but discussion prompts are higher-engagement when they land
    /// so we don't bury them under quizzes.
    /// </summary>
    public async Task<AmbientQuestion?> NextAsync(CancellationToken ct)
    {
        var roll = RandomNumberGenerator.GetInt32(100);
        if (roll < 60)
        {
            var mcq = await TryFetchMcqAsync(ct);
            if (mcq is not null) return mcq;
        }
        // Either we rolled discussion OR MCQ fetch failed. Either
        // way, lean on the hardcoded bank — it's always available.
        return PickDiscussion();
    }

    // ─── MCQ via the existing quiz pipeline ────────────────────────
    private async Task<AmbientQuestion?> TryFetchMcqAsync(CancellationToken ct)
    {
        try
        {
            var qs = await _quiz.FetchAsync(QuizCategory.Any, QuizDifficulty.Easy, 1, ct);
            if (qs.Count == 0) return null;
            var q = qs[0];
            return new AmbientQuestion(
                Id: q.Id,
                Mode: AmbientQuestionMode.Mcq,
                Text: q.Question,
                Options: q.Options,
                Category: q.Category,
                EmittedAtUtc: DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ambient MCQ fetch failed; falling back to discussion");
            return null;
        }
    }

    // ─── Discussion bank ───────────────────────────────────────────
    //
    // Themes intentionally span travel, food, tech, philosophy,
    // pop culture, & "would you rather" so the room never sees the
    // same vibe twice in a row. Keep entries to one short line —
    // long prompts bury the engagement.

    private static readonly string[] DiscussionBank = new[]
    {
        "If you could live in any country for a year, which one and why?",
        "What's one food from your hometown that the world doesn't know about yet?",
        "Hot take: physical books or e-books — which actually wins?",
        "What's the most overrated movie everyone seems to love?",
        "If you had unlimited budget, what's the first thing you'd build?",
        "Which language would you learn next, and why?",
        "What's a small daily habit that genuinely changed your life?",
        "Pineapple on pizza — yes, no, or only in specific contexts?",
        "Best invention of the 21st century — argue your pick.",
        "Which city has the best public transport you've ever used?",
        "What's your favourite underrated festival from anywhere in the world?",
        "If you could swap careers for a month, what would you try?",
        "What's a skill you wish was taught in schools but isn't?",
        "Most beautiful sunrise/sunset you've ever seen — where?",
        "If aliens visited Earth tomorrow, which country would you want them to land in?",
        "What's one local custom that travellers always get wrong?",
        "Best childhood snack that you'd still defend today?",
        "Which historical figure would you want to have dinner with?",
        "If you had to delete one app from your phone forever, which one?",
        "Coffee or chai — and which exact preparation?",
        "What's a piece of advice you'd tell your past self three years ago?",
        "Which country's street food deserves more global hype?",
        "Best concert/live performance you've ever been to?",
        "What's a hobby you've always wanted to try but haven't?",
        "If you could fix one thing about your city, what would it be?",
        "Books that everyone says they love but you secretly couldn't finish?",
        "Best ₹500 (or $5) you've ever spent — what was it?",
        "Most underrated tourist spot in your country?",
        "What's a tradition from your culture that you wish more people knew about?",
        "Which fictional world would you want to live in for a week?",
    };

    private AmbientQuestion PickDiscussion()
    {
        var idx = RandomNumberGenerator.GetInt32(DiscussionBank.Length);
        return new AmbientQuestion(
            Id: Guid.NewGuid().ToString("N")[..12],
            Mode: AmbientQuestionMode.Discussion,
            Text: DiscussionBank[idx],
            Options: null,
            Category: "Around the world",
            EmittedAtUtc: DateTime.UtcNow);
    }
}
