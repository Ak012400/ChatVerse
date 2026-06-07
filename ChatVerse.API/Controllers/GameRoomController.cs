using ChatVerse.API.Extensions;
using ChatVerse.API.Models.Games;
using ChatVerse.API.Services.Games;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;
using System.Text;

namespace ChatVerse.API.Controllers;

// ============================================================
//  GameRoomController — REST surface for the Gaming Hall.
//
//  Flow:
//    1) Browse:   GET  /api/game-rooms              → list active rooms
//    2) Create:   POST /api/game-rooms              → host creates room
//    3) Join:     POST /api/game-rooms/{slug}/join  → register as player/spectator
//    4) Open:     GET  /api/game-rooms/{slug}       → fetch fresh snapshot
//                 then connect to GameHub.JoinRoom(slug) to attach to
//                 the live event stream
//
//  Why REST for join instead of putting it on the hub?
//    Cap checks + persistence happen here, and we want them to be
//    transactional — a refresh shouldn't accidentally double-add a
//    user. REST also lets us return rich error codes (403 capacity,
//    404 not found, 409 already-ended) that map cleanly to HTTP, vs
//    overloading SignalR's free-form error payloads.
// ============================================================

[ApiController]
[Route("api/game-rooms")]
[Authorize]
public sealed class GameRoomController : ControllerBase
{
    private readonly GameSessionRegistry _registry;
    private readonly ILogger<GameRoomController> _logger;

    // Bounds. Surface them as constants so tests can reference them.
    public const int MinPlayers = 2;
    public const int MaxPlayers = 8;
    public const int MinQuestions = 5;
    public const int MaxQuestions = 20;
    public const int MinSecondsPerQuestion = 10;
    public const int MaxSecondsPerQuestion = 30;

    public GameRoomController(
        GameSessionRegistry registry,
        ILogger<GameRoomController> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    // ───────────────────────────────────────────────────────────────
    //  GET /api/game-rooms/health
    //  Anonymous diagnostic endpoint. If this returns 200 with the
    //  expected version string, we know the Gaming Hall assembly is
    //  loaded and routed — which also means GameHub (same assembly,
    //  registered side-by-side in Program.cs) IS in this build.
    //
    //  Bump `version` on each deploy that ships meaningful changes
    //  so we can tell, from a single fetch, whether we're hitting a
    //  fresh build or a stale cached image.
    // ───────────────────────────────────────────────────────────────
    [HttpGet("health")]
    [AllowAnonymous]
    public IActionResult Health()
    {
        return Ok(new
        {
            gameHubRegistered = true,
            timestamp = DateTime.UtcNow,
            version = "phase1-gaming-v1",
        });
    }

    // ───────────────────────────────────────────────────────────────
    //  GET /api/game-rooms?source={chatSlug}
    //  List currently-active rooms. Optional `source` query param
    //  scopes the list to games launched from a specific chat room
    //  (used by the chat-embedded Active Games panel). Ended rooms
    //  are filtered out and private rooms are hidden.
    // ───────────────────────────────────────────────────────────────
    [HttpGet]
    public async Task<IActionResult> ListActive(
        [FromQuery] string? source,
        CancellationToken ct)
    {
        var rooms = await _registry.ListActiveAsync(ct, sourceChatSlug: source, publicOnly: true);
        rooms = rooms.Where(r => r.Status != GameStatus.Ended).ToList();
        return Ok(ApiResponse<List<GameRoomDto>>.Ok(rooms));
    }

    // ───────────────────────────────────────────────────────────────
    //  GET /api/game-rooms/random?source={chatSlug}&type={Quiz|Jokes}
    //  Get-or-create the always-on random room for this (chat, type)
    //  pair. Caller is auto-added as Player (or Spectator if full).
    //  Idempotent: repeat calls return the same slug.
    // ───────────────────────────────────────────────────────────────
    [HttpGet("random")]
    public async Task<IActionResult> GetOrJoinRandom(
        [FromQuery] string source,
        [FromQuery] GameType type,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source))
            return BadRequest(ApiResponse.Fail("source query param is required."));
        if (type != GameType.Quiz && type != GameType.Jokes)
            return BadRequest(ApiResponse.Fail("Random rooms only support Quiz and Jokes."));

        var userId = JwtService.GetUserId(User).ToString();
        var username = JwtService.GetUsername(User);

        var session = await _registry.GetOrCreateRandomAsync(source, type, userId, username, ct);

        // Always attempt to add the caller — JoinAsync is idempotent
        // and handles the "you were already in" case cleanly.
        var join = await session.JoinAsync(userId, username, GameRole.Player, ct);

        return Ok(ApiResponse<object>.Ok(new
        {
            slug = session.Slug,
            assignedRole = join.AssignedRole.ToString(),
            note = join.Reason,
        }));
    }

    // ───────────────────────────────────────────────────────────────
    //  POST /api/game-rooms
    //  Host creates a new room. Returns the slug so the client can
    //  redirect into /games/{slug} and start its hub handshake.
    // ───────────────────────────────────────────────────────────────
    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateGameRoomRequest req, CancellationToken ct)
    {
        if (req is null)
            return BadRequest(ApiResponse.Fail("Missing body."));
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(ApiResponse.Fail("Room name is required."));
        if (req.Name.Length > 40)
            return BadRequest(ApiResponse.Fail("Room name too long (40 max)."));

        // Allow the launched game types. Ludo remains gated until
        // its session lands in Sprint B.
        if (req.Type != GameType.Quiz && req.Type != GameType.Jokes && req.Type != GameType.Chess)
            return BadRequest(ApiResponse.Fail(
                $"Game type '{req.Type}' is not available yet. Try Quiz, Jokes, or Chess."));

        if (req.MaxPlayers < MinPlayers || req.MaxPlayers > MaxPlayers)
            return BadRequest(ApiResponse.Fail(
                $"maxPlayers must be {MinPlayers}-{MaxPlayers}."));
        if (req.QuestionCount < MinQuestions || req.QuestionCount > MaxQuestions)
            return BadRequest(ApiResponse.Fail(
                $"questionCount must be {MinQuestions}-{MaxQuestions}."));
        if (req.SecondsPerQuestion < MinSecondsPerQuestion ||
            req.SecondsPerQuestion > MaxSecondsPerQuestion)
            return BadRequest(ApiResponse.Fail(
                $"secondsPerQuestion must be {MinSecondsPerQuestion}-{MaxSecondsPerQuestion}."));

        var userId = JwtService.GetUserId(User).ToString();
        var username = JwtService.GetUsername(User);

        var slug = MakeSlug(req.Name);
        var settings = new QuizSettings
        {
            Category = req.Category,
            Difficulty = req.Difficulty,
            QuestionCount = req.QuestionCount,
            SecondsPerQuestion = req.SecondsPerQuestion,
            MaxPlayers = req.MaxPlayers,
        };

        var session = await _registry.CreateAsync(
            slug, req.Type, req.Name.Trim(), userId, username, settings, ct,
            isPublic: req.IsPublic,
            sourceChatSlug: req.SourceChatSlug,
            isRandom: false);

        // Auto-join the host as a Player so they don't have to
        // double-tap (create → join).
        await session.JoinAsync(userId, username, GameRole.Player, ct);

        _logger.LogInformation(
            "Game room {Slug} created by {User} (type={Type}, isPublic={IsPublic}, source={Source})",
            slug, username, req.Type, req.IsPublic, req.SourceChatSlug ?? "(none)");

        return Ok(ApiResponse<object>.Ok(new
        {
            slug,
            name = req.Name.Trim(),
            type = req.Type,
            isPublic = req.IsPublic,
        }));
    }

    // ───────────────────────────────────────────────────────────────
    //  POST /api/game-rooms/{slug}/join
    //  Register as player or spectator. Idempotent — repeat calls
    //  return the existing assignment.
    // ───────────────────────────────────────────────────────────────
    [HttpPost("{slug}/join")]
    public async Task<IActionResult> Join(
        string slug,
        [FromBody] JoinGameRoomRequest req,
        CancellationToken ct)
    {
        var session = await _registry.GetOrLoadAsync(slug, ct);
        if (session is null)
            return NotFound(ApiResponse.Fail("Room not found or expired."));

        var userId = JwtService.GetUserId(User).ToString();
        var username = JwtService.GetUsername(User);

        var result = await session.JoinAsync(userId, username, req.Role, ct);
        if (!result.Accepted)
            return Conflict(ApiResponse.Fail(result.Reason ?? "Could not join."));

        return Ok(ApiResponse<object>.Ok(new
        {
            slug,
            assignedRole = result.AssignedRole.ToString(),
            note = result.Reason,
        }));
    }

    // ───────────────────────────────────────────────────────────────
    //  GET /api/game-rooms/{slug}
    //  Fetch a fresh snapshot (no hub connection needed). Used by the
    //  room page on first paint while the hub handshake is in flight.
    // ───────────────────────────────────────────────────────────────
    [HttpGet("{slug}")]
    public async Task<IActionResult> Snapshot(string slug, CancellationToken ct)
    {
        var session = await _registry.GetOrLoadAsync(slug, ct);
        if (session is null)
            return NotFound(ApiResponse.Fail("Room not found or expired."));

        var userId = JwtService.GetUserId(User).ToString();
        var snap = await session.GetSnapshotAsync(userId, ct);
        return Ok(ApiResponse<GameRoomSnapshot>.Ok(snap));
    }

    // ───────────────────────────────────────────────────────────────
    //  Helpers
    // ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Slug = sanitised-name + 6-char random suffix.
    /// The suffix avoids collisions when two users name their rooms
    /// "Quiz Night" simultaneously, while keeping the prefix legible
    /// in URLs and analytics.
    /// </summary>
    private static string MakeSlug(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (ch == ' ' || ch == '-' || ch == '_') sb.Append('-');
            // anything else (emoji, punctuation, unicode) → skipped
        }
        // Collapse multi-dashes + trim ends.
        var cleaned = string.Join('-',
            sb.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrEmpty(cleaned)) cleaned = "room";
        if (cleaned.Length > 24) cleaned = cleaned[..24];

        // 6-char base32-ish suffix (no l/0/1 to avoid confusion).
        const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
        var suffix = new char[6];
        for (int i = 0; i < suffix.Length; i++)
            suffix[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];

        return $"q-{cleaned}-{new string(suffix)}";
    }
}
