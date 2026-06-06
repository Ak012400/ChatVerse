using ChatVerse.API.Extensions;
using ChatVerse.API.Models;
using ChatVerse.Infrastructure.ExternalServices.AI;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
    private readonly ILogger<TranslateController> _logger;

    // Whitelist of language codes we know our prompt handles well.
    // Anything else is rejected with 400 rather than passed through.
    private static readonly HashSet<string> SupportedTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        "en", "hi", "ta", "te", "bn", "mr", "kn", "ml", "gu", "pa",
        "ur", "ar", "es", "fr", "de", "ja", "zh", "ko", "ru", "pt", "it",
    };

    public TranslateController(AiChatProvider ai, ILogger<TranslateController> logger)
    {
        _ai = ai;
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
            new("user", req.Text.Trim()),
        };

        var translated = await _ai.CompleteAsync(
            new AiChatProvider.ChatRequest(msgs, Temperature: 0.2, MaxTokens: 400),
            ct);

        if (string.IsNullOrWhiteSpace(translated))
            return StatusCode(503, ApiResponse.Fail("Translation provider unavailable"));

        return Ok(ApiResponse<object>.Ok(new
        {
            translated = translated.Trim(),
            targetLang = req.TargetLang,
        }));
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
