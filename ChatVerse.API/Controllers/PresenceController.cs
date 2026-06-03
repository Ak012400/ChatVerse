using ChatVerse.API.Extensions;
using ChatVerse.API.Models;
using ChatVerse.Infrastructure.Persistence.Redis;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatVerse.API.Controllers;

/// <summary>
/// Live presence metrics. Cheap to call (Redis SCARD) — frontend
/// polls every ~30s. Anonymous to render so even the landing page
/// can show "247 online now" as a social-proof signal.
/// </summary>
[ApiController]
[Route("api/presence")]
public class PresenceController : ControllerBase
{
    private readonly RedisService _redis;

    public PresenceController(RedisService redis)
    {
        _redis = redis;
    }

    [HttpGet("stats")]
    [AllowAnonymous]
    public async Task<IActionResult> Stats([FromQuery] string? rooms)
    {
        var global = await _redis.GetGlobalOnlineCountAsync();

        Dictionary<string, long> byRoom = new();
        if (!string.IsNullOrWhiteSpace(rooms))
        {
            var slugs = rooms.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (slugs.Length > 0)
                byRoom = await _redis.GetRoomOnlineCountsAsync(slugs);
        }

        return Ok(ApiResponse<object>.Ok(new
        {
            globalOnline = global,
            byRoom
        }));
    }
}
