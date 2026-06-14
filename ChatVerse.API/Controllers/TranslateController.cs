using ChatVerse.API.Extensions;
using ChatVerse.API.Models;
using ChatVerse.Infrastructure.ExternalServices.AI;
using ChatVerse.Infrastructure.Persistence.Redis;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;
using System.Text;

namespace ChatVerse.API.Controllers;

/// <summary>
/// Per-message translation. The chat UI renders a tiny translate icon
/// under any message whose detected language differs from the reader's
/// preferred UI language; clicking it POSTs the original text and the
/// target language code here, and we return the translation.
///
/// Translation runs through AiChatProvider, which fans out across the
/// same Groq → Gemini fallback chain used elsewhere — no separate
/// translation provider needed, and the same per-day quota covers all
/// AI features together.
///
/// Authenticated only — anonymous translation would be free abuse bait.
/// </summary>
[ApiController]
[Route("api/translate")]
[Authorize]
public class TranslateController : ControllerBase
{
    private readonly AiChatProvider _ai;
    private readonly RedisService _redis;
    private readonly ILogger<TranslateController> _logger;

    // Whitelist of language codes we know our prompt handles well.
    // Anything else is rejected with 400 rather than passed through.
    private static readonly HashSet<string> SupportedTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        "en", "hi", "ta", "te", "bn", "mr", "kn", "ml", "gu", "pa",
        "ur", "ar", "es", "fr", "de", "ja", "zh", "ko", "ru", "pt", "it",
    };

    // 24h cache TTL — translation of "hello" into Hindi today is the
    // same tomorrow. Voice captions repeat phrases constantly ("haan",
    // "okay", "thank you") so this saves a huge chunk of Groq calls.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    public TranslateController(
        AiChatProvider ai,
        RedisService redis,
        ILogger<TranslateController> logger)
    {
        _ai = ai;
        _redis = redis;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Translate([FromBody] TranslateRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Text))
            return BadRequest(ApiResponse.Fail("Empty text"));
        if (req.Text.Length > 2000)
            return BadRequest(ApiResponse.Fail("Text too long (max 2000 chars)"));
        if (string.IsNullOrWhiteSpace(req.TargetLang) || !SupportedTargets.Contains(req.TargetLang))
            return BadRequest(ApiResponse.Fail("Unsupported target language"));

        var trimmed = req.Text.Trim();
        var targetLower = req.TargetLang.ToLowerInvariant();

        // ── Redis cache check ─────────────────────────────────────
        //  Key is a SHA-1 of `{trimmed}|{targetLower}` so very long
        //  inputs don't blow up Redis key sizes. Collisions at SHA-1
        //  are astronomically rare for this use case.
        var cacheKey = $"tx:cache:{targetLower}:{Sha1(trimmed)}";
        var cached = await _redis.GetStringAsync(cacheKey);
        if (!string.IsNullOrEmpty(cached))
        {
            return Ok(ApiResponse<object>.Ok(new
            {
                translated = cached,
                targetLang = req.TargetLang,
                cached = true,
            }));
        }

        // Keep the prompt small and direct — translation models do better
        // with concrete instruction than verbose framing. Asking for ONLY
        // the translation suppresses the model's tendency to add prefaces.
        var languageName = LanguageName(req.TargetLang);
        var system =
            $"You are a translation service. Translate user text into {languageName}. " +
            $"Reply with ONLY the translated text — no quotes, no preamble, no notes, no romanisation.";

        var msgs = new List<AiChatProvider.ChatMessage>
        {
            new("system", system),
            new("user", trimmed),
        };

        var translated = await _ai.CompleteAsync(
            new AiChatProvider.ChatRequest(msgs, Temperature: 0.2, MaxTokens: 400),
            ct);

        if (string.IsNullOrWhiteSpace(translated))
            return StatusCode(503, ApiResponse.Fail("Translation provider unavailable"));

        var clean = translated.Trim();
        // Fire-and-forget cache write — keeps the response fast.
        _ = _redis.SetStringAsync(cacheKey, clean, CacheTtl);

        return Ok(ApiResponse<object>.Ok(new
        {
            translated = clean,
            targetLang = req.TargetLang,
            cached = false,
        }));
    }

    private static string Sha1(string input)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string LanguageName(string code) => code.ToLowerInvariant() switch
    {
        "en" => "English",
        "hi" => "Hindi",
        "ta" => "Tamil",
        "te" => "Telugu",
        "bn" => "Bengali",
        "mr" => "Marathi",
        "kn" => "Kannada",
        "ml" => "Malayalam",
        "gu" => "Gujarati",
        "pa" => "Punjabi",
        "ur" => "Urdu",
        "ar" => "Arabic",
        "es" => "Spanish",
        "fr" => "French",
        "de" => "German",
        "ja" => "Japanese",
        "zh" => "Mandarin Chinese",
        "ko" => "Korean",
        "ru" => "Russian",
        "pt" => "Portuguese",
        "it" => "Italian",
        _ => "English",
    };
}

public record TranslateRequest(
    [System.ComponentModel.DataAnnotations.Required] string Text,
    [System.ComponentModel.DataAnnotations.Required] string TargetLang
);
