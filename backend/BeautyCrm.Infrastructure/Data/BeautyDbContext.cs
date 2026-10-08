using System.Linq.Expressions;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BeautyCrm.Infrastructure.Data;

/// <summary>
/// Beauty CRM persistence model. Tenant isolation is enforced by PostgreSQL RLS (see migration
/// AddBeautySchema); this context only stamps tenant_id on new rows and updated_at on changes.
/// Cross-tenant references are blocked by composite FKs (tenant_id, x_id) -> (tenant_id, id).
/// </summary>
public class BeautyDbContext : DbContext
{
    private readonly ITenantContext _tenant;

    public BeautyDbContext(DbContextOptions<BeautyDbContext> options, ITenantContext tenant)
        : base(options) => _tenant = tenant;

    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Specialist> Specialists => Set<Specialist>();
    public DbSet<SpecialistLocation> SpecialistLocations => Set<SpecialistLocation>();
    public DbSet<SpecialistServiceLink> SpecialistServices => Set<SpecialistServiceLink>();
    public DbSet<SpecialistAbsence> SpecialistAbsences => Set<SpecialistAbsence>();
    public DbSet<Service> Services => Set<Service>();
    public DbSet<ServicePrice> ServicePrices => Set<ServicePrice>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<ClientNote> ClientNotes => Set<ClientNote>();
    public DbSet<Promotion> Promotions => Set<Promotion>();
    public DbSet<PromotionLocation> PromotionLocations => Set<PromotionLocation>();
    public DbSet<PromotionService> PromotionServices => Set<PromotionService>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<AiAction> AiActions => Set<AiAction>();
    public DbSet<Reminder> Reminders => Set<Reminder>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<CancellationSettingsRow> CancellationSettings => Set<CancellationSettingsRow>();

    // auth (TASK-684)
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Invite> Invites => Set<Invite>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampEntities();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampEntities();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void StampEntities()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in ChangeTracker.Entries<ITenantEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.TenantId == Guid.Empty && _tenant.TenantId is { } tenantId)
                    entry.Entity.TenantId = tenantId; // a mismatching explicit tenant is rejected by RLS WITH CHECK
                if (entry.Entity is TenantEntity t)
                {
                    if (t.CreatedAt == default) t.CreatedAt = now;
                    t.UpdatedAt = now;
                }
                else if (entry.Entity is PromotionLocation pl && pl.CreatedAt == default) pl.CreatedAt = now;
                else if (entry.Entity is PromotionService ps && ps.CreatedAt == default) ps.CreatedAt = now;
            }
            else if (entry.State == EntityState.Modified && entry.Entity is TenantEntity t)
            {
                t.UpdatedAt = now;
            }
        }
    }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.HasPostgresExtension("btree_gist");

        mb.Entity<Location>(b =>
        {
            Base(b, "beauty_locations");
            b.Property(x => x.Name).HasMaxLength(200);
            b.Property(x => x.Address).HasMaxLength(500);
            b.Property(x => x.Phone).HasMaxLength(32);
            b.Property(x => x.Timezone).HasMaxLength(64);
        });

        mb.Entity<Specialist>(b =>
        {
            Base(b, "beauty_specialists");
            b.Property(x => x.FullName).HasMaxLength(200);
            b.Property(x => x.Title).HasMaxLength(200);
            b.Property(x => x.Position).HasMaxLength(200);
            b.Property(x => x.Phone).HasMaxLength(32);
            b.Property(x => x.Email).HasMaxLength(320);
            b.Property(x => x.PhotoUrl).HasMaxLength(2048);
        });

        mb.Entity<SpecialistLocation>(b =>
        {
            Base(b, "beauty_specialist_locations");
            b.Property(x => x.WorkingHours).HasColumnType("jsonb");
            TenantFk(b, x => x.Specialist, x => new { x.TenantId, x.SpecialistId }, DeleteBehavior.Cascade);
            TenantFk(b, x => x.Location, x => new { x.TenantId, x.LocationId }, DeleteBehavior.Cascade);
            b.HasIndex(x => new { x.TenantId, x.SpecialistId, x.LocationId }).IsUnique();
        });

        mb.Entity<Service>(b =>
        {
            Base(b, "beauty_services", t =>
                t.HasCheckConstraint("ck_beauty_services_duration_positive", "duration_minutes > 0"));
            b.Property(x => x.Name).HasMaxLength(200);
            b.Property(x => x.Category).HasMaxLength(100);
        });

        mb.Entity<SpecialistServiceLink>(b =>
        {
            b.ToTable("beauty_specialist_services");
            b.HasKey(x => new { x.TenantId, x.SpecialistId, x.ServiceId });
            TenantFk(b, x => x.Specialist, x => new { x.TenantId, x.SpecialistId }, DeleteBehavior.Cascade);
            TenantFk(b, x => x.Service, x => new { x.TenantId, x.ServiceId }, DeleteBehavior.Cascade);
            b.HasIndex(x => new { x.TenantId, x.ServiceId });
        });

        mb.Entity<SpecialistAbsence>(b =>
        {
            b.ToTable("beauty_specialist_absences", t =>
            {
                t.HasCheckConstraint("ck_beauty_specialist_absences_type", "type IN ('sick', 'vacation', 'day_off', 'other')");
                t.HasCheckConstraint("ck_beauty_specialist_absences_status", "status IN ('requested', 'approved', 'rejected', 'cancelled')");
                t.HasCheckConstraint("ck_beauty_specialist_absences_dates", "date_to >= date_from");
                t.HasCheckConstraint("ck_beauty_specialist_absences_note", "note IS NULL OR char_length(note) <= 500");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.HasAlternateKey(x => new { x.TenantId, x.Id });
            b.Property(x => x.Type).HasMaxLength(16);
            b.Property(x => x.Status).HasMaxLength(16);
            b.Property(x => x.Note).HasMaxLength(500);
            TenantFk(b, x => x.Specialist, x => new { x.TenantId, x.SpecialistId }, DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(x => new { x.TenantId, x.RequestedByUserId })
                .HasPrincipalKey(u => new { u.TenantId, u.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(x => new { x.TenantId, x.DecidedByUserId })
                .HasPrincipalKey(u => new { u.TenantId, u.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(x => new { x.TenantId, x.CancelledByUserId })
                .HasPrincipalKey(u => new { u.TenantId, u.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.TenantId, x.SpecialistId, x.DateFrom });
            // exclusion constraint (немає перетину requested/approved відсутностей майстра) — у SQL міграції
        });

        mb.Entity<ServicePrice>(b =>
        {
            Base(b, "beauty_service_prices", t =>
                t.HasCheckConstraint("ck_beauty_service_prices_price_non_negative", "price >= 0"));
            b.Property(x => x.Price).HasPrecision(12, 2);
            TenantFk(b, x => x.Service, x => new { x.TenantId, x.ServiceId }, DeleteBehavior.Cascade);
            TenantFk(b, x => x.Location, x => new { x.TenantId, x.LocationId }, DeleteBehavior.Cascade);
            // one network price (location_id NULL) + at most one override per location
            b.HasIndex(x => new { x.TenantId, x.ServiceId, x.LocationId }).IsUnique().AreNullsDistinct(false);
        });

        mb.Entity<Client>(b =>
        {
            Base(b, "beauty_clients");
            b.Property(x => x.FullName).HasMaxLength(200);
            b.Property(x => x.Phone).HasMaxLength(32);
            b.Property(x => x.Email).HasMaxLength(320);
            b.Property(x => x.MarketingConsent).HasDefaultValue(false);
            b.Property(x => x.Unsubscribed).HasDefaultValue(false);
            b.HasIndex(x => new { x.TenantId, x.Phone }).HasFilter("deleted_at IS NULL");
        });

        mb.Entity<ClientNote>(b =>
        {
            Base(b, "beauty_client_notes");
            TenantFk(b, x => x.Client, x => new { x.TenantId, x.ClientId }, DeleteBehavior.Cascade);
        });

        mb.Entity<Promotion>(b =>
        {
            Base(b, "beauty_promotions", t =>
            {
                Check<DiscountType>(t, "beauty_promotions", "discount_type");
                t.HasCheckConstraint("ck_beauty_promotions_discount_value",
                    "discount_value > 0 AND (discount_type <> 'percent' OR discount_value <= 100)");
                t.HasCheckConstraint("ck_beauty_promotions_period", "ends_at IS NULL OR starts_at IS NULL OR ends_at > starts_at");
            });
            b.Property(x => x.Name).HasMaxLength(200);
            Enum(b.Property(x => x.DiscountType));
            b.Property(x => x.DiscountValue).HasPrecision(12, 2);
        });

        mb.Entity<PromotionLocation>(b =>
        {
            b.ToTable("beauty_promotion_locations");
            b.HasKey(x => new { x.PromotionId, x.LocationId });
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            TenantFk(b, x => x.Promotion, x => new { x.TenantId, x.PromotionId }, DeleteBehavior.Cascade);
            TenantFk(b, x => x.Location, x => new { x.TenantId, x.LocationId }, DeleteBehavior.Cascade);
        });

        mb.Entity<PromotionService>(b =>
        {
            b.ToTable("beauty_promotion_services");
            b.HasKey(x => new { x.PromotionId, x.ServiceId });
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            TenantFk(b, x => x.Promotion, x => new { x.TenantId, x.PromotionId }, DeleteBehavior.Cascade);
            TenantFk(b, x => x.Service, x => new { x.TenantId, x.ServiceId }, DeleteBehavior.Cascade);
        });

        mb.Entity<Appointment>(b =>
        {
            Base(b, "beauty_appointments", t =>
            {
                Check<AppointmentStatus>(t, "beauty_appointments", "status");
                Check<AppointmentSource>(t, "beauty_appointments", "source");
                Check<ReminderOption>(t, "beauty_appointments", "reminder_option");
                t.HasCheckConstraint("ck_beauty_appointments_payment_method",
                    $"payment_method IS NULL OR {EnumText<PaymentMethod>.CheckSql("payment_method")}");
                t.HasCheckConstraint("ck_beauty_appointments_duration_positive", "duration_minutes > 0");
                // TASK-697: хто скасував. Колонки заповнюються лише для скасованих; user id — лише для staff.
                t.HasCheckConstraint("ck_beauty_appointments_cancelled_by_type",
                    "cancelled_by_type IS NULL OR cancelled_by_type IN ('client', 'staff', 'system')");
                t.HasCheckConstraint("ck_beauty_appointments_cancelled_by_user",
                    "cancelled_by_user_id IS NULL OR cancelled_by_type = 'staff'");
                t.HasCheckConstraint("ck_beauty_appointments_cancel_meta_status",
                    "(cancelled_by_type IS NULL AND cancel_reason IS NULL) OR status = 'cancelled'");
                t.HasCheckConstraint("ck_beauty_appointments_cancel_reason_len",
                    "cancel_reason IS NULL OR char_length(cancel_reason) <= 300");
                t.HasCheckConstraint("ck_beauty_appointments_prices_non_negative", "price_original >= 0 AND price_final >= 0");
            });
            Enum(b.Property(x => x.Status)).HasDefaultValue(AppointmentStatus.Pending);
            Enum(b.Property(x => x.Source));
            Enum(b.Property(x => x.ReminderOption)).HasDefaultValue(ReminderOption.None);
            b.Property(x => x.PaymentMethod).HasConversion(new EnumMemberConverter<PaymentMethod>()).HasMaxLength(32);
            b.Property(x => x.PriceOriginal).HasPrecision(12, 2);
            b.Property(x => x.PriceFinal).HasPrecision(12, 2);
            b.Property(x => x.CancelledByType).HasMaxLength(16);
            b.Property(x => x.CancelReason).HasMaxLength(300);
            b.HasOne<User>().WithMany().HasForeignKey(x => new { x.TenantId, x.CancelledByUserId })
                .HasPrincipalKey(u => new { u.TenantId, u.Id }).OnDelete(DeleteBehavior.Restrict);
            // maintained by trigger beauty_appointments_set_ends_at (migration SQL)
            b.Property(x => x.EndsAt).ValueGeneratedOnAddOrUpdate();

            TenantFk(b, x => x.Location, x => new { x.TenantId, x.LocationId }, DeleteBehavior.Restrict);
            TenantFk(b, x => x.Specialist, x => new { x.TenantId, x.SpecialistId }, DeleteBehavior.Restrict);
            TenantFk(b, x => x.Service, x => new { x.TenantId, x.ServiceId }, DeleteBehavior.Restrict);
            TenantFk(b, x => x.Client, x => new { x.TenantId, x.ClientId }, DeleteBehavior.Restrict);
            TenantFk(b, x => x.Promotion, x => new { x.TenantId, x.PromotionId }, DeleteBehavior.Restrict);

            // calendar views: location / specialist day ranges
            b.HasIndex(x => new { x.TenantId, x.LocationId, x.StartsAt });
            b.HasIndex(x => new { x.TenantId, x.SpecialistId, x.StartsAt });
            b.HasIndex(x => new { x.TenantId, x.ClientId, x.StartsAt });
            // публічний запис (TASK-688): пошук за токеном і ідемпотентність
            b.Property(x => x.PublicTokenHash).HasMaxLength(64);
            b.Property(x => x.IdempotencyKeyHash).HasMaxLength(64);
            b.Property(x => x.IdempotencyRequestHash).HasMaxLength(64);
            b.HasIndex(x => new { x.TenantId, x.PublicTokenHash }).IsUnique().HasFilter("public_token_hash IS NOT NULL")
                .HasDatabaseName("ux_beauty_appointments_public_token");
            b.HasIndex(x => new { x.TenantId, x.IdempotencyKeyHash }).IsUnique().HasFilter("idempotency_key_hash IS NOT NULL")
                .HasDatabaseName("ux_beauty_appointments_idempotency");
            // exclusion constraint (no overlapping active appointments per specialist) is in migration SQL
        });

        mb.Entity<Channel>(b =>
        {
            Base(b, "beauty_channels", t => Check<ChannelType>(t, "beauty_channels", "type"));
            Enum(b.Property(x => x.Type));
            b.Property(x => x.Name).HasMaxLength(200);
            b.Property(x => x.CredentialsLast4).HasMaxLength(4);
            b.Property(x => x.Settings).HasColumnType("jsonb");
            TenantFk(b, x => x.Location, x => new { x.TenantId, x.LocationId }, DeleteBehavior.Restrict);
        });

        mb.Entity<Conversation>(b =>
        {
            Base(b, "beauty_conversations", t => Check<ConversationStatus>(t, "beauty_conversations", "status"));
            Enum(b.Property(x => x.Status)).HasDefaultValue(ConversationStatus.Open);
            b.Property(x => x.ExternalChatId).HasMaxLength(256);
            TenantFk(b, x => x.Channel, x => new { x.TenantId, x.ChannelId }, DeleteBehavior.Restrict);
            TenantFk(b, x => x.Client, x => new { x.TenantId, x.ClientId }, DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.TenantId, x.ChannelId, x.ExternalChatId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.LastMessageAt }).HasFilter("status = 'open'");
        });

        mb.Entity<Message>(b =>
        {
            Base(b, "beauty_messages", t =>
            {
                Check<MessageDirection>(t, "beauty_messages", "direction");
                Check<MessageSenderType>(t, "beauty_messages", "sender_type");
            });
            Enum(b.Property(x => x.Direction));
            Enum(b.Property(x => x.SenderType));
            b.Property(x => x.ExternalMessageId).HasMaxLength(256);
            b.Property(x => x.SentAt).HasDefaultValueSql("now()");
            b.Property(x => x.Status).HasMaxLength(16).HasDefaultValue("received");
            b.Property(x => x.IdempotencyKey).HasMaxLength(300);
            b.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique().HasFilter("idempotency_key IS NOT NULL");
            TenantFk(b, x => x.Conversation, x => new { x.TenantId, x.ConversationId }, DeleteBehavior.Cascade);
            b.HasIndex(x => new { x.TenantId, x.ConversationId, x.SentAt });
        });

        mb.Entity<AiAction>(b =>
        {
            Base(b, "beauty_ai_actions", t => Check<AiActionStatus>(t, "beauty_ai_actions", "status"));
            Enum(b.Property(x => x.Status)).HasDefaultValue(AiActionStatus.Proposed);
            b.Property(x => x.ToolName).HasMaxLength(100);
            b.Property(x => x.Payload).HasColumnType("jsonb");
            b.Property(x => x.Result).HasColumnType("jsonb");
            TenantFk(b, x => x.Conversation, x => new { x.TenantId, x.ConversationId }, DeleteBehavior.Restrict);
            TenantFk(b, x => x.Appointment, x => new { x.TenantId, x.AppointmentId }, DeleteBehavior.Restrict);
            // confirm-mode queue: actions awaiting a human decision
            b.HasIndex(x => new { x.TenantId, x.CreatedAt }).HasFilter("status = 'proposed'");
        });

        mb.Entity<Reminder>(b =>
        {
            Base(b, "beauty_reminders", t => Check<ReminderStatus>(t, "beauty_reminders", "status"));
            Enum(b.Property(x => x.Status)).HasDefaultValue(ReminderStatus.Scheduled);
            TenantFk(b, x => x.Appointment, x => new { x.TenantId, x.AppointmentId }, DeleteBehavior.Cascade);
            // due reminders per tenant
            b.HasIndex(x => new { x.TenantId, x.ScheduledAt }).HasFilter("status = 'scheduled'");
        });

        mb.Entity<Payment>(b =>
        {
            Base(b, "beauty_payments", t =>
            {
                Check<PaymentMethod>(t, "beauty_payments", "method");
                Check<PaymentStatus>(t, "beauty_payments", "status");
                t.HasCheckConstraint("ck_beauty_payments_amount_positive", "amount > 0");
            });
            Enum(b.Property(x => x.Method));
            Enum(b.Property(x => x.Status)).HasDefaultValue(PaymentStatus.Pending);
            b.Property(x => x.Amount).HasPrecision(12, 2);
            b.Property(x => x.Provider).HasMaxLength(64);
            b.Property(x => x.ProviderPaymentId).HasMaxLength(256);
            TenantFk(b, x => x.Appointment, x => new { x.TenantId, x.AppointmentId }, DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.TenantId, x.Provider, x.ProviderPaymentId }).HasFilter("provider_payment_id IS NOT NULL");
        });

        mb.Entity<CancellationSettingsRow>(b =>
        {
            Base(b, "beauty_cancellation_settings", t =>
            {
                t.HasCheckConstraint("ck_beauty_cancellation_settings_window", "window_hours >= 0 AND window_hours <= 720");
                t.HasCheckConstraint("ck_beauty_cancellation_settings_percents",
                    "refund_percent_in_window BETWEEN 0 AND 100 AND refund_percent_outside BETWEEN 0 AND 100 AND fee_percent BETWEEN 0 AND 100");
            });
            b.Property(x => x.WindowHours).HasDefaultValue(12);
            b.Property(x => x.RefundPercentInWindow).HasDefaultValue(50);
            b.Property(x => x.RefundPercentOutside).HasDefaultValue(100);
            b.Property(x => x.DeductFee).HasDefaultValue(false);
            b.Property(x => x.FeePercent).HasDefaultValue(0);
            b.HasIndex(x => x.TenantId).IsUnique(); // один рядок на tenant
        });

        ConfigureAuth(mb);
    }

    // ---------- auth: tenants, users, invites, refresh tokens (RLS in migration auth_tenants_users_invites) ----------

    private static void ConfigureAuth(ModelBuilder mb)
    {
        mb.Entity<Tenant>(b =>
        {
            b.ToTable("tenants", t =>
            {
                t.HasCheckConstraint("ck_tenants_status", "status IN ('active', 'suspended')");
                t.HasCheckConstraint("ck_tenants_slug", "slug ~ '^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]$'");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(200);
            b.Property(x => x.Slug).HasMaxLength(64);
            b.Property(x => x.Modules).HasColumnType("text[]").HasDefaultValueSql("'{}'");
            b.Property(x => x.Status).HasMaxLength(16).HasDefaultValue("active");
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
            b.HasIndex(x => x.Slug).IsUnique();
        });

        mb.Entity<User>(b =>
        {
            Base(b, "users", t => t.HasCheckConstraint("ck_users_role", "role IN ('owner', 'admin', 'specialist')"));
            b.Property(x => x.Email).HasMaxLength(320);
            b.Property(x => x.FullName).HasMaxLength(200);
            b.Property(x => x.PasswordHash).HasMaxLength(255);
            b.Property(x => x.Role).HasMaxLength(16);
            b.Property(x => x.IsActive).HasDefaultValue(true);
            b.Property(x => x.FailedLoginCount).HasDefaultValue(0);
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            TenantFk(b, x => x.Specialist, x => new { x.TenantId, x.SpecialistId }, DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.TenantId, x.Email }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.SpecialistId }).IsUnique().HasFilter("specialist_id IS NOT NULL");
        });

        mb.Entity<Invite>(b =>
        {
            Base(b, "invites", t => t.HasCheckConstraint("ck_invites_role", "role IN ('admin', 'specialist')"));
            b.Property(x => x.Email).HasMaxLength(320);
            b.Property(x => x.Role).HasMaxLength(16);
            b.Property(x => x.TokenHash).HasMaxLength(64);
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            TenantFk(b, x => x.InvitedBy, x => new { x.TenantId, x.InvitedByUserId }, DeleteBehavior.Restrict);
            b.HasOne<Specialist>().WithMany().HasForeignKey(x => new { x.TenantId, x.SpecialistId })
                .HasPrincipalKey(p => new { p.TenantId, p.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.TenantId, x.TokenHash }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.Email });
        });

        mb.Entity<RefreshToken>(b =>
        {
            Base(b, "refresh_tokens");
            b.Property(x => x.TokenHash).HasMaxLength(64);
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            TenantFk(b, x => x.User, x => new { x.TenantId, x.UserId }, DeleteBehavior.Cascade);
            b.HasIndex(x => new { x.TenantId, x.TokenHash }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.UserId });
        });
    }

    // ---------- helpers ----------

    private static void Base<T>(EntityTypeBuilder<T> b, string table, Action<TableBuilder<T>>? configureTable = null)
        where T : TenantEntity
    {
        if (configureTable is null) b.ToTable(table);
        else b.ToTable(table, configureTable);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
        // target for composite tenant-safe FKs; also serves tenant_id lookups
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
    }

    private static void TenantFk<TDependent, TPrincipal>(
        EntityTypeBuilder<TDependent> b,
        Expression<Func<TDependent, TPrincipal?>> navigation,
        Expression<Func<TDependent, object?>> foreignKey,
        DeleteBehavior onDelete)
        where TDependent : class
        where TPrincipal : TenantEntity
    {
        b.HasOne(navigation)
            .WithMany()
            .HasForeignKey(foreignKey)
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(onDelete);
    }

    private static PropertyBuilder<TEnum> Enum<TEnum>(PropertyBuilder<TEnum> p) where TEnum : struct, System.Enum =>
        p.HasConversion(new EnumMemberConverter<TEnum>()).HasMaxLength(32);

    private static void Check<TEnum>(TableBuilder t, string table, string column) where TEnum : struct, System.Enum =>
        t.HasCheckConstraint($"ck_{table}_{column}", EnumText<TEnum>.CheckSql(column));
}
