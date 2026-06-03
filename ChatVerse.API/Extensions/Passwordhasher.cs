using System.Security.Cryptography;
using System.Text;

namespace ChatVerse.API.Extensions;

/// <summary>
/// Password hashing service.
///
/// Default: <see cref="Hash"/> uses BCrypt with cost factor 12.
///
/// Backward compatibility: <see cref="Verify"/> also recognises the legacy
/// 64-character lowercase-hex SHA-256 format that older accounts were
/// created with. When a legacy hash matches, the caller is told via
/// <c>needsRehash</c> so the new BCrypt hash can be persisted on the
/// successful login — quietly migrating users without forcing resets.
/// </summary>
public static class PasswordHasher
{
    private const int BCryptWorkFactor = 12;

    /// <summary>Hash a password with BCrypt for storage.</summary>
    public static string Hash(string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Password cannot be empty", nameof(password));

        return BCrypt.Net.BCrypt.HashPassword(password, BCryptWorkFactor);
    }

    /// <summary>
    /// Verify a password against a stored hash. Supports both new BCrypt
    /// and legacy SHA-256 hex hashes. The <c>needsRehash</c> flag is true
    /// when a legacy SHA-256 hash was successfully matched — callers should
    /// then re-hash with <see cref="Hash"/> and update the DB.
    /// </summary>
    public static (bool IsValid, bool NeedsRehash) Verify(string password, string storedHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(storedHash))
            return (false, false);

        // BCrypt hashes always start with $2a$, $2b$, $2x$, or $2y$
        if (storedHash.Length >= 4 && storedHash[0] == '$' && storedHash[1] == '2')
        {
            try
            {
                var ok = BCrypt.Net.BCrypt.Verify(password, storedHash);
                return (ok, false);
            }
            catch
            {
                return (false, false);
            }
        }

        // Legacy: 64-char lowercase hex SHA-256
        if (storedHash.Length == 64 && IsLowercaseHex(storedHash))
        {
            var sha = LegacySha256Hex(password);
            // Constant-time compare to avoid leaking length-based timing info
            if (FixedTimeEquals(sha, storedHash))
                return (true, true);
            return (false, false);
        }

        // Unknown format — fail closed
        return (false, false);
    }

    private static string LegacySha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool IsLowercaseHex(string s)
    {
        foreach (var c in s)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                return false;
        }
        return true;
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
