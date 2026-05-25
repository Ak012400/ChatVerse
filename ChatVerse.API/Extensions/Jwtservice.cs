using ChatVerse.Domain.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace ChatVerse.API.Extensions;

public class JwtService
{
    private readonly string _secretKey;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly int _expiryHours;

    public JwtService(IConfiguration config)
    {
        _secretKey = config["Jwt:SecretKey"]!;
        _issuer = config["Jwt:Issuer"]!;
        _audience = config["Jwt:Audience"]!;
        _expiryHours = int.Parse(config["Jwt:ExpiryHours"] ?? "24");
    }

    /// <summary>
    /// Mint a JWT for a registered or guest user.
    /// </summary>
    public string GenerateToken(
        Guid userId,
        string username,
        bool isGuest,
        short trustScore,
        bool isEmailVerified,
        bool ageVerified)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(JwtClaims.UserId,          userId.ToString()),
            new Claim(JwtClaims.Username,         username),
            new Claim(JwtClaims.IsGuest,          isGuest.ToString().ToLower()),
            new Claim(JwtClaims.TrustScore,       trustScore.ToString()),
            new Claim(JwtClaims.IsEmailVerified,  isEmailVerified.ToString().ToLower()),
            new Claim(JwtClaims.AgeVerified,      ageVerified.ToString().ToLower()),
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddHours(_expiryHours),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Extract userId from HttpContext — use in controllers after [Authorize]
    /// </summary>
    public static Guid GetUserId(ClaimsPrincipal user)
    {
        var claim = user.FindFirst(JwtClaims.UserId)?.Value
                 ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.Parse(claim!);
    }

    public static string GetUsername(ClaimsPrincipal user)
        => user.FindFirst(JwtClaims.Username)?.Value ?? "";

    public static bool GetIsGuest(ClaimsPrincipal user)
        => user.FindFirst(JwtClaims.IsGuest)?.Value == "true";

    public static bool GetAgeVerified(ClaimsPrincipal user)
        => user.FindFirst(JwtClaims.AgeVerified)?.Value == "true";

    public static short GetTrustScore(ClaimsPrincipal user)
    {
        var val = user.FindFirst(JwtClaims.TrustScore)?.Value;
        return short.TryParse(val, out var score) ? score : (short)50;
    }
}