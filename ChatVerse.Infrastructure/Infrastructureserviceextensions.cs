using ChatVerse.Infrastructure.ExternalServices.Email;
using ChatVerse.Infrastructure.ExternalServices.OpenAI;
using ChatVerse.Infrastructure.Persistence.MongoDB;
using ChatVerse.Infrastructure.Persistence.PostgreSQL;
using ChatVerse.Infrastructure.Persistence.Redis;
using ChatVerse.Infrastructure.Services.LiveKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using StackExchange.Redis;

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
        var redisConnStr = config.GetConnectionString("Redis")!;

        services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(redisConnStr));

        services.AddScoped<RedisService>();

        // ── Brevo Email ───────────────────────────────────────
        services.AddHttpClient<BrevoEmailService>();

        // ── Groq Moderation (free, replaces OpenAI) ──────────
        services.AddHttpClient<OpenAIModerationService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddScoped<ModerationOrchestrator>();
        // ── LiveKit Service ───────────────────────────────────────────
        services.AddSingleton<LiveKitService>();

        return services;
    }
}