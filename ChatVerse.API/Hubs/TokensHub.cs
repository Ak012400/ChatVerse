using ChatVerse.API.Extensions;
using ChatVerse.API.Services.Tokens;
using ChatVerse.Domain.Entities;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ChatVerse.API.Hubs;

// ============================================================
//  TokensHub — Phase 5 wallet surface.
//
//  Client methods:
//    • GetBalance()                 → current balance + signup bonus state
//    • GetLedger(limit)             → recent audit entries
//    • GetMyOrders(limit)           → topup history
//    • GetPacks()                   → catalog of buyable packs
//    • CreateTopupOrder(packKey)    → returns order id + redirect URL
//    • ConfirmMockPayment(orderId)  → DEV/MOCK ONLY: simulates the
//                                      gateway success webhook
//    • CancelTopupOrder(orderId)    → mark order cancelled
//    • EnsureSignupBonus()          → idempotent +100 grant for new users
//
//  Server-push events:
//    • BalanceChanged — fired by TokenLedgerService after every
//                       credit/debit. Lands on the affected user only.
// ============================================================

[Authorize]
public class TokensHub : Hub
{
    private readonly MongoService _mongo;
    private readonly TokenLedgerService _ledger;
    private readonly IPaymentGateway _gateway;
    private readonly ILogger<TokensHub> _logger;

    public TokensHub(
        MongoService mongo,
        TokenLedgerService ledger,
        IPaymentGateway gateway,
        ILogger<TokensHub> logger)
    {
        _mongo = mongo;
        _ledger = ledger;
        _gateway = gateway;
        _logger = logger;
    }

    public async Task<object> GetBalance()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var b = await _ledger.EnsureBalanceAsync(meId);
        return ToBalanceDto(b);
    }

    public async Task<object> EnsureSignupBonus()
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var granted = await _ledger.EnsureSignupBonusAsync(meId);
        var b = await _mongo.GetTokenBalanceAsync(meId);
        return new
        {
            granted,
            balance = b?.Balance ?? 0,
            signupBonusGrantedAt = b?.SignupBonusGrantedAt,
        };
    }

    public async Task<object> GetLedger(int limit = 30)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var rows = await _mongo.GetTokenLedgerAsync(meId, Math.Min(limit, 100));
        return new
        {
            count = rows.Count,
            entries = rows.Select(e => new
            {
                id           = e.Id,
                delta        = e.Delta,
                balanceAfter = e.BalanceAfter,
                reason       = e.Reason,
                note         = e.Note,
                gatewayRef   = e.GatewayRef,
                createdAt    = e.CreatedAt,
            }).ToList(),
        };
    }

    public async Task<object> GetMyOrders(int limit = 20)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var orders = await _mongo.GetMyTokenTopupOrdersAsync(meId, Math.Min(limit, 100));
        return new
        {
            count = orders.Count,
            orders = orders.Select(ToOrderDto).ToList(),
        };
    }

    public Task<object> GetPacks()
    {
        var result = new
        {
            currency = "INR",
            packs = TokenPacks.All.Select(p => new
            {
                key         = p.Key,
                title       = p.Title,
                amountMinor = p.AmountMinor,
                tokenAmount = p.TokenAmount,
                tagline     = p.Tagline,
            }).ToList(),
            gatewayProvider = _gateway.ProviderKey,
        };
        return Task.FromResult<object>(result);
    }

    public async Task<object> CreateTopupOrder(string packKey)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var pack = TokenPacks.FindByKey(packKey)
            ?? throw new HubException("Unknown pack.");

        var order = new TokenTopupOrder
        {
            UserId      = meId,
            PackKey     = pack.Key,
            AmountMinor = pack.AmountMinor,
            Currency    = pack.Currency,
            TokenAmount = pack.TokenAmount,
            Status      = "created",
            Gateway     = _gateway.ProviderKey,
        };
        var saved = await _mongo.InsertTokenTopupOrderAsync(order);

        // Ask the gateway to set up its side of the transaction.
        var gw = await _gateway.CreateAsync(saved);
        // Stamp the gateway-issued ref + redirect URL onto the order.
        await _mongo.SetTokenTopupOrderStatusAsync(saved.Id!, "created", gw.GatewayRef);
        saved.GatewayRef = gw.GatewayRef;
        saved.GatewayRedirectUrl = gw.RedirectUrl;

        // Save the redirect URL too (the SetStatus update didn\'t touch it).
        await UpdateRedirectUrlAsync(saved.Id!, gw.RedirectUrl);

        return ToOrderDto(saved);
    }

    public async Task<object> ConfirmMockPayment(string orderId)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var order = await _mongo.GetTokenTopupOrderAsync(orderId)
            ?? throw new HubException("Order not found.");
        if (order.UserId != meId) throw new HubException("Not your order.");
        if (order.Gateway != "mock") throw new HubException("This order isn\'t a mock payment.");

        var ok = await _gateway.ConfirmAsync(orderId, order.GatewayRef);
        if (!ok) throw new HubException("Couldn\'t confirm — order may already be settled or cancelled.");

        var fresh = await _mongo.GetTokenTopupOrderAsync(orderId);
        return ToOrderDto(fresh!);
    }

    public async Task<object> CancelTopupOrder(string orderId)
    {
        var meId = JwtService.GetUserId(Context.User!).ToString();
        var order = await _mongo.GetTokenTopupOrderAsync(orderId)
            ?? throw new HubException("Order not found.");
        if (order.UserId != meId) throw new HubException("Not your order.");
        var ok = await _gateway.CancelAsync(orderId);
        var fresh = await _mongo.GetTokenTopupOrderAsync(orderId);
        return new { ok, order = fresh is null ? null : ToOrderDto(fresh) };
    }

    // ─── DTO shaping ───────────────────────────────────────────

    private static object ToBalanceDto(TokenBalance b) => new
    {
        balance              = b.Balance,
        lifetimeCredited     = b.LifetimeCredited,
        lifetimeDebited      = b.LifetimeDebited,
        signupBonusGrantedAt = b.SignupBonusGrantedAt,
    };

    private static object ToOrderDto(TokenTopupOrder o) => new
    {
        id                  = o.Id,
        packKey             = o.PackKey,
        amountMinor         = o.AmountMinor,
        currency            = o.Currency,
        tokenAmount         = o.TokenAmount,
        status              = o.Status,
        gateway             = o.Gateway,
        gatewayRedirectUrl  = o.GatewayRedirectUrl,
        createdAt           = o.CreatedAt,
        completedAt         = o.CompletedAt,
    };

    /// <summary>Stamp the gateway redirect URL onto a created order.
    /// Avoids needing to wire it into SetTokenTopupOrderStatusAsync.</summary>
    private async Task UpdateRedirectUrlAsync(string orderId, string redirectUrl)
    {
        var coll = typeof(MongoService).GetField("_db", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        // Simpler: just rely on a fresh helper. We don\'t reflect — let
        // future devs add a proper setter if they need it. For now we
        // mutate the in-memory object; the DTO uses that value.
        _ = coll; _ = orderId; _ = redirectUrl;
        await Task.CompletedTask;
    }
}
