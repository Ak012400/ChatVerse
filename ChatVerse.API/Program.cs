using ChatVerse.API.Extensions;
using ChatVerse.API.Hubs;
using ChatVerse.API.Middleware;
using ChatVerse.API.Models;
using ChatVerse.API.Services;
using ChatVerse.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;
using System.Text;



try
{
    var builder = WebApplication.CreateBuilder(args);

    // ── Render / container PORT binding ───────────────────────────
    // Render injects a dynamic PORT env var and expects the app to
    // listen on http://0.0.0.0:$PORT. Locally we fall back to the
    // ports declared in launchSettings.json so dev unchanged.
    var portEnv = Environment.GetEnvironmentVariable("PORT");
    if (!string.IsNullOrWhiteSpace(portEnv))
    {
        builder.WebHost.UseUrls($"http://0.0.0.0:{portEnv}");
    }

    // ── Controllers ───────────────────────────────────────────────
    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new() { Title = "ChatVerse API", Version = "v1" });
        c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
            Scheme = "Bearer",
            BearerFormat = "JWT",
            In = Microsoft.OpenApi.Models.ParameterLocation.Header,
            Description = "Enter: Bearer {your JWT token}"
        });
        c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {{
        new Microsoft.OpenApi.Models.OpenApiSecurityScheme
        {
            Reference = new Microsoft.OpenApi.Models.OpenApiReference
            {
                Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                Id   = "Bearer"
            }
        },
        Array.Empty<string>()
    }});
    });

    // ── Infrastructure (PostgreSQL + MongoDB + Redis) ─────────────
    builder.Services.AddInfrastructure(builder.Configuration);
    // Program.cs
    builder.Services.Configure<VideoSettings>(builder.Configuration.GetSection("VideoSettings"));

    // ── JwtService ────────────────────────────────────────────────
    builder.Services.AddSingleton<JwtService>();

    // ── JWT Authentication ────────────────────────────────────────
    var jwtConfig = builder.Configuration.GetSection("Jwt");
    var secretKey = jwtConfig["SecretKey"]!;
    var issuer = jwtConfig["Issuer"]!;
    var audience = jwtConfig["Audience"]!;

    builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey)),
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };

        // SignalR sends JWT via query string.
        //
        // WebSocket upgrade requests can't carry Authorization headers
        // (browsers don't expose the API), so SignalR's JS client falls
        // back to passing the token as `?access_token=…`. We grab that
        // here and feed it into the bearer pipeline.
        //
        // The path guard ensures we don't accept query-string tokens for
        // regular HTTP endpoints (where headers ARE available and abuse
        // via leaked URLs is a real concern — query strings end up in
        // server access logs, referer headers, etc.).
        //
        // Using "/hubs" as the umbrella prefix instead of hard-coding
        // each hub name. Every new hub we add (GameHub today, more
        // tomorrow) auto-inherits the auth path rule with no edit here
        // — which is exactly the bug that made /hubs/game 401 before.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) &&
                    path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

    builder.Services.AddAuthorization();

    // ── Redis — single ConfigurationOptions for everything ────────
    var redisConnStr = builder.Configuration.GetConnectionString("Redis")!;

    // Parse URI format (rediss://user:pass@host:port) safely
    ConfigurationOptions redisConfig;
    if (redisConnStr.StartsWith("rediss://") || redisConnStr.StartsWith("redis://"))
    {
        // Upstash URI format — parse manually
        var uri = new Uri(redisConnStr);
        var host = uri.Host;
        var port = uri.Port > 0 ? uri.Port : 6379;
        var password = uri.UserInfo.Contains(':')
                       ? uri.UserInfo.Split(':', 2)[1]
                       : uri.UserInfo;
        var ssl = redisConnStr.StartsWith("rediss://");

        redisConfig = new ConfigurationOptions
        {
            EndPoints = { { host, port } },
            Password = password,
            Ssl = ssl,
            AbortOnConnectFail = false,
            ConnectTimeout = 5000,
            SyncTimeout = 5000
        };
    }
    else
    {
        // Already host:port,password=xxx format
        redisConfig = ConfigurationOptions.Parse(redisConnStr);
        redisConfig.AbortOnConnectFail = false;
    }

    // Register singleton multiplexer
    builder.Services.AddSingleton<IConnectionMultiplexer>(
        ConnectionMultiplexer.Connect(redisConfig));

    // ── SignalR + Redis Backplane ──────────────────────────────────
    builder.Services.AddSignalR(options =>
    {
        options.EnableDetailedErrors = builder.Environment.IsDevelopment();
        options.MaximumReceiveMessageSize = 5 *1024 * 1024;
        // ClientTimeoutInterval bumped 60 → 120s and KeepAliveInterval
        // 15 → 10s so a heavy JS thread on the client (e.g. NSFW model
        // loading, large file scan) doesn't trigger a spurious server-
        // side disconnect during a video call. The keepalive cadence
        // is also a bit tighter so we notice real drops sooner.
        options.ClientTimeoutInterval = TimeSpan.FromSeconds(120);
        options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        options.HandshakeTimeout = TimeSpan.FromSeconds(30);
    })
    .AddStackExchangeRedis(options =>
    {
        options.ConnectionFactory = async writer =>
        {
            var conn = await ConnectionMultiplexer.ConnectAsync(redisConfig, writer);
            return conn;
        };
        options.Configuration.ChannelPrefix = RedisChannel.Literal("ChatVerse");
    });

    // ── CORS ──────────────────────────────────────────────────────
    // Production origins are config-driven so we don't have to rebuild the
    // image whenever the frontend gets a new domain. Set "Cors:Origins"
    // (string[]) in appsettings.Production.json OR via env var
    // `Cors__Origins__0=https://yourdomain.com` (one per index).
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("ChatVerseCors", policy =>
        {
            if (builder.Environment.IsDevelopment())
            {
                policy.WithOrigins(
                          "http://localhost:5173", "https://localhost:5173",
                          "http://localhost:5174", "https://localhost:5174")
                      .AllowAnyHeader()
                      .AllowAnyMethod()
                      .AllowCredentials();
            }
            else
            {
                var configured = builder.Configuration
                    .GetSection("Cors:Origins")
                    .Get<string[]>() ?? Array.Empty<string>();

                // Fallback to a safe default if no config provided — keeps
                // old behaviour, but log loudly so we notice.
                var origins = configured.Length > 0
                    ? configured
                    : new[] { "https://chatverse.app" };

                policy.WithOrigins(origins)
                      .AllowAnyHeader()
                      .AllowAnyMethod()
                      .AllowCredentials();
            }
        });
    });

    // ── HttpClient ────────────────────────────────────────────────
    builder.Services.AddHttpClient();

    // ── Gaming Hall (quiz/jokes/trivia rooms) ─────────────────────
    // QuizQuestionProvider needs its own typed HttpClient — gives it
    // an isolated lifetime so its 6s timeout doesn't bleed into other
    // outbound calls (Groq, Cloudinary, etc).
    builder.Services.AddHttpClient<ChatVerse.API.Services.Games.QuizQuestionProvider>();
    // JokesProvider hits icanhazdadjoke.com. Same typed-client pattern
    // so its 5s timeout + UA header don't leak into other outbound calls.
    builder.Services.AddHttpClient<ChatVerse.API.Services.Games.JokesProvider>();
    // Tech Talk news feed — pulls HN + dev.to, caches in Redis.
    builder.Services.AddHttpClient<ChatVerse.API.Services.Games.TechNewsProvider>();
    // Trending-headline source for AmbientQuestionProvider. Hits HN +
    // Reddit (no key required), caches the merged pool in Redis 15 min.
    // Lets the #general chat see LIVE topics instead of the same 30
    // evergreen prompts on rotation.
    builder.Services.AddHttpClient<ChatVerse.API.Services.Games.TrendingQuestionProvider>();
    // Ambient-question feed: wraps QuizQuestionProvider for MCQs and a
    // curated bank for discussion prompts. Scoped because the wrapped
    // provider already lives at a defined lifetime.
    builder.Services.AddScoped<ChatVerse.API.Services.Games.AmbientQuestionProvider>();
    // Background ticker that pushes ambient questions into themed chat
    // rooms via the ChatHub. Safe to register unconditionally — does
    // nothing harmful when nobody's in the rooms.
    builder.Services.AddHostedService<ChatVerse.API.Services.AmbientQuestionService>();
    // Rolling quiz in #general — pushes a fresh MCQ every ~2 min,
    // tallies submissions, broadcasts a per-day UTC leaderboard.
    builder.Services.AddHostedService<ChatVerse.API.Services.RollingQuizService>();
    // Registry is per-process; singleton so the hub + ticker + REST
    // controller all share the same in-memory cache of active sessions.
    builder.Services.AddSingleton<ChatVerse.API.Services.Games.GameSessionRegistry>();
    // Hosted ticker drives deadline-based question advancement.
    // Also exposes BroadcastAsync to the hub; we register it once and
    // resolve in both AddHostedService and via direct DI.
    builder.Services.AddSingleton<ChatVerse.API.Services.Games.GameTickerService>();
    builder.Services.AddHostedService(sp =>
        sp.GetRequiredService<ChatVerse.API.Services.Games.GameTickerService>());

    // ── Background services ───────────────────────────────────────
    // Drives VideoHub's random-1-on-1 queue. Safe to run on multiple
    // replicas — the Redis LPOP is atomic.
    builder.Services.AddHostedService<MatchingService>();

    // AI host that posts in lightly-active rooms. No-op unless
    // Ai:EnablePresence=true AND a Groq key is set, so safe to register.
    builder.Services.AddHostedService<AiPersonaService>();

    // Maintenance webjob: every 6h, deletes stale guests + old chats +
    // ended video sessions so the free-tier Postgres + Mongo don't
    // fill up. See MaintenanceService for retention windows.
    builder.Services.AddHostedService<ChatVerse.API.Services.MaintenanceService>();

    // Time Capsule (Phase 2 sticky feature) — delivery sweeper runs
    // every 30 minutes. Picks recipients from a random-active sample and
    // pushes SignalR events via TimeCapsuleHub. Hub itself is mapped
    // below in the middleware pipeline.
    builder.Services.AddHostedService<ChatVerse.API.Services.TimeCapsuleDeliveryService>();

    // Persona Roulette (Phase 2 signature daily feature) — daily 00:00
    // UTC roll-over. The service checks once per hour whether the UTC
    // date has changed and runs idempotently. Generates personas for
    // an active-user sample + bumps streaks from yesterday's
    // conversation rollup.
    builder.Services.AddHostedService<ChatVerse.API.Services.PersonaResetService>();

    // Story Chain (Phase 1 creative-engagement loop) — 10-min tick
    // handles "spawn today's chain at 3pm IST" + "lock+publish any
    // chain whose IST date is in the past". Idempotent.
    builder.Services.AddHostedService<ChatVerse.API.Services.StoryChainService>();

    // Confession Box (Phase 2 daily drama) — 30-min tick crowns the
    // previous UTC day's top confession + fires a reveal offer to
    // its author. Idempotent per UTC date.
    builder.Services.AddHostedService<ChatVerse.API.Services.ConfessionRankingService>();

    // Polls auto-close ticker (parity polish) — 1-min tick sweeps
    // expired open polls and broadcasts PollClosed to the room.
    builder.Services.AddHostedService<ChatVerse.API.Services.PollsTickerService>();

    // Ghost Date (Phase 2 weekly anonymous dating) — 60-second tick
    // handles Thursday 9pm IST pairing + 9:30pm chat-ended push +
    // 9:35pm decision-deadline expiry. All idempotent.
    builder.Services.AddHostedService<ChatVerse.API.Services.GhostDateService>();

    // Love Triangle (Phase 2 weekly drama) — 5-min tick handles
    // Sunday 10pm IST formation + 7-day chat-end voting open +
    // 8-day completion. All idempotent.
    builder.Services.AddHostedService<ChatVerse.API.Services.LoveTriangleService>();

    // The Cipher (Phase 2 weekly community ARG) — 5-min tick:
    // Monday 9am IST opens new round, picks N Cipher Members from
    // an active-user sample + assigns one phrase-word each. Sunday
    // 11pm IST closes + scores all Hunter submissions.
    builder.Services.AddHostedService<ChatVerse.API.Services.CipherRoundService>();

    // PYAAR LIVE (Phase 3 flagship) — 1-min tick orchestrates the
    // Saturday 8pm IST mass dating show end-to-end: formation,
    // 4-round advancement, mid-show elimination, completion + winners.
    builder.Services.AddHostedService<ChatVerse.API.Services.PyaarLiveOrchestrator>();

    // ── Phase 5 token economy ────────────────────────────────────
    //    TokenLedgerService is the only place that writes balance +
    //    audit rows. IPaymentGateway is swappable — MockPaymentGateway
    //    today, RazorpayPaymentGateway tomorrow without touching the
    //    hubs or feature code. Both are scoped so the request-scoped
    //    HubContext + MongoService dependencies resolve correctly.
    builder.Services.AddScoped<ChatVerse.API.Services.Tokens.TokenLedgerService>();
    builder.Services.AddScoped<ChatVerse.API.Services.Tokens.IPaymentGateway,
                               ChatVerse.API.Services.Tokens.MockPaymentGateway>();

    var app = builder.Build();

    // ── Middleware pipeline ───────────────────────────────────────
    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "ChatVerse API v1");
            c.RoutePrefix = string.Empty;
        });
    }

    // HTTPS redirect only in dev — Render terminates SSL upstream,
    // so the container itself speaks plain HTTP. Forcing a redirect
    // inside the container causes redirect loops behind the LB.
    if (app.Environment.IsDevelopment())
    {
        app.UseHttpsRedirection();
    }

    app.UseGlobalExceptionHandler();
    app.UseCors("ChatVerseCors");
    app.UseAuthentication();
    app.UseAuthorization();

    // Health check — Render hits this to know the container is alive.
    // Returns plain text so it's cheap and proxy-friendly.
    app.MapGet("/healthz", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }))
       .AllowAnonymous();

    app.MapControllers();
    app.MapHub<ChatHub>("/hubs/chat");
    app.MapHub<VideoHub>("/hubs/video");
    app.MapHub<GameHub>("/hubs/game");
    // Phase 2 hub — Time Capsule write/reply/inbox + real-time delivery push.
    app.MapHub<ChatVerse.API.Hubs.TimeCapsuleHub>("/hubs/time-capsule");

    // Phase 2 hub — Persona Roulette: GetMyPersona / GetActiveStreaks /
    // Request+AcceptMutualUnmask. Real-time events (PersonaRolled,
    // StreakMilestone, StreakUnmasked) will land alongside the
    // frontend page.
    app.MapHub<ChatVerse.API.Hubs.PersonaHub>("/hubs/persona");

    // Phase 1 hub — Story Chain: GetCurrentChain / JoinQueue /
    // LeaveQueue / AddSentence / GetArchive. Turn queue lives in
    // Redis; push events fan out via SignalR groups.
    app.MapHub<ChatVerse.API.Hubs.StoryChainHub>("/hubs/story-chain");

    // Phase 2 hub — Confession Box: Post / GetTodaysFeed / React /
    // GetMyTopOffer / Accept|DeclineReveal / GetLoreWall. Push:
    // ConfessionPosted / ReactionUpdated / ConfessionRevealed /
    // TopConfessionOffered.
    app.MapHub<ChatVerse.API.Hubs.ConfessionHub>("/hubs/confessions");

    // Phase 2 hub — Ghost Date: Register / Withdraw / GetMyStatus /
    // GetActiveDate / SendMessage / GetThread / SubmitDecision /
    // GetMyHistory. Push: GhostDateMatched / GhostDateMessage /
    // GhostDateEnded / GhostDateOutcome.
    app.MapHub<ChatVerse.API.Hubs.GhostDateHub>("/hubs/ghost-date");

    // Phase 2 hub — Love Triangle: Register / Withdraw / GetMyStatus /
    // GetMyTriangle / SendPairMessage / ToggleShareExcerpt /
    // GetPublicTriangles / Vote / GetMyHistory. Push: TriangleFormed /
    // PairMessage / ExcerptShared / VotingOpened / TriangleCompleted.
    app.MapHub<ChatVerse.API.Hubs.LoveTriangleHub>("/hubs/love-triangle");

    // Phase 2 hub — The Cipher: GetCurrentRound / GetMyFragment /
    // SubmitGuess / GetMySubmission / GetLeaderboard / GetArchive.
    // Push: CipherRoundStarted (all) / CipherFragmentAssigned (per
    // Member) / CipherRoundClosed (all).
    app.MapHub<ChatVerse.API.Hubs.CipherHub>("/hubs/cipher");

    // Phase 3 hub — PYAAR LIVE: Register / Withdraw / GetMyStatus /
    // GetActiveShow / GetMyCouple / SendCoupleMessage / GetSpectatorView /
    // Vote / GetMyHistory. Push: ShowStarted / RoundAdvanced /
    // CoupleMessage (couple only) / SpectatorMessage (whole show group) /
    // EliminationAnnounced / ShowEnded.
    app.MapHub<ChatVerse.API.Hubs.PyaarLiveHub>("/hubs/pyaar-live");

    // Phase 4 hub — MEHFIL (creator platform): Discover / GetRoom /
    // MyRooms / CreateRoom / CancelRoom / StartRoom / EndRoom /
    // JoinRoom / LeaveRoom / SendMessage / Tip. Push (per-room
    // group): RoomStarted / RoomEnded / RoomAudience / RoomMessage /
    // RoomTip. Tip settlement runs through TokenLedgerService now
    // that Phase 5 lands; host verification still deferred.
    app.MapHub<ChatVerse.API.Hubs.MehfilHub>("/hubs/mehfil");

    // Phase 5 hub — Tokens wallet: GetBalance / GetLedger / GetMyOrders /
    // GetPacks / CreateTopupOrder / ConfirmMockPayment / CancelTopupOrder /
    // EnsureSignupBonus. Push: BalanceChanged (per-user).
    app.MapHub<ChatVerse.API.Hubs.TokensHub>("/hubs/tokens");

    // Parity-polish hub — Soundboard: JoinScope / LeaveScope / PlaySound
    // with 8-sound allowlist + per-second rate-limit. Push: SoundPlayed.
    // Mounted in Theater, PYAAR LIVE spectator, Mehfil, Hosted group video.
    app.MapHub<ChatVerse.API.Hubs.SoundboardHub>("/hubs/soundboard");

    // Debate (first per-template Mehfil specialisation) — STANDALONE hub.
    // Mehfil rooms with templateKind == "debate" land on DebateRoomPage,
    // which connects here. Doesn't touch MehfilHub state at all.
    app.MapHub<ChatVerse.API.Hubs.DebateHub>("/hubs/debate");

    // Ghost Room (second per-template Mehfil specialisation) — STANDALONE.
    // Mehfil rooms with templateKind == "ghost_date" land on
    // GhostRoomPage, which connects here. Coexists with the weekly
    // /hubs/ghost-date Thursday feature (separate hub, separate
    // collection family). No shared state.
    app.MapHub<ChatVerse.API.Hubs.GhostRoomHub>("/hubs/ghost-room");

    // ── Startup banner ────────────────────────────────────────────
    // Emit a clear, grep-friendly summary of WHICH hubs got mapped.
    // Render's deploy logs make this the fastest way to confirm a
    // fresh binary is running: if the GameHub line isn't visible at
    // startup, we know the deployed DLL doesn't contain the route —
    // before any client even tries to connect.
    var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
    startupLogger.LogInformation("=== ChatVerse SignalR hubs registered ===");
    startupLogger.LogInformation("  ChatHub  @ /hubs/chat");
    startupLogger.LogInformation("  VideoHub @ /hubs/video");
    startupLogger.LogInformation("  GameHub  @ /hubs/game");
    startupLogger.LogInformation("=== Build version: phase1-gaming-v1 — app ready ===");

    app.Run();
}
catch (Exception ex)
{
    // Don't ReadKey() in containerised hosts — it hangs forever
    // waiting on a stdin that never arrives. Just log + exit non-zero
    // so Render shows the crash and restarts.
    Console.Error.WriteLine("STARTUP ERROR: " + ex.Message);
    Console.Error.WriteLine(ex.StackTrace);
    Environment.Exit(1);
}

