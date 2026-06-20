using ChatVerse.API.Extensions;
using ChatVerse.API.Services;
using ChatVerse.Domain.Entities;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ChatVerse.API.Hubs;

// ============================================================
//  LoveTriangleHub — weekly 3-person drama surface.
//
//  Client methods:
//    • Register / Withdraw / GetMyStatus
//    • GetMyTriangle()            → full state with all 3 pair threads
//    • SendPairMessage(pairKey, content)
//    • ToggleShareExcerpt(messageId)
//    • GetPublicTriangles(weekOffset)  → spectator feed
//    • GetTriangleExcerpts(triangleId) → anonymous excerpts for non-members
//    • Vote(triangleId, pairKey)        → audience vote (non-members)
//    • GetMyHistory()
//
//  Server-push events (per-user fan-out):
//    • TriangleFormed    — pairing tick just placed you in a triangle
//    • PairMessage       — new line in one of your pair threads
//    • ExcerptShared     — someone in your triangle shared a line publicly
//    • VotingOpened      — day-7 hit, audience can now vote
//    • TriangleCompleted — winning pair declared
//
//  Privacy split:
//    • Pair-thread DTOs include real usernames (members know each other).
//    • Public-feed DTOs anonymise to Member A / B / C.
//    • Caller's relationship to a triangle is computed server-side
//      and surfaced as { myRole: "A"|"B"|"C"|null }.
// ============================================================

[Authorize]
public class LoveTriangleHub : Hub
{
    private readonly MongoService _mongo;
    private readonly ILogger<LoveTriangleHub> _logger;

    public LoveTriangleHub(MongoService mongo, ILogger<LoveTriangleHub> logger)
    {
        _mongo = mongo;
        _logger = logger;
    }

    // ─── Time helpers ──────────────────────────────────────────

    /// <summary>The upcoming Sunday 10pm IST in UTC. If we're past
    /// this Sunday 10pm, returns next Sunday.</summary>
    public static DateTime NextSundayTenPmIst(DateTime nowUtc)
    {
        var ist = nowUtc.AddHours(5).AddMinutes(30);
        // DayOfWeek.Sunday == 0
        var daysUntilSun = ((int)DayOfWeek.Sunday - (int)ist.DayOfWeek + 7) % 7;
        var sunDate = ist.Date.AddDays(daysUntilSun);
        var sun10pmIst = sunDate.AddHours(22);
        if (daysUntilSun == 0 && ist >= sun10pmIst)
            sun10pmIst = sun10pmIst.AddDays(7);
        // Convert IST datetime back to UTC.
        return sun10pmIst.AddHours(-5).AddMinutes(-30);
    }

    private static string WeekStartOf(DateTime nextSunUtc) =>
        nextSunUtc.AddHours(5).AddMinutes(30).ToString("yyyy-MM-dd");

    private static string MemberRoleOf(LoveTriangle t, string userId)
    {
        if (t.UserAId == userId) return "A";
        if (t.UserBId == userId) return "B";
        if (t.UserCId == userId) return "C";
        return "";
    }

    private static string PairKeyFor(string a, string b)
    {
        // Canonicalise to alphabetical "a-b" / "b-c" / "a-c".
        var (lo, hi) = string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);
        return $"{lo.ToLowerInvariant()}-{hi.ToLowerInvariant()}";
    }

    // ─── Registration ──────────────────────────────────────────

    public async Task<object> Register()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var target = NextSundayTenPmIst(DateTime.UtcNow);
        var weekStart = WeekStartOf(target);
        var reg = await _mongo.RegisterForLoveTriangleAsync(meId, weekStart);
        return ToStatusDto(reg, target);
    }

    public async Task<object> Withdraw()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var target = NextSundayTenPmIst(DateTime.UtcNow);
        var weekStart = WeekStartOf(target);
        var ok = await _mongo.WithdrawFromLoveTriangleAsync(meId, weekStart);
        if (!ok) throw new HubException("Couldn't withdraw — pairing may have happened.");
        var fresh = await _mongo.GetMyLoveTriangleRegistrationAsync(meId, weekStart);
        return ToStatusDto(fresh, target);
    }

    public async Task<object> GetMyStatus()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var target = NextSundayTenPmIst(DateTime.UtcNow);
        var weekStart = WeekStartOf(target);
        var reg = await _mongo.GetMyLoveTriangleRegistrationAsync(meId, weekStart);
        var active = await _mongo.GetMyActiveLoveTriangleAsync(meId);
        return new
        {
            registration  = ToStatusDto(reg, target),
            activeTriangle = active is null ? null : await BuildMyTriangleDtoAsync(active, meId),
        };
    }

    // ─── Triangle (member view) ────────────────────────────────

    public async Task<object> GetMyTriangle()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var t = await _mongo.GetMyActiveLoveTriangleAsync(meId);
        if (t is null) throw new HubException("You're not in an active triangle right now.");
        return await BuildMyTriangleDtoAsync(t, meId);
    }

    public async Task<object> GetPairThread(string pairKey)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var t = await _mongo.GetMyActiveLoveTriangleAsync(meId);
        if (t is null) throw new HubException("No active triangle.");

        var myRole = MemberRoleOf(t, meId);
        // I can only read pairs I'm in. The third side I see only as
        // public excerpts (or, in member view, NOT AT ALL — drama).
        if (!pairKey.Contains(myRole.ToLowerInvariant()))
            throw new HubException("Not your pair to peek into.");

        var msgs = await _mongo.GetLoveTrianglePairThreadAsync(t.Id!, pairKey);
        return new
        {
            pairKey,
            count    = msgs.Count,
            messages = msgs.Select(m => ToPairMessageDto(m, viewerId: meId)).ToList(),
        };
    }

    public async Task<object> SendPairMessage(string pairKey, string content)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var meName = JwtService.GetUsername(Context.User!);

        if (string.IsNullOrWhiteSpace(content))
            throw new HubException("Message can't be empty.");
        if (content.Length > MongoService.LoveTrianglePairMessageMax)
            throw new HubException($"Keep it under {MongoService.LoveTrianglePairMessageMax} chars.");

        var t = await _mongo.GetMyActiveLoveTriangleAsync(meId);
        if (t is null) throw new HubException("Your triangle isn't active.");
        if (t.Status != "active") throw new HubException("Chat phase has ended.");

        var myRole = MemberRoleOf(t, meId);
        if (!pairKey.Contains(myRole.ToLowerInvariant()))
            throw new HubException("Not your pair to message.");

        var msg = new LoveTrianglePairMessage
        {
            TriangleId     = t.Id!,
            PairKey        = pairKey,
            SenderUserId   = meId,
            SenderUsername = meName,
            Content        = content.Trim(),
        };
        var saved = await _mongo.InsertLoveTrianglePairMessageAsync(msg);

        // Push to the OTHER member in the pair.
        var otherUserId = pairKey.Split('-') switch
        {
            ["a", "b"] => meId == t.UserAId ? t.UserBId : t.UserAId,
            ["b", "c"] => meId == t.UserBId ? t.UserCId : t.UserBId,
            ["a", "c"] => meId == t.UserAId ? t.UserCId : t.UserAId,
            _ => meId,
        };
        await Clients.User(otherUserId).SendAsync("PairMessage", new
        {
            triangleId = t.Id,
            pairKey,
            message    = ToPairMessageDto(saved, viewerId: otherUserId),
        });
        return ToPairMessageDto(saved, viewerId: meId);
    }

    public async Task<object> ToggleShareExcerpt(string messageId)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var updated = await _mongo.ToggleLoveTriangleExcerptAsync(messageId, meId);
        if (updated is null) throw new HubException("Message not found.");

        // If it's now shared, push to the OTHER triangle member (and
        // future-spectator-fan-out can ride on this too).
        if (updated.IsShared)
        {
            var t = await _mongo.GetLoveTriangleByIdAsync(updated.TriangleId);
            if (t is not null)
            {
                foreach (var uid in new[] { t.UserAId, t.UserBId, t.UserCId })
                {
                    if (uid != meId)
                        await Clients.User(uid).SendAsync("ExcerptShared", new
                        {
                            triangleId = t.Id,
                            pairKey    = updated.PairKey,
                            excerpt    = ToPublicExcerptDto(updated, t),
                        });
                }
            }
        }
        return ToPairMessageDto(updated, viewerId: meId);
    }

    // ─── Public (audience) view ────────────────────────────────

    public async Task<object> GetPublicTriangles(int weekOffset = 0)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var triangles = await _mongo.GetPublicLoveTrianglesAsync(weekOffset);
        return new
        {
            count     = triangles.Count,
            triangles = triangles.Select(t => ToPublicTriangleDto(t, meId)).ToList(),
        };
    }

    public async Task<object> GetTriangleExcerpts(string triangleId)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var t = await _mongo.GetLoveTriangleByIdAsync(triangleId);
        if (t is null) throw new HubException("Triangle not found.");
        var excerpts = await _mongo.GetLoveTrianglePublicExcerptsAsync(triangleId);
        return new
        {
            triangle = ToPublicTriangleDto(t, meId),
            excerpts = excerpts.Select(e => ToPublicExcerptDto(e, t)).ToList(),
        };
    }

    public async Task<object> Vote(string triangleId, string pairKey)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var updated = await _mongo.VoteOnLoveTriangleAsync(triangleId, meId, pairKey);
        if (updated is null) throw new HubException("Triangle not found.");
        if (updated.Status != "voting")
            throw new HubException("Voting isn't open on this triangle.");
        // Triangle members can't vote — caller layer enforced inside the
        // Mongo method; surface here too for the friendlier error.
        if (updated.UserAId == meId || updated.UserBId == meId || updated.UserCId == meId)
            throw new HubException("You can't vote on your own triangle.");
        return ToPublicTriangleDto(updated, meId);
    }

    public async Task<object> GetMyHistory()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var items = await _mongo.GetMyLoveTriangleHistoryAsync(meId);
        return new
        {
            count     = items.Count,
            triangles = items.Select(t => new
            {
                id           = t.Id,
                weekStart    = t.WeekStart,
                myRole       = MemberRoleOf(t, meId),
                status       = t.Status,
                winningPair  = t.WinningPair,
                completedAt  = t.CompletedAt,
            }).ToList(),
        };
    }

    // ─── DTO shaping ───────────────────────────────────────────

    private static object ToStatusDto(LoveTriangleRegistration? reg, DateTime nextEventUtc)
    {
        return new
        {
            nextEventAt   = nextEventUtc,
            status        = reg?.Status ?? "not_registered",
            registeredAt  = reg?.RegisteredAt,
            triangleId    = reg?.TriangleId,
            weekStart     = reg?.WeekStart ?? nextEventUtc.AddHours(5).AddMinutes(30).ToString("yyyy-MM-dd"),
        };
    }

    private async Task<object> BuildMyTriangleDtoAsync(LoveTriangle t, string viewerId)
    {
        var myRole = MemberRoleOf(t, viewerId);
        // Counts per pair — never reveal who voted to members.
        var voteCounts = MongoService.LoveTrianglePairKeys
            .ToDictionary(k => k, k => t.VotesByPair.TryGetValue(k, out var v) ? v.Count : 0);

        // For pairs I'm in, fetch the message threads in advance so the
        // page lands fully loaded.
        var threads = new Dictionary<string, object>();
        foreach (var pk in MongoService.LoveTrianglePairKeys)
        {
            if (!pk.Contains(myRole.ToLowerInvariant())) continue;
            var msgs = await _mongo.GetLoveTrianglePairThreadAsync(t.Id!, pk, 100);
            threads[pk] = new
            {
                count    = msgs.Count,
                messages = msgs.Select(m => ToPairMessageDto(m, viewerId)).ToList(),
            };
        }

        return new
        {
            id            = t.Id,
            weekStart     = t.WeekStart,
            scheduledFor  = t.ScheduledFor,
            chatEndsAt    = t.ChatEndsAt,
            votingEndsAt  = t.VotingEndsAt,
            status        = t.Status,
            myRole,
            members       = new
            {
                a = new { username = t.UserAUsername },
                b = new { username = t.UserBUsername },
                c = new { username = t.UserCUsername },
            },
            myPairs       = MongoService.LoveTrianglePairKeys
                                .Where(k => k.Contains(myRole.ToLowerInvariant())).ToList(),
            threads,
            voteCounts,
            winningPair   = t.WinningPair,
        };
    }

    private static object ToPairMessageDto(LoveTrianglePairMessage m, string viewerId) => new
    {
        id              = m.Id,
        content         = m.Content,
        senderUsername  = m.SenderUsername,
        mine            = m.SenderUserId == viewerId,
        isShared        = m.IsShared,
        sharedByMe      = m.SharedByUserId == viewerId,
        createdAt       = m.CreatedAt,
    };

    private static object ToPublicTriangleDto(LoveTriangle t, string viewerId)
    {
        var voteCounts = MongoService.LoveTrianglePairKeys
            .ToDictionary(k => k, k => t.VotesByPair.TryGetValue(k, out var v) ? v.Count : 0);
        string? myVote = null;
        foreach (var (k, list) in t.VotesByPair)
        {
            if (list.Contains(viewerId)) { myVote = k; break; }
        }
        var isMember = t.UserAId == viewerId || t.UserBId == viewerId || t.UserCId == viewerId;
        return new
        {
            id            = t.Id,
            weekStart     = t.WeekStart,
            scheduledFor  = t.ScheduledFor,
            chatEndsAt    = t.ChatEndsAt,
            votingEndsAt  = t.VotingEndsAt,
            status        = t.Status,
            // Audience NEVER sees member usernames.
            members       = new { a = "Member A", b = "Member B", c = "Member C" },
            voteCounts,
            myVote,
            isMember,
            winningPair   = t.WinningPair,
        };
    }

    private static object ToPublicExcerptDto(LoveTrianglePairMessage m, LoveTriangle t)
    {
        // Anonymise: which member is the sender? Show as "Member A/B/C".
        var role =
            m.SenderUserId == t.UserAId ? "A" :
            m.SenderUserId == t.UserBId ? "B" :
            m.SenderUserId == t.UserCId ? "C" : "?";
        return new
        {
            id           = m.Id,
            pairKey      = m.PairKey,
            senderRole   = role,
            content      = m.Content,
            sharedAt     = m.SharedAt,
            createdAt    = m.CreatedAt,
        };
    }
}
