using ChatVerse.Domain.Entities;
using ChatVerse.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Data;

namespace ChatVerse.Infrastructure.Persistence.PostgreSQL;

public class ChatVerseDbContext : DbContext
{
    public ChatVerseDbContext(DbContextOptions<ChatVerseDbContext> options)
        : base(options) { }

    // ── No DbSet<T> for direct table queries ─────────────────
    // All access goes through stored procedures only.
    // EF Core is used purely for:
    //   1. Connection management
    //   2. FromSqlRaw / ExecuteSqlRaw for proc calls
    //   3. Migrations to create tables + indexes

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Map schemas
        modelBuilder.HasDefaultSchema("user_auth");

        // user_auth.users
        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("users", "user_auth");
            e.HasKey(u => u.Id);
            e.Property(u => u.Status)
             .HasConversion<string>()
             .HasColumnType("user_auth.user_status");
            e.Property(u => u.AgeVerifyMethod)
             .HasConversion<string>()
             .HasColumnType("iam.age_verify_method");
        });

        // user_auth.otp_codes
        modelBuilder.Entity<OtpCode>(e =>
        {
            e.ToTable("otp_codes", "user_auth");
            e.HasKey(o => o.Id);
            e.Property(o => o.Purpose)
             .HasConversion<string>()
             .HasColumnType("user_auth.otp_purpose");
        });

        // trust.trust_events
        modelBuilder.Entity<TrustEvent>(e =>
        {
            e.ToTable("trust_events", "trust");
            e.HasKey(t => t.Id);
            e.Property(t => t.EventType)
             .HasConversion<string>()
             .HasColumnType("trust.event_type");
        });

        // trust.user_reports
        modelBuilder.Entity<UserReport>(e =>
        {
            e.ToTable("user_reports", "trust");
            e.HasKey(r => r.Id);
            e.Property(r => r.Status)
             .HasConversion<string>()
             .HasColumnType("trust.report_status");
        });

        // billing.subscriptions
        modelBuilder.Entity<Subscription>(e =>
        {
            e.ToTable("subscriptions", "billing");
            e.HasKey(s => s.Id);
            e.Property(s => s.PlanType)
             .HasConversion<string>()
             .HasColumnType("billing.plan_type");
            e.Property(s => s.Status)
             .HasConversion<string>()
             .HasColumnType("billing.subscription_status");
        });

        // billing.payments
        modelBuilder.Entity<Payment>(e =>
        {
            e.ToTable("payments", "billing");
            e.HasKey(p => p.Id);
            e.Property(p => p.Status)
             .HasConversion<string>()
             .HasColumnType("billing.payment_status");
        });

        // chat.room_bans
        modelBuilder.Entity<RoomBan>(e =>
        {
            e.ToTable("room_bans", "chat");
            e.HasKey(b => b.Id);
            e.Property(b => b.BanType)
             .HasConversion<string>()
             .HasColumnType("chat.ban_type");
        });

        // iam entities
        modelBuilder.Entity<AgeDeclaration>(e =>
        {
            e.ToTable("age_declarations", "iam");
            e.HasKey(a => a.Id);
        });

        modelBuilder.Entity<AiMaturitySession>(e =>
        {
            e.ToTable("ai_maturity_sessions", "iam");
            e.HasKey(a => a.Id);
        });

        modelBuilder.Entity<DocumentVerification>(e =>
        {
            e.ToTable("document_verifications", "iam");
            e.HasKey(d => d.Id);
            e.Property(d => d.Status)
             .HasConversion<string>()
             .HasColumnType("iam.doc_verify_status");
        });

        modelBuilder.Entity<TenureCheck>(e =>
        {
            e.ToTable("tenure_checks", "iam");
            e.HasKey(t => t.Id);
        });
    }
}