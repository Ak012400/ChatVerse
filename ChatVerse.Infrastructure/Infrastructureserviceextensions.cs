using ChatVerse.Infrastructure.Persistence.MongoDB;
using ChatVerse.Infrastructure.Persistence.PostgreSQL;
using ChatVerse.Infrastructure.Persistence.Redis;
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
        {
            var uri = new Uri(redisConnStr);
            var cfg = new StackExchange.Redis.ConfigurationOptions
            {
                EndPoints = { $"{uri.Host}:{uri.Port}" },
                Ssl = uri.Scheme == "rediss",
                AbortOnConnectFail = false
            };
            // UserInfo is "default:password" — take the last part as password
            var userInfo = uri.UserInfo;
            if (!string.IsNullOrEmpty(userInfo))
                cfg.Password = userInfo.Split(':').Last();

            return ConnectionMultiplexer.Connect(cfg);
        });

        services.AddScoped<RedisService>();

        return services;
    }
}