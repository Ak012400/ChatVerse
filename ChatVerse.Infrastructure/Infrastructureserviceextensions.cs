using ChatVerse.Infrastructure.ExternalServices.Cloudinary;
using ChatVerse.Infrastructure.ExternalServices.Email;
using ChatVerse.Infrastructure.ExternalServices.OpenAI;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using ChatVerse.Infrastructure.Persistence.PostgreSQL;
using ChatVerse.Infrastructure.Persistence.Redis;
using ChatVerse.Infrastructure.Services.LiveKit;
using ChatVerse.Infrastructure.Services.UserState;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace ChatVerse.Infrastructure;

/// <summary>
/// Single extension method — Program.cs calls builder.Services.AddInfrastructure(config)
/// and everything is wired up.
/// </summary>
public static class InfrastructureServiceExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration config)
    {
        // ── PostgreSQL (Supabase) ─────────────────────────────
        services.AddDbContext<ChatVerseDbContext>(options =>
            options.UseNpgsql(
                config.GetConnectionString("PostgreSQL"),
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations", "user_auth")
            )
        );
        services.AddScoped<PostgresProcService>();

        // ── MongoDB Atlas ─────────────────────────────────────
        var mongoConnStr = config.GetConnectionString("MongoDB")!;
        var mongoDbName = config["MongoDB:DatabaseName"] ?? "chatverse";

        services.AddSingleton<IMongoClient>(_ =>
            new MongoClient(mongoConnStr));

        services.AddScoped<MongoService>(sp =>
            new MongoService(
                sp.GetRequiredService<IMongoClient>(),
                mongoDbName
            ));

        // ── Upstash Redis ─────────────────────────────────────
        //  IConnectionMultiplexer is registered in Program.cs (with proper
        //  Upstash rediss:// URI parsing). Registering here too would just
        //  be overwritten — keeping a single source of truth for that.
        services.AddScoped<RedisService>();

        // ── User-state cache (trust score + age verified) ─────
        //  Read by hubs and controllers instead of JWT claims so bans
        //  / trust penalties propagate within seconds.
        services.AddScoped<UserStateService>();

        // ── Brevo Email (SMTP path — bypasses Brevo's API IP allow-list) ──
        // MailKit opens a fresh SMTP connection per send so it doesn't
        // need pooling. Singleton because config is immutable per process.
        services.AddSingleton<BrevoEmailService>();

        // ── AI chat provider (Groq → Gemini fallback) ────────
        // Used by both text moderation and AiPersonaService so neither
        // is pinned to a single quota.
        services.AddHttpClient<ChatVerse.Infrastructure.ExternalServices.AI.AiChatProvider>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(20);
        });

        // ── Groq Moderation (free, replaces OpenAI) ──────────
        services.AddHttpClient<OpenAIModerationService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddScoped<ModerationOrchestrator>();
        // ── LiveKit Service ───────────────────────────────────────────
        services.AddSingleton<LiveKitService>();

        // ── Cloudinary (avatars + private docs) ──────────────────────
        // SDK manages its own HttpClient internally — register as plain
        // singleton (config + secret are immutable for the process).
        services.AddSingleton<CloudinaryService>();

        return services;
    }
}