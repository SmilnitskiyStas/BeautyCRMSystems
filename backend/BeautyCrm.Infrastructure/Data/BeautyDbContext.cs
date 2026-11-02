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
    public DbSet<LocationClosure> LocationClosures => Set<LocationClosure>();
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
    public DbSet<ChatBookingProposal> ChatBookingProposals => Set<ChatBookingProposal>();
    public DbSet<ChatKeyboard> ChatKeyboards => Set<ChatKeyboard>(); // TASK-749 (§23.3)
    public DbSet<ChannelPollState> ChannelPollStates => Set<ChannelPollState>();
    public DbSet<AiAction> AiActions => Set<AiAction>();
    public DbSet<Reminder> Reminders => Set<Reminder>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<CancellationSettingsRow> CancellationSettings => Set<CancellationSettingsRow>();

    // Нагадування, вподобання, задачі дзвінків (TASK-736, §22.3)
    public DbSet<ClientContactPreference> ClientContactPreferences => Set<ClientContactPreference>();
    public DbSet<CallTask> CallTasks => Set<CallTask>();
    public DbSet<ReminderSettingsRow> ReminderSettings => Set<ReminderSettingsRow>();

    // AI settings (TASK-713, §20.4)
    public DbSet<AiSettingsRow> AiSettings => Set<AiSettingsRow>();
    public DbSet<AiProviderKey> AiProviderKeys => Set<AiProviderKey>();
    public DbSet<AiUsageRow> AiUsage => Set<AiUsageRow>();
    public DbSet<AiSettingsAuditRow> AiSettingsAudit => Set<AiSettingsAuditRow>();

    // Собівартість послуг (TASK-762, §24.3)
    public DbSet<ServiceCost> ServiceCosts => Set<ServiceCost>();
    public DbSet<ServiceCostAuditRow> ServiceCostAudit => Set<ServiceCostAuditRow>();

    // Маркетингова згода, кампанії, купони (TASK-776, §26.3)
    public DbSet<MarketingConsent> MarketingConsents => Set<MarketingConsent>();
    public DbSet<MarketingConsentEvent> MarketingConsentEvents => Set<MarketingConsentEvent>();
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<CampaignRecipient> CampaignRecipients => Set<CampaignRecipient>();
    public DbSet<Coupon> Coupons => Set<Coupon>();
    public DbSet<CouponRedemption> CouponRedemptions => Set<CouponRedemption>();

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
            Base(b, "beauty_locations", t =>
                t.HasCheckConstraint("ck_beauty_locations_closed_weekdays",
                    "closed_weekdays <@ ARRAY['mon','tue','wed','thu','fri','sat','sun']::text[]"));
            b.Property(x => x.Name).HasMaxLength(200);
            b.Property(x => x.Address).HasMaxLength(500);
            b.Property(x => x.Phone).HasMaxLength(32);
            b.Property(x => x.Timezone).HasMaxLength(64);
            b.Property(x => x.ClosedWeekdays).HasColumnType("text[]").HasDefaultValueSql("'{}'");
            b.Property(x => x.WorkingHours).HasColumnType("jsonb");
        });

        mb.Entity<LocationClosure>(b =>
        {
            b.ToTable("beauty_location_closures", t =>
            {
                t.HasCheckConstraint("ck_beauty_location_closures_dates", "date_to >= date_from");
                t.HasCheckConstraint("ck_beauty_location_closures_reason", "reason IS NULL OR char_length(reason) <= 200");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.HasAlternateKey(x => new { x.TenantId, x.Id });
            b.Property(x => x.Reason).HasMaxLength(200);
            TenantFk(b, x => x.Location, x => new { x.TenantId, x.LocationId }, DeleteBehavior.Cascade);
            b.HasOne<User>().WithMany().HasForeignKey(x => new { x.TenantId, x.CreatedByUserId })
                .HasPrincipalKey(u => new { u.TenantId, u.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.TenantId, x.LocationId, x.DateFrom });
            // exclusion constraint (немає перетину закриттів одного закладу) — у SQL міграції
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
            Base(b, "beauty_clients", t =>
            {
                // TASK-767 (§25.3): телефон, зібраний у чаті. Формат спадкового phone не перевіряється.
                t.HasCheckConstraint("ck_beauty_clients_phone_source",
                    "phone_source IS NULL OR phone_source IN ('telegram_contact', 'manual_chat', 'web_form', 'staff')");
                t.HasCheckConstraint("ck_beauty_clients_phone_source_requires_phone", "phone_source IS NULL OR phone IS NOT NULL");
                t.HasCheckConstraint("ck_beauty_clients_phone_pending_format", "phone_pending IS NULL OR phone_pending ~ '^\\+[1-9][0-9]{7,14}$'");
                t.HasCheckConstraint("ck_beauty_clients_phone_pending_source",
                    "phone_pending_source IS NULL OR phone_pending_source IN ('telegram_contact', 'manual_chat')");
                t.HasCheckConstraint("ck_beauty_clients_phone_pending_all_or_none",
                    "(phone_pending IS NULL) = (phone_pending_source IS NULL) AND (phone_pending IS NULL) = (phone_pending_at IS NULL)");
                t.HasCheckConstraint("ck_beauty_clients_duplicate_not_self", "duplicate_of_client_id IS NULL OR duplicate_of_client_id <> id");
            });
            b.Property(x => x.FullName).HasMaxLength(200);
            b.Property(x => x.Phone).HasMaxLength(32);
            b.Property(x => x.PhoneSource).HasMaxLength(16);
            b.Property(x => x.PhonePending).HasMaxLength(16);
            b.Property(x => x.PhonePendingSource).HasMaxLength(16);
            TenantFk(b, x => x.DuplicateOfClient, x => new { x.TenantId, x.DuplicateOfClientId }, DeleteBehavior.Restrict);
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
                // TASK-776 (§26.3): code_only діє лише через купон
                t.HasCheckConstraint("ck_beauty_promotions_access", "access IN ('public', 'code_only')");
            });
            b.Property(x => x.Access).HasMaxLength(16).HasDefaultValue("public");
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
                // TASK-736 (§22.3): план нагадувань на записі
                t.HasCheckConstraint("ck_beauty_appointments_reminder_method", "reminder_method IN ('none', 'auto', 'telegram', 'call')");
                t.HasCheckConstraint("ck_beauty_appointments_reminder_offsets",
                    $"reminder_offsets <@ {ReminderOffsetsSql} AND cardinality(reminder_offsets) <= 3");
                t.HasCheckConstraint("ck_beauty_appointments_reminder_none_empty",
                    "(reminder_method = 'none') = (cardinality(reminder_offsets) = 0)");
                t.HasCheckConstraint("ck_beauty_appointments_reminder_call_single",
                    "reminder_method <> 'call' OR cardinality(reminder_offsets) = 1");
                // TASK-762 (§24.3): знімок собівартості лише для completed
                t.HasCheckConstraint("ck_beauty_appointments_cost_values",
                    "(cost_snapshot IS NULL OR cost_snapshot >= 0) AND (master_payout_snapshot IS NULL OR master_payout_snapshot >= 0) " +
                    "AND (master_percent_snapshot IS NULL OR master_percent_snapshot BETWEEN 0 AND 100)");
                t.HasCheckConstraint("ck_beauty_appointments_cost_status",
                    "(cost_snapshot IS NULL AND master_percent_snapshot IS NULL AND master_payout_snapshot IS NULL AND cost_captured_at IS NULL) OR status = 'completed'");
                t.HasCheckConstraint("ck_beauty_appointments_cost_consistency",
                    "(cost_snapshot IS NULL OR cost_captured_at IS NOT NULL) AND (master_payout_snapshot IS NULL OR cost_snapshot IS NOT NULL) " +
                    "AND (master_percent_snapshot IS NULL OR master_payout_snapshot IS NOT NULL)");
            });
            b.Property(x => x.CostSnapshot).HasPrecision(12, 2);
            b.Property(x => x.MasterPercentSnapshot).HasPrecision(5, 2);
            b.Property(x => x.MasterPayoutSnapshot).HasPrecision(12, 2);
            Enum(b.Property(x => x.Status)).HasDefaultValue(AppointmentStatus.Pending);
            Enum(b.Property(x => x.Source));
            Enum(b.Property(x => x.ReminderOption)).HasDefaultValue(ReminderOption.None);
            b.Property(x => x.ReminderMethod).HasMaxLength(16).HasDefaultValue("none");
            b.Property(x => x.ReminderOffsets).HasColumnType("smallint[]").HasDefaultValueSql("'{}'");
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
            // TASK-776 (§26.3): купон і кампанія-джерело
            TenantFk(b, x => x.Coupon, x => new { x.TenantId, x.CouponId }, DeleteBehavior.Restrict);
            TenantFk(b, x => x.Campaign, x => new { x.TenantId, x.CampaignId }, DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.TenantId, x.CouponId }).HasFilter("coupon_id IS NOT NULL");
            b.HasIndex(x => new { x.TenantId, x.CampaignId }).HasFilter("campaign_id IS NOT NULL");

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
            Base(b, "beauty_conversations", t =>
            {
                Check<ConversationStatus>(t, "beauty_conversations", "status");
                t.HasCheckConstraint("ck_beauty_conversations_ai_state", "ai_state IN ('active', 'handoff')");
                // TASK-736 (§22.3): питання про нагадування - усі три поля разом або жодне
                // TASK-767 (§25.3): phone_request - надмножина; для phone_request запис може бути NULL
                t.HasCheckConstraint("ck_beauty_conversations_pending_prompt", "pending_prompt IS NULL OR pending_prompt IN ('reminder_choice', 'phone_request')");
                t.HasCheckConstraint("ck_beauty_conversations_pending_prompt_all_or_none",
                    "(pending_prompt IS NULL AND pending_prompt_appointment_id IS NULL AND pending_prompt_expires_at IS NULL) " +
                    "OR (pending_prompt IS NOT NULL AND pending_prompt = 'reminder_choice' AND pending_prompt_appointment_id IS NOT NULL AND pending_prompt_expires_at IS NOT NULL) " +
                    "OR (pending_prompt IS NOT NULL AND pending_prompt = 'phone_request' AND pending_prompt_expires_at IS NOT NULL)");
                // TASK-776 (§26.3): пропозиція розмови (кампанія / купон) живе рівно до offer_until
                t.HasCheckConstraint("ck_beauty_conversations_offer_until_null",
                    "offer_until IS NOT NULL OR (offer_campaign_id IS NULL AND offer_coupon_id IS NULL)");
                t.HasCheckConstraint("ck_beauty_conversations_offer_until_set",
                    "offer_until IS NULL OR offer_campaign_id IS NOT NULL OR offer_coupon_id IS NOT NULL");
            });
            TenantFk(b, x => x.OfferCampaign, x => new { x.TenantId, x.OfferCampaignId }, DeleteBehavior.Restrict);
            TenantFk(b, x => x.OfferCoupon, x => new { x.TenantId, x.OfferCouponId }, DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.TenantId, x.OfferCampaignId }).HasFilter("offer_campaign_id IS NOT NULL");
            b.HasIndex(x => new { x.TenantId, x.OfferCouponId }).HasFilter("offer_coupon_id IS NOT NULL");
            b.Property(x => x.PendingPrompt).HasMaxLength(32);
            TenantFk(b, x => x.PendingPromptAppointment, x => new { x.TenantId, x.PendingPromptAppointmentId }, DeleteBehavior.Restrict);
            b.HasIndex(new[] { nameof(Conversation.TenantId), nameof(Conversation.AwaitingStaffSince) }, "ix_beauty_conversations_awaiting_staff")
                .HasDatabaseName("ix_beauty_conversations_awaiting_staff")
                .HasFilter("awaiting_staff_since IS NOT NULL");
            b.HasIndex(new[] { nameof(Conversation.TenantId), nameof(Conversation.LastMessageAt) }, "ix_beauty_conversations_last_message_desc")
                .HasDatabaseName("ix_beauty_conversations_last_message_desc")
                .IsDescending(false, true);
            b.HasIndex(x => new { x.TenantId, x.PendingPromptAppointmentId });
            b.Property(x => x.AiState).HasMaxLength(16).HasDefaultValue("active");
            b.Property(x => x.HandoffReason).HasMaxLength(32);
            b.HasIndex(x => new { x.TenantId, x.AiState });
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
                // TASK-749 (§23.3): callback - надмножина без backfill
                // TASK-767 (§25.3): contact - надмножина без backfill
                t.HasCheckConstraint("ck_beauty_messages_kind", "kind IN ('text', 'command', 'unsupported', 'callback', 'contact')");
                t.HasCheckConstraint("ck_beauty_messages_contact_verdict", "contact_verdict IS NULL OR contact_verdict IN ('own', 'foreign', 'invalid')");
                t.HasCheckConstraint("ck_beauty_messages_contact_fields", "kind = 'contact' OR (contact_phone IS NULL AND contact_verdict IS NULL)");
                t.HasCheckConstraint("ck_beauty_messages_contact_phone",
                    "contact_phone IS NULL OR (contact_verdict IS NOT NULL AND contact_verdict = 'own' AND contact_phone ~ '^\\+[1-9][0-9]{7,14}$')");
                t.HasCheckConstraint("ck_beauty_messages_actions",
                    "actions IS NULL OR (direction = 'outbound' AND jsonb_typeof(actions) = 'object' AND octet_length(actions::text) <= 4096)");
                t.HasCheckConstraint("ck_beauty_messages_callback_fields",
                    "kind = 'callback' OR (callback_data IS NULL AND callback_message_ref IS NULL)");
                t.HasCheckConstraint("ck_beauty_messages_sender_user", "sender_user_id IS NULL OR sender_type = 'staff'");
            });
            b.HasOne<User>().WithMany().HasForeignKey(x => new { x.TenantId, x.SenderUserId })
                .HasPrincipalKey(u => new { u.TenantId, u.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.TenantId, x.SenderUserId });
            b.Property(x => x.Kind).HasMaxLength(16).HasDefaultValue("text");
            b.Property(x => x.Actions).HasColumnType("jsonb");
            b.Property(x => x.CallbackData).HasMaxLength(64);
            b.Property(x => x.CallbackMessageRef).HasMaxLength(32);
            b.Property(x => x.ContactPhone).HasMaxLength(16);
            b.Property(x => x.ContactVerdict).HasMaxLength(8);
            // sweeper (§25.5.7): обнулення contact_phone старше ContactRetentionHours
            b.HasIndex(new[] { nameof(Message.TenantId), nameof(Message.CreatedAt) }, "ix_beauty_messages_contact_phone")
                .HasDatabaseName("ix_beauty_messages_contact_phone")
                .HasFilter("contact_phone IS NOT NULL");
            // очередь необроблених вхідних (sweeper/пакет ходу); окремий іменований індекс поряд зі звичайним
            b.HasIndex(new[] { nameof(Message.TenantId), nameof(Message.ConversationId), nameof(Message.SentAt) }, "ix_beauty_messages_inbound_received")
                .HasDatabaseName("ix_beauty_messages_inbound_received")
                .HasFilter("direction = 'inbound' AND status = 'received'");
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
            Base(b, "beauty_reminders", t =>
            {
                Check<ReminderStatus>(t, "beauty_reminders", "status");
                t.HasCheckConstraint("ck_beauty_reminders_method", "method IS NULL OR method IN ('telegram', 'auto')");
            });
            Enum(b.Property(x => x.Status)).HasDefaultValue(ReminderStatus.Scheduled);
            b.Property(x => x.Method).HasMaxLength(16);
            b.Property(x => x.SkipReason).HasMaxLength(32);
            TenantFk(b, x => x.Appointment, x => new { x.TenantId, x.AppointmentId }, DeleteBehavior.Cascade);
            b.HasOne<Message>().WithMany().HasForeignKey(x => new { x.TenantId, x.MessageId })
                .HasPrincipalKey(m => new { m.TenantId, m.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.TenantId, x.AppointmentId }); // FK; унікальний індекс нижче частковий і не покриває всі рядки
            b.HasIndex(x => new { x.TenantId, x.MessageId });
            // одна точка (offset) на запис, поки scheduled; спадкові рядки (offset NULL) не обмежуються
            b.HasIndex(x => new { x.TenantId, x.AppointmentId, x.OffsetMinutes }).IsUnique()
                .HasFilter("status = 'scheduled' AND offset_minutes IS NOT NULL")
                .HasDatabaseName("ux_beauty_reminders_point");
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

        ConfigureChatAssistant(mb);
        ConfigureReminders(mb);
        ConfigureChatButtons(mb);
        ConfigureAiSettings(mb);
        ConfigureServiceCosts(mb);
        ConfigureMarketing(mb);
        ConfigureAuth(mb);
    }

    // ---------- Собівартість послуг (TASK-762, §24.3, ADR-014; RLS in migration beauty_service_costs) ----------

    private static void ConfigureServiceCosts(ModelBuilder mb)
    {
        mb.Entity<ServiceCost>(b =>
        {
            Base(b, "beauty_service_costs", t =>
            {
                t.HasCheckConstraint("ck_beauty_service_costs_cost", "cost_amount >= 0 AND cost_amount <= 1000000");
                t.HasCheckConstraint("ck_beauty_service_costs_master_percent", "master_percent IS NULL OR master_percent BETWEEN 0 AND 100");
            });
            b.Property(x => x.CostAmount).HasPrecision(12, 2);
            b.Property(x => x.MasterPercent).HasPrecision(5, 2);
            TenantFk(b, x => x.Service, x => new { x.TenantId, x.ServiceId }, DeleteBehavior.Cascade);
            TenantFk(b, x => x.Location, x => new { x.TenantId, x.LocationId }, DeleteBehavior.Cascade);
            b.HasOne<User>().WithMany().HasForeignKey(x => new { x.TenantId, x.UpdatedByUserId })
                .HasPrincipalKey(u => new { u.TenantId, u.Id }).OnDelete(DeleteBehavior.Restrict);
            // одна мережева (location_id NULL) + не більше одного override на заклад; також індекс FK service_id
            b.HasIndex(x => new { x.TenantId, x.ServiceId, x.LocationId }).IsUnique().AreNullsDistinct(false)
                .HasDatabaseName("ux_beauty_service_costs_scope");
            b.HasIndex(x => new { x.TenantId, x.LocationId });
            b.HasIndex(x => new { x.TenantId, x.UpdatedByUserId });
        });

        mb.Entity<ServiceCostAuditRow>(b =>
        {
            b.ToTable("beauty_service_cost_audit", t =>
            {
                t.HasCheckConstraint("ck_beauty_service_cost_audit_action", "action IN ('set', 'delete')");
                t.HasCheckConstraint("ck_beauty_service_cost_audit_changed_fields", "changed_fields <@ ARRAY['cost_amount', 'master_percent']::text[]");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.HasAlternateKey(x => new { x.TenantId, x.Id });
            b.Property(x => x.Action).HasMaxLength(8);
            b.Property(x => x.ChangedFields).HasColumnType("text[]");
            b.HasOne<Service>().WithMany().HasForeignKey(x => new { x.TenantId, x.ServiceId })
                .HasPrincipalKey(s => new { s.TenantId, s.Id }).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Location>().WithMany().HasForeignKey(x => new { x.TenantId, x.LocationId })
                .HasPrincipalKey(l => new { l.TenantId, l.Id }).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<User>().WithMany().HasForeignKey(x => new { x.TenantId, x.UserId })
                .HasPrincipalKey(u => new { u.TenantId, u.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.TenantId, x.ServiceId, x.CreatedAt });
            b.HasIndex(x => new { x.TenantId, x.LocationId });
            b.HasIndex(x => new { x.TenantId, x.UserId });
        });
    }

    // ---------- Маркетингова згода, кампанії, купони (TASK-776, §26.3, ADR-015; RLS in migration beauty_marketing) ----------

    private const string Hex64 = "^[0-9a-f]{64}$";

    private static void ConfigureMarketing(ModelBuilder mb)
    {
        mb.Entity<MarketingConsent>(b =>
        {
            b.ToTable("beauty_marketing_consents", t =>
            {
                t.HasCheckConstraint("ck_beauty_marketing_consents_channel", "channel IN ('telegram', 'push')");
                t.HasCheckConstraint("ck_beauty_marketing_consents_level", "level IN ('full', 'selective', 'none')");
                t.HasCheckConstraint("ck_beauty_marketing_consents_source", "source IN ('chat', 'admin', 'stop')");
                t.HasCheckConstraint("ck_beauty_marketing_consents_admin", "(source = 'admin') = (updated_by_user_id IS NOT NULL)");
                t.HasCheckConstraint("ck_beauty_marketing_consents_version", "source <> 'chat' OR level = 'none' OR text_version IS NOT NULL");
            });
            b.HasKey(x => new { x.TenantId, x.ClientId, x.Channel });
            b.Property(x => x.Channel).HasMaxLength(16);
            b.Property(x => x.Level).HasMaxLength(16);
            b.Property(x => x.Source).HasMaxLength(16);
            b.Property(x => x.TextVersion).HasMaxLength(32);
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
            TenantFk(b, x => x.Client, x => new { x.TenantId, x.ClientId }, DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(x => new { x.TenantId, x.UpdatedByUserId })
                .HasPrincipalKey(u => new { u.TenantId, u.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Conversation>().WithMany().HasForeignKey(x => new { x.TenantId, x.ConversationId })
                .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
            // PK (tenant_id, client_id, channel) покриває FK клієнта
            b.HasIndex(x => new { x.TenantId, x.UpdatedByUserId }).HasFilter("updated_by_user_id IS NOT NULL");
            b.HasIndex(x => new { x.TenantId, x.ConversationId }).HasFilter("conversation_id IS NOT NULL");
        });

        mb.Entity<MarketingConsentEvent>(b =>
        {
            b.ToTable("beauty_marketing_consent_events", t =>
            {
                t.HasCheckConstraint("ck_beauty_marketing_consent_events_channel", "channel IN ('telegram', 'push')");
                t.HasCheckConstraint("ck_beauty_marketing_consent_events_level_from", "level_from IS NULL OR level_from IN ('full', 'selective', 'none')");
                t.HasCheckConstraint("ck_beauty_marketing_consent_events_level_to", "level_to IN ('full', 'selective', 'none')");
                t.HasCheckConstraint("ck_beauty_marketing_consent_events_source", "source IN ('chat', 'admin', 'stop')");
                t.HasCheckConstraint("ck_beauty_marketing_consent_events_via", "via IS NULL OR via IN ('button', 'command', 'text', 'api')");
                t.HasCheckConstraint("ck_beauty_marketing_consent_events_basis", "basis IS NULL OR basis IN ('written', 'in_person', 'other')");
                t.HasCheckConstraint("ck_beauty_marketing_consent_events_basis_admin", "basis IS NULL OR source = 'admin'");
                t.HasCheckConstraint("ck_beauty_marketing_consent_events_basis_note", "basis IS NULL OR basis_note IS NOT NULL");
                t.HasCheckConstraint("ck_beauty_marketing_consent_events_changed", "level_from IS DISTINCT FROM level_to");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.HasAlternateKey(x => new { x.TenantId, x.Id });
            b.Property(x => x.Channel).HasMaxLength(16);
            b.Property(x => x.LevelFrom).HasMaxLength(16);
            b.Property(x => x.LevelTo).HasMaxLength(16);
            b.Property(x => x.Source).HasMaxLength(16);
            b.Property(x => x.Via).HasMaxLength(16);
            b.Property(x => x.TextVersion).HasMaxLength(32);
            b.Property(x => x.Basis).HasMaxLength(16);
            b.Property(x => x.BasisNote).HasMaxLength(200);
            TenantFk(b, x => x.Client, x => new { x.TenantId, x.ClientId }, DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(x => new { x.TenantId, x.ActorUserId })
                .HasPrincipalKey(u => new { u.TenantId, u.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Campaign>().WithMany().HasForeignKey(x => new { x.TenantId, x.CampaignId })
                .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Conversation>().WithMany().HasForeignKey(x => new { x.TenantId, x.ConversationId })
                .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.TenantId, x.ClientId, x.CreatedAt });
            b.HasIndex(x => new { x.TenantId, x.CampaignId, x.CreatedAt }).HasFilter("campaign_id IS NOT NULL");
            b.HasIndex(x => new { x.TenantId, x.ActorUserId }).HasFilter("actor_user_id IS NOT NULL");
            b.HasIndex(x => new { x.TenantId, x.ConversationId }).HasFilter("conversation_id IS NOT NULL");
        });

        mb.Entity<Campaign>(b =>
        {
            Base(b, "beauty_campaigns", t =>
            {
                t.HasCheckConstraint("ck_beauty_campaigns_type", "type IN ('promo', 'news')");
                t.HasCheckConstraint("ck_beauty_campaigns_segment_kind", "segment_kind IN ('lapsed', 'frequent', 'new', 'all_consented')");
                t.HasCheckConstraint("ck_beauty_campaigns_segment_ranges",
                    "(segment_days IS NULL OR segment_days BETWEEN 1 AND 730) AND (segment_min_visits IS NULL OR segment_min_visits BETWEEN 1 AND 100) " +
                    "AND (segment_period_days IS NULL OR segment_period_days BETWEEN 1 AND 730)");
                t.HasCheckConstraint("ck_beauty_campaigns_coupon_mode", "coupon_mode IN ('none', 'personal', 'shared')");
                t.HasCheckConstraint("ck_beauty_campaigns_status", "status IN ('draft', 'scheduled', 'sending', 'done', 'cancelled')");
                t.HasCheckConstraint("ck_beauty_campaigns_type_promotion",
                    "(type <> 'news' OR (promotion_id IS NULL AND coupon_mode = 'none')) AND (type <> 'promo' OR promotion_id IS NOT NULL)");
                t.HasCheckConstraint("ck_beauty_campaigns_segment_params",
                    "(segment_kind <> 'lapsed' OR (segment_days IS NOT NULL AND segment_min_visits IS NULL AND segment_period_days IS NULL)) " +
                    "AND (segment_kind <> 'frequent' OR (segment_min_visits IS NOT NULL AND segment_period_days IS NOT NULL AND segment_days IS NULL AND segment_service_id IS NULL)) " +
                    "AND (segment_kind NOT IN ('new', 'all_consented') OR (segment_days IS NULL AND segment_service_id IS NULL AND segment_min_visits IS NULL AND segment_period_days IS NULL))");
                t.HasCheckConstraint("ck_beauty_campaigns_shared_coupon", "(coupon_mode = 'shared') = (shared_coupon_id IS NOT NULL)");
                t.HasCheckConstraint("ck_beauty_campaigns_personal_valid_to", "coupon_mode <> 'personal' OR coupon_valid_to IS NOT NULL");
                t.HasCheckConstraint("ck_beauty_campaigns_coupon_period",
                    "coupon_valid_from IS NULL OR coupon_valid_to IS NULL OR coupon_valid_to > coupon_valid_from");
                t.HasCheckConstraint("ck_beauty_campaigns_confirmed",
                    "status NOT IN ('scheduled', 'sending', 'done') OR (confirmed_by_user_id IS NOT NULL AND confirmed_at IS NOT NULL AND scheduled_at IS NOT NULL)");
                t.HasCheckConstraint("ck_beauty_campaigns_cancelled", "(status = 'cancelled') = (cancelled_at IS NOT NULL)");
                t.HasCheckConstraint("ck_beauty_campaigns_revision", "revision >= 1");
                t.HasCheckConstraint("ck_beauty_campaigns_recipients_total", "recipients_total IS NULL OR recipients_total >= 0");
            });
            b.Property(x => x.Name).HasMaxLength(200);
            b.Property(x => x.Type).HasMaxLength(8);
            b.Property(x => x.SegmentKind).HasMaxLength(16);
            b.Property(x => x.Template).HasMaxLength(1000);
            b.Property(x => x.CouponMode).HasMaxLength(8).HasDefaultValue("none");
            b.Property(x => x.Status).HasMaxLength(12).HasDefaultValue("draft");
            b.Property(x => x.Revision).HasDefaultValue(1);
            b.Property(x => x.ConfirmKeyHash).HasColumnType("character(64)");
            TenantFk(b, x => x.Promotion, x => new { x.TenantId, x.PromotionId }, DeleteBehavior.Restrict);
            TenantFk(b, x => x.SegmentService, x => new { x.TenantId, x.SegmentServiceId }, DeleteBehavior.Restrict);
            TenantFk(b, x => x.Location, x => new { x.TenantId, x.LocationId }, DeleteBehavior.Restrict);
            TenantFk(b, x => x.SharedCoupon, x => new { x.TenantId, x.SharedCouponId }, DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(x => new { x.TenantId, x.CreatedByUserId })
                .HasPrincipalKey(u => new { u.TenantId, u.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(x => new { x.TenantId, x.ConfirmedByUserId })
                .HasPrincipalKey(u => new { u.TenantId, u.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(x => new { x.TenantId, x.CancelledByUserId })
                .HasPrincipalKey(u => new { u.TenantId, u.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.TenantId, x.Status, x.ScheduledAt });
            b.HasIndex(x => new { x.TenantId, x.PromotionId });
            b.HasIndex(x => new { x.TenantId, x.SegmentServiceId }).HasFilter("segment_service_id IS NOT NULL");
            b.HasIndex(x => new { x.TenantId, x.LocationId }).HasFilter("location_id IS NOT NULL");
            b.HasIndex(x => new { x.TenantId, x.SharedCouponId }).HasFilter("shared_coupon_id IS NOT NULL");
            b.HasIndex(x => new { x.TenantId, x.CreatedByUserId });
            b.HasIndex(x => new { x.TenantId, x.ConfirmedByUserId }).HasFilter("confirmed_by_user_id IS NOT NULL");
            b.HasIndex(x => new { x.TenantId, x.CancelledByUserId }).HasFilter("cancelled_by_user_id IS NOT NULL");
        });

        mb.Entity<CampaignRecipient>(b =>
        {
            Base(b, "beauty_campaign_recipients", t =>
            {
                t.HasCheckConstraint("ck_beauty_campaign_recipients_status", "status IN ('pending', 'queued', 'sent', 'failed', 'skipped')");
                t.HasCheckConstraint("ck_beauty_campaign_recipients_message", "status NOT IN ('queued', 'sent', 'failed') OR message_id IS NOT NULL");
                t.HasCheckConstraint("ck_beauty_campaign_recipients_skip_reason", "status <> 'skipped' OR skip_reason IS NOT NULL");
            });
            b.Property(x => x.Status).HasMaxLength(8).HasDefaultValue("pending");
            b.Property(x => x.SkipReason).HasMaxLength(32);
            TenantFk(b, x => x.Campaign, x => new { x.TenantId, x.CampaignId }, DeleteBehavior.Restrict);
            TenantFk(b, x => x.Client, x => new { x.TenantId, x.ClientId }, DeleteBehavior.Restrict);
            TenantFk(b, x => x.Message, x => new { x.TenantId, x.MessageId }, DeleteBehavior.Restrict);
            TenantFk(b, x => x.Coupon, x => new { x.TenantId, x.CouponId }, DeleteBehavior.Restrict);
            b.HasOne<Conversation>().WithMany().HasForeignKey(x => new { x.TenantId, x.ConversationId })
                .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
            // ключ ідемпотентності одержувача (і індекс FK campaign_id)
            b.HasIndex(x => new { x.TenantId, x.CampaignId, x.ClientId }).IsUnique().HasDatabaseName("ux_beauty_campaign_recipients_campaign_client");
            b.HasIndex(x => new { x.TenantId, x.CampaignId, x.Status });
            b.HasIndex(x => new { x.TenantId, x.ClientId, x.QueuedAt });
            b.HasIndex(x => new { x.TenantId, x.MessageId }).HasFilter("message_id IS NOT NULL");
            b.HasIndex(x => new { x.TenantId, x.CouponId }).HasFilter("coupon_id IS NOT NULL");
            b.HasIndex(x => new { x.TenantId, x.ConversationId }).HasFilter("conversation_id IS NOT NULL");
        });

        mb.Entity<Coupon>(b =>
        {
            Base(b, "beauty_coupons", t =>
            {
                t.HasCheckConstraint("ck_beauty_coupons_kind", "kind IN ('personal', 'shared')");
                t.HasCheckConstraint("ck_beauty_coupons_status", "status IN ('active', 'revoked')");
                t.HasCheckConstraint("ck_beauty_coupons_code_hash", $"code_hash ~ '{Hex64}'");
                t.HasCheckConstraint("ck_beauty_coupons_period", "valid_to > valid_from");
                t.HasCheckConstraint("ck_beauty_coupons_kind_fields",
                    "(kind <> 'personal' OR (client_id IS NOT NULL AND campaign_id IS NOT NULL AND code IS NULL AND max_uses IS NOT NULL AND max_uses = 1)) " +
                    "AND (kind <> 'shared' OR (client_id IS NULL AND code IS NOT NULL))");
                t.HasCheckConstraint("ck_beauty_coupons_uses", "uses_count >= 0 AND (max_uses IS NULL OR uses_count <= max_uses)");
                t.HasCheckConstraint("ck_beauty_coupons_max_uses", "max_uses IS NULL OR max_uses BETWEEN 1 AND 100000");
            });
            b.Property(x => x.Kind).HasMaxLength(8);
            b.Property(x => x.CodeHash).HasColumnType("character(64)");
            b.Property(x => x.Code).HasMaxLength(20);
            b.Property(x => x.CodeHint).HasMaxLength(4);
            b.Property(x => x.Status).HasMaxLength(8).HasDefaultValue("active");
            b.Property(x => x.UsesCount).HasDefaultValue(0);
            TenantFk(b, x => x.Promotion, x => new { x.TenantId, x.PromotionId }, DeleteBehavior.Restrict);
            TenantFk(b, x => x.Campaign, x => new { x.TenantId, x.CampaignId }, DeleteBehavior.Restrict);
            TenantFk(b, x => x.Client, x => new { x.TenantId, x.ClientId }, DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(x => new { x.TenantId, x.CreatedByUserId })
                .HasPrincipalKey(u => new { u.TenantId, u.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.TenantId, x.CodeHash }).IsUnique().HasDatabaseName("ux_beauty_coupons_code_hash");
            b.HasIndex(x => new { x.TenantId, x.ClientId }).HasFilter("client_id IS NOT NULL");
            b.HasIndex(x => new { x.TenantId, x.CampaignId });
            b.HasIndex(x => new { x.TenantId, x.PromotionId });
            b.HasIndex(x => new { x.TenantId, x.CreatedByUserId }).HasFilter("created_by_user_id IS NOT NULL");
        });

        mb.Entity<CouponRedemption>(b =>
        {
            b.ToTable("beauty_coupon_redemptions", t =>
            {
                t.HasCheckConstraint("ck_beauty_coupon_redemptions_amount", "discount_amount >= 0");
                t.HasCheckConstraint("ck_beauty_coupon_redemptions_status", "status IN ('active', 'released')");
                t.HasCheckConstraint("ck_beauty_coupon_redemptions_reason", "release_reason IS NULL OR release_reason IN ('cancelled', 'payment_failed')");
                t.HasCheckConstraint("ck_beauty_coupon_redemptions_released",
                    "(status = 'released') = (released_at IS NOT NULL AND release_reason IS NOT NULL)");
                // §26.3 дослівно пропускає released_at без release_reason (і навпаки) у active через AND з NULL: забороняємо половинний стан
                t.HasCheckConstraint("ck_beauty_coupon_redemptions_released_pair", "(released_at IS NULL) = (release_reason IS NULL)");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.HasAlternateKey(x => new { x.TenantId, x.Id });
            b.Property(x => x.DiscountAmount).HasPrecision(12, 2);
            b.Property(x => x.Status).HasMaxLength(8).HasDefaultValue("active");
            b.Property(x => x.ReleaseReason).HasMaxLength(16);
            b.HasOne(x => x.Coupon).WithMany().HasForeignKey(x => new { x.TenantId, x.CouponId })
                .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(x => x.Appointment).WithMany().HasForeignKey(x => new { x.TenantId, x.AppointmentId })
                .HasPrincipalKey(a => new { a.TenantId, a.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(x => x.Client).WithMany().HasForeignKey(x => new { x.TenantId, x.ClientId })
                .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.TenantId, x.AppointmentId }).IsUnique().HasDatabaseName("ux_beauty_coupon_redemptions_appointment");
            b.HasIndex(x => new { x.TenantId, x.CouponId, x.ClientId }).IsUnique().HasFilter("status = 'active'")
                .HasDatabaseName("ux_beauty_coupon_redemptions_active");
            b.HasIndex(x => new { x.TenantId, x.CouponId });
            b.HasIndex(x => new { x.TenantId, x.ClientId });
        });
    }

    // ---------- Нагадування клієнтам, вподобання, задачі дзвінків (TASK-736, §22.3; RLS in migration beauty_reminders_contacts_inbox) ----------

    private const string ReminderOffsetsSql = "ARRAY[60, 120, 1440]::smallint[]";

    private static void ConfigureReminders(ModelBuilder mb)
    {
        mb.Entity<ClientContactPreference>(b =>
        {
            b.ToTable("beauty_client_contact_preferences", t =>
            {
                t.HasCheckConstraint("ck_beauty_client_contact_preferences_method", "method IN ('none', 'telegram', 'call')");
                t.HasCheckConstraint("ck_beauty_client_contact_preferences_offsets",
                    $"offsets_minutes <@ {ReminderOffsetsSql} AND cardinality(offsets_minutes) <= 3");
                t.HasCheckConstraint("ck_beauty_client_contact_preferences_none_empty",
                    "(method = 'none') = (cardinality(offsets_minutes) = 0)");
                t.HasCheckConstraint("ck_beauty_client_contact_preferences_call_single",
                    "method <> 'call' OR cardinality(offsets_minutes) = 1");
                t.HasCheckConstraint("ck_beauty_client_contact_preferences_source", "source IN ('chat', 'admin', 'stop', 'web')");
                t.HasCheckConstraint("ck_beauty_client_contact_preferences_updated_by", "updated_by_user_id IS NULL OR source = 'admin'");
            });
            b.HasKey(x => new { x.TenantId, x.ClientId });
            b.Property(x => x.Method).HasMaxLength(16);
            b.Property(x => x.Source).HasMaxLength(16);
            b.Property(x => x.OffsetsMinutes).HasColumnType("smallint[]").HasDefaultValueSql("'{}'");
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
            TenantFk(b, x => x.Client, x => new { x.TenantId, x.ClientId }, DeleteBehavior.Cascade);
            b.HasOne<Conversation>().WithMany().HasForeignKey(x => new { x.TenantId, x.TelegramConversationId })
                .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(x => new { x.TenantId, x.UpdatedByUserId })
                .HasPrincipalKey(u => new { u.TenantId, u.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.TenantId, x.TelegramConversationId });
            b.HasIndex(x => new { x.TenantId, x.UpdatedByUserId });
        });

        mb.Entity<CallTask>(b =>
        {
            Base(b, "beauty_call_tasks", t =>
            {
                t.HasCheckConstraint("ck_beauty_call_tasks_reason", "reason IN ('reminder', 'delivery_failed')");
                t.HasCheckConstraint("ck_beauty_call_tasks_status", "status IN ('open', 'done', 'skipped', 'cancelled')");
                t.HasCheckConstraint("ck_beauty_call_tasks_completed",
                    "(status IN ('done', 'skipped') AND completed_by_user_id IS NOT NULL AND completed_at IS NOT NULL) " +
                    "OR (status NOT IN ('done', 'skipped') AND completed_by_user_id IS NULL AND completed_at IS NULL)");
            });
            b.Property(x => x.Reason).HasMaxLength(16);
            b.Property(x => x.Status).HasMaxLength(16).HasDefaultValue("open");
            b.Property(x => x.ClosedReason).HasMaxLength(32);
            b.Property(x => x.Note).HasMaxLength(300);
            TenantFk(b, x => x.Appointment, x => new { x.TenantId, x.AppointmentId }, DeleteBehavior.Restrict);
            TenantFk(b, x => x.Client, x => new { x.TenantId, x.ClientId }, DeleteBehavior.Restrict);
            TenantFk(b, x => x.Location, x => new { x.TenantId, x.LocationId }, DeleteBehavior.Restrict);
            TenantFk(b, x => x.Reminder, x => new { x.TenantId, x.ReminderId }, DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompletedByUserId })
                .HasPrincipalKey(u => new { u.TenantId, u.Id }).OnDelete(DeleteBehavior.Restrict);
            // одна задача на нагадування-точку (також індекс для FK reminder_id)
            b.HasIndex(x => new { x.TenantId, x.ReminderId }).IsUnique().HasFilter("reminder_id IS NOT NULL")
                .HasDatabaseName("ux_beauty_call_tasks_reminder");
            // одна відкрита задача-нагадування на запис
            b.HasIndex(x => new { x.TenantId, x.AppointmentId }).IsUnique().HasFilter("status = 'open' AND reason = 'reminder'")
                .HasDatabaseName("ux_beauty_call_tasks_open_reminder");
            b.HasIndex(x => new { x.TenantId, x.Status, x.DueAt });
            b.HasIndex(x => new { x.TenantId, x.LocationId, x.DueAt });
            b.HasIndex(x => new { x.TenantId, x.AppointmentId });
            b.HasIndex(x => new { x.TenantId, x.ClientId });
            b.HasIndex(x => new { x.TenantId, x.CompletedByUserId });
        });

        mb.Entity<ReminderSettingsRow>(b =>
        {
            b.ToTable("beauty_reminder_settings", t =>
                t.HasCheckConstraint("ck_beauty_reminder_settings_quiet", "quiet_start <> quiet_end"));
            b.HasKey(x => x.TenantId);
            b.Property(x => x.TelegramEnabled).HasDefaultValue(true);
            b.Property(x => x.CallEnabled).HasDefaultValue(true);
            b.Property(x => x.QuietStart).HasColumnType("time without time zone").HasDefaultValueSql("'22:00'").HasSentinel(new TimeOnly(22, 0));
            b.Property(x => x.QuietEnd).HasColumnType("time without time zone").HasDefaultValueSql("'08:00'").HasSentinel(new TimeOnly(8, 0));
            b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
            b.HasOne<User>().WithMany().HasForeignKey(x => new { x.TenantId, x.UpdatedByUserId })
                .HasPrincipalKey(u => new { u.TenantId, u.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.TenantId, x.UpdatedByUserId });
        });
    }

    // ---------- Інлайн-кнопки Telegram (TASK-749, §23.3, ADR-013; RLS in migration beauty_chat_buttons) ----------

    private static void ConfigureChatButtons(ModelBuilder mb)
    {
        mb.Entity<ChatKeyboard>(b =>
        {
            Base(b, "beauty_chat_keyboards", t =>
            {
                t.HasCheckConstraint("ck_beauty_chat_keyboards_kind",
                    "kind IN ('booking_confirm', 'slot_pick', 'reminder_choice', 'appointment_list', 'appointment_actions', 'cancel_confirm', 'marketing_consent', 'campaign_offer')");
                // TASK-776 (§26.3): кнопка кампанії завжди прив'язана до одержувача
                t.HasCheckConstraint("ck_beauty_chat_keyboards_campaign_recipient", "kind <> 'campaign_offer' OR campaign_recipient_id IS NOT NULL");
                t.HasCheckConstraint("ck_beauty_chat_keyboards_token", "token ~ '^[A-Za-z0-9_-]{22}$'");
                t.HasCheckConstraint("ck_beauty_chat_keyboards_options", "jsonb_typeof(options) = 'array' AND jsonb_array_length(options) <= 10");
                t.HasCheckConstraint("ck_beauty_chat_keyboards_status", "status IN ('active', 'used', 'expired', 'revoked')");
                t.HasCheckConstraint("ck_beauty_chat_keyboards_used",
                    "(status = 'used' AND used_at IS NOT NULL AND used_by_message_id IS NOT NULL) OR (status <> 'used' AND used_at IS NULL AND used_by_message_id IS NULL)");
                t.HasCheckConstraint("ck_beauty_chat_keyboards_used_option",
                    "used_option IS NULL OR (status = 'used' AND used_option BETWEEN 0 AND 9)");
                t.HasCheckConstraint("ck_beauty_chat_keyboards_proposal", "kind <> 'booking_confirm' OR proposal_id IS NOT NULL");
                t.HasCheckConstraint("ck_beauty_chat_keyboards_appointment",
                    "kind NOT IN ('appointment_actions', 'cancel_confirm', 'reminder_choice') OR appointment_id IS NOT NULL");
                t.HasCheckConstraint("ck_beauty_chat_keyboards_clear_attempts", "clear_attempts >= 0");
            });
            b.Property(x => x.Kind).HasMaxLength(24);
            b.Property(x => x.Token).HasMaxLength(22);
            b.Property(x => x.Options).HasColumnType("jsonb");
            b.Property(x => x.Status).HasMaxLength(16).HasDefaultValue("active");
            b.Property(x => x.ClearAttempts).HasDefaultValue((short)0);
            TenantFk(b, x => x.Conversation, x => new { x.TenantId, x.ConversationId }, DeleteBehavior.Restrict);
            TenantFk(b, x => x.Message, x => new { x.TenantId, x.MessageId }, DeleteBehavior.Restrict);
            TenantFk(b, x => x.Proposal, x => new { x.TenantId, x.ProposalId }, DeleteBehavior.Restrict);
            TenantFk(b, x => x.Appointment, x => new { x.TenantId, x.AppointmentId }, DeleteBehavior.Restrict);
            TenantFk(b, x => x.UsedByMessage, x => new { x.TenantId, x.UsedByMessageId }, DeleteBehavior.Restrict);
            TenantFk(b, x => x.CampaignRecipient, x => new { x.TenantId, x.CampaignRecipientId }, DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.TenantId, x.CampaignRecipientId });
            b.HasIndex(x => new { x.TenantId, x.MessageId }).IsUnique(); // одна клавіатура на повідомлення (і індекс FK)
            b.HasIndex(x => new { x.TenantId, x.Token }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.ConversationId }); // FK; частковий нижче не покриває всі рядки
            b.HasIndex(new[] { nameof(ChatKeyboard.TenantId), nameof(ChatKeyboard.ConversationId) }, "ix_beauty_chat_keyboards_active")
                .HasDatabaseName("ix_beauty_chat_keyboards_active").HasFilter("status = 'active'");
            b.HasIndex(x => new { x.TenantId, x.UpdatedAt }).HasFilter("status <> 'active' AND markup_cleared_at IS NULL")
                .HasDatabaseName("ix_beauty_chat_keyboards_sweeper");
            b.HasIndex(x => new { x.TenantId, x.ProposalId });
            b.HasIndex(x => new { x.TenantId, x.AppointmentId });
            b.HasIndex(x => new { x.TenantId, x.UsedByMessageId });
        });
    }

    // ---------- Асистент у чаті клієнта (TASK-724, §21.4; RLS in migration beauty_chat_assistant) ----------

    private static void ConfigureChatAssistant(ModelBuilder mb)
    {
        mb.Entity<ChatBookingProposal>(b =>
        {
            Base(b, "beauty_chat_booking_proposals", t =>
            {
                t.HasCheckConstraint("ck_beauty_chat_booking_proposals_status",
                    "status IN ('prepared', 'shown', 'confirmed', 'rejected', 'expired', 'superseded', 'failed')");
                t.HasCheckConstraint("ck_beauty_chat_booking_proposals_duration", "duration_minutes > 0");
                t.HasCheckConstraint("ck_beauty_chat_booking_proposals_prices", "price_original >= 0 AND price_final >= 0");
                t.HasCheckConstraint("ck_beauty_chat_booking_proposals_token_hash", "confirmation_token_hash ~ '^[0-9a-f]{64}$'");
                t.HasCheckConstraint("ck_beauty_chat_booking_proposals_failure_code",
                    "failure_code IS NULL OR char_length(failure_code) BETWEEN 1 AND 64");
            });
            b.Property(x => x.Status).HasMaxLength(16).HasDefaultValue("prepared");
            b.Property(x => x.PriceOriginal).HasPrecision(10, 2);
            b.Property(x => x.PriceFinal).HasPrecision(10, 2);
            b.Property(x => x.ConfirmationTokenHash).HasColumnType("character(64)");
            b.Property(x => x.FailureCode).HasMaxLength(64);
            TenantFk(b, x => x.Conversation, x => new { x.TenantId, x.ConversationId }, DeleteBehavior.Restrict);
            TenantFk(b, x => x.Client, x => new { x.TenantId, x.ClientId }, DeleteBehavior.Restrict);
            TenantFk(b, x => x.Location, x => new { x.TenantId, x.LocationId }, DeleteBehavior.Restrict);
            TenantFk(b, x => x.Specialist, x => new { x.TenantId, x.SpecialistId }, DeleteBehavior.Restrict);
            TenantFk(b, x => x.Service, x => new { x.TenantId, x.ServiceId }, DeleteBehavior.Restrict);
            TenantFk(b, x => x.Promotion, x => new { x.TenantId, x.PromotionId }, DeleteBehavior.Restrict);
            TenantFk(b, x => x.Coupon, x => new { x.TenantId, x.CouponId }, DeleteBehavior.Restrict); // TASK-776 (§26.3)
            TenantFk(b, x => x.Campaign, x => new { x.TenantId, x.CampaignId }, DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.TenantId, x.CouponId }).HasFilter("coupon_id IS NOT NULL");
            b.HasIndex(x => new { x.TenantId, x.CampaignId }).HasFilter("campaign_id IS NOT NULL");
            TenantFk(b, x => x.SummaryMessage, x => new { x.TenantId, x.SummaryMessageId }, DeleteBehavior.Restrict);
            TenantFk(b, x => x.Appointment, x => new { x.TenantId, x.AppointmentId }, DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.TenantId, x.ConfirmationTokenHash }).IsUnique()
                .HasDatabaseName("ux_beauty_chat_booking_proposals_token");
            // одна активна пропозиція на розмову
            b.HasIndex(x => new { x.TenantId, x.ConversationId }).IsUnique()
                .HasFilter("status IN ('prepared', 'shown')")
                .HasDatabaseName("ux_beauty_chat_booking_proposals_active");
            b.HasIndex(x => new { x.TenantId, x.ConversationId, x.CreatedAt });
            b.HasIndex(x => new { x.TenantId, x.ClientId });
            b.HasIndex(x => new { x.TenantId, x.LocationId });
            b.HasIndex(x => new { x.TenantId, x.SpecialistId });
            b.HasIndex(x => new { x.TenantId, x.ServiceId });
            b.HasIndex(x => new { x.TenantId, x.PromotionId });
            b.HasIndex(x => new { x.TenantId, x.SummaryMessageId });
            b.HasIndex(x => new { x.TenantId, x.AppointmentId });
        });

        mb.Entity<ChannelPollState>(b =>
        {
            b.ToTable("beauty_channel_poll_state", t =>
                t.HasCheckConstraint("ck_beauty_channel_poll_state_last_update_id", "last_update_id >= 0"));
            b.HasKey(x => new { x.TenantId, x.ChannelId });
            b.Property(x => x.LastUpdateId).HasDefaultValue(0L);
            b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
            b.HasOne(x => x.Channel).WithMany().HasForeignKey(x => new { x.TenantId, x.ChannelId })
                .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Cascade);
        });
    }

    // ---------- AI settings per tenant (TASK-713, §20.4; RLS in migration beauty_ai_settings_and_usage) ----------

    private const string ProviderRegex = "^[a-z][a-z0-9_]{1,31}$";

    private static void ConfigureAiSettings(ModelBuilder mb)
    {
        mb.Entity<AiSettingsRow>(b =>
        {
            Base(b, "beauty_ai_settings", t =>
            {
                t.HasCheckConstraint("ck_beauty_ai_settings_key_source", "key_source IN ('own', 'platform')");
                t.HasCheckConstraint("ck_beauty_ai_settings_chat_provider", $"chat_provider ~ '{ProviderRegex}'");
                t.HasCheckConstraint("ck_beauty_ai_settings_analytics_provider", $"analytics_provider ~ '{ProviderRegex}'");
                t.HasCheckConstraint("ck_beauty_ai_settings_chat_model", "char_length(chat_model) BETWEEN 1 AND 100");
                t.HasCheckConstraint("ck_beauty_ai_settings_analytics_model", "char_length(analytics_model) BETWEEN 1 AND 100");
                t.HasCheckConstraint("ck_beauty_ai_settings_mode", "mode IN ('suggest', 'confirm', 'auto')");
                t.HasCheckConstraint("ck_beauty_ai_settings_prompt_addendum", "prompt_addendum IS NULL OR char_length(prompt_addendum) <= 4000");
                t.HasCheckConstraint("ck_beauty_ai_settings_monthly_token_limit",
                    "monthly_token_limit IS NULL OR monthly_token_limit BETWEEN 1 AND 100000000000");
                t.HasCheckConstraint("ck_beauty_ai_settings_monthly_request_limit",
                    "monthly_request_limit IS NULL OR monthly_request_limit BETWEEN 1 AND 10000000");
                t.HasCheckConstraint("ck_beauty_ai_settings_max_output_tokens", "max_output_tokens BETWEEN 64 AND 8192");
                t.HasCheckConstraint("ck_beauty_ai_settings_platform_quotas",
                    "(platform_monthly_token_quota IS NULL OR platform_monthly_token_quota >= 0) AND (platform_monthly_request_quota IS NULL OR platform_monthly_request_quota >= 0)");
            });
            b.Property(x => x.KeySource).HasMaxLength(16).HasDefaultValue("platform");
            b.Property(x => x.ChatProvider).HasMaxLength(32).HasDefaultValue("anthropic");
            b.Property(x => x.ChatModel).HasMaxLength(100);
            b.Property(x => x.AnalyticsProvider).HasMaxLength(32).HasDefaultValue("anthropic");
            b.Property(x => x.AnalyticsModel).HasMaxLength(100);
            b.Property(x => x.Mode).HasMaxLength(16).HasDefaultValue("confirm");
            b.Property(x => x.PromptAddendum).HasColumnType("text");
            b.Property(x => x.MaxOutputTokens).HasDefaultValue(1024);
            b.HasOne<User>().WithMany().HasForeignKey(x => new { x.TenantId, x.UpdatedByUserId })
                .HasPrincipalKey(u => new { u.TenantId, u.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.TenantId).IsUnique(); // один рядок на tenant
            b.HasIndex(x => new { x.TenantId, x.UpdatedByUserId });
        });

        mb.Entity<AiProviderKey>(b =>
        {
            Base(b, "beauty_ai_provider_keys", t =>
            {
                t.HasCheckConstraint("ck_beauty_ai_provider_keys_provider", $"provider ~ '{ProviderRegex}'");
                t.HasCheckConstraint("ck_beauty_ai_provider_keys_key_encrypted", "key_encrypted LIKE 'v2:%'");
                t.HasCheckConstraint("ck_beauty_ai_provider_keys_key_kid", "key_kid ~ '^[a-z0-9-]{1,32}$'");
            });
            b.Property(x => x.Provider).HasMaxLength(32);
            b.Property(x => x.KeyEncrypted).HasColumnType("text");
            b.Property(x => x.KeyKid).HasMaxLength(32);
            b.Property(x => x.KeyLast4).HasMaxLength(4);
            b.HasOne<User>().WithMany().HasForeignKey(x => new { x.TenantId, x.CreatedByUserId })
                .HasPrincipalKey(u => new { u.TenantId, u.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(x => new { x.TenantId, x.UpdatedByUserId })
                .HasPrincipalKey(u => new { u.TenantId, u.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.TenantId, x.Provider }).IsUnique();
            b.HasIndex(x => x.KeyKid); // ротація keyring
            b.HasIndex(x => new { x.TenantId, x.CreatedByUserId });
            b.HasIndex(x => new { x.TenantId, x.UpdatedByUserId });
        });

        mb.Entity<AiUsageRow>(b =>
        {
            Base(b, "beauty_ai_usage", t =>
            {
                t.HasCheckConstraint("ck_beauty_ai_usage_period_start", "extract(day from period_start) = 1");
                t.HasCheckConstraint("ck_beauty_ai_usage_role", "role IN ('chat', 'analytics')");
                t.HasCheckConstraint("ck_beauty_ai_usage_provider", $"provider ~ '{ProviderRegex}'");
                t.HasCheckConstraint("ck_beauty_ai_usage_key_source", "key_source IN ('own', 'platform')");
                t.HasCheckConstraint("ck_beauty_ai_usage_counters",
                    "request_count >= 0 AND error_count >= 0 AND input_tokens >= 0 AND output_tokens >= 0");
            });
            b.Property(x => x.PeriodStart).HasColumnType("date");
            b.Property(x => x.Role).HasMaxLength(16);
            b.Property(x => x.Provider).HasMaxLength(32);
            b.Property(x => x.Model).HasMaxLength(100);
            b.Property(x => x.KeySource).HasMaxLength(16);
            b.Property(x => x.RequestCount).HasDefaultValue(0);
            b.Property(x => x.ErrorCount).HasDefaultValue(0);
            b.Property(x => x.InputTokens).HasDefaultValue(0L);
            b.Property(x => x.OutputTokens).HasDefaultValue(0L);
            // префікс (tenant_id, period_start) обслуговується цим унікальним індексом
            b.HasIndex(x => new { x.TenantId, x.PeriodStart, x.Role, x.Provider, x.Model, x.KeySource }).IsUnique();
        });

        mb.Entity<AiSettingsAuditRow>(b =>
        {
            b.ToTable("beauty_ai_settings_audit", t =>
            {
                t.HasCheckConstraint("ck_beauty_ai_settings_audit_action",
                    "action IN ('settings_updated', 'key_source_changed', 'key_set', 'key_replaced', 'key_deleted', 'key_tested', 'prompt_reset', 'platform_quota_changed')");
                t.HasCheckConstraint("ck_beauty_ai_settings_audit_provider", $"provider IS NULL OR provider ~ '{ProviderRegex}'");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            b.HasAlternateKey(x => new { x.TenantId, x.Id });
            b.Property(x => x.Action).HasMaxLength(32);
            b.Property(x => x.Provider).HasMaxLength(32);
            b.Property(x => x.ChangedFields).HasColumnType("text[]").HasDefaultValueSql("'{}'");
            b.HasOne<User>().WithMany().HasForeignKey(x => new { x.TenantId, x.UserId })
                .HasPrincipalKey(u => new { u.TenantId, u.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.TenantId, x.CreatedAt }).IsDescending(false, true);
            b.HasIndex(x => new { x.TenantId, x.UserId });
        });
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
