using ChatVerse.Domain.Entities;

namespace ChatVerse.API.Services.Tokens;

// ============================================================
//  IPaymentGateway — the abstraction that lets us swap from the
//  current MockPaymentGateway to a real Razorpay (or Stripe, or
//  any other PSP) without touching the hub or ledger.
//
//  Three operations:
//    1. CreateAsync  — given a TopupOrder row that\'s just been
//                       inserted in "created" state, do whatever
//                       the gateway needs to do (e.g. POST to its
//                       /orders API), then return the redirect URL
//                       and gateway-side order ref.
//    2. ConfirmAsync — settle a pending order to "succeeded". For
//                       Mock this is called by our own ConfirmMockPayment
//                       hub method. For Razorpay this would be called
//                       by a webhook controller after signature verify.
//    3. CancelAsync  — user cancelled out of the flow.
// ============================================================

public sealed record CreateOrderResult(string GatewayRef, string RedirectUrl);

public interface IPaymentGateway
{
    string ProviderKey { get; }

    Task<CreateOrderResult> CreateAsync(TokenTopupOrder order, CancellationToken ct = default);

    /// <summary>For real gateways this is invoked by the webhook
    /// after signature verification. For mock it\'s invoked by the
    /// hub\'s ConfirmMockPayment method. Returns true if the order
    /// just transitioned to succeeded.</summary>
    Task<bool> ConfirmAsync(string orderId, string? gatewayRef, CancellationToken ct = default);

    Task<bool> CancelAsync(string orderId, CancellationToken ct = default);
}
