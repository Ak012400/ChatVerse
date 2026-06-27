using ChatVerse.API.Extensions;
using ChatVerse.API.Models;
using ChatVerse.Domain.Entities;
using ChatVerse.Infrastructure.ExternalServices.AI;
using ChatVerse.Infrastructure.ExternalServices.Email;
using ChatVerse.Infrastructure.Persistence.PostgreSQL;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatVerse.API.Controllers;

/// <summary>
/// AI support assistant + ticket pipeline.
///
///   POST /api/support/ask     — user asks a question, LLM answers
///                                with comprehensive ChatVerse knowledge.
///                                Multi-turn supported via Messages[].
///   POST /api/support/ticket  — user creates a support ticket; we email
///                                the support inbox with their identity
///                                stamped in.
///
/// Uses the same AiChatProvider as TranslateController (Groq → Gemini
/// fallback chain). System prompt covers every locked product feature.
/// </summary>
[ApiController]
[Route("api/support")]
[Authorize]
public class SupportController : ControllerBase
{
    private readonly AiChatProvider _ai;
    private readonly BrevoEmailService _email;
    private readonly ChatVerseDbContext _db;
    private readonly ILogger<SupportController> _logger;

    public SupportController(
        AiChatProvider ai,
        BrevoEmailService email,
        ChatVerseDbContext db,
        ILogger<SupportController> logger)
    {
        _ai = ai;
        _email = email;
        _db = db;
        _logger = logger;
    }

    // ============================================================
    //  Knowledge base (system prompt)
    //
    //  The single source of truth the LLM consults. Keep it concise
    //  but feature-complete — adding a new feature means updating this
    //  block. Avoid version numbers / dates so it doesn't go stale.
    // ============================================================
    // NOTE: `static readonly` (not `const`) because the .Trim() call at
    // the end of the literal is evaluated at runtime — const requires
    // a compile-time constant expression.
    private static readonly string SystemPrompt = @"
You are ChatVerse Assistant — a helpful, friendly support AI inside the ChatVerse app.
You answer in the user's language (Hindi, English, Hinglish), keep replies short
(2-5 sentences unless asked for detail), and never invent features that don't exist.

If the user reports a bug or asks for human help, end your reply with:
  ""If you'd like, I can create a support ticket for you.""
…so the UI knows to surface the ticket form.

About ChatVerse (the product):
ChatVerse is a real-time anonymous chat + video platform with AI moderation. Core surfaces:

CHAT — themed lounges (General, Gaming, Music, Tech, Random). Each room has
  AI moderation, message reactions, image upload with on-device NSFW scan,
  live captions + voice translation (Hindi ↔ English).

VIDEO — 4 modes:
  • Random 1-on-1 (Omegle-style)
  • Random group (up to 6)
  • Hosted group (up to 50, room name shareable)
  • Direct invite (call any user by username)
  Plus a Theater watch-party mode (dual: YouTube/Vimeo iframe co-browse OR
  LiveKit screen-share for Netflix/Prime). Captions + voice TTS work in calls.

EMBEDDED GAMES — Chess (with director mode + reconnect grace),
  Ludo (server-authoritative dice), Quiz (with rolling #general quiz +
  daily leaderboard), Jokes. Launch from any gameable lounge.

DAILY / WEEKLY FEATURES:
  • Time Capsule — write a message, delivered to a random user 7/14/30 days later.
  • Persona Roulette — every day you get a new anonymous persona; chat as it,
    build streaks, optionally mutually unmask after 7 days.
  • Story Chain — daily collaborative story, one sentence per user, max 50.
  • Confession Box — anonymous daily confessions, react with 6 emojis,
    top-of-day author gets a reveal-or-stay-ghost choice.
  • Ghost Date — Thursday 9pm IST anonymous 30-min text date, decision at end.
  • Love Triangle — Sunday 10pm IST 3-person weekly drama with audience voting.
  • The Cipher — community ARG, weekly. Members get phrase fragments, hunters
    submit guesses with accuracy scoring.
  • PYAAR LIVE — Saturday 8pm IST flagship mass dating show: 10 couples,
    4 rounds, mid-show elimination, top 3 winners by spectator votes.

MEHFIL (host-run rooms with 2 locked templates):
  • Debate — 5v5 stage (Pro/Con), real-time mic rotation 60/90/120s per turn,
    5/10 min rounds, audience can chat + raise hand to challenge speakers.
    Public mode auto-seats nominees; private mode is invite-code gated;
    server picks random topic from a bank if host doesn't provide one.
  • Roast — 5v5 stage (Roasters/Roastees), same mic mechanics, host MUST
    provide a roast subject (no random topic).

TOKEN ECONOMY — 100 token signup bonus, top up via Razorpay (mock gateway
  for now), tip hosts in Mehfil rooms with Rose / Bouquet / Crown gifts.

PRIVACY DNA — Confession Box, Ghost Date, Cipher, and Debate/Roast
  nomination bios are matchmaker / host / monitor-only. Audience never
  sees real names/age/gender unless mutual reveal happens.

If asked anything outside ChatVerse, politely steer back: ""I can help with
ChatVerse questions; for other topics try a general assistant.""
".Trim();

    // ─── /api/support/ask ─────────────────────────────────────

    [HttpPost("ask")]
    public async Task<IActionResult> Ask([FromBody] SupportAskRequest req, CancellationToken ct)
    {
        if (req.Messages is null || req.Messages.Count == 0)
            return BadRequest(ApiResponse.Fail("No messages"));
        if (req.Messages.Count > 20)
            return BadRequest(ApiResponse.Fail("Conversation too long; start a new ticket."));
        if (req.Messages.Any(m => string.IsNullOrWhiteSpace(m.Content) || m.Content.Length > 2000))
            return BadRequest(ApiResponse.Fail("Each message must be 1-2000 chars."));

        // Prepend system prompt; reject anything that tries to override it.
        var msgs = new List<AiChatProvider.ChatMessage>
        {
            new("system", SystemPrompt),
        };
        foreach (var m in req.Messages)
        {
            // Only honour the two roles we actually use; ignore "system"
            // from the client (the user shouldn't be able to set policy).
            var role = string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? "assistant" : "user";
            msgs.Add(new(role, m.Content.Trim()));
        }

        try
        {
            var reply = await _ai.CompleteAsync(
                new AiChatProvider.ChatRequest(msgs, Temperature: 0.5, MaxTokens: 400),
                ct);
            if (string.IsNullOrWhiteSpace(reply))
            {
                return Ok(ApiResponse<object>.Ok(new
                {
                    reply = "Sorry, my AI provider is busy right now. Try again in a moment, or create a support ticket and the team will email you back.",
                    offerTicket = true,
                }));
            }
            // Lightweight heuristic: if the model offered a ticket
            // (or the user is clearly stuck), surface the ticket CTA.
            var offerTicket =
                reply.Contains("create a support ticket", StringComparison.OrdinalIgnoreCase) ||
                reply.Contains("email the team", StringComparison.OrdinalIgnoreCase) ||
                reply.Contains("create a ticket", StringComparison.OrdinalIgnoreCase);
            return Ok(ApiResponse<object>.Ok(new { reply, offerTicket }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Support ask failed");
            return StatusCode(500, ApiResponse.Fail("AI service is unavailable. Please create a ticket."));
        }
    }

    // ─── /api/support/ticket ──────────────────────────────────

    [HttpPost("ticket")]
    public async Task<IActionResult> Ticket([FromBody] SupportTicketRequest req, CancellationToken ct)
    {
        var subject = (req.Subject ?? "").Trim();
        var body    = (req.Body ?? "").Trim();
        if (subject.Length is < 4 or > 200)
            return BadRequest(ApiResponse.Fail("Subject must be 4-200 chars."));
        if (body.Length is < 10 or > 8000)
            return BadRequest(ApiResponse.Fail("Body must be 10-8000 chars."));

        var meId = JwtService.GetUserId(User);
        var user = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == meId, ct);
        if (user is null) return Unauthorized();

        var ok = await _email.SendSupportTicketAsync(
            userEmail: user.Email ?? "(no email on file)",
            username:  user.Username,
            userId:    meId.ToString(),
            subject:   subject,
            body:      body);

        if (!ok)
        {
            _logger.LogError("Support ticket email failed for user {UserId}", meId);
            return StatusCode(500, ApiResponse.Fail("Could not send your ticket. Please try again."));
        }
        return Ok(ApiResponse<object>.Ok(new
        {
            ok = true,
            message = "Ticket sent. We'll email you back at the address on your account.",
        }));
    }
}

public record SupportAskMessage(string Role, string Content);
public record SupportAskRequest(IReadOnlyList<SupportAskMessage> Messages);
public record SupportTicketRequest(string Subject, string Body);
