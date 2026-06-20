using ChatVerse.API.Hubs;
using ChatVerse.Domain.Entities;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using Microsoft.AspNetCore.SignalR;

namespace ChatVerse.API.Services.Tokens;

// ============================================================
//  Locked reason codes for the ledger. Every credit/debit must
//  use one of these — keeps the audit log greppable.
// ============================================================
public static class TokenReasons
{
    public const string SignupBonus   = "signup_bonus";
    public const string DailyLogin    = "daily_login";    // future
    public const string StreakBonus   = "streak_bonus";   // future
    public const string Refer         = "refer_friend";   // future
    public const string Topup         = "topup";
    public const string TipSent       = "tip_sent";
    public const string TipReceived   = "tip_received";
    public const string MehfilEntry   = "mehfil_entry";   // future
    public const string PrizePyaar    = "prize_pyaar";    // future
    public const string PrizeCipher   = "prize_cipher";   // future
    public const string AdminGrant    = "admin_grant";
    public const string Refund        = "refund";
}

// ============================================================
//  TokenLedgerService — semantic wrapper around the Mongo
//  primitives in MongoService.ApplyTokenDeltaAsync. Every feature
//  that touches tokens should call one of these methods rather
//  than dealing with deltas directly — easier to grep for "who
//  spends tokens" and easier to wire side effects (push events,
//  audit logging) in one place.
//
//  Also owns the BalanceChanged SignalR push so the wallet UI
//  updates live without a re-fetch.
// ============================================================
public sealed class TokenLedgerService
{
    private readonly MongoService _mongo;
    private readonly IHubContext<TokensHub> _hubCtx;
    private readonly ILogger<TokenLedgerService> _logger;

    public TokenLedgerService(
        MongoService mongo,
        IHubContext<TokensHub> hubCtx,
        ILogger<TokenLedgerService> logger)
    {
        _mongo = mongo;
        _hubCtx = hubCtx;
        _logger = logger;
    }

    public Task<TokenBalance> EnsureBalanceAsync(string userId) =>
        _mongo.GetOrCreateTokenBalanceAsync(userId);

    public async Task<bool> EnsureSignupBonusAsync(string userId)
    {
        var granted = await _mongo.EnsureSignupBonusAsync(userId);
        if (granted) await PushBalanceAsync(userId);
        return granted;
    }

    public async Task<(bool Ok, int? NewBalance, string? Error)> CreditAsync(
        string userId, int amount, string reason, string? note = null, string? gatewayRef = null)
    {
        if (amount <= 0) return (false, null, "Credit amount must be positive.");
        var (ok, post, _, error) = await _mongo.ApplyTokenDeltaAsync(userId, amount, reason, note, gatewayRef);
        if (ok) await PushBalanceAsync(userId, post!.Balance);
        return (ok, post?.Balance, error);
    }

    public async Task<(bool Ok, int? NewBalance, string? Error)> DebitAsync(
        string userId, int amount, string reason, string? note = null, string? gatewayRef = null)
    {
        if (amount <= 0) return (false, null, "Debit amount must be positive.");
        var (ok, post, _, error) = await _mongo.ApplyTokenDeltaAsync(userId, -amount, reason, note, gatewayRef);
        if (ok) await PushBalanceAsync(userId, post!.Balance);
        return (ok, post?.Balance, error);
    }

    /// <summary>Paired debit + credit (e.g. a tip from sender to host).
    /// We deliberately keep them as two ledger rows even though they
    /// belong together — clearer audit + survives a partial failure
    /// because the second op is idempotent on its own.</summary>
    public async Task<(bool Ok, string? Error)> TransferAsync(
        string fromUserId, string toUserId, int amount,
        string debitReason, string creditReason,
        string? note = null)
    {
        var (debitOk, _, debitErr) = await DebitAsync(fromUserId, amount, debitReason, note);
        if (!debitOk) return (false, debitErr);
        var (creditOk, _, creditErr) = await CreditAsync(toUserId, amount, creditReason, note);
        if (!creditOk)
        {
            // Reverse the debit — log the rollback so audit is clean.
            await CreditAsync(fromUserId, amount, TokenReasons.Refund, $"rollback: {creditErr}");
            return (false, creditErr);
        }
        return (true, null);
    }

    private async Task PushBalanceAsync(string userId, int? balance = null)
    {
        var b = balance ?? (await _mongo.GetTokenBalanceAsync(userId))?.Balance;
        if (b is null) return;
        try
        {
            await _hubCtx.Clients.User(userId).SendAsync("BalanceChanged", new
            {
                balance = b.Value,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push BalanceChanged for user {User}", userId);
        }
    }
}
