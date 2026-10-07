using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class add_beauty_schema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:btree_gist", ",,");

            migrationBuilder.CreateTable(
                name: "beauty_clients",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    full_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    birth_date = table.Column<DateOnly>(type: "date", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_clients", x => x.id);
                    table.UniqueConstraint("ak_beauty_clients_tenant_id_id", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "beauty_locations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    timezone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_locations", x => x.id);
                    table.UniqueConstraint("ak_beauty_locations_tenant_id_id", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "beauty_promotions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    discount_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    discount_value = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_promotions", x => x.id);
                    table.UniqueConstraint("ak_beauty_promotions_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_promotions_discount_type", "discount_type IN ('percent', 'fixed')");
                    table.CheckConstraint("ck_beauty_promotions_discount_value", "discount_value > 0 AND (discount_type <> 'percent' OR discount_value <= 100)");
                    table.CheckConstraint("ck_beauty_promotions_period", "ends_at IS NULL OR starts_at IS NULL OR ends_at > starts_at");
                });

            migrationBuilder.CreateTable(
                name: "beauty_services",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_services", x => x.id);
                    table.UniqueConstraint("ak_beauty_services_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_services_duration_positive", "duration_minutes > 0");
                });

            migrationBuilder.CreateTable(
                name: "beauty_specialists",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    full_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    photo_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_specialists", x => x.id);
                    table.UniqueConstraint("ak_beauty_specialists_tenant_id_id", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "beauty_client_notes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    body = table.Column<string>(type: "text", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_client_notes", x => x.id);
                    table.UniqueConstraint("ak_beauty_client_notes_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_beauty_client_notes_beauty_clients_tenant_id_client_id",
                        columns: x => new { x.tenant_id, x.client_id },
                        principalTable: "beauty_clients",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "beauty_channels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    credentials_encrypted = table.Column<string>(type: "text", nullable: true),
                    credentials_last4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    settings = table.Column<string>(type: "jsonb", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_channels", x => x.id);
                    table.UniqueConstraint("ak_beauty_channels_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_channels_type", "type IN ('telegram', 'instagram', 'facebook', 'whatsapp', 'viber', 'widget')");
                    table.ForeignKey(
                        name: "fk_beauty_channels_beauty_locations_tenant_id_location_id",
                        columns: x => new { x.tenant_id, x.location_id },
                        principalTable: "beauty_locations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "beauty_promotion_locations",
                columns: table => new
                {
                    promotion_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_promotion_locations", x => new { x.promotion_id, x.location_id });
                    table.ForeignKey(
                        name: "fk_beauty_promotion_locations_beauty_locations_tenant_id_locat",
                        columns: x => new { x.tenant_id, x.location_id },
                        principalTable: "beauty_locations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_beauty_promotion_locations_beauty_promotions_tenant_id_prom",
                        columns: x => new { x.tenant_id, x.promotion_id },
                        principalTable: "beauty_promotions",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "beauty_promotion_services",
                columns: table => new
                {
                    promotion_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_promotion_services", x => new { x.promotion_id, x.service_id });
                    table.ForeignKey(
                        name: "fk_beauty_promotion_services_beauty_promotions_tenant_id_promo",
                        columns: x => new { x.tenant_id, x.promotion_id },
                        principalTable: "beauty_promotions",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_beauty_promotion_services_beauty_services_tenant_id_service",
                        columns: x => new { x.tenant_id, x.service_id },
                        principalTable: "beauty_services",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "beauty_service_prices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_service_prices", x => x.id);
                    table.UniqueConstraint("ak_beauty_service_prices_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_service_prices_price_non_negative", "price >= 0");
                    table.ForeignKey(
                        name: "fk_beauty_service_prices_beauty_locations_tenant_id_location_id",
                        columns: x => new { x.tenant_id, x.location_id },
                        principalTable: "beauty_locations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_beauty_service_prices_beauty_services_tenant_id_service_id",
                        columns: x => new { x.tenant_id, x.service_id },
                        principalTable: "beauty_services",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "beauty_appointments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    specialist_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "pending"),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    price_original = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    price_final = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    promotion_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reminder_option = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "none"),
                    payment_method = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_appointments", x => x.id);
                    table.UniqueConstraint("ak_beauty_appointments_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_appointments_duration_positive", "duration_minutes > 0");
                    table.CheckConstraint("ck_beauty_appointments_payment_method", "payment_method IS NULL OR payment_method IN ('card', 'cash')");
                    table.CheckConstraint("ck_beauty_appointments_prices_non_negative", "price_original >= 0 AND price_final >= 0");
                    table.CheckConstraint("ck_beauty_appointments_reminder_option", "reminder_option IN ('none', '1h', '2h')");
                    table.CheckConstraint("ck_beauty_appointments_source", "source IN ('admin', 'online', 'telegram', 'instagram')");
                    table.CheckConstraint("ck_beauty_appointments_status", "status IN ('pending', 'confirmed', 'completed', 'cancelled', 'no_show')");
                    table.ForeignKey(
                        name: "fk_beauty_appointments_beauty_clients_tenant_id_client_id",
                        columns: x => new { x.tenant_id, x.client_id },
                        principalTable: "beauty_clients",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_appointments_beauty_locations_tenant_id_location_id",
                        columns: x => new { x.tenant_id, x.location_id },
                        principalTable: "beauty_locations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_appointments_beauty_promotions_tenant_id_promotion_id",
                        columns: x => new { x.tenant_id, x.promotion_id },
                        principalTable: "beauty_promotions",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_appointments_beauty_services_tenant_id_service_id",
                        columns: x => new { x.tenant_id, x.service_id },
                        principalTable: "beauty_services",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_appointments_beauty_specialists_tenant_id_specialist",
                        columns: x => new { x.tenant_id, x.specialist_id },
                        principalTable: "beauty_specialists",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "beauty_specialist_locations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    specialist_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    working_hours = table.Column<string>(type: "jsonb", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_specialist_locations", x => x.id);
                    table.UniqueConstraint("ak_beauty_specialist_locations_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_beauty_specialist_locations_beauty_locations_tenant_id_loca",
                        columns: x => new { x.tenant_id, x.location_id },
                        principalTable: "beauty_locations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_beauty_specialist_locations_beauty_specialists_tenant_id_sp",
                        columns: x => new { x.tenant_id, x.specialist_id },
                        principalTable: "beauty_specialists",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "beauty_conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: true),
                    external_chat_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "open"),
                    last_message_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_conversations", x => x.id);
                    table.UniqueConstraint("ak_beauty_conversations_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_conversations_status", "status IN ('open', 'closed')");
                    table.ForeignKey(
                        name: "fk_beauty_conversations_beauty_channels_tenant_id_channel_id",
                        columns: x => new { x.tenant_id, x.channel_id },
                        principalTable: "beauty_channels",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_conversations_beauty_clients_tenant_id_client_id",
                        columns: x => new { x.tenant_id, x.client_id },
                        principalTable: "beauty_clients",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "beauty_payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    appointment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    method = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "pending"),
                    provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    provider_payment_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_payments", x => x.id);
                    table.UniqueConstraint("ak_beauty_payments_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_payments_amount_positive", "amount > 0");
                    table.CheckConstraint("ck_beauty_payments_method", "method IN ('card', 'cash')");
                    table.CheckConstraint("ck_beauty_payments_status", "status IN ('pending', 'paid', 'failed', 'refunded')");
                    table.ForeignKey(
                        name: "fk_beauty_payments_beauty_appointments_tenant_id_appointment_id",
                        columns: x => new { x.tenant_id, x.appointment_id },
                        principalTable: "beauty_appointments",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "beauty_reminders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    appointment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scheduled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "scheduled"),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_reminders", x => x.id);
                    table.UniqueConstraint("ak_beauty_reminders_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_reminders_status", "status IN ('scheduled', 'sent', 'failed', 'cancelled')");
                    table.ForeignKey(
                        name: "fk_beauty_reminders_beauty_appointments_tenant_id_appointment_",
                        columns: x => new { x.tenant_id, x.appointment_id },
                        principalTable: "beauty_appointments",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "beauty_ai_actions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    appointment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tool_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "proposed"),
                    result = table.Column<string>(type: "jsonb", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    confirmed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    executed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_ai_actions", x => x.id);
                    table.UniqueConstraint("ak_beauty_ai_actions_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_ai_actions_status", "status IN ('proposed', 'confirmed', 'rejected', 'executed', 'failed')");
                    table.ForeignKey(
                        name: "fk_beauty_ai_actions_beauty_appointments_tenant_id_appointment",
                        columns: x => new { x.tenant_id, x.appointment_id },
                        principalTable: "beauty_appointments",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_beauty_ai_actions_beauty_conversations_tenant_id_conversati",
                        columns: x => new { x.tenant_id, x.conversation_id },
                        principalTable: "beauty_conversations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "beauty_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    direction = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    sender_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    external_message_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beauty_messages", x => x.id);
                    table.UniqueConstraint("ak_beauty_messages_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_beauty_messages_direction", "direction IN ('inbound', 'outbound')");
                    table.CheckConstraint("ck_beauty_messages_sender_type", "sender_type IN ('client', 'staff', 'ai', 'system')");
                    table.ForeignKey(
                        name: "fk_beauty_messages_beauty_conversations_tenant_id_conversation",
                        columns: x => new { x.tenant_id, x.conversation_id },
                        principalTable: "beauty_conversations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_ai_actions_tenant_id_appointment_id",
                table: "beauty_ai_actions",
                columns: new[] { "tenant_id", "appointment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_ai_actions_tenant_id_conversation_id",
                table: "beauty_ai_actions",
                columns: new[] { "tenant_id", "conversation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_ai_actions_tenant_id_created_at",
                table: "beauty_ai_actions",
                columns: new[] { "tenant_id", "created_at" },
                filter: "status = 'proposed'");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_appointments_tenant_id_client_id_starts_at",
                table: "beauty_appointments",
                columns: new[] { "tenant_id", "client_id", "starts_at" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_appointments_tenant_id_location_id_starts_at",
                table: "beauty_appointments",
                columns: new[] { "tenant_id", "location_id", "starts_at" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_appointments_tenant_id_promotion_id",
                table: "beauty_appointments",
                columns: new[] { "tenant_id", "promotion_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_appointments_tenant_id_service_id",
                table: "beauty_appointments",
                columns: new[] { "tenant_id", "service_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_appointments_tenant_id_specialist_id_starts_at",
                table: "beauty_appointments",
                columns: new[] { "tenant_id", "specialist_id", "starts_at" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_channels_tenant_id_location_id",
                table: "beauty_channels",
                columns: new[] { "tenant_id", "location_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_client_notes_tenant_id_client_id",
                table: "beauty_client_notes",
                columns: new[] { "tenant_id", "client_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_clients_tenant_id_phone",
                table: "beauty_clients",
                columns: new[] { "tenant_id", "phone" },
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_conversations_tenant_id_channel_id_external_chat_id",
                table: "beauty_conversations",
                columns: new[] { "tenant_id", "channel_id", "external_chat_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_beauty_conversations_tenant_id_client_id",
                table: "beauty_conversations",
                columns: new[] { "tenant_id", "client_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_conversations_tenant_id_last_message_at",
                table: "beauty_conversations",
                columns: new[] { "tenant_id", "last_message_at" },
                filter: "status = 'open'");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_messages_tenant_id_conversation_id_sent_at",
                table: "beauty_messages",
                columns: new[] { "tenant_id", "conversation_id", "sent_at" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_payments_tenant_id_appointment_id",
                table: "beauty_payments",
                columns: new[] { "tenant_id", "appointment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_payments_tenant_id_provider_provider_payment_id",
                table: "beauty_payments",
                columns: new[] { "tenant_id", "provider", "provider_payment_id" },
                filter: "provider_payment_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_promotion_locations_tenant_id_location_id",
                table: "beauty_promotion_locations",
                columns: new[] { "tenant_id", "location_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_promotion_locations_tenant_id_promotion_id",
                table: "beauty_promotion_locations",
                columns: new[] { "tenant_id", "promotion_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_promotion_services_tenant_id_promotion_id",
                table: "beauty_promotion_services",
                columns: new[] { "tenant_id", "promotion_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_promotion_services_tenant_id_service_id",
                table: "beauty_promotion_services",
                columns: new[] { "tenant_id", "service_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_reminders_tenant_id_appointment_id",
                table: "beauty_reminders",
                columns: new[] { "tenant_id", "appointment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_reminders_tenant_id_scheduled_at",
                table: "beauty_reminders",
                columns: new[] { "tenant_id", "scheduled_at" },
                filter: "status = 'scheduled'");

            migrationBuilder.CreateIndex(
                name: "ix_beauty_service_prices_tenant_id_location_id",
                table: "beauty_service_prices",
                columns: new[] { "tenant_id", "location_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_service_prices_tenant_id_service_id_location_id",
                table: "beauty_service_prices",
                columns: new[] { "tenant_id", "service_id", "location_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_beauty_specialist_locations_tenant_id_location_id",
                table: "beauty_specialist_locations",
                columns: new[] { "tenant_id", "location_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beauty_specialist_locations_tenant_id_specialist_id_locatio",
                table: "beauty_specialist_locations",
                columns: new[] { "tenant_id", "specialist_id", "location_id" },
                unique: true);

            // RLS policies, ends_at trigger, exclusion constraint — see .Sql.cs
            UpCustomSql(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            DownCustomSql(migrationBuilder);

            migrationBuilder.DropTable(
                name: "beauty_ai_actions");

            migrationBuilder.DropTable(
                name: "beauty_client_notes");

            migrationBuilder.DropTable(
                name: "beauty_messages");

            migrationBuilder.DropTable(
                name: "beauty_payments");

            migrationBuilder.DropTable(
                name: "beauty_promotion_locations");

            migrationBuilder.DropTable(
                name: "beauty_promotion_services");

            migrationBuilder.DropTable(
                name: "beauty_reminders");

            migrationBuilder.DropTable(
                name: "beauty_service_prices");

            migrationBuilder.DropTable(
                name: "beauty_specialist_locations");

            migrationBuilder.DropTable(
                name: "beauty_conversations");

            migrationBuilder.DropTable(
                name: "beauty_appointments");

            migrationBuilder.DropTable(
                name: "beauty_channels");

            migrationBuilder.DropTable(
                name: "beauty_clients");

            migrationBuilder.DropTable(
                name: "beauty_promotions");

            migrationBuilder.DropTable(
                name: "beauty_services");

            migrationBuilder.DropTable(
                name: "beauty_specialists");

            migrationBuilder.DropTable(
                name: "beauty_locations");
        }
    }
}
