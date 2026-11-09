using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Написана вручну (не `migrations add`: знімок має відому розбіжність імен ключів). Лише розширення CHECK-обмежень
    /// текстових enum-ів (ADR: enum = text + CHECK, нове значення = заміна CHECK):
    ///   beauty_appointments.reminder_option: + '24h' (нагадування за добу);
    ///   beauty_channels.type: + 'email' (email-доставка підтверджень; адреса клієнта — beauty_conversations.external_chat_id).
    /// Інших CHECK на reminder-значення немає (beauty_reminders має лише status). Колонки beauty_messages (kind/channel) не існує:
    /// канал визначається через conversation -> channel, вид — за префіксом idempotency_key ("confirmation:{appointmentId}").
    /// Down відмовляє (RAISE EXCEPTION), якщо вже є рядки з новими значеннями, — дані не губимо мовчки.
    /// </summary>
    [DbContext(typeof(BeautyDbContext))]
    [Migration("20261109150000_beauty_reminder_24h_email_channel")]
    public class beauty_reminder_24h_email_channel : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE beauty_appointments DROP CONSTRAINT IF EXISTS ck_beauty_appointments_reminder_option;
                ALTER TABLE beauty_appointments ADD CONSTRAINT ck_beauty_appointments_reminder_option
                    CHECK (reminder_option IN ('none', '1h', '2h', '24h'));
                ALTER TABLE beauty_channels DROP CONSTRAINT IF EXISTS ck_beauty_channels_type;
                ALTER TABLE beauty_channels ADD CONSTRAINT ck_beauty_channels_type
                    CHECK (type IN ('telegram', 'instagram', 'facebook', 'whatsapp', 'viber', 'widget', 'email'));
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $do$
                BEGIN
                    IF EXISTS (SELECT 1 FROM beauty_appointments WHERE reminder_option = '24h') THEN
                        RAISE EXCEPTION 'Cannot roll back: beauty_appointments rows with reminder_option = ''24h'' exist';
                    END IF;
                    IF EXISTS (SELECT 1 FROM beauty_channels WHERE type = 'email') THEN
                        RAISE EXCEPTION 'Cannot roll back: beauty_channels rows with type = ''email'' exist';
                    END IF;
                END
                $do$;
                ALTER TABLE beauty_appointments DROP CONSTRAINT IF EXISTS ck_beauty_appointments_reminder_option;
                ALTER TABLE beauty_appointments ADD CONSTRAINT ck_beauty_appointments_reminder_option
                    CHECK (reminder_option IN ('none', '1h', '2h'));
                ALTER TABLE beauty_channels DROP CONSTRAINT IF EXISTS ck_beauty_channels_type;
                ALTER TABLE beauty_channels ADD CONSTRAINT ck_beauty_channels_type
                    CHECK (type IN ('telegram', 'instagram', 'facebook', 'whatsapp', 'viber', 'widget'));
                """);
        }
    }
}
