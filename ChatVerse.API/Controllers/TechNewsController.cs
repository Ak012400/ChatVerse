using ChatVerse.API.Extensions;
using ChatVerse.API.Services.Games;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatVerse.API.Controllers;

// ============================================================
//  TechNewsController — serves the Tech Talk chat room's
//  "Live News" sidebar.
//
//  Single endpoint with Redis caching baked into the provider;
//  no per-user state, no auth checks beyond standard JWT (we
//  keep this behind [Authorize] like the rest of the gaming
//  APIs — guests don't see Tech Talk in their sidebar anyway).
// ============================================================

[ApiController]
[Route("api/tech-news")]
[Authorize]
public sealed class TechNewsController : ControllerBase
{
    private readonly TechNewsProvider _provider;

    public TechNewsController(TechNewsProvider provider)
    {
        _provider = provider;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var items = await _provider.FetchAsync(ct);
        return Ok(ApiResponse<List<TechNewsItem>>.Ok(items));
    }
}
