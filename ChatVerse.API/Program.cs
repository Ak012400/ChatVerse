using ChatVerse.API.Extensions;
using ChatVerse.API.Hubs;
using ChatVerse.API.Middleware;
using ChatVerse.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

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

    // SignalR sends JWT via query string
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;
            if (!string.IsNullOrEmpty(accessToken) &&
               (path.StartsWithSegments("/hubs/chat") ||
                path.StartsWithSegments("/hubs/video")))
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
    options.MaximumReceiveMessageSize = 32 * 1024;
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
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
builder.Services.AddCors(options =>
{
    options.AddPolicy("ChatVerseCors", policy =>
    {
        if (builder.Environment.IsDevelopment())
        {
            policy.WithOrigins("http://localhost:5173", "https://localhost:5173")
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials();
        }
        else
        {
            policy.WithOrigins("https://chatverse.app")
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials();
        }
    });
});

// ── HttpClient ────────────────────────────────────────────────
builder.Services.AddHttpClient();

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

app.UseHttpsRedirection();
app.UseGlobalExceptionHandler();
app.UseCors("ChatVerseCors");
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<ChatHub>("/hubs/chat");
app.MapHub<VideoHub>("/hubs/video"); // uncomment when ready

app.Run();