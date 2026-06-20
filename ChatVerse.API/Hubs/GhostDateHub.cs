using ChatVerse.API.Extensions;
using ChatVerse.API.Services;
using ChatVerse.Domain.Entities;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ChatVerse.API.Hubs;

// ============================================================
//  GhostDateHub — weekly anonymous dating surface.
//
//  Client methods:
//    • Register()             → opt in for the next Thursday 9pm IST
//    • Withdraw()             → withdraw before pairing happens
//    • GetMyStatus()          → registered/matched/in-date/decided/outcome
//    • GetActiveDate()        → live-date snapshot if I'm in one
//    • SendMessage(content)   → write inside the 30-min window
//    • GetThread()            → my own date's thread (auth-gated)
//    • SubmitDecision(reveal) → my post-chat reveal vote
//    • GetMyHistory()         → past dates (anonymised unless mutual_reveal)
//
//  Server-push events (User-scope, NOT group-scope — only my
//  client + the other party's clients get events for our date):
//    • GhostDateMatched   — pairing tick just placed me with someone
//    • GhostDateMessage   — the other side sent a line
//    • GhostDateEnded     — 30-min chat just ended, decision modal
//    • GhostDateOutcome   — both decisions in, here's the resolution
//
//  Privacy:
//    • Real user IDs are NEVER serialised back to clients during
//      live or pre-decision phases.
//    • Other side surfaces as { theyDisplay: "Voyager" } until
//      outcome=mutual_reveal, then their real username appears.
//    • Per-message "mine" flag is server-computed.
// ============================================================

[Authorize]
public class GhostDateHub : Hub
{
    private readonly MongoService _mongo;
    private readonly ILogger<GhostDateHub> _logger;

    public GhostDateHub(MongoService mongo, ILogger<GhostDateHub> logger)
    {
        _mongo = mongo;
        _logger = logger;
    }

    // ─── Time helpers ──────────────────────────────────────────

    /// <summary>The IST datetime for the upcoming Thursday 9pm. If
    /// we're past Thursday 9pm IST this week, returns NEXT week's
    /// Thursday 9pm IST.</summary>
    public static DateTime NextThursdayNinePmIst(DateTime nowUtc)
    {
        var ist = nowUtc.AddHours(5).AddMinutes(30);
        // DayOfWeek.Thursday == 4
        var daysUntilThu = ((int)DayOfWeek.Thursday - (int)ist.DayOfWeek + 7) % 7;
        var thuDate = ist.Date.AddDays(daysUntilThu);
        var thu9pmIst = thuDate.AddHours(21);
        // If already past today's 9pm IST, roll forward a week.
        if (daysUntilThu == 0 && ist >= thu9pmIst)
            thu9pmIst = thu9pmIst.AddDays(7);
        return thu9pmIst;
    }

    private static string EventDateOf(DateTime nextThuNinePmIst) =>
        nextThuNinePmIst.ToString("yyyy-MM-dd");

    // ─── Registration ──────────────────────────────────────────

    public async Task<object> Register()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var target = NextThursdayNinePmIst(DateTime.UtcNow);
        var reg = await _mongo.RegisterForGhostDateAsync(meId, EventDateOf(target));
        return ToStatusDto(reg, target);
    }

    public async Task<object> Withdraw()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var target = NextThursdayNinePmIst(DateTime.UtcNow);
        var eventDate = EventDateOf(target);
        var ok = await _mongo.WithdrawFromGhostDateAsync(meId, eventDate);
        if (!ok) throw new HubException("Couldn't withdraw — pairing may have already happened.");
        var fresh = await _mongo.GetMyRegistrationAsync(meId, eventDate);
        return ToStatusDto(fresh, target);
    }

    public async Task<object> GetMyStatus()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var target = NextThursdayNinePmIst(DateTime.UtcNow);
        var eventDate = EventDateOf(target);
        var reg = await _mongo.GetMyRegistrationAsync(meId, eventDate);

        // Active date check — even if there's no upcoming registration,
        // I might be inside a live 30-min window from a prior tick.
        var live = await _mongo.GetMyActiveGhostDateAsync(meId);
        var dto = ToStatusDto(reg, target);
        if (live is not null)
        {
            return new
            {
                registration = dto,
                activeDate   = ToActiveDateDto(live, meId),
            };
        }
        return new { registration = dto, activeDate = (object?)null };
    }

    // ─── Live chat ─────────────────────────────────────────────

    public async Task<object> GetActiveDate()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var live = await _mongo.GetMyActiveGhostDateAsync(meId);
        if (live is null) throw new HubException("You're not in a live ghost date right now.");
        return ToActiveDateDto(live, meId);
    }

    public async Task<object> SendMessage(string content)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();

        if (string.IsNullOrWhiteSpace(content))
            throw new HubException("Message can't be empty.");
        if (content.Length > MongoService.GhostDateMessageMaxChars)
            throw new HubException(
                $"Keep it under {MongoService.GhostDateMessageMaxChars} characters.");

        var live = await _mongo.GetMyActiveGhostDateAsync(meId);
        if (live is null) throw new HubException("Your date isn't live anymore.");
        if (DateTime.UtcNow > live.ExpiresAt)
            throw new HubException("The chat window just ended.");

        var msg = new GhostDateMessage
        {
            DateId       = live.Id!,
            SenderUserId = meId,
            Content      = content.Trim(),
        };
        var saved = await _mongo.InsertGhostDateMessageAsync(msg);

        // Push to BOTH parties; client decides "mine" via the flag.
        var otherUserId = live.UserAId == meId ? live.UserBId : live.UserAId;
        var dtoMine  = ToMessageDto(saved, viewerId: meId);
        var dtoOther = ToMessageDto(saved, viewerId: otherUserId);

        await Clients.User(meId).SendAsync("GhostDateMessage", dtoMine);
        await Clients.User(otherUserId).SendAsync("GhostDateMessage", dtoOther);

        return dtoMine;
    }

    public async Task<object> GetThread()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var live = await _mongo.GetMyActiveGhostDateAsync(meId);
        if (live is null) throw new HubException("No live thread.");
        var messages = await _mongo.GetGhostDateThreadAsync(live.Id!);
        return new
        {
            dateId   = live.Id,
            count    = messages.Count,
            messages = messages.Select(m => ToMessageDto(m, viewerId: meId)).ToList(),
        };
    }

    // ─── Decision + outcome ────────────────────────────────────

    public async Task<object> SubmitDecision(string dateId, bool reveal)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var post = await _mongo.SubmitGhostDateDecisionAsync(dateId, meId, reveal);
        if (post is null) throw new HubException("Date not found or not yours.");

        // If outcome just resolved, push to BOTH parties.
        if (post.Outcome is not null && post.OutcomeAt is not null
            && (DateTime.UtcNow - post.OutcomeAt.Value).TotalSeconds < 5)
        {
            await Clients.User(post.UserAId).SendAsync("GhostDateOutcome",
                ToOutcomeDto(post, viewerId: post.UserAId));
            await Clients.User(post.UserBId).SendAsync("GhostDateOutcome",
                ToOutcomeDto(post, viewerId: post.UserBId));
        }
        return ToActiveDateDto(post, viewerId: meId);
    }

    public async Task<object> GetMyHistory()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var items = await _mongo.GetGhostDateHistoryAsync(meId);
        return new
        {
            count = items.Count,
            dates = items.Select(d => ToHistoryDto(d, viewerId: meId)).ToList(),
        };
    }

    // ─── Status + DTO shaping ──────────────────────────────────

    private static object ToStatusDto(GhostDateRegistration? reg, DateTime nextEventIst)
    {
        return new
        {
            nextEventAt   = nextEventIst,           // when the next pairing fires
            status        = reg?.Status ?? "not_registered",
            registeredAt  = reg?.RegisteredAt,
            pairedDateId  = reg?.PairedDateId,
            eventDate     = reg?.TargetEventDate ?? nextEventIst.ToString("yyyy-MM-dd"),
        };
    }

    private static object ToActiveDateDto(GhostDate d, string viewerId)
    {
        var myDecision   = d.UserAId == viewerId ? d.UserARevealedAfter : d.UserBRevealedAfter;
        var theirDecided = d.UserAId == viewerId ? d.UserBDecidedAt    is not null
                                                : d.UserADecidedAt    is not null;
        var revealed = d.Outcome == "mutual_reveal";
        return new
        {
            id                = d.Id,
            scheduledFor      = d.ScheduledFor,
            expiresAt         = d.ExpiresAt,
            decisionDeadline  = d.DecisionDeadline,
            // Other-side identity only surfaces on mutual_reveal.
            theirDisplay      = revealed
                ? (d.UserAId == viewerId ? d.UserBUsername : d.UserAUsername)
                : "Voyager",
            myDecision,
            theirDecided,
            outcome           = d.Outcome,
        };
    }

    private static object ToMessageDto(GhostDateMessage m, string viewerId) => new
    {
        id        = m.Id,
        content   = m.Content,
        createdAt = m.CreatedAt,
        mine      = m.SenderUserId == viewerId,
    };

    private static object ToOutcomeDto(GhostDate d, string viewerId)
    {
        var revealed = d.Outcome == "mutual_reveal";
        return new
        {
            id              = d.Id,
            outcome         = d.Outcome,
            outcomeAt       = d.OutcomeAt,
            theirDisplay    = revealed
                ? (d.UserAId == viewerId ? d.UserBUsername : d.UserAUsername)
                : null,
            nextEligibleMatchAt = d.NextEligibleMatchAt,   // populated only on mutual_pass
        };
    }

    private static object ToHistoryDto(GhostDate d, string viewerId)
    {
        var revealed = d.Outcome == "mutual_reveal";
        return new
        {
            id            = d.Id,
            eventDate     = d.EventDate,
            scheduledFor  = d.ScheduledFor,
            outcome       = d.Outcome,
            theirDisplay  = revealed
                ? (d.UserAId == viewerId ? d.UserBUsername : d.UserAUsername)
                : "Voyager",
        };
    }
}
