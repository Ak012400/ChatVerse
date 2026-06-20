using ChatVerse.Domain.Entities;
using ChatVerse.Infrastructure.Persistence.MongoDB;

namespace ChatVerse.API.Services.Tokens;

// ============================================================
//  MockPaymentGateway — fake-but-realistic payment processor.
//
//  Flow (matches what a real Razorpay/Stripe integration looks like):
//    1. CreateAsync → generates a fake gateway-ref (mock_xxxxx)
//       and points RedirectUrl at our own frontend route
//       /tokens/mock-gateway?orderId=...&ref=... The frontend
//       renders a faux-checkout page (card form, Pay button).
//    2. User clicks Pay → frontend hits TokensHub.ConfirmMockPayment
//       → hub calls IPaymentGateway.ConfirmAsync → THIS class flips
//       the order to "succeeded" + asks the ledger service to credit
//       the configured token amount.
//    3. CancelAsync → flips status to "cancelled".
//
//  Switching to a real Razorpay implementation later is a single-
//  file change: add RazorpayPaymentGateway : IPaymentGateway, swap
//  the DI registration in Program.cs, write a webhook controller
//  that calls ConfirmAsync. No domain or hub changes needed.
// ============================================================

public sealed class MockPaymentGateway : IPaymentGateway
{
    private readonly MongoService _mongo;
    private readonly TokenLedgerService _ledger;
    private readonly ILogger<MockPaymentGateway> _logger;

    public string ProviderKey => "mock";

    public MockPaymentGateway(MongoService mongo, TokenLedgerService ledger, ILogger<MockPaymentGateway> logger)
    {
        _mongo = mongo;
        _ledger = ledger;
        _logger = logger;
    }

    public Task<CreateOrderResult> CreateAsync(TokenTopupOrder order, CancellationToken ct = default)
    {
        // Pretend gateway-issued ref. In Razorpay this comes back from
        // the /orders API; here we generate our own.
        var gatewayRef = "mock_" + Guid.NewGuid().ToString("N").Substring(0, 12);
        // Frontend route — relative URL so it works on any environment.
        var redirectUrl = $"/tokens/mock-gateway?orderId={order.Id}&ref={gatewayRef}";
        return Task.FromResult(new CreateOrderResult(gatewayRef, redirectUrl));
    }

    public async Task<bool> ConfirmAsync(string orderId, string? gatewayRef, CancellationToken ct = default)
    {
        var order = await _mongo.GetTokenTopupOrderAsync(orderId);
        if (order is null) return false;
        if (order.Status == "succeeded") return false;       // already done — idempotent no-op
        if (order.Status == "cancelled" || order.Status == "failed") return false;

        // Atomic state flip first — only the call that successfully
        // transitions the order proceeds to credit tokens. This is
        // the dedup gate against double-credit on webhook replays.
        var transitioned = await _mongo.SetTokenTopupOrderStatusAsync(
            orderId, "pending", gatewayRef);
        if (!transitioned) return false;

        // Credit ledger.
        var (ok, _, error) = await _ledger.CreditAsync(
            order.UserId,
            order.TokenAmount,
            TokenReasons.Topup,
            note: $"topup:{order.PackKey}",
            gatewayRef: orderId);

        if (!ok)
        {
            _logger.LogError("Token credit failed for order {Order}: {Err}", orderId, error);
            await _mongo.SetTokenTopupOrderStatusAsync(orderId, "failed");
            return false;
        }

        await _mongo.SetTokenTopupOrderStatusAsync(orderId, "succeeded");
        return true;
    }

    public async Task<bool> CancelAsync(string orderId, CancellationToken ct = default)
    {
        return await _mongo.SetTokenTopupOrderStatusAsync(orderId, "cancelled");
    }
}
