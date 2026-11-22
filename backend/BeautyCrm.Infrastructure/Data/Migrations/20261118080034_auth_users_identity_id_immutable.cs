using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    /// <summary>
    /// ADR-024, review R-3 (TASK-874). Написана вручну на основі виводу `dotnet ef migrations add` (модель не змінилась: порожній Up;
    /// згенерований Designer прибрано, як у auth_global_identities). Зміст: тригер <c>users_identity_id_immutable</c> забороняє зміну
    /// <c>users.identity_id</c> (UPDATE ... SET identity_id = інше значення -> 23514). Роль runtime має UPDATE на users (масовий GRANT),
    /// а <c>users_identity_email_guard</c> перевіряє лише пошту, тож без цього членство можна було б «перепідключити» до чужої ідентичності.
    /// Легітимних шляхів зміни немає: backfill виконується до NOT NULL (рядки з identity_id IS NULL), <c>auth_identity_release</c> змінює
    /// email / is_active / sessions_valid_after / access_version (колонкового гранту на identity_id у неї немає), merge дублікатів - лише в
    /// backfill попередньої міграції.
    /// Окрема функція, а не <c>CREATE OR REPLACE</c> наявної: <c>users_identity_email_guard</c> після міграції auth_global_identities
    /// належить ролі beautycrm_identity, а мігратор не є її власником. Нова функція SECURITY INVOKER, без звернень до таблиць
    /// (лише OLD/NEW), search_path зафіксовано; EXECUTE для тригера перевіряється лише при CREATE TRIGGER, тож роль запису користувачів
    /// права на неї не потребує; REVOKE FROM PUBLIC для узгодженості з рештою функцій.
    /// Down прибирає тригер і функцію; даних не змінює.
    /// </summary>
    [DbContext(typeof(BeautyDbContext))]
    [Migration("20261118080034_auth_users_identity_id_immutable")]
    public class auth_users_identity_id_immutable : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION public.users_identity_id_immutable_guard() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $fn$
                BEGIN
                    IF NEW.identity_id IS DISTINCT FROM OLD.identity_id THEN
                        RAISE EXCEPTION 'users.identity_id is immutable (ADR-024)' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END
                $fn$;
                REVOKE ALL ON FUNCTION public.users_identity_id_immutable_guard() FROM PUBLIC;
                CREATE TRIGGER users_identity_id_immutable
                    BEFORE UPDATE OF identity_id ON users
                    FOR EACH ROW EXECUTE FUNCTION public.users_identity_id_immutable_guard();
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER users_identity_id_immutable ON users;
                DROP FUNCTION public.users_identity_id_immutable_guard();
                """);
        }
    }
}
