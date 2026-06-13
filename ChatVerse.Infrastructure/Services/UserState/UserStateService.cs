using ChatVerse.Infrastructure.Persistence.PostgreSQL;
using ChatVerse.Infrastructure.Persistence.Redis;
using Microsoft.Extensions.Logging;

namespace ChatVerse.Infrastructure.Services.UserState;

/// <summary>
/// Reads trust_score / age_verified through a short-TTL Redis cache
/// (see <see cref="ChatVerse.Domain.Constants.RedisTTL.UserState"/>),
/// falling back to PostgreSQL on miss.
///
/// Hubs and controllers MUST use this instead of JWT claims so that
/// bans, trust penalties, and age verifications take effect within
/// seconds — not at token expiry. Anything that mutates trust or
/// age-verified should call <see cref="InvalidateAsync"/> immediately
/// so the next read pulls fresh data.
/// </summary>
public class UserStateService
{
    private readonly RedisService _redis;
    private readonly PostgresProcService _postgres;
    private readonly ILogger<UserStateService> _logger;

    public UserStateService(
        RedisService redis,
        PostgresProcService postgres,
        ILogger<UserStateService> logger)
    {
        _redis = redis;
        _postgres = postgres;
        _logger = logger;
    }

    public async Task<short> GetTrustScoreAsync(Guid userId)
    {
        var id = userId.ToString();
        var cached = await _redis.GetTrustScoreCacheAsync(id);
        if (cached.HasValue) return cached.Value;

        var (score, verified) = await SafeLoadAsync(userId);
        await _redis.SetTrustScoreCacheAsync(id, score);
        await _redis.SetAgeVerifiedCacheAsync(id, verified);
        return score;
    }

    public async Task<bool> GetAgeVerifiedAsync(Guid userId)
    {
        var id = userId.ToString();
        var cached = await _redis.GetAgeVerifiedCacheAsync(id);
        if (cached.HasValue) return cached.Value;

        var (score, verified) = await SafeLoadAsync(userId);
        await _redis.SetTrustScoreCacheAsync(id, score);
        await _redis.SetAgeVerifiedCacheAsync(id, verified);
        return verified;
    }

    /// <summary>
    /// Drop cached trust + age-verified values for a user. Call after
    /// applying a trust event, after a successful age verification gate,
    /// or any other state change.
    /// </summary>
    public Task InvalidateAsync(Guid userId)
        => _redis.InvalidateUserStateAsync(userId.ToString());

    private async Task<(short, bool)> SafeLoadAsync(Guid userId)
    {
        try
        {
            return await _postgres.GetUserStateAsync(userId);
        }
        catch (Exception ex)
        {
            // Fail closed — never grant elevated trust on DB failure.
            _logger.LogWarning(ex, "UserState load failed for {UserId} — defaulting", userId);
            return ((short)50, false);
        }
    }
}
