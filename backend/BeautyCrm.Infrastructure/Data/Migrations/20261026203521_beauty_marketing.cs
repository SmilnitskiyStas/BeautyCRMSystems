using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class beauty_marketing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_chat_keyboards_kind",
                table: "beauty_chat_keyboards");

            migrationBuilder.AddColumn<string>(
                name: "access",
                table: "beauty_promotions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "public");

            migrationBuilder.AddColumn<Guid>(
                name: "offer_campaign_id",
                table: "beauty_conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "offer_coupon_id",
                table: "beauty_conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "offer_until",
                table: "beauty_conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "marketing_offered_at",
                table: "beauty_clients",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "campaign_recipient_id",
                table: "beauty_chat_keyboards",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "campaign_id",
                table: "beauty_chat_booking_proposals",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "coupon_id",
                table: "beauty_chat_booking_proposals",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "campaign_id",
                table: "beauty_appointments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "coupon_id",
                table: "beauty_appointments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "beauty_marketing_consents",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    level = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    text_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_marketing_consents", x => new { x.tenant_id, x.client_id, x.channel });
                    table.CheckConstraint("ck_beauty_marketing_consents_admin", "(source = 'admin') = (updated_by_user_id IS NOT NULL)");
                    table.CheckConstraint("ck_beauty_marketing_consents_channel", "channel IN ('telegram', 'push')");
                    table.CheckConstraint("ck_beauty_marketing_consents_level", "level IN ('full', 'selective', 'none')");
                    table.CheckConstraint("ck_beauty_marketing_consents_source", "source IN ('chat', 'admin', 'stop')");
                    table.CheckConstraint("ck_beauty_marketing_consents_version", "source <> 'chat' OR level = 'none' OR text_version IS NOT NULL");
                    table.ForeignKey(
                        name: "fk_beauty_marketing_consents_beauty_clients_tenant_id_client_id",
                        columns: x => new { x.tenant_id, x.client_id },
                        principalTable: "beauty_clients",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_marketing_consents_beauty_conversations_tenant_id_co",
                        columns: x => new { x.tenant_id, x.conversation_id },
                        principalTable: "beauty_conversations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_marketing_consents_users_tenant_id_updated_by_user_id",
                        columns: x => new { x.tenant_id, x.updated_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "beauty_campaign_recipients",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false, defaultValue: "pending"),
                    skip_reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    coupon_id = table.Column<Guid>(type: "uuid", nullable: true),
                    queued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    clicked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_campaign_recipients", x => x.id);
                    table.UniqueConstraint("ak_campaign_recipients_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_campaign_recipients_message", "status NOT IN ('queued', 'sent', 'failed') OR message_id IS NOT NULL");
                    table.CheckConstraint("ck_beauty_campaign_recipients_skip_reason", "status <> 'skipped' OR skip_reason IS NOT NULL");
                    table.CheckConstraint("ck_beauty_campaign_recipients_status", "status IN ('pending', 'queued', 'sent', 'failed', 'skipped')");
                    table.ForeignKey(
                        name: "fk_beauty_campaign_recipients_beauty_clients_tenant_id_client_",
                        columns: x => new { x.tenant_id, x.client_id },
                        principalTable: "beauty_clients",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_campaign_recipients_beauty_conversations_tenant_id_c",
                        columns: x => new { x.tenant_id, x.conversation_id },
                        principalTable: "beauty_conversations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_campaign_recipients_beauty_messages_tenant_id_messag",
                        columns: x => new { x.tenant_id, x.message_id },
                        principalTable: "beauty_messages",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "beauty_campaigns",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    type = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    promotion_id = table.Column<Guid>(type: "uuid", nullable: true),
                    segment_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    segment_days = table.Column<short>(type: "smallint", nullable: true),
                    segment_service_id = table.Column<Guid>(type: "uuid", nullable: true),
                    segment_min_visits = table.Column<short>(type: "smallint", nullable: true),
                    segment_period_days = table.Column<short>(type: "smallint", nullable: true),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    template = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    coupon_mode = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false, defaultValue: "none"),
                    shared_coupon_id = table.Column<Guid>(type: "uuid", nullable: true),
                    coupon_valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    coupon_valid_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false, defaultValue: "draft"),
                    revision = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    scheduled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    recipients_total = table.Column<int>(type: "integer", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    confirmed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    confirm_key_hash = table.Column<string>(type: "character(64)", nullable: true),
                    cancelled_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_campaigns", x => x.id);
                    table.UniqueConstraint("ak_campaigns_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_campaigns_cancelled", "(status = 'cancelled') = (cancelled_at IS NOT NULL)");
                    table.CheckConstraint("ck_beauty_campaigns_confirmed", "status NOT IN ('scheduled', 'sending', 'done') OR (confirmed_by_user_id IS NOT NULL AND confirmed_at IS NOT NULL AND scheduled_at IS NOT NULL)");
                    table.CheckConstraint("ck_beauty_campaigns_coupon_mode", "coupon_mode IN ('none', 'personal', 'shared')");
                    table.CheckConstraint("ck_beauty_campaigns_coupon_period", "coupon_valid_from IS NULL OR coupon_valid_to IS NULL OR coupon_valid_to > coupon_valid_from");
                    table.CheckConstraint("ck_beauty_campaigns_personal_valid_to", "coupon_mode <> 'personal' OR coupon_valid_to IS NOT NULL");
                    table.CheckConstraint("ck_beauty_campaigns_recipients_total", "recipients_total IS NULL OR recipients_total >= 0");
                    table.CheckConstraint("ck_beauty_campaigns_revision", "revision >= 1");
                    table.CheckConstraint("ck_beauty_campaigns_segment_kind", "segment_kind IN ('lapsed', 'frequent', 'new', 'all_consented')");
                    table.CheckConstraint("ck_beauty_campaigns_segment_params", "(segment_kind <> 'lapsed' OR (segment_days IS NOT NULL AND segment_min_visits IS NULL AND segment_period_days IS NULL)) AND (segment_kind <> 'frequent' OR (segment_min_visits IS NOT NULL AND segment_period_days IS NOT NULL AND segment_days IS NULL AND segment_service_id IS NULL)) AND (segment_kind NOT IN ('new', 'all_consented') OR (segment_days IS NULL AND segment_service_id IS NULL AND segment_min_visits IS NULL AND segment_period_days IS NULL))");
                    table.CheckConstraint("ck_beauty_campaigns_segment_ranges", "(segment_days IS NULL OR segment_days BETWEEN 1 AND 730) AND (segment_min_visits IS NULL OR segment_min_visits BETWEEN 1 AND 100) AND (segment_period_days IS NULL OR segment_period_days BETWEEN 1 AND 730)");
                    table.CheckConstraint("ck_beauty_campaigns_shared_coupon", "(coupon_mode = 'shared') = (shared_coupon_id IS NOT NULL)");
                    table.CheckConstraint("ck_beauty_campaigns_status", "status IN ('draft', 'scheduled', 'sending', 'done', 'cancelled')");
                    table.CheckConstraint("ck_beauty_campaigns_type", "type IN ('promo', 'news')");
                    table.CheckConstraint("ck_beauty_campaigns_type_promotion", "(type <> 'news' OR (promotion_id IS NULL AND coupon_mode = 'none')) AND (type <> 'promo' OR promotion_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_beauty_campaigns_beauty_locations_tenant_id_location_id",
                        columns: x => new { x.tenant_id, x.location_id },
                        principalTable: "beauty_locations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_campaigns_beauty_promotions_tenant_id_promotion_id",
                        columns: x => new { x.tenant_id, x.promotion_id },
                        principalTable: "beauty_promotions",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_campaigns_beauty_services_tenant_id_segment_service_",
                        columns: x => new { x.tenant_id, x.segment_service_id },
                        principalTable: "beauty_services",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_campaigns_users_tenant_id_cancelled_by_user_id",
                        columns: x => new { x.tenant_id, x.cancelled_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_campaigns_users_tenant_id_confirmed_by_user_id",
                        columns: x => new { x.tenant_id, x.confirmed_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_campaigns_users_tenant_id_created_by_user_id",
                        columns: x => new { x.tenant_id, x.created_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "beauty_coupons",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    promotion_id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: true),
                    client_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code_hash = table.Column<string>(type: "character(64)", nullable: false),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    code_hint = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    max_uses = table.Column<int>(type: "integer", nullable: true),
                    uses_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    status = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false, defaultValue: "active"),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_coupons", x => x.id);
                    table.UniqueConstraint("ak_coupons_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_coupons_code_hash", "code_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_beauty_coupons_kind", "kind IN ('personal', 'shared')");
                    table.CheckConstraint("ck_beauty_coupons_kind_fields", "(kind <> 'personal' OR (client_id IS NOT NULL AND campaign_id IS NOT NULL AND code IS NULL AND max_uses IS NOT NULL AND max_uses = 1)) AND (kind <> 'shared' OR (client_id IS NULL AND code IS NOT NULL))");
                    table.CheckConstraint("ck_beauty_coupons_max_uses", "max_uses IS NULL OR max_uses BETWEEN 1 AND 100000");
                    table.CheckConstraint("ck_beauty_coupons_period", "valid_to > valid_from");
                    table.CheckConstraint("ck_beauty_coupons_status", "status IN ('active', 'revoked')");
                    table.CheckConstraint("ck_beauty_coupons_uses", "uses_count >= 0 AND (max_uses IS NULL OR uses_count <= max_uses)");
                    table.ForeignKey(
                        name: "fk_beauty_coupons_beauty_campaigns_tenant_id_campaign_id",
                        columns: x => new { x.tenant_id, x.campaign_id },
                        principalTable: "beauty_campaigns",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_coupons_beauty_clients_tenant_id_client_id",
                        columns: x => new { x.tenant_id, x.client_id },
                        principalTable: "beauty_clients",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_coupons_beauty_promotions_tenant_id_promotion_id",
                        columns: x => new { x.tenant_id, x.promotion_id },
                        principalTable: "beauty_promotions",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_coupons_users_tenant_id_created_by_user_id",
                        columns: x => new { x.tenant_id, x.created_by_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "beauty_marketing_consent_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    level_from = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    level_to = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    via = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    text_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    basis = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    basis_note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: true),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_marketing_consent_events", x => x.id);
                    table.UniqueConstraint("ak_beauty_marketing_consent_events_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_marketing_consent_events_basis", "basis IS NULL OR basis IN ('written', 'in_person', 'other')");
                    table.CheckConstraint("ck_beauty_marketing_consent_events_basis_admin", "basis IS NULL OR source = 'admin'");
                    table.CheckConstraint("ck_beauty_marketing_consent_events_basis_note", "basis IS NULL OR basis_note IS NOT NULL");
                    table.CheckConstraint("ck_beauty_marketing_consent_events_changed", "level_from IS DISTINCT FROM level_to");
                    table.CheckConstraint("ck_beauty_marketing_consent_events_channel", "channel IN ('telegram', 'push')");
                    table.CheckConstraint("ck_beauty_marketing_consent_events_level_from", "level_from IS NULL OR level_from IN ('full', 'selective', 'none')");
                    table.CheckConstraint("ck_beauty_marketing_consent_events_level_to", "level_to IN ('full', 'selective', 'none')");
                    table.CheckConstraint("ck_beauty_marketing_consent_events_source", "source IN ('chat', 'admin', 'stop')");
                    table.CheckConstraint("ck_beauty_marketing_consent_events_via", "via IS NULL OR via IN ('button', 'command', 'text', 'api')");
                    table.ForeignKey(
                        name: "fk_beauty_marketing_consent_events_beauty_clients_tenant_id_cl",
                        columns: x => new { x.tenant_id, x.client_id },
                        principalTable: "beauty_clients",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_marketing_consent_events_beauty_conversations_tenant",
                        columns: x => new { x.tenant_id, x.conversation_id },
                        principalTable: "beauty_conversations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_marketing_consent_events_campaigns_tenant_id_campaig",
                        columns: x => new { x.tenant_id, x.campaign_id },
                        principalTable: "beauty_campaigns",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_marketing_consent_events_users_tenant_id_actor_user_",
                        columns: x => new { x.tenant_id, x.actor_user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "beauty_coupon_redemptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    coupon_id = table.Column<Guid>(type: "uuid", nullable: false),
                    appointment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    status = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false, defaultValue: "active"),
                    release_reason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_coupon_redemptions", x => x.id);
                    table.UniqueConstraint("ak_beauty_coupon_redemptions_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_coupon_redemptions_amount", "discount_amount >= 0");
                    table.CheckConstraint("ck_beauty_coupon_redemptions_reason", "release_reason IS NULL OR release_reason IN ('cancelled', 'payment_failed')");
                    table.CheckConstraint("ck_beauty_coupon_redemptions_released", "(status = 'released') = (released_at IS NOT NULL AND release_reason IS NOT NULL)");
                    table.CheckConstraint("ck_beauty_coupon_redemptions_released_pair", "(released_at IS NULL) = (release_reason IS NULL)");
                    table.CheckConstraint("ck_beauty_coupon_redemptions_status", "status IN ('active', 'released')");
                    table.ForeignKey(
                        name: "fk_beauty_coupon_redemptions_beauty_appointments_tenant_id_app",
                        columns: x => new { x.tenant_id, x.appointment_id },
                        principalTable: "beauty_appointments",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_coupon_redemptions_beauty_clients_tenant_id_client_id",
                        columns: x => new { x.tenant_id, x.client_id },
                        principalTable: "beauty_clients",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_coupon_redemptions_beauty_coupons_tenant_id_coupon_id",
                        columns: x => new { x.tenant_id, x.coupon_id },
                        principalTable: "beauty_coupons",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_promotions_access",
                table: "beauty_promotions",
                sql: "access IN ('public', 'code_only')");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_conversations_tenant_id_offer_campaign_id",
                table: "beauty_conversations",
                columns: new[] { "tenant_id", "offer_campaign_id" },
                filter: "offer_campaign_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_conversations_tenant_id_offer_coupon_id",
                table: "beauty_conversations",
                columns: new[] { "tenant_id", "offer_coupon_id" },
                filter: "offer_coupon_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_conversations_offer_until_null",
                table: "beauty_conversations",
                sql: "offer_until IS NOT NULL OR (offer_campaign_id IS NULL AND offer_coupon_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_conversations_offer_until_set",
                table: "beauty_conversations",
                sql: "offer_until IS NULL OR offer_campaign_id IS NOT NULL OR offer_coupon_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_chat_keyboards_tenant_id_campaign_recipient_id",
                table: "beauty_chat_keyboards",
                columns: new[] { "tenant_id", "campaign_recipient_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_chat_keyboards_campaign_recipient",
                table: "beauty_chat_keyboards",
                sql: "kind <> 'campaign_offer' OR campaign_recipient_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_chat_keyboards_kind",
                table: "beauty_chat_keyboards",
                sql: "kind IN ('booking_confirm', 'slot_pick', 'reminder_choice', 'appointment_list', 'appointment_actions', 'cancel_confirm', 'marketing_consent', 'campaign_offer')");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_chat_booking_proposals_tenant_id_campaign_id",
                table: "beauty_chat_booking_proposals",
                columns: new[] { "tenant_id", "campaign_id" },
                filter: "campaign_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_chat_booking_proposals_tenant_id_coupon_id",
                table: "beauty_chat_booking_proposals",
                columns: new[] { "tenant_id", "coupon_id" },
                filter: "coupon_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_appointments_tenant_id_campaign_id",
                table: "beauty_appointments",
                columns: new[] { "tenant_id", "campaign_id" },
                filter: "campaign_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_appointments_tenant_id_coupon_id",
                table: "beauty_appointments",
                columns: new[] { "tenant_id", "coupon_id" },
                filter: "coupon_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_campaign_recipients_tenant_id_campaign_id_status",
                table: "beauty_campaign_recipients",
                columns: new[] { "tenant_id", "campaign_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_campaign_recipients_tenant_id_client_id_queued_at",
                table: "beauty_campaign_recipients",
                columns: new[] { "tenant_id", "client_id", "queued_at" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_campaign_recipients_tenant_id_conversation_id",
                table: "beauty_campaign_recipients",
                columns: new[] { "tenant_id", "conversation_id" },
                filter: "conversation_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_campaign_recipients_tenant_id_coupon_id",
                table: "beauty_campaign_recipients",
                columns: new[] { "tenant_id", "coupon_id" },
                filter: "coupon_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_campaign_recipients_tenant_id_message_id",
                table: "beauty_campaign_recipients",
                columns: new[] { "tenant_id", "message_id" },
                filter: "message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_beauty_campaign_recipients_campaign_client",
                table: "beauty_campaign_recipients",
                columns: new[] { "tenant_id", "campaign_id", "client_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_beauty_campaigns_tenant_id_cancelled_by_user_id",
                table: "beauty_campaigns",
                columns: new[] { "tenant_id", "cancelled_by_user_id" },
                filter: "cancelled_by_user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_campaigns_tenant_id_confirmed_by_user_id",
                table: "beauty_campaigns",
                columns: new[] { "tenant_id", "confirmed_by_user_id" },
                filter: "confirmed_by_user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_campaigns_tenant_id_created_by_user_id",
                table: "beauty_campaigns",
                columns: new[] { "tenant_id", "created_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_campaigns_tenant_id_location_id",
                table: "beauty_campaigns",
                columns: new[] { "tenant_id", "location_id" },
                filter: "location_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_campaigns_tenant_id_promotion_id",
                table: "beauty_campaigns",
                columns: new[] { "tenant_id", "promotion_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_campaigns_tenant_id_segment_service_id",
                table: "beauty_campaigns",
                columns: new[] { "tenant_id", "segment_service_id" },
                filter: "segment_service_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_campaigns_tenant_id_shared_coupon_id",
                table: "beauty_campaigns",
                columns: new[] { "tenant_id", "shared_coupon_id" },
                filter: "shared_coupon_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_campaigns_tenant_id_status_scheduled_at",
                table: "beauty_campaigns",
                columns: new[] { "tenant_id", "status", "scheduled_at" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_coupon_redemptions_tenant_id_client_id",
                table: "beauty_coupon_redemptions",
                columns: new[] { "tenant_id", "client_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_coupon_redemptions_tenant_id_coupon_id",
                table: "beauty_coupon_redemptions",
                columns: new[] { "tenant_id", "coupon_id" });

            migrationBuilder.CreateIndex(
                name: "ux_beauty_coupon_redemptions_active",
                table: "beauty_coupon_redemptions",
                columns: new[] { "tenant_id", "coupon_id", "client_id" },
                unique: true,
                filter: "status = 'active'");

            migrationBuilder.CreateIndex(
                name: "ux_beauty_coupon_redemptions_appointment",
                table: "beauty_coupon_redemptions",
                columns: new[] { "tenant_id", "appointment_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_beauty_coupons_tenant_id_campaign_id",
                table: "beauty_coupons",
                columns: new[] { "tenant_id", "campaign_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_coupons_tenant_id_client_id",
                table: "beauty_coupons",
                columns: new[] { "tenant_id", "client_id" },
                filter: "client_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_coupons_tenant_id_created_by_user_id",
                table: "beauty_coupons",
                columns: new[] { "tenant_id", "created_by_user_id" },
                filter: "created_by_user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_coupons_tenant_id_promotion_id",
                table: "beauty_coupons",
                columns: new[] { "tenant_id", "promotion_id" });

            migrationBuilder.CreateIndex(
                name: "ux_beauty_coupons_code_hash",
                table: "beauty_coupons",
                columns: new[] { "tenant_id", "code_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_beauty_marketing_consent_events_tenant_id_actor_user_id",
                table: "beauty_marketing_consent_events",
                columns: new[] { "tenant_id", "actor_user_id" },
                filter: "actor_user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_marketing_consent_events_tenant_id_campaign_id_creat",
                table: "beauty_marketing_consent_events",
                columns: new[] { "tenant_id", "campaign_id", "created_at" },
                filter: "campaign_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_marketing_consent_events_tenant_id_client_id_created",
                table: "beauty_marketing_consent_events",
                columns: new[] { "tenant_id", "client_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_marketing_consent_events_tenant_id_conversation_id",
                table: "beauty_marketing_consent_events",
                columns: new[] { "tenant_id", "conversation_id" },
                filter: "conversation_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_marketing_consents_tenant_id_conversation_id",
                table: "beauty_marketing_consents",
                columns: new[] { "tenant_id", "conversation_id" },
                filter: "conversation_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_marketing_consents_tenant_id_updated_by_user_id",
                table: "beauty_marketing_consents",
                columns: new[] { "tenant_id", "updated_by_user_id" },
                filter: "updated_by_user_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_beauty_appointments_campaigns_tenant_id_campaign_id",
                table: "beauty_appointments",
                columns: new[] { "tenant_id", "campaign_id" },
                principalTable: "beauty_campaigns",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_beauty_appointments_coupons_tenant_id_coupon_id",
                table: "beauty_appointments",
                columns: new[] { "tenant_id", "coupon_id" },
                principalTable: "beauty_coupons",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_beauty_chat_booking_proposals_campaigns_tenant_id_campaign_",
                table: "beauty_chat_booking_proposals",
                columns: new[] { "tenant_id", "campaign_id" },
                principalTable: "beauty_campaigns",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_beauty_chat_booking_proposals_coupons_tenant_id_coupon_id",
                table: "beauty_chat_booking_proposals",
                columns: new[] { "tenant_id", "coupon_id" },
                principalTable: "beauty_coupons",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_beauty_chat_keyboards_campaign_recipients_tenant_id_campaig",
                table: "beauty_chat_keyboards",
                columns: new[] { "tenant_id", "campaign_recipient_id" },
                principalTable: "beauty_campaign_recipients",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_beauty_conversations_campaigns_tenant_id_offer_campaign_id",
                table: "beauty_conversations",
                columns: new[] { "tenant_id", "offer_campaign_id" },
                principalTable: "beauty_campaigns",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_beauty_conversations_coupons_tenant_id_offer_coupon_id",
                table: "beauty_conversations",
                columns: new[] { "tenant_id", "offer_coupon_id" },
                principalTable: "beauty_coupons",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_beauty_campaign_recipients_beauty_campaigns_tenant_id_campa",
                table: "beauty_campaign_recipients",
                columns: new[] { "tenant_id", "campaign_id" },
                principalTable: "beauty_campaigns",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_beauty_campaign_recipients_coupons_tenant_id_coupon_id",
                table: "beauty_campaign_recipients",
                columns: new[] { "tenant_id", "coupon_id" },
                principalTable: "beauty_coupons",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_beauty_campaigns_coupons_tenant_id_shared_coupon_id",
                table: "beauty_campaigns",
                columns: new[] { "tenant_id", "shared_coupon_id" },
                principalTable: "beauty_coupons",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            // ---- Дані (gate §26.16 п. 2): наявні прапорці marketing_consent не мають доказу (текст, час, джерело, версія) ->
            // скидаються в false БЕЗ перенесення в beauty_marketing_consents і БЕЗ запису в events (§26.3 / ADR-015 §2.1:
            // рядок історії з level_from = level_to неможливий, а «згоди» як факту немає). FORCE знімається лише на час UPDATE
            // (роль-власник міграції обходить RLS без FORCE) і одразу повертається; updated_at не чіпаємо (це не дія клієнта).
            migrationBuilder.Sql("ALTER TABLE beauty_clients NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("UPDATE beauty_clients SET marketing_consent = false WHERE marketing_consent;");
            migrationBuilder.Sql("ALTER TABLE beauty_clients FORCE ROW LEVEL SECURITY;");

            // ---- RLS (ENABLE + FORCE, fail-closed без app.tenant_id) на 6 нових таблицях ----
            foreach (var table in new[]
            {
                "beauty_marketing_consents", "beauty_marketing_consent_events", "beauty_campaigns",
                "beauty_campaign_recipients", "beauty_coupons", "beauty_coupon_redemptions",
            })
            {
                migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($@"
                CREATE POLICY tenant_isolation ON {table}
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);");
            }

            // GRANT (не в міграції: ролі залежать від середовища, див. runbook, §26.3):
            //   API-роль: SELECT/INSERT/UPDATE на consents, campaigns, recipients, coupons, redemptions; DELETE на campaigns;
            //             лише SELECT/INSERT на consent_events (append-only; UPDATE/DELETE -> 42501);
            //   роль воркера: SELECT на consents, campaigns, promotions(+_services/_locations), services, locations, coupons;
            //             UPDATE на campaigns (status, started_at, finished_at, updated_at), recipients; INSERT/UPDATE на coupons.

            // Гарантія безпеки: FORCE RLS має діяти на нових і змінених таблицях, інакше міграція падає.
            migrationBuilder.Sql(@"
DO $$
DECLARE
    t text;
BEGIN
    FOREACH t IN ARRAY ARRAY[
        'beauty_marketing_consents', 'beauty_marketing_consent_events', 'beauty_campaigns', 'beauty_campaign_recipients', 'beauty_coupons', 'beauty_coupon_redemptions',
        'beauty_clients', 'beauty_promotions', 'beauty_appointments', 'beauty_conversations', 'beauty_chat_keyboards', 'beauty_chat_booking_proposals']
    LOOP
        IF NOT EXISTS (
            SELECT 1 FROM pg_class c
            WHERE c.oid = to_regclass('public.' || t) AND c.relrowsecurity AND c.relforcerowsecurity) THEN
            RAISE EXCEPTION 'FORCE ROW LEVEL SECURITY is not enabled on %', t;
        END IF;
    END LOOP;
END
$$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_beauty_appointments_campaigns_tenant_id_campaign_id",
                table: "beauty_appointments");

            migrationBuilder.DropForeignKey(
                name: "fk_beauty_appointments_coupons_tenant_id_coupon_id",
                table: "beauty_appointments");

            migrationBuilder.DropForeignKey(
                name: "fk_beauty_chat_booking_proposals_campaigns_tenant_id_campaign_",
                table: "beauty_chat_booking_proposals");

            migrationBuilder.DropForeignKey(
                name: "fk_beauty_chat_booking_proposals_coupons_tenant_id_coupon_id",
                table: "beauty_chat_booking_proposals");

            migrationBuilder.DropForeignKey(
                name: "fk_beauty_chat_keyboards_campaign_recipients_tenant_id_campaig",
                table: "beauty_chat_keyboards");

            migrationBuilder.DropForeignKey(
                name: "fk_beauty_conversations_campaigns_tenant_id_offer_campaign_id",
                table: "beauty_conversations");

            migrationBuilder.DropForeignKey(
                name: "fk_beauty_conversations_coupons_tenant_id_offer_coupon_id",
                table: "beauty_conversations");

            migrationBuilder.DropForeignKey(
                name: "fk_beauty_coupons_beauty_campaigns_tenant_id_campaign_id",
                table: "beauty_coupons");

            migrationBuilder.DropTable(
                name: "beauty_campaign_recipients");

            migrationBuilder.DropTable(
                name: "beauty_coupon_redemptions");

            migrationBuilder.DropTable(
                name: "beauty_marketing_consent_events");

            migrationBuilder.DropTable(
                name: "beauty_marketing_consents");

            migrationBuilder.DropTable(
                name: "beauty_campaigns");

            migrationBuilder.DropTable(
                name: "beauty_coupons");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_promotions_access",
                table: "beauty_promotions");

            migrationBuilder.DropIndex(
                name: "ix_beauty_conversations_tenant_id_offer_campaign_id",
                table: "beauty_conversations");

            migrationBuilder.DropIndex(
                name: "ix_beauty_conversations_tenant_id_offer_coupon_id",
                table: "beauty_conversations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_conversations_offer_until_null",
                table: "beauty_conversations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_conversations_offer_until_set",
                table: "beauty_conversations");

            migrationBuilder.DropIndex(
                name: "ix_beauty_chat_keyboards_tenant_id_campaign_recipient_id",
                table: "beauty_chat_keyboards");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_chat_keyboards_campaign_recipient",
                table: "beauty_chat_keyboards");

            migrationBuilder.DropCheckConstraint(
                name: "ck_beauty_chat_keyboards_kind",
                table: "beauty_chat_keyboards");

            migrationBuilder.DropIndex(
                name: "ix_beauty_chat_booking_proposals_tenant_id_campaign_id",
                table: "beauty_chat_booking_proposals");

            migrationBuilder.DropIndex(
                name: "ix_beauty_chat_booking_proposals_tenant_id_coupon_id",
                table: "beauty_chat_booking_proposals");

            migrationBuilder.DropIndex(
                name: "ix_beauty_appointments_tenant_id_campaign_id",
                table: "beauty_appointments");

            migrationBuilder.DropIndex(
                name: "ix_beauty_appointments_tenant_id_coupon_id",
                table: "beauty_appointments");

            migrationBuilder.DropColumn(
                name: "access",
                table: "beauty_promotions");

            migrationBuilder.DropColumn(
                name: "offer_campaign_id",
                table: "beauty_conversations");

            migrationBuilder.DropColumn(
                name: "offer_coupon_id",
                table: "beauty_conversations");

            migrationBuilder.DropColumn(
                name: "offer_until",
                table: "beauty_conversations");

            migrationBuilder.DropColumn(
                name: "marketing_offered_at",
                table: "beauty_clients");

            migrationBuilder.DropColumn(
                name: "campaign_recipient_id",
                table: "beauty_chat_keyboards");

            migrationBuilder.DropColumn(
                name: "campaign_id",
                table: "beauty_chat_booking_proposals");

            migrationBuilder.DropColumn(
                name: "coupon_id",
                table: "beauty_chat_booking_proposals");

            migrationBuilder.DropColumn(
                name: "campaign_id",
                table: "beauty_appointments");

            migrationBuilder.DropColumn(
                name: "coupon_id",
                table: "beauty_appointments");

            // Відкат: клавіатури нових видів (marketing_consent, campaign_offer) старий CHECK не знає й вони вже мертві
            // (одержувачі кампаній зникають разом з таблицями) -> рядки видаляються. FORCE знімається лише на час DELETE.
            // Скинутий Up-ом marketing_consent не відновлюється; згоди, кампанії, купони, погашення й журнал згод втрачаються.
            migrationBuilder.Sql("ALTER TABLE beauty_chat_keyboards NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("DELETE FROM beauty_chat_keyboards WHERE kind IN ('marketing_consent', 'campaign_offer');");
            migrationBuilder.Sql("ALTER TABLE beauty_chat_keyboards FORCE ROW LEVEL SECURITY;");

            migrationBuilder.AddCheckConstraint(
                name: "ck_beauty_chat_keyboards_kind",
                table: "beauty_chat_keyboards",
                sql: "kind IN ('booking_confirm', 'slot_pick', 'reminder_choice', 'appointment_list', 'appointment_actions', 'cancel_confirm')");
        }
    }
}
