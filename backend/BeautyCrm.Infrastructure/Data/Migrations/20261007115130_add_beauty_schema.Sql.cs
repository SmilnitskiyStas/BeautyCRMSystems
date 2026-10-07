using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeautyCrm.Infrastructure.Data.Migrations
{
    // Hand-written part of add_beauty_schema: things EF does not generate.
    // Frozen together with the migration — do not edit after it has been applied anywhere;
    // add a new migration instead.
    public partial class add_beauty_schema
    {
        private static readonly string[] TenantTables =
        {
            "beauty_locations",
            "beauty_specialists",
            "beauty_specialist_locations",
            "beauty_services",
            "beauty_service_prices",
            "beauty_appointments",
            "beauty_clients",
            "beauty_client_notes",
            "beauty_promotions",
            "beauty_promotion_locations",
            "beauty_promotion_services",
            "beauty_channels",
            "beauty_conversations",
            "beauty_messages",
            "beauty_ai_actions",
            "beauty_reminders",
            "beauty_payments",
        };

        private const string TenantPredicate =
            "tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid";

        private static void UpCustomSql(MigrationBuilder mb)
        {
            // ends_at = starts_at + duration (timestamptz + interval is STABLE, so it cannot be a
            // generated column / index expression — maintained by trigger instead).
            mb.Sql("""
                CREATE FUNCTION beauty_appointments_set_ends_at() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    NEW.ends_at := NEW.starts_at + make_interval(mins => NEW.duration_minutes);
                    RETURN NEW;
                END;
                $$;
                """);
            mb.Sql("""
                CREATE TRIGGER trg_beauty_appointments_set_ends_at
                    BEFORE INSERT OR UPDATE ON beauty_appointments
                    FOR EACH ROW EXECUTE FUNCTION beauty_appointments_set_ends_at();
                """);

            // A specialist cannot have two overlapping active appointments (across all locations).
            // Cancelled / no_show slots do not block. Half-open range: back-to-back is allowed.
            mb.Sql("""
                ALTER TABLE beauty_appointments
                    ADD CONSTRAINT ex_beauty_appointments_specialist_no_overlap
                    EXCLUDE USING gist (
                        tenant_id WITH =,
                        specialist_id WITH =,
                        tstzrange(starts_at, ends_at, '[)') WITH &&
                    ) WHERE (status IN ('pending', 'confirmed', 'completed'));
                """);

            // Tenant isolation. FORCE so the table owner is also subject to RLS; only superusers /
            // BYPASSRLS roles skip it — the runtime app role must be neither.
            foreach (var table in TenantTables)
            {
                mb.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
                mb.Sql($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
                mb.Sql($"""
                    CREATE POLICY tenant_isolation ON {table}
                        USING ({TenantPredicate})
                        WITH CHECK ({TenantPredicate});
                    """);
            }
        }

        private static void DownCustomSql(MigrationBuilder mb)
        {
            foreach (var table in TenantTables)
            {
                mb.Sql($"DROP POLICY IF EXISTS tenant_isolation ON {table};");
                mb.Sql($"ALTER TABLE {table} NO FORCE ROW LEVEL SECURITY;");
                mb.Sql($"ALTER TABLE {table} DISABLE ROW LEVEL SECURITY;");
            }

            mb.Sql("ALTER TABLE beauty_appointments DROP CONSTRAINT IF EXISTS ex_beauty_appointments_specialist_no_overlap;");
            mb.Sql("DROP TRIGGER IF EXISTS trg_beauty_appointments_set_ends_at ON beauty_appointments;");
            mb.Sql("DROP FUNCTION IF EXISTS beauty_appointments_set_ends_at();");
        }
    }
}
