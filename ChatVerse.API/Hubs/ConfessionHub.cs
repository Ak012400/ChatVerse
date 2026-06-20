using ChatVerse.API.Extensions;
using ChatVerse.API.Services;
using ChatVerse.Domain.Entities;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ChatVerse.API.Hubs;

// ============================================================
//  ConfessionHub — Phase 2 Confession Box surface.
//
//  Client methods:
//    • Post(content)                       → write anonymously
//    • GetTodaysFeed(skip, limit)          → paged anonymous feed
//    • React(confessionId, emoji)          → toggle one of 6 emojis
//    • GetMyTopOffer()                     → is any of MY confessions
//                                            today's top + still
//                                            awaiting my decision?
//    • AcceptReveal(confessionId)          → reveal + public banner
//    • DeclineReveal(confessionId)         → Ghost Voice badge
//    • GetLoreWall(weeksAgo)               → top 5 of that week
//
//  Server-push events (group = "confession-feed:<dateUtc>"):
//    • ConfessionPosted    — new confession landed in today's feed
//    • ReactionUpdated     — reaction counts changed
//    • ConfessionRevealed  — author accepted the reveal offer
//    • TopConfessionOffered — fanned to the author only
//
//  Privacy:
//    • Anonymous feed DTO NEVER includes AuthorUserId. Username
//      surfaces ONLY when AuthorOptedReveal == true.
//    • The "mineFlag" on a DTO is computed server-side by matching
//      caller's user id against AuthorUserId — that boolean is the
//      ONLY leak about identity (and only back to the author).
// ============================================================

[Authorize]
public class ConfessionHub : Hub
{
    private readonly MongoService _mongo;
    private readonly ILogger<ConfessionHub> _logger;

    public ConfessionHub(MongoService mongo, ILogger<ConfessionHub> logger)
    {
        _mongo = mongo;
        _logger = logger;
    }

    private static string TodayUtc() => DateTime.UtcNow.ToString("yyyy-MM-dd");
    private static string FeedGroup(string dateUtc) => $"confession-feed:{dateUtc}";

    public override async Task OnConnectedAsync()
    {
        // Auto-join today's feed group so the client receives push
        // events without an explicit subscribe call.
        await Groups.AddToGroupAsync(Context.ConnectionId, FeedGroup(TodayUtc()));
        await base.OnConnectedAsync();
    }

    // ─── Post ──────────────────────────────────────────────────

    public async Task<object> Post(string content)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var meName = JwtService.GetUsername(Context.User!);

        if (string.IsNullOrWhiteSpace(content))
            throw new HubException("Confession can't be empty.");
        if (content.Length > MongoService.ConfessionMaxChars)
            throw new HubException($"Keep it under {MongoService.ConfessionMaxChars} characters.");

        var c = new Confession
        {
            AuthorUserId   = meId,
            AuthorUsername = meName,
            Content        = content.Trim(),
            Date           = TodayUtc(),
        };

        var saved = await _mongo.InsertConfessionAsync(c);
        var dto = ToFeedDto(saved, meId);

        // Fan to today's feed group, including the poster — they see
        // their own card glide in at the top, with the "mine" flag set.
        await Clients.Group(FeedGroup(saved.Date))
            .SendAsync("ConfessionPosted", dto);

        return dto;
    }

    // ─── Feed ──────────────────────────────────────────────────

    public async Task<object> GetTodaysFeed(int skip = 0, int limit = 20)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var date = TodayUtc();
        var items = await _mongo.GetConfessionsForDateAsync(date, skip, Math.Min(limit, 50));
        return new
        {
            date,
            count       = items.Count,
            confessions = items.Select(c => ToFeedDto(c, meId)).ToList(),
            allowedEmojis = MongoService.ConfessionAllowedEmojis,
        };
    }

    // ─── React ─────────────────────────────────────────────────

    public async Task<object> React(string confessionId, string emoji)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var updated = await _mongo.ToggleConfessionReactionAsync(confessionId, meId, emoji);
        if (updated is null)
            throw new HubException("Couldn't react — confession may have expired.");

        var dto = ToFeedDto(updated, meId);
        // Push the FULL updated card to everyone on today's feed so
        // counts stay live. Sends the same payload as the post event
        // — client merges by id.
        await Clients.Group(FeedGroup(updated.Date))
            .SendAsync("ReactionUpdated", dto);
        return dto;
    }

    // ─── Reveal mechanics ──────────────────────────────────────

    public async Task<object> GetMyTopOffer()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var offer = await _mongo.GetUndecidedTopOfferForAuthorAsync(meId);
        if (offer is null) return new { hasOffer = false };
        return new
        {
            hasOffer    = true,
            id          = offer.Id,
            content     = offer.Content,
            totalReactions = offer.TotalReactions,
            topRankedAt = offer.TopRankedAt,
        };
    }

    public async Task<object> AcceptReveal(string confessionId)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var ok = await _mongo.SetAuthorRevealDecisionAsync(confessionId, meId, accept: true);
        if (!ok)
            throw new HubException("Couldn't accept reveal — may already be decided.");

        var fresh = await _mongo.GetConfessionByIdAsync(confessionId);
        if (fresh is null) throw new HubException("Confession vanished.");

        // Public reveal — fan out the FULL author name to anyone
        // watching the date this confession belongs to.
        await Clients.Group(FeedGroup(fresh.Date))
            .SendAsync("ConfessionRevealed", new
            {
                id             = fresh.Id,
                authorUsername = fresh.AuthorUsername,
                revealedAt     = fresh.RevealedAt,
            });
        return ToFeedDto(fresh, meId);
    }

    public async Task<bool> DeclineReveal(string confessionId)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var ok = await _mongo.SetAuthorRevealDecisionAsync(confessionId, meId, accept: false);
        if (!ok)
            throw new HubException("Couldn't decline — may already be decided.");
        return true;
    }

    // ─── Lore Wall ─────────────────────────────────────────────

    public async Task<object> GetLoreWall(int weeksAgo = 0)
    {
        // Week starts on Monday — that way Sunday's top still counts
        // for the same week as the Friday's top. weeksAgo=0 = THIS
        // week; 1 = last week; etc.
        var now = DateTime.UtcNow.Date;
        var daysSinceMonday = ((int)now.DayOfWeek + 6) % 7;
        var thisMonday = now.AddDays(-daysSinceMonday);
        var weekStart = thisMonday.AddDays(-7 * weeksAgo);

        var items = await _mongo.GetLoreWallAsync(weekStart, limit: 5);
        var meId = JwtService.GetUserId(Context.User!).ToString();
        return new
        {
            weekStart  = weekStart.ToString("yyyy-MM-dd"),
            count      = items.Count,
            confessions = items.Select(c => ToFeedDto(c, meId)).ToList(),
        };
    }

    // ─── DTO shaping (privacy gate lives here) ─────────────────

    private static object ToFeedDto(Confession c, string callerUserId)
    {
        var mine = c.AuthorUserId == callerUserId;
        var revealed = c.AuthorOptedReveal == true;
        // Reactions: { emoji -> count, mineEmoji: <emoji|null> }
        string? mineEmoji = null;
        var counts = new Dictionary<string, int>();
        foreach (var (emoji, users) in c.Reactions)
        {
            counts[emoji] = users.Count;
            if (users.Contains(callerUserId)) mineEmoji = emoji;
        }

        return new
        {
            id              = c.Id,
            content         = c.Content,
            totalReactions  = c.TotalReactions,
            reactionCounts  = counts,
            mineEmoji,
            mine,
            topRanked       = c.TopRankedAt != null,
            // Author surfaces ONLY when reveal accepted. Otherwise null.
            revealedUsername = revealed ? c.AuthorUsername : null,
            createdAt       = c.CreatedAt,
            // Ghost Voice profile badge — derivable client-side from
            // (topRanked && !revealedUsername && AuthorOptedReveal==false).
            // We don't expose AuthorOptedReveal directly to non-authors
            // because it could leak identity ("I see Y said no to reveal").
        };
    }
}
