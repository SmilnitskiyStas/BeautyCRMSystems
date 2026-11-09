using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Написана вручну (не `migrations add`: знімок має відомі розбіжності). beauty_cancellation_settings:
    ///   show_terms_to_client boolean NOT NULL DEFAULT true — показувати клієнту умови скасування;
    ///   custom_terms_text text NULL (CHECK char_length ≤ 500) — власний текст умов.
    /// Існуючі рядки отримують true / NULL (поведінка клієнта не змінюється). Числові правила повернення не зачіпаються.
    /// Down видаляє колонки (дані про текст/прапорець губляться — вони лише презентаційні).
    /// </summary>
    [DbContext(typeof(BeautyDbContext))]
    [Migration("20261110090000_beauty_cancellation_terms_visibility")]
    public class beauty_cancellation_terms_visibility : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE beauty_cancellation_settings ADD COLUMN IF NOT EXISTS show_terms_to_client boolean NOT NULL DEFAULT true;
                ALTER TABLE beauty_cancellation_settings ADD COLUMN IF NOT EXISTS custom_terms_text text NULL;
                ALTER TABLE beauty_cancellation_settings DROP CONSTRAINT IF EXISTS ck_beauty_cancellation_settings_terms_text;
                ALTER TABLE beauty_cancellation_settings ADD CONSTRAINT ck_beauty_cancellation_settings_terms_text
                    CHECK (custom_terms_text IS NULL OR char_length(custom_terms_text) <= 500);
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE beauty_cancellation_settings DROP CONSTRAINT IF EXISTS ck_beauty_cancellation_settings_terms_text;
                ALTER TABLE beauty_cancellation_settings DROP COLUMN IF EXISTS custom_terms_text;
                ALTER TABLE beauty_cancellation_settings DROP COLUMN IF EXISTS show_terms_to_client;
                """);
        }
    }
}
