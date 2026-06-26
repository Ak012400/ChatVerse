using ChatVerse.API.Extensions;
using ChatVerse.Domain.Entities;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using ChatVerse.Infrastructure.Services.LiveKit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Security.Cryptography;
using System.Text;

namespace ChatVerse.API.Hubs;

// ============================================================
//  GhostRoomHub — second per-template Mehfil specialisation.
//
//  STANDALONE per the per-feature isolation policy. Mounts at
//  /hubs/ghost-room. Doesn't touch MehfilHub, GhostDateHub (weekly
//  feature), or any other surface.
//
//  Coexistence: /hubs/ghost-date (weekly Thursday auto-pair) stays
//  intact. This hub serves the new MEHFIL TEMPLATE "ghost_date".
//
//  Anonymity contract:
//   • Voyager identities (V1 / V2 / ...) are server-allocated per
//     room. UserId is NEVER serialised in pair chat or public push.
//   • Real bios (name + age + gender + interested-in + short bio)
//     flow ONLY through GetNominationsForMatchmaker (matchmaker-gated)
//     AND NominationRaisedPrivate push (matchmaker sub-group).
//   • mutual_reveal — real usernames surface ONLY when BOTH voyagers
//     vote reveal at round end.
//
//  LiveKit:
//   • When a pair is created, server reserves the LiveKit room name
//     `ghost-pair-{pairId}`. Voyagers in that pair (and only them)
//     can call GetPairLiveKitToken to mint an audio token.
//   • Audio-only by default (CanPublish=true, video disabled at
//     the LiveKit Room option level on the client side).
// ============================================================

[Authorize]
public class GhostRoomHub : Hub
{
    private readonly MongoService _mongo;
    private readonly LiveKitService _liveKit;
    private readonly IHubContext<GhostRoomHub> _hubCtx;
    private readonly ILogger<GhostRoomHub> _logger;

    private const int MaxBioChars = 50;
    private const int MaxNameChars = 80;
    private const int MaxChatChars = 1000;
    private const int MaxReasonChars = 200;

    private static readonly int[] AllowedCapacities = new[] { 4, 6, 8, 10, 12 };
    private static readonly int[] AllowedRoundDurations = new[] { 5, 10, 15, 20 };
    private static readonly HashSet<string> AllowedGenders = new(StringComparer.OrdinalIgnoreCase) { "male", "female", "other", "" };
    private static readonly HashSet<string> AllowedInterests = new(StringComparer.OrdinalIgnoreCase) { "male", "female", "other", "any" };
    private static readonly HashSet<string> AllowedPrivacy = new(StringComparer.OrdinalIgnoreCase) { "public", "private" };

    public GhostRoomHub(
        MongoService mongo,
        LiveKitService liveKit,
        IHubContext<GhostRoomHub> hubCtx,
        ILogger<GhostRoomHub> logger)
    {
        _mongo = mongo;
        _liveKit = liveKit;
        _hubCtx = hubCtx;
        _logger = logger;
    }

    private static string RoomGroup(string roomId) => $"ghost:{roomId}";
    private static string MatchmakerGroup(string roomId) => $"ghost-mm:{roomId}";
    private static string PairGroup(string pairId) => $"ghost-pair:{pairId}";
    private static string PairLivekitRoomName(string pairId) => $"ghost-pair-{pairId}";

    /// <summary>Authorise + return (room, isMatchmaker). Throws if not
    /// a ghost room, not allowed in, etc.</summary>
    private async Task<(MehfilRoom room, GhostRoomConfig config, bool isMatchmaker)> AuthorizeAsync(string roomId, CancellationToken ct)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();

        var room = await _mongo.GetMehfilRoomByIdAsync(roomId, ct);
        if (room is null) throw new HubException("Room not found.");
        if (!string.Equals(room.TemplateKind, "ghost_date", StringComparison.OrdinalIgnoreCase))
            throw new HubException("Not a ghost-dating room.");

        if (await _mongo.IsBannedFromGhostAsync(roomId, meId, ct))
            throw new HubException("You are banned from this room.");

        var config = await _mongo.GetGhostRoomConfigAsync(roomId, ct)
                     ?? new GhostRoomConfig
                     {
                         RoomId               = roomId,
                         MatchmakerUserId     = room.HostUserId,
                         Privacy              = "public",
                         MaxVoyagers          = 8,
                         RoundDurationMinutes = 10,
                         CreatedAt            = DateTime.UtcNow,
                     };
        var isMatchmaker = string.Equals(room.HostUserId, meId, StringComparison.OrdinalIgnoreCase);
        return (room, config, isMatchmaker);
    }

    private void RequireMatchmaker(bool isMatchmaker)
    {
        if (!isMatchmaker) throw new HubException("Matchmaker only.");
    }

    // ─── Connection lifecycle ──────────────────────────────────────

    public async Task<object> JoinGhostRoom(string roomId, string? inviteCode)
    {
        var ct = Context.ConnectionAborted;
        var (room, config, isMatchmaker) = await AuthorizeAsync(roomId, ct);

        // Private rooms require the matching invite code (host bypasses).
        if (!isMatchmaker
            && string.Equals(config.Privacy, "private", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(inviteCode)
                || !string.Equals(config.InviteCode, inviteCode, StringComparison.Ordinal))
            {
                throw new HubException("Invite code required for this private room.");
            }
        }

        var meId = JwtService.GetUserId(Context.User!).ToString();
        var voyager = await _mongo.GetOrCreateVoyagerAsync(roomId, meId, ct);

        await Groups.AddToGroupAsync(Context.ConnectionId, RoomGroup(roomId), ct);
        if (isMatchmaker)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, MatchmakerGroup(roomId), ct);
        }

        // If user is currently in an active pair, also join that pair's group.
        var activePair = await _mongo.GetActivePairForUserAsync(roomId, meId, ct);
        if (activePair is not null)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, PairGroup(activePair.Id!), ct);
        }

        return await BuildRoomStateAsync(room, config, voyager, isMatchmaker, activePair, ct);
    }

    public async Task LeaveGhostRoom(string roomId)
    {
        var ct = Context.ConnectionAborted;
        var meId = JwtService.GetUserId(Context.User!).ToString();
        await _mongo.MarkVoyagerLeftAsync(roomId, meId, ct);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, RoomGroup(roomId), ct);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, MatchmakerGroup(roomId), ct);
    }

    public async Task<object> GetRoomState(string roomId, string? inviteCode)
        => await JoinGhostRoom(roomId, inviteCode);

    // ─── Matchmaker: config + rounds ──────────────────────────────

    public async Task<object> ConfigureRoom(
        string roomId, string privacy, int maxVoyagers, int roundDurationMinutes, bool autoPair = false)
    {
        var ct = Context.ConnectionAborted;
        var (room, _, isMatchmaker) = await AuthorizeAsync(roomId, ct);
        RequireMatchmaker(isMatchmaker);

        if (!AllowedPrivacy.Contains(privacy)) throw new HubException("Privacy public/private only.");
        if (!AllowedCapacities.Contains(maxVoyagers))
            throw new HubException("Capacity 4/6/8/10/12.");
        if (!AllowedRoundDurations.Contains(roundDurationMinutes))
            throw new HubException("Round 5/10/15/20 min.");

        var c = new GhostRoomConfig
        {
            RoomId               = roomId,
            MatchmakerUserId     = room.HostUserId,
            Privacy              = privacy.ToLowerInvariant(),
            InviteCode           = string.Equals(privacy, "private", StringComparison.OrdinalIgnoreCase)
                                   ? GenerateInviteCode()
                                   : null,
            MaxVoyagers          = maxVoyagers,
            RoundDurationMinutes = roundDurationMinutes,
            // AUTO-PAIR mode: server pairs voyagers continuously as they
            // raise hands, no matchmaker intervention. Public Ghost Rooms
            // typically run this way (Omegle-style); private rooms can
            // also opt in if the host wants to step back.
            AutoPair             = autoPair,
            CreatedAt            = DateTime.UtcNow,
        };
        await _mongo.UpsertGhostRoomConfigAsync(c, ct);

        var voyager = await _mongo.GetOrCreateVoyagerAsync(roomId, room.HostUserId, ct);
        var state = await BuildRoomStateAsync(room, c, voyager, isMatchmaker: true, activePair: null, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync("RoomConfigured", state, ct);
        return state;
    }

    // ─── Voyager: nomination ──────────────────────────────────────

    public async Task<string?> NominateForPairing(
        string roomId, string realName, int age, string gender, string interestedIn, string shortBio)
    {
        var ct = Context.ConnectionAborted;
        var (_, _, _) = await AuthorizeAsync(roomId, ct);

        var meId = JwtService.GetUserId(Context.User!).ToString();
        var voyager = await _mongo.GetVoyagerAsync(roomId, meId, ct)
                      ?? throw new HubException("Join the room first.");
        if (voyager.Status == "paired") throw new HubException("Already paired.");

        var trimmedName = (realName ?? "").Trim();
        if (trimmedName.Length == 0 || trimmedName.Length > MaxNameChars)
            throw new HubException("Real name 1-80 chars required.");
        if (age < 13 || age > 120) throw new HubException("Age 13-120 required.");
        var g = (gender ?? "").Trim().ToLowerInvariant();
        if (!AllowedGenders.Contains(g)) throw new HubException("Gender male/female/other.");
        var ii = (interestedIn ?? "any").Trim().ToLowerInvariant();
        if (!AllowedInterests.Contains(ii)) throw new HubException("Interested-in male/female/other/any.");
        var bio = (shortBio ?? "").Trim();
        if (bio.Length > MaxBioChars) bio = bio.Substring(0, MaxBioChars);

        var nomination = new GhostNomination
        {
            RoomId       = roomId,
            UserId       = meId,
            VoyagerTag   = voyager.VoyagerTag,
            RealName     = trimmedName,
            Age          = age,
            Gender       = g,
            InterestedIn = ii,
            ShortBio     = bio,
            Status       = "pending",
            RaisedAt     = DateTime.UtcNow,
        };
        var inserted = await _mongo.InsertGhostNominationAsync(nomination, ct);
        if (inserted is null) return null;

        await _mongo.SetVoyagerStatusAsync(voyager.Id!, "nominated", ct);

        // ── AUTO-PAIR (Public Ghost Room mode) ────────────────────
        // If the room is configured with AutoPair=true, immediately
        // look for any OTHER pending nomination and create the pair
        // server-side without waiting for a matchmaker. First-come-
        // first-paired so the wait stays minimal.
        var cfg = await _mongo.GetGhostRoomConfigAsync(roomId, ct);
        if (cfg is not null && cfg.AutoPair)
        {
            var pending = await _mongo.GetPendingGhostNominationsForMatchmakerAsync(roomId, ct);
            // Exclude self — we want a DIFFERENT user.
            var partner = pending.FirstOrDefault(p =>
                !string.Equals(p.UserId, meId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(p.Id, inserted.Id, StringComparison.OrdinalIgnoreCase));
            if (partner is not null)
            {
                var room = await _mongo.GetMehfilRoomByIdAsync(roomId, ct);
                if (room is not null)
                {
                    var pair = await CreatePairAsync(roomId, inserted, partner, ct);
                    await PushPairCreated(roomId, pair, inserted, partner, ct);
                    return inserted.Id;
                }
            }
        }

        // Public push: count only.
        var count = await _mongo.CountPendingGhostNominationsAsync(roomId, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId))
            .SendAsync("NominationCountChanged", new { count }, ct);

        // Matchmaker private push: full bio (with VoyagerTag for mapping).
        await _hubCtx.Clients.Group(MatchmakerGroup(roomId))
            .SendAsync("NominationRaisedPrivate", ShapePrivilegedNomination(inserted), ct);

        return inserted.Id;
    }

    public async Task WithdrawNomination(string roomId)
    {
        var ct = Context.ConnectionAborted;
        var (_, _, _) = await AuthorizeAsync(roomId, ct);
        var meId = JwtService.GetUserId(Context.User!).ToString();
        await _mongo.WithdrawGhostNominationAsync(roomId, meId, ct);

        var voyager = await _mongo.GetVoyagerAsync(roomId, meId, ct);
        if (voyager is not null) await _mongo.SetVoyagerStatusAsync(voyager.Id!, "lobby", ct);

        var count = await _mongo.CountPendingGhostNominationsAsync(roomId, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId))
            .SendAsync("NominationCountChanged", new { count }, ct);
        await _hubCtx.Clients.Group(MatchmakerGroup(roomId))
            .SendAsync("NominationWithdrawnPrivate", new { userId = meId }, ct);
    }

    public async Task<List<object>> GetNominationsForMatchmaker(string roomId)
    {
        var ct = Context.ConnectionAborted;
        var (_, _, isMatchmaker) = await AuthorizeAsync(roomId, ct);
        RequireMatchmaker(isMatchmaker);

        var rows = await _mongo.GetPendingGhostNominationsForMatchmakerAsync(roomId, ct);
        return rows.Select(ShapePrivilegedNomination).ToList();
    }

    // ─── Matchmaker: pair assignment ──────────────────────────────

    /// <summary>Manual pair assignment. Both nominations must be pending.</summary>
    public async Task<string?> AssignPair(string roomId, string nominationAId, string nominationBId)
    {
        var ct = Context.ConnectionAborted;
        var (room, _, isMatchmaker) = await AuthorizeAsync(roomId, ct);
        RequireMatchmaker(isMatchmaker);

        var a = await _mongo.GetGhostNominationAsync(nominationAId, ct);
        var b = await _mongo.GetGhostNominationAsync(nominationBId, ct);
        if (a is null || b is null || a.RoomId != roomId || b.RoomId != roomId)
            throw new HubException("Nomination not found.");
        if (a.Status != "pending" || b.Status != "pending")
            throw new HubException("Nominations already resolved.");
        if (string.Equals(a.UserId, b.UserId, StringComparison.OrdinalIgnoreCase))
            throw new HubException("Cannot pair voyager with themselves.");

        var pair = await CreatePairAsync(roomId, a, b, ct);

        await _mongo.LogGhostMatchmakerActionAsync(new GhostMatchmakerAction
        {
            RoomId           = roomId,
            MatchmakerUserId = room.HostUserId,
            TargetUserId     = a.UserId,
            ActionType       = "assign_pair",
            Reason           = $"with={b.UserId}",
            At               = DateTime.UtcNow,
        }, ct);

        await PushPairCreated(roomId, pair, a, b, ct);
        return pair.Id;
    }

    /// <summary>Auto-pair all currently-pending nominations randomly.
    /// Honours interest-balanced flag for soft preference matching.</summary>
    public async Task<int> AutoPairRemaining(string roomId, bool interestBalanced)
    {
        var ct = Context.ConnectionAborted;
        var (room, _, isMatchmaker) = await AuthorizeAsync(roomId, ct);
        RequireMatchmaker(isMatchmaker);

        var nominations = await _mongo.GetPendingGhostNominationsForMatchmakerAsync(roomId, ct);
        if (nominations.Count < 2) return 0;

        // Shuffle Fisher-Yates.
        var pool = nominations.OrderBy(_ => Guid.NewGuid()).ToList();

        // Interest-balanced: try to pair voyagers where each prefers the
        // other's gender (best-effort, falls back to random for leftovers).
        var pairs = new List<(GhostNomination, GhostNomination)>();
        if (interestBalanced)
        {
            var unmatched = pool.ToList();
            for (var i = 0; i < unmatched.Count; i++)
            {
                if (unmatched[i] is null) continue;
                var a = unmatched[i]!;
                var partnerIdx = -1;
                for (var j = i + 1; j < unmatched.Count; j++)
                {
                    if (unmatched[j] is null) continue;
                    var b = unmatched[j]!;
                    if (Compatible(a, b))
                    {
                        partnerIdx = j;
                        break;
                    }
                }
                if (partnerIdx > 0)
                {
                    pairs.Add((a, unmatched[partnerIdx]!));
                    unmatched[i] = null!;
                    unmatched[partnerIdx] = null!;
                }
            }
            // Random-pair the leftovers.
            var leftovers = unmatched.Where(x => x is not null).Cast<GhostNomination>().ToList();
            for (var i = 0; i + 1 < leftovers.Count; i += 2)
            {
                pairs.Add((leftovers[i], leftovers[i + 1]));
            }
        }
        else
        {
            for (var i = 0; i + 1 < pool.Count; i += 2)
            {
                pairs.Add((pool[i], pool[i + 1]));
            }
        }

        var created = 0;
        foreach (var (a, b) in pairs)
        {
            var pair = await CreatePairAsync(roomId, a, b, ct);
            await PushPairCreated(roomId, pair, a, b, ct);
            created++;
        }
        await _mongo.LogGhostMatchmakerActionAsync(new GhostMatchmakerAction
        {
            RoomId           = roomId,
            MatchmakerUserId = room.HostUserId,
            TargetUserId     = "",
            ActionType       = "auto_pair",
            Reason           = $"created={created},interestBalanced={interestBalanced}",
            At               = DateTime.UtcNow,
        }, ct);
        return created;
    }

    private async Task<GhostPair> CreatePairAsync(string roomId, GhostNomination a, GhostNomination b, CancellationToken ct)
    {
        var pair = new GhostPair
        {
            RoomId            = roomId,
            RoundNumber       = 1,
            VoyagerAUserId    = a.UserId,
            VoyagerATag       = a.VoyagerTag,
            VoyagerBUserId    = b.UserId,
            VoyagerBTag       = b.VoyagerTag,
            LivekitRoomName   = "", // set after we know the id
            CreatedAt         = DateTime.UtcNow,
            StartedAt         = DateTime.UtcNow,
        };
        await _mongo.InsertGhostPairAsync(pair, ct);
        pair.LivekitRoomName = PairLivekitRoomName(pair.Id!);

        // Persist the LiveKit room name now that we have the pair id.
        await _mongo.SetGhostPairStartedAsync(pair.Id!, ct);

        // Mark both nominations as paired + flip voyager status.
        await _mongo.SetGhostNominationStatusAsync(a.Id!, "paired", ct);
        await _mongo.SetGhostNominationStatusAsync(b.Id!, "paired", ct);

        var va = await _mongo.GetVoyagerAsync(roomId, a.UserId, ct);
        var vb = await _mongo.GetVoyagerAsync(roomId, b.UserId, ct);
        if (va is not null) await _mongo.SetVoyagerStatusAsync(va.Id!, "paired", ct);
        if (vb is not null) await _mongo.SetVoyagerStatusAsync(vb.Id!, "paired", ct);

        // Create LiveKit room for this pair (audio-only, 2 participants).
        try
        {
            await _liveKit.CreateRoomAsync(
                pair.LivekitRoomName,
                emptyTimeoutSeconds: 60,
                maxParticipants: 2);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LiveKit room create failed for ghost pair {PairId}", pair.Id);
        }

        return pair;
    }

    private async Task PushPairCreated(string roomId, GhostPair pair, GhostNomination a, GhostNomination b, CancellationToken ct)
    {
        // Push to both voyagers (user-scoped). Includes a private invite
        // to the pair's SignalR group so they receive chat events.
        var payload = ShapePair(pair);
        await _hubCtx.Clients.User(a.UserId).SendAsync("PairAssigned", payload, ct);
        await _hubCtx.Clients.User(b.UserId).SendAsync("PairAssigned", payload, ct);

        // Public hall broadcast — counts only (no userId / no bios).
        var count = await _mongo.CountPendingGhostNominationsAsync(roomId, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId))
            .SendAsync("NominationCountChanged", new { count }, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId))
            .SendAsync("PairCreatedPublic", new
            {
                pairId        = pair.Id,
                voyagerATag   = pair.VoyagerATag,
                voyagerBTag   = pair.VoyagerBTag,
                roundNumber   = pair.RoundNumber,
            }, ct);
    }

    /// <summary>Subscribe the calling connection to a pair's SignalR
    /// group. Must already be a voyager IN that pair (server checks).</summary>
    public async Task JoinPairGroup(string pairId)
    {
        var ct = Context.ConnectionAborted;
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var pair = await _mongo.GetGhostPairAsync(pairId, ct);
        if (pair is null) throw new HubException("Pair not found.");
        if (!string.Equals(pair.VoyagerAUserId, meId, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(pair.VoyagerBUserId, meId, StringComparison.OrdinalIgnoreCase))
            throw new HubException("Not your pair.");
        await Groups.AddToGroupAsync(Context.ConnectionId, PairGroup(pairId), ct);
    }

    // ─── Pair chat ────────────────────────────────────────────────

    public async Task<string?> SendPairMessage(string pairId, string content)
    {
        var ct = Context.ConnectionAborted;
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var pair = await _mongo.GetGhostPairAsync(pairId, ct);
        if (pair is null) throw new HubException("Pair not found.");
        if (pair.EndedAt.HasValue) throw new HubException("Pair ended.");

        var trimmed = (content ?? "").Trim();
        if (trimmed.Length == 0) return null;
        if (trimmed.Length > MaxChatChars) trimmed = trimmed.Substring(0, MaxChatChars);

        string senderTag;
        if (string.Equals(pair.VoyagerAUserId, meId, StringComparison.OrdinalIgnoreCase))
            senderTag = pair.VoyagerATag;
        else if (string.Equals(pair.VoyagerBUserId, meId, StringComparison.OrdinalIgnoreCase))
            senderTag = pair.VoyagerBTag;
        else throw new HubException("Not your pair.");

        var msg = new GhostPairMessage
        {
            PairId           = pairId,
            SenderUserId     = meId,
            SenderVoyagerTag = senderTag,
            Content          = trimmed,
            CreatedAt        = DateTime.UtcNow,
        };
        var saved = await _mongo.InsertGhostPairMessageAsync(msg, ct);

        await _hubCtx.Clients.Group(PairGroup(pairId)).SendAsync(
            "PairMessage", ShapePairMessage(saved), ct);
        return saved.Id;
    }

    // ─── Pair reveal vote + outcome ───────────────────────────────

    public async Task<object?> VoteRevealAtRoundEnd(string pairId, bool wantsReveal)
    {
        var ct = Context.ConnectionAborted;
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var pair = await _mongo.GetGhostPairAsync(pairId, ct);
        if (pair is null) throw new HubException("Pair not found.");
        if (pair.EndedAt.HasValue) throw new HubException("Pair already ended.");
        if (!string.Equals(pair.VoyagerAUserId, meId, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(pair.VoyagerBUserId, meId, StringComparison.OrdinalIgnoreCase))
            throw new HubException("Not your pair.");

        await _mongo.SetGhostPairRevealVoteAsync(pairId, meId, wantsReveal, ct);

        // If both have voted, resolve the outcome immediately.
        var fresh = await _mongo.GetGhostPairAsync(pairId, ct);
        if (fresh is not null && fresh.VoyagerAWantsReveal is not null && fresh.VoyagerBWantsReveal is not null)
        {
            var resolved = await _mongo.ResolveGhostPairOutcomeAsync(pairId, ct);
            if (resolved?.Outcome == "mutual_reveal")
            {
                // Capture both real usernames for the reveal record.
                // (We don't store username on GhostPair to keep it lean —
                //  look up from the original nominations.)
                var nA = await _mongo.GetVoyagerAsync(resolved.RoomId, resolved.VoyagerAUserId, ct);
                var nB = await _mongo.GetVoyagerAsync(resolved.RoomId, resolved.VoyagerBUserId, ct);
                await _mongo.InsertGhostRevealAsync(new GhostReveal
                {
                    RoomId        = resolved.RoomId,
                    PairId        = resolved.Id!,
                    UserAId       = resolved.VoyagerAUserId,
                    UserAUsername = nA?.UserId ?? "",
                    UserBId       = resolved.VoyagerBUserId,
                    UserBUsername = nB?.UserId ?? "",
                    RevealedAt    = DateTime.UtcNow,
                }, ct);
            }

            var payload = ShapeOutcome(resolved!);
            await _hubCtx.Clients.Group(PairGroup(pairId)).SendAsync("PairOutcome", payload, ct);
            return payload;
        }

        return null;
    }

    // ─── Matchmaker: round end / rotate ───────────────────────────

    public async Task EndRound(string roomId)
    {
        var ct = Context.ConnectionAborted;
        var (room, _, isMatchmaker) = await AuthorizeAsync(roomId, ct);
        RequireMatchmaker(isMatchmaker);

        await _mongo.EndAllActivePairsAsync(roomId, ct);
        await _mongo.LogGhostMatchmakerActionAsync(new GhostMatchmakerAction
        {
            RoomId           = roomId,
            MatchmakerUserId = room.HostUserId,
            TargetUserId     = "",
            ActionType       = "end_round",
            At               = DateTime.UtcNow,
        }, ct);

        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync(
            "RoundEnded", new { roomId, at = DateTime.UtcNow }, ct);
    }

    // ─── LiveKit token (per voyager, per pair) ────────────────────

    public async Task<object> GetPairLiveKitToken(string pairId)
    {
        var ct = Context.ConnectionAborted;
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var meUsername = JwtService.GetUsername(Context.User!);

        var pair = await _mongo.GetGhostPairAsync(pairId, ct);
        if (pair is null) throw new HubException("Pair not found.");
        if (pair.EndedAt.HasValue) throw new HubException("Pair ended.");
        if (!string.Equals(pair.VoyagerAUserId, meId, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(pair.VoyagerBUserId, meId, StringComparison.OrdinalIgnoreCase))
            throw new HubException("Not your pair.");

        // Use the user's VoyagerTag as the LiveKit identity so even at
        // the wire level the other voyager only sees the tag, not the
        // real userId. (LiveKit "identity" surfaces to peers.)
        var myTag = string.Equals(pair.VoyagerAUserId, meId, StringComparison.OrdinalIgnoreCase)
            ? pair.VoyagerATag
            : pair.VoyagerBTag;

        var token = _liveKit.GenerateToken(
            roomName: pair.LivekitRoomName,
            userId: myTag,
            username: myTag,
            canPublish: true,
            canSubscribe: true);

        return new
        {
            roomName   = pair.LivekitRoomName,
            serverUrl  = _liveKit.GetServerUrl(),
            token,
            voyagerTag = myTag,
            meUsername, // server-side audit only — the client shouldn't show this
        };
    }

    // ─── Moderation ───────────────────────────────────────────────

    public async Task HighlightGoodVibe(string roomId, string targetUserId)
    {
        var ct = Context.ConnectionAborted;
        var (room, _, isMatchmaker) = await AuthorizeAsync(roomId, ct);
        RequireMatchmaker(isMatchmaker);

        await _mongo.LogGhostMatchmakerActionAsync(new GhostMatchmakerAction
        {
            RoomId           = roomId,
            MatchmakerUserId = room.HostUserId,
            TargetUserId     = targetUserId,
            ActionType       = "highlight",
            At               = DateTime.UtcNow,
        }, ct);

        var voyager = await _mongo.GetVoyagerAsync(roomId, targetUserId, ct);
        await _hubCtx.Clients.Group(RoomGroup(roomId)).SendAsync(
            "VoyagerHighlighted",
            new { targetVoyagerTag = voyager?.VoyagerTag ?? "", at = DateTime.UtcNow },
            ct);
    }

    public async Task KickFromGhost(string roomId, string targetUserId, string? reason)
    {
        var ct = Context.ConnectionAborted;
        var (room, _, isMatchmaker) = await AuthorizeAsync(roomId, ct);
        RequireMatchmaker(isMatchmaker);
        await _mongo.MarkVoyagerLeftAsync(roomId, targetUserId, ct);
        await _mongo.LogGhostMatchmakerActionAsync(new GhostMatchmakerAction
        {
            RoomId           = roomId,
            MatchmakerUserId = room.HostUserId,
            TargetUserId     = targetUserId,
            ActionType       = "kick",
            Reason           = ClampReason(reason),
            At               = DateTime.UtcNow,
        }, ct);
        await _hubCtx.Clients.User(targetUserId).SendAsync(
            "GhostKicked", new { roomId, reason = ClampReason(reason) }, ct);
    }

    public async Task BanFromGhost(string roomId, string targetUserId, string? reason)
    {
        var ct = Context.ConnectionAborted;
        var (room, _, isMatchmaker) = await AuthorizeAsync(roomId, ct);
        RequireMatchmaker(isMatchmaker);

        await _mongo.BanFromGhostAsync(new GhostBan
        {
            RoomId           = roomId,
            UserId           = targetUserId,
            MatchmakerUserId = room.HostUserId,
            Reason           = ClampReason(reason),
            CreatedAt        = DateTime.UtcNow,
        }, ct);
        await _mongo.MarkVoyagerLeftAsync(roomId, targetUserId, ct);
        await _mongo.LogGhostMatchmakerActionAsync(new GhostMatchmakerAction
        {
            RoomId           = roomId,
            MatchmakerUserId = room.HostUserId,
            TargetUserId     = targetUserId,
            ActionType       = "ban",
            Reason           = ClampReason(reason),
            At               = DateTime.UtcNow,
        }, ct);
        await _hubCtx.Clients.User(targetUserId).SendAsync(
            "GhostBanned", new { roomId, reason = ClampReason(reason) }, ct);
    }

    // ─── State builder ────────────────────────────────────────────

    private async Task<object> BuildRoomStateAsync(
        MehfilRoom room, GhostRoomConfig config, GhostVoyager me,
        bool isMatchmaker, GhostPair? activePair, CancellationToken ct)
    {
        var voyagers = await _mongo.GetActiveVoyagersAsync(room.Id!, ct);
        var pendingCount = await _mongo.CountPendingGhostNominationsAsync(room.Id!, ct);

        // Pair chat: only include if I'm currently paired (privacy).
        List<GhostPairMessage> pairMessages = new();
        if (activePair is not null)
        {
            pairMessages = await _mongo.GetPairMessagesAsync(activePair.Id!, limit: 80, ct);
        }

        return new
        {
            room = new
            {
                id           = room.Id,
                title        = room.Title,
                hostUserId   = room.HostUserId,
                hostUsername = room.HostUsername,
                status       = room.Status,
            },
            config = new
            {
                privacy              = config.Privacy,
                inviteCode           = isMatchmaker ? config.InviteCode : null, // only matchmaker sees the code
                maxVoyagers          = config.MaxVoyagers,
                roundDurationMinutes = config.RoundDurationMinutes,
                autoPair             = config.AutoPair,
            },
            isMatchmaker,
            me = new
            {
                voyagerTag = me.VoyagerTag,
                status     = me.Status,
            },
            voyagers = voyagers.Select(v => new
            {
                voyagerTag = v.VoyagerTag,
                status     = v.Status,
                joinedAt   = v.JoinedAt,
            }).ToList(),
            pendingNominationCount = pendingCount,
            activePair = activePair is null ? null : ShapePair(activePair),
            pairMessages = pairMessages.Select(ShapePairMessage).ToList(),
        };
    }

    private static object ShapePair(GhostPair p) => new
    {
        id           = p.Id,
        roundNumber  = p.RoundNumber,
        voyagerATag  = p.VoyagerATag,
        voyagerBTag  = p.VoyagerBTag,
        startedAt    = p.StartedAt,
        endedAt      = p.EndedAt,
        outcome      = p.Outcome,
    };

    private static object ShapePairMessage(GhostPairMessage m) => new
    {
        id               = m.Id,
        pairId           = m.PairId,
        senderVoyagerTag = m.SenderVoyagerTag,
        // NOTE: senderUserId intentionally NOT serialised — clients
        // would never need it (the tag is the wire-visible identity).
        content          = m.Content,
        createdAt        = m.CreatedAt,
    };

    private static object ShapeOutcome(GhostPair p) => new
    {
        pairId      = p.Id,
        outcome     = p.Outcome,
        // For mutual_reveal we let the FRONTEND ask for usernames via
        // a follow-up call (GhostReveal row) so this push stays compact.
        endedAt     = p.EndedAt,
    };

    /// <summary>Privileged nomination shape — bios + tag. Matchmaker only.</summary>
    private static object ShapePrivilegedNomination(GhostNomination n) => new
    {
        id           = n.Id,
        userId       = n.UserId,
        voyagerTag   = n.VoyagerTag,
        realName     = n.RealName,
        age          = n.Age,
        gender       = n.Gender,
        interestedIn = n.InterestedIn,
        shortBio     = n.ShortBio,
        raisedAt     = n.RaisedAt,
    };

    private static bool Compatible(GhostNomination a, GhostNomination b)
    {
        // a "wants" gender of b AND b "wants" gender of a (or either is "any").
        var aOk = string.Equals(a.InterestedIn, "any", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(a.InterestedIn, b.Gender, StringComparison.OrdinalIgnoreCase);
        var bOk = string.Equals(b.InterestedIn, "any", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(b.InterestedIn, a.Gender, StringComparison.OrdinalIgnoreCase);
        return aOk && bOk;
    }

    private static string GenerateInviteCode()
    {
        // 8-char base32-ish — sufficient entropy for invite-only rooms;
        // not a security token.
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        var bytes = RandomNumberGenerator.GetBytes(8);
        var sb = new StringBuilder(8);
        foreach (var bb in bytes) sb.Append(alphabet[bb % alphabet.Length]);
        return sb.ToString();
    }

    private static string? ClampReason(string? reason)
    {
        if (string.IsNullOrEmpty(reason)) return null;
        return reason.Length > MaxReasonChars ? reason.Substring(0, MaxReasonChars) : reason;
    }
}
