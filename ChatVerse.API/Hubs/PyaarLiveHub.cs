using ChatVerse.API.Extensions;
using ChatVerse.API.Services;
using ChatVerse.Domain.Entities;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ChatVerse.API.Hubs;

// ============================================================
//  PyaarLiveHub — flagship Saturday mass dating show surface.
//
//  Client methods:
//    • Register / Withdraw / GetMyStatus
//    • GetActiveShow()            → show + couples + round state
//    • GetMyCouple()              → my couple + thread (if in one)
//    • SendCoupleMessage(content) → couple-private DM (during live round)
//    • GetSpectatorView()         → all couples' recent messages
//    • Vote(coupleId)             → spectator vote (non-couple users only)
//    • GetMyHistory()
//
//  Server-push events:
//    • ShowStarted          (everyone): show is live, round 1 begins
//    • RoundAdvanced        (everyone): new round number + label + ends-at
//    • CoupleMessage        (couple members only): new message in our thread
//    • SpectatorMessage     (spectators on this show): NEW couple message
//                            arrived in some couple's thread (lighter
//                            payload — just couple-id + sender + content)
//    • EliminationAnnounced (everyone): 3 couples kicked after round 2
//    • ShowEnded            (everyone): final ranks
//
//  Privacy split:
//    • Couples see each other's real usernames inside their thread.
//      That's by design — PYAAR LIVE is reveal-first dating, not anon.
//    • Spectators see all couples in a grid + each couple's CodeName.
//      Usernames are not hidden from spectators either (couples opted
//      in for the spotlight). But voting only affects ranking, not DMs.
// ============================================================

[Authorize]
public class PyaarLiveHub : Hub
{
    private readonly MongoService _mongo;
    private readonly IHubContext<PyaarLiveHub> _hubCtx;
    private readonly ILogger<PyaarLiveHub> _logger;

    public PyaarLiveHub(MongoService mongo, IHubContext<PyaarLiveHub> hubCtx, ILogger<PyaarLiveHub> logger)
    {
        _mongo = mongo;
        _hubCtx = hubCtx;
        _logger = logger;
    }

    // ─── Time helpers ──────────────────────────────────────────

    /// <summary>Next Saturday 8pm IST in UTC.</summary>
    public static DateTime NextSaturdayEightPmIst(DateTime nowUtc)
    {
        var ist = nowUtc.AddHours(5).AddMinutes(30);
        var daysUntilSat = ((int)DayOfWeek.Saturday - (int)ist.DayOfWeek + 7) % 7;
        var satDate = ist.Date.AddDays(daysUntilSat);
        var sat8pmIst = satDate.AddHours(20);
        if (daysUntilSat == 0 && ist >= sat8pmIst)
            sat8pmIst = sat8pmIst.AddDays(7);
        return sat8pmIst.AddHours(-5).AddMinutes(-30);
    }

    private static string EventDateOf(DateTime nextSatUtc) =>
        nextSatUtc.AddHours(5).AddMinutes(30).ToString("yyyy-MM-dd");

    private static string ShowGroup(string showId) => $"pyaar-live:{showId}";

    // ─── Connection lifecycle ──────────────────────────────────

    public override async Task OnConnectedAsync()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var active = await _mongo.GetActivePyaarShowAsync();
        if (active is not null)
            await Groups.AddToGroupAsync(Context.ConnectionId, ShowGroup(active.Id!));
        await base.OnConnectedAsync();
        _ = meId;  // suppress unused warning if logger removed later
    }

    // ─── Registration ──────────────────────────────────────────

    public async Task<object> Register()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var target = NextSaturdayEightPmIst(DateTime.UtcNow);
        var reg = await _mongo.RegisterForPyaarLiveAsync(meId, EventDateOf(target));
        return ToStatusDto(reg, target);
    }

    public async Task<object> Withdraw()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var target = NextSaturdayEightPmIst(DateTime.UtcNow);
        var eventDate = EventDateOf(target);
        var ok = await _mongo.WithdrawFromPyaarLiveAsync(meId, eventDate);
        if (!ok) throw new HubException("Couldn't withdraw — pairing may have already happened.");
        var fresh = await _mongo.GetMyPyaarRegistrationAsync(meId, eventDate);
        return ToStatusDto(fresh, target);
    }

    public async Task<object> GetMyStatus()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var target = NextSaturdayEightPmIst(DateTime.UtcNow);
        var eventDate = EventDateOf(target);
        var reg = await _mongo.GetMyPyaarRegistrationAsync(meId, eventDate);
        var active = await _mongo.GetActivePyaarShowAsync();
        object? activeShowDto = null;
        object? myCoupleDto = null;
        if (active is not null)
        {
            activeShowDto = await BuildShowDtoAsync(active, meId);
            var couple = await _mongo.GetMyCoupleAsync(active.Id!, meId);
            if (couple is not null)
                myCoupleDto = await BuildMyCoupleDtoAsync(active, couple, meId);
        }
        return new
        {
            registration = ToStatusDto(reg, target),
            activeShow   = activeShowDto,
            myCouple     = myCoupleDto,
        };
    }

    // ─── Show + couple reads ───────────────────────────────────

    public async Task<object> GetActiveShow()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var active = await _mongo.GetActivePyaarShowAsync();
        if (active is null) throw new HubException("No live show right now.");
        return await BuildShowDtoAsync(active, meId);
    }

    public async Task<object> GetMyCouple()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var active = await _mongo.GetActivePyaarShowAsync()
            ?? throw new HubException("No live show right now.");
        var couple = await _mongo.GetMyCoupleAsync(active.Id!, meId)
            ?? throw new HubException("You're not in a couple this show.");
        return await BuildMyCoupleDtoAsync(active, couple, meId);
    }

    public async Task<object> SendCoupleMessage(string content)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var meName = JwtService.GetUsername(Context.User!);

        if (string.IsNullOrWhiteSpace(content))
            throw new HubException("Message can't be empty.");
        if (content.Length > MongoService.PyaarMessageMaxChars)
            throw new HubException($"Keep it under {MongoService.PyaarMessageMaxChars} characters.");

        var active = await _mongo.GetActivePyaarShowAsync()
            ?? throw new HubException("No live show right now.");
        if (active.CurrentRound is < 1 or > 4)
            throw new HubException("Couples can only message during live rounds.");
        var couple = await _mongo.GetMyCoupleAsync(active.Id!, meId)
            ?? throw new HubException("You're not in a couple this show.");
        if (couple.EliminatedAt is not null)
            throw new HubException("Your couple was eliminated. Watch the rest as a spectator.");

        var msg = new PyaarMessage
        {
            ShowId         = active.Id!,
            CoupleId       = couple.Id!,
            SenderUserId   = meId,
            SenderUsername = meName,
            RoundNumber    = active.CurrentRound,
            Content        = content.Trim(),
        };
        var saved = await _mongo.InsertPyaarMessageAsync(msg);

        // Push the FULL DTO to both couple members.
        var fullDto = ToMessageDto(saved);
        await _hubCtx.Clients.User(couple.UserAId).SendAsync("CoupleMessage", fullDto);
        await _hubCtx.Clients.User(couple.UserBId).SendAsync("CoupleMessage", fullDto);

        // Lighter push to the whole show group so spectators' grid
        // updates without leaking deep payload. We send the same DTO
        // since usernames aren't hidden from spectators here — only
        // the couple-thread privacy is the COUPLES' DM with each other.
        // Spectator view shows all messages by design (it's a live show).
        await _hubCtx.Clients.Group(ShowGroup(active.Id!))
            .SendAsync("SpectatorMessage", fullDto);

        return fullDto;
    }

    public async Task<object> GetSpectatorView()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var active = await _mongo.GetActivePyaarShowAsync()
            ?? throw new HubException("No live show right now.");
        var couples = await _mongo.GetCouplesForShowAsync(active.Id!);
        var recent  = await _mongo.GetPyaarShowRecentMessagesAsync(active.Id!, perCoupleLimit: 6);

        // Group messages by couple.
        var byCouple = recent.GroupBy(m => m.CoupleId).ToDictionary(g => g.Key, g => g.Select(ToMessageDto).ToList());
        var myVote   = await _mongo.GetMyPyaarVoteAsync(active.Id!, meId);

        return new
        {
            show     = await BuildShowDtoAsync(active, meId),
            myVote   = myVote?.CoupleId,
            couples  = couples.Select(c => new
            {
                id           = c.Id,
                codename     = c.Codename,
                coupleNumber = c.CoupleNumber,
                memberA      = c.UserAUsername,
                memberB      = c.UserBUsername,
                voteCount    = c.VoteCount,
                eliminated   = c.EliminatedAt != null,
                finalRank    = c.FinalRank,
                recent       = byCouple.TryGetValue(c.Id!, out var list) ? list : new List<object>(),
            }).ToList(),
        };
    }

    // ─── Vote ──────────────────────────────────────────────────

    public async Task<object> Vote(string coupleId)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var active = await _mongo.GetActivePyaarShowAsync()
            ?? throw new HubException("No live show right now.");
        if (active.CurrentRound < 1)
            throw new HubException("Voting is closed pre-show.");

        // Couples can't vote on their own show.
        var mine = await _mongo.GetMyCoupleAsync(active.Id!, meId);
        if (mine is not null)
            throw new HubException("Couples can't vote in their own show.");

        var couple = await _mongo.GetCoupleByIdAsync(coupleId)
            ?? throw new HubException("Couple not found.");
        if (couple.ShowId != active.Id)
            throw new HubException("That couple isn't in the live show.");
        if (couple.EliminatedAt is not null)
            throw new HubException("That couple has been eliminated.");

        await _mongo.CastPyaarVoteAsync(active.Id!, meId, coupleId);
        return await BuildShowDtoAsync(active, meId);
    }

    // ─── History ───────────────────────────────────────────────

    public async Task<object> GetMyHistory()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var shows = await _mongo.GetCompletedPyaarShowsAsync(10);
        var rows = new List<object>();
        foreach (var s in shows)
        {
            var mine = await _mongo.GetMyCoupleAsync(s.Id!, meId);
            rows.Add(new
            {
                showId      = s.Id,
                eventDate   = s.EventDate,
                scheduledFor = s.ScheduledFor,
                myCouple    = mine is null ? null : new
                {
                    codename  = mine.Codename,
                    finalRank = mine.FinalRank,
                    eliminated = mine.EliminatedAt != null,
                    eliminatedInRound = mine.EliminatedInRound,
                },
                winnersCount = s.WinningCoupleIds.Count,
            });
        }
        return new { count = rows.Count, shows = rows };
    }

    // ─── DTO shaping ───────────────────────────────────────────

    private static object ToStatusDto(PyaarRegistration? reg, DateTime nextEventUtc) => new
    {
        nextEventAt  = nextEventUtc,
        status       = reg?.Status ?? "not_registered",
        registeredAt = reg?.RegisteredAt,
        eventDate    = reg?.EventDate ?? nextEventUtc.AddHours(5).AddMinutes(30).ToString("yyyy-MM-dd"),
    };

    private async Task<object> BuildShowDtoAsync(PyaarShow show, string viewerId)
    {
        var couples = await _mongo.GetCouplesForShowAsync(show.Id!);
        var mineCouple = couples.FirstOrDefault(c => c.UserAId == viewerId || c.UserBId == viewerId);
        return new
        {
            id               = show.Id,
            eventDate        = show.EventDate,
            scheduledFor     = show.ScheduledFor,
            status           = show.Status,
            currentRound     = show.CurrentRound,
            currentRoundLabel = show.CurrentRoundLabel,
            currentRoundEndsAt = show.CurrentRoundEndsAt,
            prizePool        = show.PrizePool,
            iAmInCouple      = mineCouple is not null,
            myCoupleId       = mineCouple?.Id,
            couples          = couples.Select(c => new
            {
                id           = c.Id,
                codename     = c.Codename,
                coupleNumber = c.CoupleNumber,
                voteCount    = c.VoteCount,
                eliminated   = c.EliminatedAt != null,
                finalRank    = c.FinalRank,
                memberA      = c.UserAUsername,
                memberB      = c.UserBUsername,
            }).ToList(),
            winningCoupleIds = show.WinningCoupleIds,
        };
    }

    private async Task<object> BuildMyCoupleDtoAsync(PyaarShow show, PyaarCouple couple, string viewerId)
    {
        var thread = await _mongo.GetPyaarCoupleThreadAsync(couple.Id!, 200);
        var partnerName = couple.UserAId == viewerId ? couple.UserBUsername : couple.UserAUsername;
        return new
        {
            id             = couple.Id,
            codename       = couple.Codename,
            coupleNumber   = couple.CoupleNumber,
            partnerUsername = partnerName,
            voteCount      = couple.VoteCount,
            eliminated     = couple.EliminatedAt != null,
            eliminatedInRound = couple.EliminatedInRound,
            finalRank      = couple.FinalRank,
            currentRound   = show.CurrentRound,
            currentRoundLabel = show.CurrentRoundLabel,
            currentRoundEndsAt = show.CurrentRoundEndsAt,
            messages       = thread.Select(m => ToMessageDtoForViewer(m, viewerId)).ToList(),
        };
    }

    private static object ToMessageDto(PyaarMessage m) => new
    {
        id             = m.Id,
        coupleId       = m.CoupleId,
        senderUsername = m.SenderUsername,
        senderUserId   = m.SenderUserId,
        roundNumber    = m.RoundNumber,
        content        = m.Content,
        createdAt      = m.CreatedAt,
    };

    private static object ToMessageDtoForViewer(PyaarMessage m, string viewerId) => new
    {
        id             = m.Id,
        coupleId       = m.CoupleId,
        senderUsername = m.SenderUsername,
        mine           = m.SenderUserId == viewerId,
        roundNumber    = m.RoundNumber,
        content        = m.Content,
        createdAt      = m.CreatedAt,
    };
}
