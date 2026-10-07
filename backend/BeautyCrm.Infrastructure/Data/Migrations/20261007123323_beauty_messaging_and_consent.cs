using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class beauty_messaging_and_consent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "attempts",
                table: "beauty_messages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "idempotency_key",
                table: "beauty_messages",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_error",
                table: "beauty_messages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "beauty_messages",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "received");

            migrationBuilder.AddColumn<bool>(
                name: "marketing_consent",
                table: "beauty_clients",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "unsubscribed",
                table: "beauty_clients",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ix_beauty_messages_tenant_id_idempotency_key",
                table: "beauty_messages",
                columns: new[] { "tenant_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            // Webhook tenant resolution: the inbound request has no tenant, only beauty_channels.id.
            // A SELECT-only permissive policy lets one channel row be read when the session sets
            // app.channel_id (transaction-local, see ChannelDirectory). Other tables stay fully isolated.
            migrationBuilder.Sql(@"
                CREATE POLICY channel_webhook_lookup ON beauty_channels
                    FOR SELECT
                    USING (id = NULLIF(current_setting('app.channel_id', true), '')::uuid);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS channel_webhook_lookup ON beauty_channels;");

            migrationBuilder.DropIndex(
                name: "ix_beauty_messages_tenant_id_idempotency_key",
                table: "beauty_messages");

            migrationBuilder.DropColumn(
                name: "attempts",
                table: "beauty_messages");

            migrationBuilder.DropColumn(
                name: "idempotency_key",
                table: "beauty_messages");

            migrationBuilder.DropColumn(
                name: "last_error",
                table: "beauty_messages");

            migrationBuilder.DropColumn(
                name: "status",
                table: "beauty_messages");

            migrationBuilder.DropColumn(
                name: "marketing_consent",
                table: "beauty_clients");

            migrationBuilder.DropColumn(
                name: "unsubscribed",
                table: "beauty_clients");
        }
    }
}
