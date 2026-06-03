using ChatVerse.API.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatVerse.API.Controllers;

/// <summary>
/// Hands the browser the ICE server list it should configure on
/// RTCPeerConnection. Keeping this server-side means we can rotate TURN
/// credentials, swap providers (Xirsys → Twilio at scale), and add
/// time-limited credentials without shipping a new frontend build.
///
/// Anonymous because guests do random 1-on-1 video too.
/// </summary>
[ApiController]
[Route("api/ice-servers")]
public class IceController : ControllerBase
{
    private readonly IConfiguration _config;

    public IceController(IConfiguration config)
    {
        _config = config;
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult Get()
    {
        var stun  = _config.GetSection("WebRtc:StunUrls").Get<string[]>() ?? Array.Empty<string>();
        var turn  = _config.GetSection("WebRtc:TurnUrls").Get<string[]>() ?? Array.Empty<string>();
        var user  = _config["WebRtc:TurnUsername"] ?? "";
        var cred  = _config["WebRtc:TurnCredential"] ?? "";

        var servers = new List<object>();

        if (stun.Length > 0)
            servers.Add(new { urls = stun });

        if (turn.Length > 0 && !string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(cred))
        {
            servers.Add(new
            {
                urls = turn,
                username = user,
                credential = cred,
            });
        }

        return Ok(ApiResponse<object>.Ok(new
        {
            iceServers = servers,
            // RTCPeerConnection accepts this verbatim as `{ iceServers: ... }`.
        }));
    }
}
