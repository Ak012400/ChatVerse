using ChatVerse.API.Extensions;
using ChatVerse.API.Services;
using ChatVerse.API.Services.StoryChain;
using ChatVerse.Domain.Entities;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace ChatVerse.API.Hubs;

// ============================================================
//  StoryChainHub — daily collaborative writing surface.
//
//  Client methods:
//    • GetCurrentChain()    → today's chain + queue state
//    • JoinQueue()          → enqueue myself, maybe assign turn
//    • LeaveQueue()         → withdraw
//    • AddSentence(text)    → write if it's my turn
//    • GetArchive()         → last 30 published chains
//
//  Server-push events:
//    • "TurnAssigned"       → fanned to the new turn-holder only
//    • "ContributionAdded"  → fanned to the chain group
//    • "QueueUpdated"       → fanned to the chain group
//    • "ChainLocked"        → fanned to the chain group
//
//  Turn semantics (Redis-backed):
//    • Sorted set "sc:q:{chainId}" — waiting users (score = join ts)
//    • String "sc:t:{chainId}"     — current turn holder, TTL 600s
//    • On turn expiry (no submission in 10 min), the NEXT inbound
//      hub call promotes the queue head — we don't run a separate
//      cron just for this.
// ============================================================

[Authorize]
public class StoryChainHub : Hub
{
    private readonly MongoService _mongo;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<StoryChainHub> _logger;

    private static readonly TimeSpan TurnTtl = TimeSpan.FromMinutes(10);
    private const int MaxSentenceChars = MongoService.StoryChainMaxSentenceChars;

    public StoryChainHub(
        MongoService mongo,
        IConnectionMultiplexer redis,
        ILogger<StoryChainHub> logger)
    {
        _mongo = mongo;
        _redis = redis;
        _logger = logger;
    }

    // ─── Redis helpers ──────────────────────────────────────────

    private IDatabase Db => _redis.GetDatabase();
    private static string QueueKey(string chainId) => $"sc:q:{chainId}";
    private static string TurnKey(string chainId)  => $"sc:t:{chainId}";

    /// <summary>The IST "today" string used as the chain key.
    /// Server is UTC; we shift by +5:30 to land in IST.</summary>
    private static string TodayIst() =>
        DateTime.UtcNow.AddHours(5).AddMinutes(30).ToString("yyyy-MM-dd");

    /// <summary>Group name for fan-out — one SignalR group per chain.</summary>
    private static string GroupOf(string chainId) => $"story-chain:{chainId}";

    // ─── Lifecycle ──────────────────────────────────────────────

    public override async Task OnConnectedAsync()
    {
        // Auto-join the current chain's group so all clients receive
        // ContributionAdded / QueueUpdated / ChainLocked without an
        // explicit subscribe call.
        var chain = await EnsureChainAsync();
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupOf(chain.Id!));
        await base.OnConnectedAsync();
    }

    // ─── State + reads ──────────────────────────────────────────

    public async Task<object> GetCurrentChain()
    {
        var chain = await EnsureChainAsync();
        // Opportunistic turn promotion — if the turn key has expired
        // and queue has members, promote one BEFORE returning state.
        await TryPromoteNextAsync(chain.Id!);
        return await BuildChainStateAsync(chain);
    }

    public async Task<object> GetArchive()
    {
        var chains = await _mongo.GetPublishedChainsAsync(30);
        return new
        {
            count  = chains.Count,
            chains = chains.Select(ToArchiveDto).ToList(),
        };
    }

    // ─── Queue ──────────────────────────────────────────────────

    public async Task<object> JoinQueue()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var chain = await EnsureChainAsync();

        if (chain.Status != "active")
            throw new HubException("Today's chain is closed.");

        if (chain.ContributorUserIds.Contains(meId))
            throw new HubException("You've already added your sentence to this chain. Come back tomorrow.");

        // Idempotent add — same user re-joining doesn't shuffle their score.
        var queueKey = QueueKey(chain.Id!);
        var existingScore = await Db.SortedSetScoreAsync(queueKey, meId);
        if (existingScore is null)
        {
            await Db.SortedSetAddAsync(queueKey, meId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await BroadcastQueueUpdateAsync(chain.Id!);
        }

        await TryPromoteNextAsync(chain.Id!);
        return await BuildChainStateAsync(chain);
    }

    public async Task<object> LeaveQueue()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var chain = await EnsureChainAsync();
        var queueKey = QueueKey(chain.Id!);

        var removed = await Db.SortedSetRemoveAsync(queueKey, meId);
        if (removed)
        {
            // If I was the active turn-holder, vacate so next person gets it.
            var turnHolder = (string?)await Db.StringGetAsync(TurnKey(chain.Id!));
            if (turnHolder == meId)
            {
                await Db.KeyDeleteAsync(TurnKey(chain.Id!));
                await TryPromoteNextAsync(chain.Id!);
            }
            else
            {
                await BroadcastQueueUpdateAsync(chain.Id!);
            }
        }
        return await BuildChainStateAsync(chain);
    }

    // ─── Add a sentence ─────────────────────────────────────────

    public async Task<object> AddSentence(string text)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var meName = JwtService.GetUsername(Context.User!);
        var chain = await EnsureChainAsync();

        if (chain.Status != "active")
            throw new HubException("Today's chain is closed.");

        if (string.IsNullOrWhiteSpace(text))
            throw new HubException("Your sentence can't be empty.");
        if (text.Length > MaxSentenceChars)
            throw new HubException($"Keep it under {MaxSentenceChars} characters.");

        // Turn check.
        var currentTurn = (string?)await Db.StringGetAsync(TurnKey(chain.Id!));
        if (currentTurn != meId)
        {
            // Self-heal: if the turn key has expired and I'm the queue
            // head, promote me. If I'm not the head, reject.
            if (currentTurn is null)
            {
                var promoted = await TryPromoteNextAsync(chain.Id!);
                if (promoted != meId)
                    throw new HubException("Not your turn yet.");
            }
            else
            {
                throw new HubException("Not your turn yet.");
            }
        }

        var contribution = new StoryContribution
        {
            SentenceText   = text.Trim(),
            AuthorUserId   = meId,
            AuthorUsername = meName,
            AddedAt        = DateTime.UtcNow,
        };

        var ok = await _mongo.AppendStoryContributionAsync(chain.Id!, contribution);
        if (!ok)
        {
            // Either already contributed, chain locked, or 50 cap hit.
            // Surface a tight error and clear the stale turn so the
            // next person can move on.
            await Db.KeyDeleteAsync(TurnKey(chain.Id!));
            throw new HubException("Couldn't save your sentence — the chain may have just locked.");
        }

        // Remove me from queue + clear turn.
        await Db.SortedSetRemoveAsync(QueueKey(chain.Id!), meId);
        await Db.KeyDeleteAsync(TurnKey(chain.Id!));

        // Fresh-read to get the new sentence count.
        var fresh = await _mongo.GetActiveStoryChainAsync(chain.PromptDate);
        var sentenceCount = fresh?.Sentences.Count ?? chain.Sentences.Count + 1;

        // Broadcast the new contribution to the chain group.
        await Clients.Group(GroupOf(chain.Id!)).SendAsync("ContributionAdded", new
        {
            chainId        = chain.Id,
            sentence       = contribution.SentenceText,
            authorUsername = contribution.AuthorUsername,
            addedAt        = contribution.AddedAt,
            totalSentences = sentenceCount,
        });

        // Did we hit the cap? Lock + publish.
        if (sentenceCount >= MongoService.StoryChainMaxContributions)
        {
            await _mongo.LockStoryChainAsync(chain.Id!);
            await _mongo.PublishStoryChainAsync(chain.Id!);
            await Clients.Group(GroupOf(chain.Id!)).SendAsync("ChainLocked", new
            {
                chainId = chain.Id,
                reason  = "cap_reached",
            });
        }
        else
        {
            await TryPromoteNextAsync(chain.Id!);
        }

        return await BuildChainStateAsync(fresh ?? chain);
    }

    // ─── Turn assignment ────────────────────────────────────────

    /// <summary>If no turn-holder, pop queue head and set the turn
    /// key with TTL. Returns the new turn holder's userId, or null
    /// if the queue is empty. Broadcasts TurnAssigned to the new
    /// holder + QueueUpdated to everyone.</summary>
    private async Task<string?> TryPromoteNextAsync(string chainId)
    {
        var turnKey = TurnKey(chainId);

        // SETNX-style: only set if missing. If something is already
        // there (turn live OR just took it), skip.
        var alreadySet = await Db.StringGetAsync(turnKey);
        if (alreadySet.HasValue) return (string?)alreadySet;

        // Pop the queue head atomically.
        var head = await Db.SortedSetPopAsync(QueueKey(chainId), Order.Ascending);
        if (head is null) return null;

        var userId = (string?)head.Value.Element;
        if (string.IsNullOrEmpty(userId)) return null;

        var ok = await Db.StringSetAsync(turnKey, userId, TurnTtl, When.NotExists);
        if (!ok)
        {
            // Lost the race — someone else just claimed the turn.
            // Push the user back onto the queue head.
            await Db.SortedSetAddAsync(
                QueueKey(chainId), userId,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1_000);
            return (string?)await Db.StringGetAsync(turnKey);
        }

        // Push a TurnAssigned event JUST to the new holder.
        await Clients.User(userId).SendAsync("TurnAssigned", new
        {
            chainId,
            expiresAt = DateTime.UtcNow.Add(TurnTtl),
        });

        await BroadcastQueueUpdateAsync(chainId);
        return userId;
    }

    private async Task BroadcastQueueUpdateAsync(string chainId)
    {
        var queueLength = await Db.SortedSetLengthAsync(QueueKey(chainId));
        var currentTurn = (string?)await Db.StringGetAsync(TurnKey(chainId));
        await Clients.Group(GroupOf(chainId)).SendAsync("QueueUpdated", new
        {
            chainId,
            queueLength,
            hasActiveTurn = currentTurn is not null,
        });
    }

    // ─── Helpers ────────────────────────────────────────────────

    private async Task<StoryChain> EnsureChainAsync()
    {
        var todayIst = TodayIst();
        var existing = await _mongo.GetActiveStoryChainAsync(todayIst);
        if (existing is not null) return existing;

        // Self-heal: the background service usually creates today's
        // chain at 3pm IST, but if someone connects before that or
        // the service is lagging, we create it on-demand here.
        var prompt = StoryPromptBank.PickForDate(todayIst);
        return await _mongo.EnsureDailyStoryChainAsync(todayIst, prompt);
    }

    private async Task<object> BuildChainStateAsync(StoryChain chain)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var queueKey = QueueKey(chain.Id!);
        var turnHolder = (string?)await Db.StringGetAsync(TurnKey(chain.Id!));
        var myScore = await Db.SortedSetScoreAsync(queueKey, meId);
        var queueLength = await Db.SortedSetLengthAsync(queueKey);
        long? myPosition = null;
        if (myScore is not null)
        {
            var rank = await Db.SortedSetRankAsync(queueKey, meId, Order.Ascending);
            myPosition = rank.HasValue ? rank.Value + 1 : null;
        }

        return new
        {
            id              = chain.Id,
            promptDate      = chain.PromptDate,
            prompt          = chain.Prompt,
            status          = chain.Status,
            sentences       = chain.Sentences.Select(ToContributionDto).ToList(),
            totalSentences  = chain.Sentences.Count,
            cap             = MongoService.StoryChainMaxContributions,
            queueLength,
            myQueuePosition = myPosition,
            myTurn          = turnHolder == meId,
            hasActiveTurn   = turnHolder is not null,
            hasContributed  = chain.ContributorUserIds.Contains(meId),
        };
    }

    private static object ToContributionDto(StoryContribution c) => new
    {
        sentence       = c.SentenceText,
        authorUsername = c.AuthorUsername,
        addedAt        = c.AddedAt,
    };

    private static object ToArchiveDto(StoryChain c) => new
    {
        id             = c.Id,
        promptDate     = c.PromptDate,
        prompt         = c.Prompt,
        totalSentences = c.Sentences.Count,
        publishedAt    = c.PublishedAt,
        sentences      = c.Sentences.Select(ToContributionDto).ToList(),
    };
}
