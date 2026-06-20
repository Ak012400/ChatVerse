namespace ChatVerse.API.Services.Tokens;

// ============================================================
//  TokenPacks — the locked catalog of buyable token packs.
//
//  AmountMinor = paise (so ₹49 = 4900). Keep integer to avoid FP.
//  TokenAmount = what the user receives.
//
//  Per VISION pricing tier breakdown:
//    Basic ₹49   → 60   tokens (~₹0.82/token)
//    Plus  ₹99   → 150  tokens (~₹0.66/token)  ← best value
//    Mega  ₹299  → 500  tokens (~₹0.60/token)
//    Whale ₹499  → 1000 tokens (~₹0.50/token)
// ============================================================

public sealed record TokenPack(
    string Key,
    string Title,
    int AmountMinor,
    string Currency,
    int TokenAmount,
    string? Tagline = null);

public static class TokenPacks
{
    public static readonly TokenPack[] All = new[]
    {
        new TokenPack("pack_49",  "Starter", 4900,  "INR",  60,  null),
        new TokenPack("pack_99",  "Plus",    9900,  "INR", 150,  "Best value"),
        new TokenPack("pack_299", "Mega",    29900, "INR", 500,  null),
        new TokenPack("pack_499", "Whale",   49900, "INR", 1000, null),
    };

    public static TokenPack? FindByKey(string key) => All.FirstOrDefault(p => p.Key == key);
}
