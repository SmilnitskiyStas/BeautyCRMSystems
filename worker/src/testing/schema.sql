CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    migration_id character varying(150) NOT NULL,
    product_version character varying(32) NOT NULL,
    CONSTRAINT pk___ef_migrations_history PRIMARY KEY (migration_id)
);

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE EXTENSION IF NOT EXISTS btree_gist;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE TABLE beauty_clients (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        full_name character varying(200) NOT NULL,
        phone character varying(32),
        email character varying(320),
        birth_date date,
        deleted_at timestamp with time zone,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_clients PRIMARY KEY (id),
        CONSTRAINT ak_beauty_clients_tenant_id_id UNIQUE (tenant_id, id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE TABLE beauty_locations (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        name character varying(200) NOT NULL,
        address character varying(500),
        phone character varying(32),
        timezone character varying(64) NOT NULL,
        is_active boolean NOT NULL,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_locations PRIMARY KEY (id),
        CONSTRAINT ak_beauty_locations_tenant_id_id UNIQUE (tenant_id, id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE TABLE beauty_promotions (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        name character varying(200) NOT NULL,
        description text,
        discount_type character varying(32) NOT NULL,
        discount_value numeric(12,2) NOT NULL,
        starts_at timestamp with time zone,
        ends_at timestamp with time zone,
        is_active boolean NOT NULL,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_promotions PRIMARY KEY (id),
        CONSTRAINT ak_beauty_promotions_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_promotions_discount_type CHECK (discount_type IN ('percent', 'fixed')),
        CONSTRAINT ck_beauty_promotions_discount_value CHECK (discount_value > 0 AND (discount_type <> 'percent' OR discount_value <= 100)),
        CONSTRAINT ck_beauty_promotions_period CHECK (ends_at IS NULL OR starts_at IS NULL OR ends_at > starts_at)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE TABLE beauty_services (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        name character varying(200) NOT NULL,
        description text,
        category character varying(100),
        duration_minutes integer NOT NULL,
        is_active boolean NOT NULL,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_services PRIMARY KEY (id),
        CONSTRAINT ak_beauty_services_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_services_duration_positive CHECK (duration_minutes > 0)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE TABLE beauty_specialists (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        full_name character varying(200) NOT NULL,
        title character varying(200),
        phone character varying(32),
        email character varying(320),
        photo_url character varying(2048),
        is_active boolean NOT NULL,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_specialists PRIMARY KEY (id),
        CONSTRAINT ak_beauty_specialists_tenant_id_id UNIQUE (tenant_id, id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE TABLE beauty_client_notes (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        client_id uuid NOT NULL,
        author_user_id uuid,
        body text NOT NULL,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_client_notes PRIMARY KEY (id),
        CONSTRAINT ak_beauty_client_notes_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT fk_beauty_client_notes_beauty_clients_tenant_id_client_id FOREIGN KEY (tenant_id, client_id) REFERENCES beauty_clients (tenant_id, id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE TABLE beauty_channels (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        location_id uuid,
        type character varying(32) NOT NULL,
        name character varying(200) NOT NULL,
        credentials_encrypted text,
        credentials_last4 character varying(4),
        settings jsonb,
        is_active boolean NOT NULL,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_channels PRIMARY KEY (id),
        CONSTRAINT ak_beauty_channels_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_channels_type CHECK (type IN ('telegram', 'instagram', 'facebook', 'whatsapp', 'viber', 'widget')),
        CONSTRAINT fk_beauty_channels_beauty_locations_tenant_id_location_id FOREIGN KEY (tenant_id, location_id) REFERENCES beauty_locations (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE TABLE beauty_promotion_locations (
        promotion_id uuid NOT NULL,
        location_id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_promotion_locations PRIMARY KEY (promotion_id, location_id),
        CONSTRAINT fk_beauty_promotion_locations_beauty_locations_tenant_id_locat FOREIGN KEY (tenant_id, location_id) REFERENCES beauty_locations (tenant_id, id) ON DELETE CASCADE,
        CONSTRAINT fk_beauty_promotion_locations_beauty_promotions_tenant_id_prom FOREIGN KEY (tenant_id, promotion_id) REFERENCES beauty_promotions (tenant_id, id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE TABLE beauty_promotion_services (
        promotion_id uuid NOT NULL,
        service_id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_promotion_services PRIMARY KEY (promotion_id, service_id),
        CONSTRAINT fk_beauty_promotion_services_beauty_promotions_tenant_id_promo FOREIGN KEY (tenant_id, promotion_id) REFERENCES beauty_promotions (tenant_id, id) ON DELETE CASCADE,
        CONSTRAINT fk_beauty_promotion_services_beauty_services_tenant_id_service FOREIGN KEY (tenant_id, service_id) REFERENCES beauty_services (tenant_id, id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE TABLE beauty_service_prices (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        service_id uuid NOT NULL,
        location_id uuid,
        price numeric(12,2) NOT NULL,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_service_prices PRIMARY KEY (id),
        CONSTRAINT ak_beauty_service_prices_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_service_prices_price_non_negative CHECK (price >= 0),
        CONSTRAINT fk_beauty_service_prices_beauty_locations_tenant_id_location_id FOREIGN KEY (tenant_id, location_id) REFERENCES beauty_locations (tenant_id, id) ON DELETE CASCADE,
        CONSTRAINT fk_beauty_service_prices_beauty_services_tenant_id_service_id FOREIGN KEY (tenant_id, service_id) REFERENCES beauty_services (tenant_id, id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE TABLE beauty_appointments (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        location_id uuid NOT NULL,
        specialist_id uuid NOT NULL,
        service_id uuid NOT NULL,
        client_id uuid NOT NULL,
        starts_at timestamp with time zone NOT NULL,
        duration_minutes integer NOT NULL,
        ends_at timestamp with time zone NOT NULL,
        status character varying(32) NOT NULL DEFAULT 'pending',
        source character varying(32) NOT NULL,
        price_original numeric(12,2) NOT NULL,
        price_final numeric(12,2) NOT NULL,
        promotion_id uuid,
        reminder_option character varying(32) NOT NULL DEFAULT 'none',
        payment_method character varying(32),
        cancelled_at timestamp with time zone,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_appointments PRIMARY KEY (id),
        CONSTRAINT ak_beauty_appointments_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_appointments_duration_positive CHECK (duration_minutes > 0),
        CONSTRAINT ck_beauty_appointments_payment_method CHECK (payment_method IS NULL OR payment_method IN ('card', 'cash')),
        CONSTRAINT ck_beauty_appointments_prices_non_negative CHECK (price_original >= 0 AND price_final >= 0),
        CONSTRAINT ck_beauty_appointments_reminder_option CHECK (reminder_option IN ('none', '1h', '2h')),
        CONSTRAINT ck_beauty_appointments_source CHECK (source IN ('admin', 'online', 'telegram', 'instagram')),
        CONSTRAINT ck_beauty_appointments_status CHECK (status IN ('pending', 'confirmed', 'completed', 'cancelled', 'no_show')),
        CONSTRAINT fk_beauty_appointments_beauty_clients_tenant_id_client_id FOREIGN KEY (tenant_id, client_id) REFERENCES beauty_clients (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_appointments_beauty_locations_tenant_id_location_id FOREIGN KEY (tenant_id, location_id) REFERENCES beauty_locations (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_appointments_beauty_promotions_tenant_id_promotion_id FOREIGN KEY (tenant_id, promotion_id) REFERENCES beauty_promotions (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_appointments_beauty_services_tenant_id_service_id FOREIGN KEY (tenant_id, service_id) REFERENCES beauty_services (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_appointments_beauty_specialists_tenant_id_specialist FOREIGN KEY (tenant_id, specialist_id) REFERENCES beauty_specialists (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE TABLE beauty_specialist_locations (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        specialist_id uuid NOT NULL,
        location_id uuid NOT NULL,
        working_hours jsonb,
        is_active boolean NOT NULL,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_specialist_locations PRIMARY KEY (id),
        CONSTRAINT ak_beauty_specialist_locations_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT fk_beauty_specialist_locations_beauty_locations_tenant_id_loca FOREIGN KEY (tenant_id, location_id) REFERENCES beauty_locations (tenant_id, id) ON DELETE CASCADE,
        CONSTRAINT fk_beauty_specialist_locations_beauty_specialists_tenant_id_sp FOREIGN KEY (tenant_id, specialist_id) REFERENCES beauty_specialists (tenant_id, id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE TABLE beauty_conversations (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        channel_id uuid NOT NULL,
        client_id uuid,
        external_chat_id character varying(256) NOT NULL,
        status character varying(32) NOT NULL DEFAULT 'open',
        last_message_at timestamp with time zone,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_conversations PRIMARY KEY (id),
        CONSTRAINT ak_beauty_conversations_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_conversations_status CHECK (status IN ('open', 'closed')),
        CONSTRAINT fk_beauty_conversations_beauty_channels_tenant_id_channel_id FOREIGN KEY (tenant_id, channel_id) REFERENCES beauty_channels (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_conversations_beauty_clients_tenant_id_client_id FOREIGN KEY (tenant_id, client_id) REFERENCES beauty_clients (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE TABLE beauty_payments (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        appointment_id uuid NOT NULL,
        amount numeric(12,2) NOT NULL,
        method character varying(32) NOT NULL,
        status character varying(32) NOT NULL DEFAULT 'pending',
        provider character varying(64),
        provider_payment_id character varying(256),
        paid_at timestamp with time zone,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_payments PRIMARY KEY (id),
        CONSTRAINT ak_beauty_payments_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_payments_amount_positive CHECK (amount > 0),
        CONSTRAINT ck_beauty_payments_method CHECK (method IN ('card', 'cash')),
        CONSTRAINT ck_beauty_payments_status CHECK (status IN ('pending', 'paid', 'failed', 'refunded')),
        CONSTRAINT fk_beauty_payments_beauty_appointments_tenant_id_appointment_id FOREIGN KEY (tenant_id, appointment_id) REFERENCES beauty_appointments (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE TABLE beauty_reminders (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        appointment_id uuid NOT NULL,
        scheduled_at timestamp with time zone NOT NULL,
        status character varying(32) NOT NULL DEFAULT 'scheduled',
        sent_at timestamp with time zone,
        error text,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_reminders PRIMARY KEY (id),
        CONSTRAINT ak_beauty_reminders_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_reminders_status CHECK (status IN ('scheduled', 'sent', 'failed', 'cancelled')),
        CONSTRAINT fk_beauty_reminders_beauty_appointments_tenant_id_appointment_ FOREIGN KEY (tenant_id, appointment_id) REFERENCES beauty_appointments (tenant_id, id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE TABLE beauty_ai_actions (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        conversation_id uuid,
        appointment_id uuid,
        tool_name character varying(100) NOT NULL,
        payload jsonb NOT NULL,
        status character varying(32) NOT NULL DEFAULT 'proposed',
        result jsonb,
        error text,
        confirmed_by_user_id uuid,
        confirmed_at timestamp with time zone,
        executed_at timestamp with time zone,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_ai_actions PRIMARY KEY (id),
        CONSTRAINT ak_beauty_ai_actions_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_ai_actions_status CHECK (status IN ('proposed', 'confirmed', 'rejected', 'executed', 'failed')),
        CONSTRAINT fk_beauty_ai_actions_beauty_appointments_tenant_id_appointment FOREIGN KEY (tenant_id, appointment_id) REFERENCES beauty_appointments (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_ai_actions_beauty_conversations_tenant_id_conversati FOREIGN KEY (tenant_id, conversation_id) REFERENCES beauty_conversations (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE TABLE beauty_messages (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        conversation_id uuid NOT NULL,
        direction character varying(32) NOT NULL,
        sender_type character varying(32) NOT NULL,
        body text NOT NULL,
        external_message_id character varying(256),
        sent_at timestamp with time zone NOT NULL DEFAULT (now()),
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_messages PRIMARY KEY (id),
        CONSTRAINT ak_beauty_messages_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_messages_direction CHECK (direction IN ('inbound', 'outbound')),
        CONSTRAINT ck_beauty_messages_sender_type CHECK (sender_type IN ('client', 'staff', 'ai', 'system')),
        CONSTRAINT fk_beauty_messages_beauty_conversations_tenant_id_conversation FOREIGN KEY (tenant_id, conversation_id) REFERENCES beauty_conversations (tenant_id, id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE INDEX ix_beauty_ai_actions_tenant_id_appointment_id ON beauty_ai_actions (tenant_id, appointment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE INDEX ix_beauty_ai_actions_tenant_id_conversation_id ON beauty_ai_actions (tenant_id, conversation_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE INDEX ix_beauty_ai_actions_tenant_id_created_at ON beauty_ai_actions (tenant_id, created_at) WHERE status = 'proposed';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE INDEX ix_beauty_appointments_tenant_id_client_id_starts_at ON beauty_appointments (tenant_id, client_id, starts_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE INDEX ix_beauty_appointments_tenant_id_location_id_starts_at ON beauty_appointments (tenant_id, location_id, starts_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE INDEX ix_beauty_appointments_tenant_id_promotion_id ON beauty_appointments (tenant_id, promotion_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE INDEX ix_beauty_appointments_tenant_id_service_id ON beauty_appointments (tenant_id, service_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE INDEX ix_beauty_appointments_tenant_id_specialist_id_starts_at ON beauty_appointments (tenant_id, specialist_id, starts_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE INDEX ix_beauty_channels_tenant_id_location_id ON beauty_channels (tenant_id, location_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE INDEX ix_beauty_client_notes_tenant_id_client_id ON beauty_client_notes (tenant_id, client_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE INDEX ix_beauty_clients_tenant_id_phone ON beauty_clients (tenant_id, phone) WHERE deleted_at IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE UNIQUE INDEX ix_beauty_conversations_tenant_id_channel_id_external_chat_id ON beauty_conversations (tenant_id, channel_id, external_chat_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE INDEX ix_beauty_conversations_tenant_id_client_id ON beauty_conversations (tenant_id, client_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE INDEX ix_beauty_conversations_tenant_id_last_message_at ON beauty_conversations (tenant_id, last_message_at) WHERE status = 'open';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE INDEX ix_beauty_messages_tenant_id_conversation_id_sent_at ON beauty_messages (tenant_id, conversation_id, sent_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE INDEX ix_beauty_payments_tenant_id_appointment_id ON beauty_payments (tenant_id, appointment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE INDEX ix_beauty_payments_tenant_id_provider_provider_payment_id ON beauty_payments (tenant_id, provider, provider_payment_id) WHERE provider_payment_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE INDEX ix_beauty_promotion_locations_tenant_id_location_id ON beauty_promotion_locations (tenant_id, location_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE INDEX ix_beauty_promotion_locations_tenant_id_promotion_id ON beauty_promotion_locations (tenant_id, promotion_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE INDEX ix_beauty_promotion_services_tenant_id_promotion_id ON beauty_promotion_services (tenant_id, promotion_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE INDEX ix_beauty_promotion_services_tenant_id_service_id ON beauty_promotion_services (tenant_id, service_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE INDEX ix_beauty_reminders_tenant_id_appointment_id ON beauty_reminders (tenant_id, appointment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE INDEX ix_beauty_reminders_tenant_id_scheduled_at ON beauty_reminders (tenant_id, scheduled_at) WHERE status = 'scheduled';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE INDEX ix_beauty_service_prices_tenant_id_location_id ON beauty_service_prices (tenant_id, location_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE UNIQUE INDEX ix_beauty_service_prices_tenant_id_service_id_location_id ON beauty_service_prices (tenant_id, service_id, location_id) NULLS NOT DISTINCT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE INDEX ix_beauty_specialist_locations_tenant_id_location_id ON beauty_specialist_locations (tenant_id, location_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE UNIQUE INDEX ix_beauty_specialist_locations_tenant_id_specialist_id_locatio ON beauty_specialist_locations (tenant_id, specialist_id, location_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE FUNCTION beauty_appointments_set_ends_at() RETURNS trigger
    LANGUAGE plpgsql AS $$
    BEGIN
        NEW.ends_at := NEW.starts_at + make_interval(mins => NEW.duration_minutes);
        RETURN NEW;
    END;
    $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE TRIGGER trg_beauty_appointments_set_ends_at
        BEFORE INSERT OR UPDATE ON beauty_appointments
        FOR EACH ROW EXECUTE FUNCTION beauty_appointments_set_ends_at();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_appointments
        ADD CONSTRAINT ex_beauty_appointments_specialist_no_overlap
        EXCLUDE USING gist (
            tenant_id WITH =,
            specialist_id WITH =,
            tstzrange(starts_at, ends_at, '[)') WITH &&
        ) WHERE (status IN ('pending', 'confirmed', 'completed'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_locations ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_locations FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE POLICY tenant_isolation ON beauty_locations
        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_specialists ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_specialists FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE POLICY tenant_isolation ON beauty_specialists
        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_specialist_locations ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_specialist_locations FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE POLICY tenant_isolation ON beauty_specialist_locations
        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_services ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_services FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE POLICY tenant_isolation ON beauty_services
        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_service_prices ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_service_prices FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE POLICY tenant_isolation ON beauty_service_prices
        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_appointments ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_appointments FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE POLICY tenant_isolation ON beauty_appointments
        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_clients ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_clients FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE POLICY tenant_isolation ON beauty_clients
        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_client_notes ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_client_notes FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE POLICY tenant_isolation ON beauty_client_notes
        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_promotions ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_promotions FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE POLICY tenant_isolation ON beauty_promotions
        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_promotion_locations ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_promotion_locations FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE POLICY tenant_isolation ON beauty_promotion_locations
        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_promotion_services ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_promotion_services FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE POLICY tenant_isolation ON beauty_promotion_services
        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_channels ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_channels FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE POLICY tenant_isolation ON beauty_channels
        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_conversations ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_conversations FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE POLICY tenant_isolation ON beauty_conversations
        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_messages ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_messages FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE POLICY tenant_isolation ON beauty_messages
        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_ai_actions ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_ai_actions FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE POLICY tenant_isolation ON beauty_ai_actions
        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_reminders ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_reminders FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE POLICY tenant_isolation ON beauty_reminders
        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_payments ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    ALTER TABLE beauty_payments FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    CREATE POLICY tenant_isolation ON beauty_payments
        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007115130_add_beauty_schema') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20261007115130_add_beauty_schema', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007123323_beauty_messaging_and_consent') THEN
    ALTER TABLE beauty_messages ADD attempts integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007123323_beauty_messaging_and_consent') THEN
    ALTER TABLE beauty_messages ADD idempotency_key character varying(300);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007123323_beauty_messaging_and_consent') THEN
    ALTER TABLE beauty_messages ADD last_error text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007123323_beauty_messaging_and_consent') THEN
    ALTER TABLE beauty_messages ADD status character varying(16) NOT NULL DEFAULT 'received';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007123323_beauty_messaging_and_consent') THEN
    ALTER TABLE beauty_clients ADD marketing_consent boolean NOT NULL DEFAULT FALSE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007123323_beauty_messaging_and_consent') THEN
    ALTER TABLE beauty_clients ADD unsubscribed boolean NOT NULL DEFAULT FALSE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007123323_beauty_messaging_and_consent') THEN
    CREATE UNIQUE INDEX ix_beauty_messages_tenant_id_idempotency_key ON beauty_messages (tenant_id, idempotency_key) WHERE idempotency_key IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007123323_beauty_messaging_and_consent') THEN

                    CREATE POLICY channel_webhook_lookup ON beauty_channels
                        FOR SELECT
                        USING (id = NULLIF(current_setting('app.channel_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007123323_beauty_messaging_and_consent') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20261007123323_beauty_messaging_and_consent', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007134016_auth_tenants_users_invites') THEN
    CREATE TABLE tenants (
        id uuid NOT NULL,
        name character varying(200) NOT NULL,
        slug character varying(64) NOT NULL,
        modules text[] NOT NULL DEFAULT ('{}'),
        status character varying(16) NOT NULL DEFAULT 'active',
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_tenants PRIMARY KEY (id),
        CONSTRAINT ck_tenants_slug CHECK (slug ~ '^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]$'),
        CONSTRAINT ck_tenants_status CHECK (status IN ('active', 'suspended'))
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007134016_auth_tenants_users_invites') THEN
    CREATE TABLE users (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        email character varying(320) NOT NULL,
        full_name character varying(200) NOT NULL,
        password_hash character varying(255) NOT NULL,
        role character varying(16) NOT NULL,
        specialist_id uuid,
        is_active boolean NOT NULL DEFAULT TRUE,
        failed_login_count integer NOT NULL DEFAULT 0,
        lockout_until timestamp with time zone,
        last_login_at timestamp with time zone,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_users PRIMARY KEY (id),
        CONSTRAINT ak_users_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_users_role CHECK (role IN ('owner', 'admin', 'specialist')),
        CONSTRAINT fk_users_beauty_specialists_tenant_id_specialist_id FOREIGN KEY (tenant_id, specialist_id) REFERENCES beauty_specialists (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_users_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007134016_auth_tenants_users_invites') THEN
    CREATE TABLE invites (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        email character varying(320) NOT NULL,
        role character varying(16) NOT NULL,
        specialist_id uuid,
        token_hash character varying(64) NOT NULL,
        expires_at timestamp with time zone NOT NULL,
        accepted_at timestamp with time zone,
        revoked_at timestamp with time zone,
        invited_by_user_id uuid,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_invites PRIMARY KEY (id),
        CONSTRAINT ak_invites_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_invites_role CHECK (role IN ('admin', 'specialist')),
        CONSTRAINT fk_invites_beauty_specialists_tenant_id_specialist_id FOREIGN KEY (tenant_id, specialist_id) REFERENCES beauty_specialists (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_invites_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT,
        CONSTRAINT fk_invites_users_tenant_id_invited_by_user_id FOREIGN KEY (tenant_id, invited_by_user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007134016_auth_tenants_users_invites') THEN
    CREATE TABLE refresh_tokens (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        user_id uuid NOT NULL,
        token_hash character varying(64) NOT NULL,
        expires_at timestamp with time zone NOT NULL,
        revoked_at timestamp with time zone,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_refresh_tokens PRIMARY KEY (id),
        CONSTRAINT ak_refresh_tokens_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT fk_refresh_tokens_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT,
        CONSTRAINT fk_refresh_tokens_users_tenant_id_user_id FOREIGN KEY (tenant_id, user_id) REFERENCES users (tenant_id, id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007134016_auth_tenants_users_invites') THEN
    CREATE INDEX ix_invites_tenant_id_email ON invites (tenant_id, email);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007134016_auth_tenants_users_invites') THEN
    CREATE INDEX ix_invites_tenant_id_invited_by_user_id ON invites (tenant_id, invited_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007134016_auth_tenants_users_invites') THEN
    CREATE INDEX ix_invites_tenant_id_specialist_id ON invites (tenant_id, specialist_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007134016_auth_tenants_users_invites') THEN
    CREATE UNIQUE INDEX ix_invites_tenant_id_token_hash ON invites (tenant_id, token_hash);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007134016_auth_tenants_users_invites') THEN
    CREATE UNIQUE INDEX ix_refresh_tokens_tenant_id_token_hash ON refresh_tokens (tenant_id, token_hash);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007134016_auth_tenants_users_invites') THEN
    CREATE INDEX ix_refresh_tokens_tenant_id_user_id ON refresh_tokens (tenant_id, user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007134016_auth_tenants_users_invites') THEN
    CREATE UNIQUE INDEX ix_tenants_slug ON tenants (slug);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007134016_auth_tenants_users_invites') THEN
    CREATE UNIQUE INDEX ix_users_tenant_id_email ON users (tenant_id, email);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007134016_auth_tenants_users_invites') THEN
    CREATE UNIQUE INDEX ix_users_tenant_id_specialist_id ON users (tenant_id, specialist_id) WHERE specialist_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007134016_auth_tenants_users_invites') THEN
    ALTER TABLE users ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007134016_auth_tenants_users_invites') THEN
    ALTER TABLE users FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007134016_auth_tenants_users_invites') THEN

                        CREATE POLICY tenant_isolation ON users
                            USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                            WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007134016_auth_tenants_users_invites') THEN
    ALTER TABLE invites ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007134016_auth_tenants_users_invites') THEN
    ALTER TABLE invites FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007134016_auth_tenants_users_invites') THEN

                        CREATE POLICY tenant_isolation ON invites
                            USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                            WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007134016_auth_tenants_users_invites') THEN
    ALTER TABLE refresh_tokens ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007134016_auth_tenants_users_invites') THEN
    ALTER TABLE refresh_tokens FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007134016_auth_tenants_users_invites') THEN

                        CREATE POLICY tenant_isolation ON refresh_tokens
                            USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                            WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007134016_auth_tenants_users_invites') THEN
    ALTER TABLE tenants ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007134016_auth_tenants_users_invites') THEN
    ALTER TABLE tenants FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007134016_auth_tenants_users_invites') THEN

                    CREATE POLICY tenant_self ON tenants
                        USING (id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007134016_auth_tenants_users_invites') THEN

                    CREATE POLICY tenants_login_lookup ON tenants
                        FOR SELECT
                        USING (slug = NULLIF(current_setting('app.tenant_slug', true), ''));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007134016_auth_tenants_users_invites') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20261007134016_auth_tenants_users_invites', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007134748_beauty_cancellation_settings') THEN
    CREATE TABLE beauty_cancellation_settings (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        window_hours integer NOT NULL DEFAULT 12,
        refund_percent_in_window integer NOT NULL DEFAULT 50,
        refund_percent_outside integer NOT NULL DEFAULT 100,
        deduct_fee boolean NOT NULL DEFAULT FALSE,
        fee_percent integer NOT NULL DEFAULT 0,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_cancellation_settings PRIMARY KEY (id),
        CONSTRAINT ak_beauty_cancellation_settings_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_cancellation_settings_percents CHECK (refund_percent_in_window BETWEEN 0 AND 100 AND refund_percent_outside BETWEEN 0 AND 100 AND fee_percent BETWEEN 0 AND 100),
        CONSTRAINT ck_beauty_cancellation_settings_window CHECK (window_hours >= 0 AND window_hours <= 720)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007134748_beauty_cancellation_settings') THEN
    CREATE UNIQUE INDEX ix_beauty_cancellation_settings_tenant_id ON beauty_cancellation_settings (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007134748_beauty_cancellation_settings') THEN
    ALTER TABLE beauty_cancellation_settings ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007134748_beauty_cancellation_settings') THEN
    ALTER TABLE beauty_cancellation_settings FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007134748_beauty_cancellation_settings') THEN

                    CREATE POLICY tenant_isolation ON beauty_cancellation_settings
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007134748_beauty_cancellation_settings') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20261007134748_beauty_cancellation_settings', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007153917_beauty_public_booking') THEN
    ALTER TABLE beauty_appointments ADD idempotency_key_hash character varying(64);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007153917_beauty_public_booking') THEN
    ALTER TABLE beauty_appointments ADD idempotency_request_hash character varying(64);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007153917_beauty_public_booking') THEN
    ALTER TABLE beauty_appointments ADD public_token_hash character varying(64);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007153917_beauty_public_booking') THEN
    CREATE UNIQUE INDEX ux_beauty_appointments_idempotency ON beauty_appointments (tenant_id, idempotency_key_hash) WHERE idempotency_key_hash IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007153917_beauty_public_booking') THEN
    CREATE UNIQUE INDEX ux_beauty_appointments_public_token ON beauty_appointments (tenant_id, public_token_hash) WHERE public_token_hash IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261007153917_beauty_public_booking') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20261007153917_beauty_public_booking', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008061428_beauty_staff_management') THEN
    ALTER TABLE beauty_specialists ADD position character varying(200);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008061428_beauty_staff_management') THEN
    CREATE TABLE beauty_specialist_absences (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        tenant_id uuid NOT NULL,
        specialist_id uuid NOT NULL,
        type character varying(16) NOT NULL,
        date_from date NOT NULL,
        date_to date NOT NULL,
        status character varying(16) NOT NULL,
        note character varying(500),
        requested_by_user_id uuid NOT NULL,
        decided_by_user_id uuid,
        decided_at timestamp with time zone,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_specialist_absences PRIMARY KEY (id),
        CONSTRAINT ak_beauty_specialist_absences_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_specialist_absences_dates CHECK (date_to >= date_from),
        CONSTRAINT ck_beauty_specialist_absences_note CHECK (note IS NULL OR char_length(note) <= 500),
        CONSTRAINT ck_beauty_specialist_absences_status CHECK (status IN ('requested', 'approved', 'rejected', 'cancelled')),
        CONSTRAINT ck_beauty_specialist_absences_type CHECK (type IN ('sick', 'vacation', 'day_off', 'other')),
        CONSTRAINT fk_beauty_specialist_absences_beauty_specialists_tenant_id_spe FOREIGN KEY (tenant_id, specialist_id) REFERENCES beauty_specialists (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_specialist_absences_users_tenant_id_decided_by_user_ FOREIGN KEY (tenant_id, decided_by_user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_specialist_absences_users_tenant_id_requested_by_use FOREIGN KEY (tenant_id, requested_by_user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008061428_beauty_staff_management') THEN
    CREATE TABLE beauty_specialist_services (
        tenant_id uuid NOT NULL,
        specialist_id uuid NOT NULL,
        service_id uuid NOT NULL,
        CONSTRAINT pk_beauty_specialist_services PRIMARY KEY (tenant_id, specialist_id, service_id),
        CONSTRAINT fk_beauty_specialist_services_beauty_services_tenant_id_servic FOREIGN KEY (tenant_id, service_id) REFERENCES beauty_services (tenant_id, id) ON DELETE CASCADE,
        CONSTRAINT fk_beauty_specialist_services_beauty_specialists_tenant_id_spe FOREIGN KEY (tenant_id, specialist_id) REFERENCES beauty_specialists (tenant_id, id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008061428_beauty_staff_management') THEN
    CREATE INDEX ix_beauty_specialist_absences_tenant_id_decided_by_user_id ON beauty_specialist_absences (tenant_id, decided_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008061428_beauty_staff_management') THEN
    CREATE INDEX ix_beauty_specialist_absences_tenant_id_requested_by_user_id ON beauty_specialist_absences (tenant_id, requested_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008061428_beauty_staff_management') THEN
    CREATE INDEX ix_beauty_specialist_absences_tenant_id_specialist_id_date_from ON beauty_specialist_absences (tenant_id, specialist_id, date_from);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008061428_beauty_staff_management') THEN
    CREATE INDEX ix_beauty_specialist_services_tenant_id_service_id ON beauty_specialist_services (tenant_id, service_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008061428_beauty_staff_management') THEN
    ALTER TABLE beauty_specialists NO FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008061428_beauty_staff_management') THEN
    ALTER TABLE beauty_services NO FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008061428_beauty_staff_management') THEN

                    INSERT INTO beauty_specialist_services (tenant_id, specialist_id, service_id)
                    SELECT sp.tenant_id, sp.id, sv.id
                    FROM beauty_specialists sp
                    JOIN beauty_services sv ON sv.tenant_id = sp.tenant_id
                    ON CONFLICT DO NOTHING;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008061428_beauty_staff_management') THEN
    ALTER TABLE beauty_specialists FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008061428_beauty_staff_management') THEN
    ALTER TABLE beauty_services FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008061428_beauty_staff_management') THEN

                    ALTER TABLE beauty_specialist_absences
                        ADD CONSTRAINT ex_beauty_specialist_absences_no_overlap
                        EXCLUDE USING gist (
                            tenant_id WITH =,
                            specialist_id WITH =,
                            daterange(date_from, date_to, '[]') WITH &&
                        ) WHERE (status IN ('requested', 'approved'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008061428_beauty_staff_management') THEN
    ALTER TABLE beauty_specialist_services ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008061428_beauty_staff_management') THEN
    ALTER TABLE beauty_specialist_services FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008061428_beauty_staff_management') THEN

                        CREATE POLICY tenant_isolation ON beauty_specialist_services
                            USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                            WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008061428_beauty_staff_management') THEN
    ALTER TABLE beauty_specialist_absences ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008061428_beauty_staff_management') THEN
    ALTER TABLE beauty_specialist_absences FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008061428_beauty_staff_management') THEN

                        CREATE POLICY tenant_isolation ON beauty_specialist_absences
                            USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                            WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008061428_beauty_staff_management') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20261008061428_beauty_staff_management', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008092926_beauty_staff_hardening') THEN
    ALTER TABLE beauty_specialist_absences ADD cancelled_at timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008092926_beauty_staff_hardening') THEN
    ALTER TABLE beauty_specialist_absences ADD cancelled_by_user_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008092926_beauty_staff_hardening') THEN
    CREATE INDEX ix_beauty_specialist_absences_tenant_id_cancelled_by_user_id ON beauty_specialist_absences (tenant_id, cancelled_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008092926_beauty_staff_hardening') THEN
    ALTER TABLE beauty_specialist_absences ADD CONSTRAINT fk_beauty_specialist_absences_users_tenant_id_cancelled_by_use FOREIGN KEY (tenant_id, cancelled_by_user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008092926_beauty_staff_hardening') THEN

    DO $$
    DECLARE
        t text;
    BEGIN
        FOREACH t IN ARRAY ARRAY['beauty_specialists', 'beauty_services', 'beauty_specialist_services', 'beauty_specialist_absences']
        LOOP
            IF NOT EXISTS (
                SELECT 1 FROM pg_class c
                WHERE c.oid = to_regclass('public.' || t) AND c.relrowsecurity AND c.relforcerowsecurity) THEN
                RAISE EXCEPTION 'FORCE ROW LEVEL SECURITY is not enabled on %', t;
            END IF;
        END LOOP;
    END
    $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008092926_beauty_staff_hardening') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20261008092926_beauty_staff_hardening', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008124507_beauty_locations_and_cancellation_history') THEN
    ALTER TABLE beauty_appointments ADD cancel_reason character varying(300);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008124507_beauty_locations_and_cancellation_history') THEN
    ALTER TABLE beauty_appointments ADD cancelled_by_type character varying(16);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008124507_beauty_locations_and_cancellation_history') THEN
    ALTER TABLE beauty_appointments ADD cancelled_by_user_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008124507_beauty_locations_and_cancellation_history') THEN
    CREATE INDEX ix_beauty_appointments_tenant_id_cancelled_by_user_id ON beauty_appointments (tenant_id, cancelled_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008124507_beauty_locations_and_cancellation_history') THEN
    ALTER TABLE beauty_appointments ADD CONSTRAINT ck_beauty_appointments_cancel_meta_status CHECK ((cancelled_by_type IS NULL AND cancel_reason IS NULL) OR status = 'cancelled');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008124507_beauty_locations_and_cancellation_history') THEN
    ALTER TABLE beauty_appointments ADD CONSTRAINT ck_beauty_appointments_cancel_reason_len CHECK (cancel_reason IS NULL OR char_length(cancel_reason) <= 300);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008124507_beauty_locations_and_cancellation_history') THEN
    ALTER TABLE beauty_appointments ADD CONSTRAINT ck_beauty_appointments_cancelled_by_type CHECK (cancelled_by_type IS NULL OR cancelled_by_type IN ('client', 'staff', 'system'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008124507_beauty_locations_and_cancellation_history') THEN
    ALTER TABLE beauty_appointments ADD CONSTRAINT ck_beauty_appointments_cancelled_by_user CHECK (cancelled_by_user_id IS NULL OR cancelled_by_type = 'staff');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008124507_beauty_locations_and_cancellation_history') THEN
    ALTER TABLE beauty_appointments ADD CONSTRAINT fk_beauty_appointments_users_tenant_id_cancelled_by_user_id FOREIGN KEY (tenant_id, cancelled_by_user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008124507_beauty_locations_and_cancellation_history') THEN
    ALTER TABLE beauty_appointments NO FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008124507_beauty_locations_and_cancellation_history') THEN

                    UPDATE beauty_appointments
                    SET cancelled_by_type = 'system'
                    WHERE status = 'cancelled' AND cancelled_by_type IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008124507_beauty_locations_and_cancellation_history') THEN
    ALTER TABLE beauty_appointments FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008124507_beauty_locations_and_cancellation_history') THEN

    DO $$
    DECLARE
        t text;
    BEGIN
        FOREACH t IN ARRAY ARRAY['beauty_appointments', 'beauty_locations']
        LOOP
            IF NOT EXISTS (
                SELECT 1 FROM pg_class c
                WHERE c.oid = to_regclass('public.' || t) AND c.relrowsecurity AND c.relforcerowsecurity) THEN
                RAISE EXCEPTION 'FORCE ROW LEVEL SECURITY is not enabled on %', t;
            END IF;
        END LOOP;
    END
    $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261008124507_beauty_locations_and_cancellation_history') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20261008124507_beauty_locations_and_cancellation_history', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261010130434_beauty_location_closures') THEN
    ALTER TABLE beauty_locations ADD closed_weekdays text[] NOT NULL DEFAULT ('{}');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261010130434_beauty_location_closures') THEN
    CREATE TABLE beauty_location_closures (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        tenant_id uuid NOT NULL,
        location_id uuid NOT NULL,
        date_from date NOT NULL,
        date_to date NOT NULL,
        reason character varying(200),
        created_by_user_id uuid,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_location_closures PRIMARY KEY (id),
        CONSTRAINT ak_beauty_location_closures_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_location_closures_dates CHECK (date_to >= date_from),
        CONSTRAINT ck_beauty_location_closures_reason CHECK (reason IS NULL OR char_length(reason) <= 200),
        CONSTRAINT fk_beauty_location_closures_beauty_locations_tenant_id_locatio FOREIGN KEY (tenant_id, location_id) REFERENCES beauty_locations (tenant_id, id) ON DELETE CASCADE,
        CONSTRAINT fk_beauty_location_closures_users_tenant_id_created_by_user_id FOREIGN KEY (tenant_id, created_by_user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261010130434_beauty_location_closures') THEN
    ALTER TABLE beauty_locations ADD CONSTRAINT ck_beauty_locations_closed_weekdays CHECK (closed_weekdays <@ ARRAY['mon','tue','wed','thu','fri','sat','sun']::text[]);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261010130434_beauty_location_closures') THEN
    CREATE INDEX ix_beauty_location_closures_tenant_id_created_by_user_id ON beauty_location_closures (tenant_id, created_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261010130434_beauty_location_closures') THEN
    CREATE INDEX ix_beauty_location_closures_tenant_id_location_id_date_from ON beauty_location_closures (tenant_id, location_id, date_from);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261010130434_beauty_location_closures') THEN

                    ALTER TABLE beauty_location_closures
                        ADD CONSTRAINT ex_beauty_location_closures_no_overlap
                        EXCLUDE USING gist (
                            tenant_id WITH =,
                            location_id WITH =,
                            daterange(date_from, date_to, '[]') WITH &&
                        );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261010130434_beauty_location_closures') THEN
    ALTER TABLE beauty_location_closures ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261010130434_beauty_location_closures') THEN
    ALTER TABLE beauty_location_closures FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261010130434_beauty_location_closures') THEN

                    CREATE POLICY tenant_isolation ON beauty_location_closures
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261010130434_beauty_location_closures') THEN

    DO $$
    DECLARE
        t text;
    BEGIN
        FOREACH t IN ARRAY ARRAY['beauty_locations', 'beauty_location_closures']
        LOOP
            IF NOT EXISTS (
                SELECT 1 FROM pg_class c
                WHERE c.oid = to_regclass('public.' || t) AND c.relrowsecurity AND c.relforcerowsecurity) THEN
                RAISE EXCEPTION 'FORCE ROW LEVEL SECURITY is not enabled on %', t;
            END IF;
        END LOOP;
    END
    $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261010130434_beauty_location_closures') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20261010130434_beauty_location_closures', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261010135605_beauty_location_working_hours') THEN
    ALTER TABLE beauty_locations ADD working_hours jsonb;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261010135605_beauty_location_working_hours') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20261010135605_beauty_location_working_hours', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024091314_beauty_ai_settings_and_usage') THEN
    CREATE TABLE beauty_ai_provider_keys (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        provider character varying(32) NOT NULL,
        key_encrypted text NOT NULL,
        key_kid character varying(32) NOT NULL,
        key_last4 character varying(4) NOT NULL,
        created_by_user_id uuid NOT NULL,
        updated_by_user_id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_ai_provider_keys PRIMARY KEY (id),
        CONSTRAINT ak_beauty_ai_provider_keys_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_ai_provider_keys_key_encrypted CHECK (key_encrypted LIKE 'v2:%'),
        CONSTRAINT ck_beauty_ai_provider_keys_key_kid CHECK (key_kid ~ '^[a-z0-9-]{1,32}$'),
        CONSTRAINT ck_beauty_ai_provider_keys_provider CHECK (provider ~ '^[a-z][a-z0-9_]{1,31}$'),
        CONSTRAINT fk_beauty_ai_provider_keys_users_tenant_id_created_by_user_id FOREIGN KEY (tenant_id, created_by_user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_ai_provider_keys_users_tenant_id_updated_by_user_id FOREIGN KEY (tenant_id, updated_by_user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024091314_beauty_ai_settings_and_usage') THEN
    CREATE TABLE beauty_ai_settings (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        key_source character varying(16) NOT NULL DEFAULT 'platform',
        chat_provider character varying(32) NOT NULL DEFAULT 'anthropic',
        chat_model character varying(100) NOT NULL,
        analytics_provider character varying(32) NOT NULL DEFAULT 'anthropic',
        analytics_model character varying(100) NOT NULL,
        mode character varying(16) NOT NULL DEFAULT 'confirm',
        prompt_addendum text,
        monthly_token_limit bigint,
        monthly_request_limit integer,
        max_output_tokens integer NOT NULL DEFAULT 1024,
        platform_enabled boolean,
        platform_monthly_token_quota bigint,
        platform_monthly_request_quota integer,
        updated_by_user_id uuid,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_ai_settings PRIMARY KEY (id),
        CONSTRAINT ak_beauty_ai_settings_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_ai_settings_analytics_model CHECK (char_length(analytics_model) BETWEEN 1 AND 100),
        CONSTRAINT ck_beauty_ai_settings_analytics_provider CHECK (analytics_provider ~ '^[a-z][a-z0-9_]{1,31}$'),
        CONSTRAINT ck_beauty_ai_settings_chat_model CHECK (char_length(chat_model) BETWEEN 1 AND 100),
        CONSTRAINT ck_beauty_ai_settings_chat_provider CHECK (chat_provider ~ '^[a-z][a-z0-9_]{1,31}$'),
        CONSTRAINT ck_beauty_ai_settings_key_source CHECK (key_source IN ('own', 'platform')),
        CONSTRAINT ck_beauty_ai_settings_max_output_tokens CHECK (max_output_tokens BETWEEN 64 AND 8192),
        CONSTRAINT ck_beauty_ai_settings_mode CHECK (mode IN ('suggest', 'confirm', 'auto')),
        CONSTRAINT ck_beauty_ai_settings_monthly_request_limit CHECK (monthly_request_limit IS NULL OR monthly_request_limit BETWEEN 1 AND 10000000),
        CONSTRAINT ck_beauty_ai_settings_monthly_token_limit CHECK (monthly_token_limit IS NULL OR monthly_token_limit BETWEEN 1 AND 100000000000),
        CONSTRAINT ck_beauty_ai_settings_platform_quotas CHECK ((platform_monthly_token_quota IS NULL OR platform_monthly_token_quota >= 0) AND (platform_monthly_request_quota IS NULL OR platform_monthly_request_quota >= 0)),
        CONSTRAINT ck_beauty_ai_settings_prompt_addendum CHECK (prompt_addendum IS NULL OR char_length(prompt_addendum) <= 4000),
        CONSTRAINT fk_beauty_ai_settings_users_tenant_id_updated_by_user_id FOREIGN KEY (tenant_id, updated_by_user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024091314_beauty_ai_settings_and_usage') THEN
    CREATE TABLE beauty_ai_settings_audit (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        tenant_id uuid NOT NULL,
        user_id uuid,
        action character varying(32) NOT NULL,
        provider character varying(32),
        changed_fields text[] NOT NULL DEFAULT ('{}'),
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_ai_settings_audit PRIMARY KEY (id),
        CONSTRAINT ak_beauty_ai_settings_audit_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_ai_settings_audit_action CHECK (action IN ('settings_updated', 'key_source_changed', 'key_set', 'key_replaced', 'key_deleted', 'key_tested', 'prompt_reset', 'platform_quota_changed')),
        CONSTRAINT ck_beauty_ai_settings_audit_provider CHECK (provider IS NULL OR provider ~ '^[a-z][a-z0-9_]{1,31}$'),
        CONSTRAINT fk_beauty_ai_settings_audit_users_tenant_id_user_id FOREIGN KEY (tenant_id, user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024091314_beauty_ai_settings_and_usage') THEN
    CREATE TABLE beauty_ai_usage (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        period_start date NOT NULL,
        role character varying(16) NOT NULL,
        provider character varying(32) NOT NULL,
        model character varying(100) NOT NULL,
        key_source character varying(16) NOT NULL,
        request_count integer NOT NULL DEFAULT 0,
        error_count integer NOT NULL DEFAULT 0,
        input_tokens bigint NOT NULL DEFAULT 0,
        output_tokens bigint NOT NULL DEFAULT 0,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_ai_usage PRIMARY KEY (id),
        CONSTRAINT ak_beauty_ai_usage_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_ai_usage_counters CHECK (request_count >= 0 AND error_count >= 0 AND input_tokens >= 0 AND output_tokens >= 0),
        CONSTRAINT ck_beauty_ai_usage_key_source CHECK (key_source IN ('own', 'platform')),
        CONSTRAINT ck_beauty_ai_usage_period_start CHECK (extract(day from period_start) = 1),
        CONSTRAINT ck_beauty_ai_usage_provider CHECK (provider ~ '^[a-z][a-z0-9_]{1,31}$'),
        CONSTRAINT ck_beauty_ai_usage_role CHECK (role IN ('chat', 'analytics'))
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024091314_beauty_ai_settings_and_usage') THEN
    CREATE INDEX ix_beauty_ai_provider_keys_key_kid ON beauty_ai_provider_keys (key_kid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024091314_beauty_ai_settings_and_usage') THEN
    CREATE INDEX ix_beauty_ai_provider_keys_tenant_id_created_by_user_id ON beauty_ai_provider_keys (tenant_id, created_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024091314_beauty_ai_settings_and_usage') THEN
    CREATE UNIQUE INDEX ix_beauty_ai_provider_keys_tenant_id_provider ON beauty_ai_provider_keys (tenant_id, provider);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024091314_beauty_ai_settings_and_usage') THEN
    CREATE INDEX ix_beauty_ai_provider_keys_tenant_id_updated_by_user_id ON beauty_ai_provider_keys (tenant_id, updated_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024091314_beauty_ai_settings_and_usage') THEN
    CREATE UNIQUE INDEX ix_beauty_ai_settings_tenant_id ON beauty_ai_settings (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024091314_beauty_ai_settings_and_usage') THEN
    CREATE INDEX ix_beauty_ai_settings_tenant_id_updated_by_user_id ON beauty_ai_settings (tenant_id, updated_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024091314_beauty_ai_settings_and_usage') THEN
    CREATE INDEX ix_beauty_ai_settings_audit_tenant_id_created_at ON beauty_ai_settings_audit (tenant_id, created_at DESC);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024091314_beauty_ai_settings_and_usage') THEN
    CREATE INDEX ix_beauty_ai_settings_audit_tenant_id_user_id ON beauty_ai_settings_audit (tenant_id, user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024091314_beauty_ai_settings_and_usage') THEN
    CREATE UNIQUE INDEX ix_beauty_ai_usage_tenant_id_period_start_role_provider_model_ ON beauty_ai_usage (tenant_id, period_start, role, provider, model, key_source);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024091314_beauty_ai_settings_and_usage') THEN
    ALTER TABLE beauty_ai_settings ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024091314_beauty_ai_settings_and_usage') THEN
    ALTER TABLE beauty_ai_settings FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024091314_beauty_ai_settings_and_usage') THEN

                    CREATE POLICY tenant_isolation ON beauty_ai_settings
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024091314_beauty_ai_settings_and_usage') THEN
    ALTER TABLE beauty_ai_provider_keys ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024091314_beauty_ai_settings_and_usage') THEN
    ALTER TABLE beauty_ai_provider_keys FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024091314_beauty_ai_settings_and_usage') THEN

                    CREATE POLICY tenant_isolation ON beauty_ai_provider_keys
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024091314_beauty_ai_settings_and_usage') THEN
    ALTER TABLE beauty_ai_usage ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024091314_beauty_ai_settings_and_usage') THEN
    ALTER TABLE beauty_ai_usage FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024091314_beauty_ai_settings_and_usage') THEN

                    CREATE POLICY tenant_isolation ON beauty_ai_usage
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024091314_beauty_ai_settings_and_usage') THEN
    ALTER TABLE beauty_ai_settings_audit ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024091314_beauty_ai_settings_and_usage') THEN
    ALTER TABLE beauty_ai_settings_audit FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024091314_beauty_ai_settings_and_usage') THEN

                    CREATE POLICY tenant_isolation ON beauty_ai_settings_audit
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024091314_beauty_ai_settings_and_usage') THEN

    DO $$
    DECLARE
        t text;
    BEGIN
        FOREACH t IN ARRAY ARRAY['beauty_ai_settings', 'beauty_ai_provider_keys', 'beauty_ai_usage', 'beauty_ai_settings_audit']
        LOOP
            IF NOT EXISTS (
                SELECT 1 FROM pg_class c
                WHERE c.oid = to_regclass('public.' || t) AND c.relrowsecurity AND c.relforcerowsecurity) THEN
                RAISE EXCEPTION 'FORCE ROW LEVEL SECURITY is not enabled on %', t;
            END IF;
        END LOOP;
    END
    $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024091314_beauty_ai_settings_and_usage') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20261024091314_beauty_ai_settings_and_usage', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024102153_beauty_chat_assistant') THEN
    ALTER TABLE beauty_messages ADD kind character varying(16) NOT NULL DEFAULT 'text';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024102153_beauty_chat_assistant') THEN
    ALTER TABLE beauty_conversations ADD ai_lease_owner uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024102153_beauty_chat_assistant') THEN
    ALTER TABLE beauty_conversations ADD ai_lease_until timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024102153_beauty_chat_assistant') THEN
    ALTER TABLE beauty_conversations ADD ai_paused_until timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024102153_beauty_chat_assistant') THEN
    ALTER TABLE beauty_conversations ADD ai_state character varying(16) NOT NULL DEFAULT 'active';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024102153_beauty_chat_assistant') THEN
    ALTER TABLE beauty_conversations ADD handoff_reason character varying(32);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024102153_beauty_chat_assistant') THEN
    CREATE TABLE beauty_channel_poll_state (
        tenant_id uuid NOT NULL,
        channel_id uuid NOT NULL,
        last_update_id bigint NOT NULL DEFAULT 0,
        lease_until timestamp with time zone,
        lease_owner uuid,
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_channel_poll_state PRIMARY KEY (tenant_id, channel_id),
        CONSTRAINT ck_beauty_channel_poll_state_last_update_id CHECK (last_update_id >= 0),
        CONSTRAINT fk_beauty_channel_poll_state_beauty_channels_tenant_id_channel FOREIGN KEY (tenant_id, channel_id) REFERENCES beauty_channels (tenant_id, id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024102153_beauty_chat_assistant') THEN
    CREATE TABLE beauty_chat_booking_proposals (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        conversation_id uuid NOT NULL,
        client_id uuid,
        location_id uuid NOT NULL,
        specialist_id uuid NOT NULL,
        service_id uuid NOT NULL,
        starts_at timestamp with time zone NOT NULL,
        duration_minutes integer NOT NULL,
        price_original numeric(10,2) NOT NULL,
        price_final numeric(10,2) NOT NULL,
        promotion_id uuid,
        status character varying(16) NOT NULL DEFAULT 'prepared',
        confirmation_token_hash character(64) NOT NULL,
        summary_message_id uuid,
        expires_at timestamp with time zone NOT NULL,
        appointment_id uuid,
        failure_code character varying(64),
        decided_at timestamp with time zone,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_chat_booking_proposals PRIMARY KEY (id),
        CONSTRAINT ak_beauty_chat_booking_proposals_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_chat_booking_proposals_duration CHECK (duration_minutes > 0),
        CONSTRAINT ck_beauty_chat_booking_proposals_failure_code CHECK (failure_code IS NULL OR char_length(failure_code) BETWEEN 1 AND 64),
        CONSTRAINT ck_beauty_chat_booking_proposals_prices CHECK (price_original >= 0 AND price_final >= 0),
        CONSTRAINT ck_beauty_chat_booking_proposals_status CHECK (status IN ('prepared', 'shown', 'confirmed', 'rejected', 'expired', 'superseded', 'failed')),
        CONSTRAINT ck_beauty_chat_booking_proposals_token_hash CHECK (confirmation_token_hash ~ '^[0-9a-f]{64}$'),
        CONSTRAINT fk_beauty_chat_booking_proposals_beauty_appointments_tenant_id FOREIGN KEY (tenant_id, appointment_id) REFERENCES beauty_appointments (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_chat_booking_proposals_beauty_clients_tenant_id_clie FOREIGN KEY (tenant_id, client_id) REFERENCES beauty_clients (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_chat_booking_proposals_beauty_conversations_tenant_i FOREIGN KEY (tenant_id, conversation_id) REFERENCES beauty_conversations (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_chat_booking_proposals_beauty_locations_tenant_id_lo FOREIGN KEY (tenant_id, location_id) REFERENCES beauty_locations (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_chat_booking_proposals_beauty_messages_tenant_id_sum FOREIGN KEY (tenant_id, summary_message_id) REFERENCES beauty_messages (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_chat_booking_proposals_beauty_promotions_tenant_id_p FOREIGN KEY (tenant_id, promotion_id) REFERENCES beauty_promotions (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_chat_booking_proposals_beauty_services_tenant_id_ser FOREIGN KEY (tenant_id, service_id) REFERENCES beauty_services (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_chat_booking_proposals_beauty_specialists_tenant_id_ FOREIGN KEY (tenant_id, specialist_id) REFERENCES beauty_specialists (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024102153_beauty_chat_assistant') THEN
    CREATE INDEX ix_beauty_messages_inbound_received ON beauty_messages (tenant_id, conversation_id, sent_at) WHERE direction = 'inbound' AND status = 'received';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024102153_beauty_chat_assistant') THEN
    ALTER TABLE beauty_messages ADD CONSTRAINT ck_beauty_messages_kind CHECK (kind IN ('text', 'command', 'unsupported'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024102153_beauty_chat_assistant') THEN
    CREATE INDEX ix_beauty_conversations_tenant_id_ai_state ON beauty_conversations (tenant_id, ai_state);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024102153_beauty_chat_assistant') THEN
    ALTER TABLE beauty_conversations ADD CONSTRAINT ck_beauty_conversations_ai_state CHECK (ai_state IN ('active', 'handoff'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024102153_beauty_chat_assistant') THEN
    CREATE INDEX ix_beauty_chat_booking_proposals_tenant_id_appointment_id ON beauty_chat_booking_proposals (tenant_id, appointment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024102153_beauty_chat_assistant') THEN
    CREATE INDEX ix_beauty_chat_booking_proposals_tenant_id_client_id ON beauty_chat_booking_proposals (tenant_id, client_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024102153_beauty_chat_assistant') THEN
    CREATE INDEX ix_beauty_chat_booking_proposals_tenant_id_conversation_id_cre ON beauty_chat_booking_proposals (tenant_id, conversation_id, created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024102153_beauty_chat_assistant') THEN
    CREATE INDEX ix_beauty_chat_booking_proposals_tenant_id_location_id ON beauty_chat_booking_proposals (tenant_id, location_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024102153_beauty_chat_assistant') THEN
    CREATE INDEX ix_beauty_chat_booking_proposals_tenant_id_promotion_id ON beauty_chat_booking_proposals (tenant_id, promotion_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024102153_beauty_chat_assistant') THEN
    CREATE INDEX ix_beauty_chat_booking_proposals_tenant_id_service_id ON beauty_chat_booking_proposals (tenant_id, service_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024102153_beauty_chat_assistant') THEN
    CREATE INDEX ix_beauty_chat_booking_proposals_tenant_id_specialist_id ON beauty_chat_booking_proposals (tenant_id, specialist_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024102153_beauty_chat_assistant') THEN
    CREATE INDEX ix_beauty_chat_booking_proposals_tenant_id_summary_message_id ON beauty_chat_booking_proposals (tenant_id, summary_message_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024102153_beauty_chat_assistant') THEN
    CREATE UNIQUE INDEX ux_beauty_chat_booking_proposals_active ON beauty_chat_booking_proposals (tenant_id, conversation_id) WHERE status IN ('prepared', 'shown');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024102153_beauty_chat_assistant') THEN
    CREATE UNIQUE INDEX ux_beauty_chat_booking_proposals_token ON beauty_chat_booking_proposals (tenant_id, confirmation_token_hash);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024102153_beauty_chat_assistant') THEN
    ALTER TABLE beauty_chat_booking_proposals ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024102153_beauty_chat_assistant') THEN
    ALTER TABLE beauty_chat_booking_proposals FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024102153_beauty_chat_assistant') THEN

                    CREATE POLICY tenant_isolation ON beauty_chat_booking_proposals
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024102153_beauty_chat_assistant') THEN
    ALTER TABLE beauty_channel_poll_state ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024102153_beauty_chat_assistant') THEN
    ALTER TABLE beauty_channel_poll_state FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024102153_beauty_chat_assistant') THEN

                    CREATE POLICY tenant_isolation ON beauty_channel_poll_state
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024102153_beauty_chat_assistant') THEN

    DO $$
    DECLARE
        t text;
    BEGIN
        FOREACH t IN ARRAY ARRAY['beauty_chat_booking_proposals', 'beauty_channel_poll_state', 'beauty_conversations', 'beauty_messages']
        LOOP
            IF NOT EXISTS (
                SELECT 1 FROM pg_class c
                WHERE c.oid = to_regclass('public.' || t) AND c.relrowsecurity AND c.relforcerowsecurity) THEN
                RAISE EXCEPTION 'FORCE ROW LEVEL SECURITY is not enabled on %', t;
            END IF;
        END LOOP;
    END
    $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024102153_beauty_chat_assistant') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20261024102153_beauty_chat_assistant', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_reminders DROP CONSTRAINT ck_beauty_reminders_status;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_appointments DROP CONSTRAINT ck_beauty_appointments_reminder_option;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_reminders ADD message_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_reminders ADD method character varying(16);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_reminders ADD offset_minutes smallint;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_reminders ADD planned_starts_at timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_reminders ADD skip_reason character varying(32);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_messages ADD sender_user_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_conversations ADD awaiting_staff_since timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_conversations ADD handoff_at timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_conversations ADD pending_prompt character varying(32);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_conversations ADD pending_prompt_appointment_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_conversations ADD pending_prompt_expires_at timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_conversations ADD reminders_stop_at timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_conversations ADD staff_last_read_at timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_conversations ADD staff_replied_at timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_appointments ADD reminder_method character varying(16) NOT NULL DEFAULT 'none';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_appointments ADD reminder_offsets smallint[] NOT NULL DEFAULT ('{}');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    CREATE TABLE beauty_call_tasks (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        appointment_id uuid NOT NULL,
        client_id uuid NOT NULL,
        location_id uuid NOT NULL,
        reminder_id uuid,
        reason character varying(16) NOT NULL,
        offset_minutes smallint,
        due_at timestamp with time zone NOT NULL,
        status character varying(16) NOT NULL DEFAULT 'open',
        closed_reason character varying(32),
        completed_by_user_id uuid,
        completed_at timestamp with time zone,
        note character varying(300),
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_call_tasks PRIMARY KEY (id),
        CONSTRAINT ak_beauty_call_tasks_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_call_tasks_completed CHECK ((status IN ('done', 'skipped') AND completed_by_user_id IS NOT NULL AND completed_at IS NOT NULL) OR (status NOT IN ('done', 'skipped') AND completed_by_user_id IS NULL AND completed_at IS NULL)),
        CONSTRAINT ck_beauty_call_tasks_reason CHECK (reason IN ('reminder', 'delivery_failed')),
        CONSTRAINT ck_beauty_call_tasks_status CHECK (status IN ('open', 'done', 'skipped', 'cancelled')),
        CONSTRAINT fk_beauty_call_tasks_beauty_appointments_tenant_id_appointment FOREIGN KEY (tenant_id, appointment_id) REFERENCES beauty_appointments (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_call_tasks_beauty_clients_tenant_id_client_id FOREIGN KEY (tenant_id, client_id) REFERENCES beauty_clients (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_call_tasks_beauty_locations_tenant_id_location_id FOREIGN KEY (tenant_id, location_id) REFERENCES beauty_locations (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_call_tasks_beauty_reminders_tenant_id_reminder_id FOREIGN KEY (tenant_id, reminder_id) REFERENCES beauty_reminders (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_call_tasks_users_tenant_id_completed_by_user_id FOREIGN KEY (tenant_id, completed_by_user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    CREATE TABLE beauty_client_contact_preferences (
        tenant_id uuid NOT NULL,
        client_id uuid NOT NULL,
        method character varying(16) NOT NULL,
        offsets_minutes smallint[] NOT NULL DEFAULT ('{}'),
        telegram_conversation_id uuid,
        source character varying(16) NOT NULL,
        updated_by_user_id uuid,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_client_contact_preferences PRIMARY KEY (tenant_id, client_id),
        CONSTRAINT ck_beauty_client_contact_preferences_call_single CHECK (method <> 'call' OR cardinality(offsets_minutes) = 1),
        CONSTRAINT ck_beauty_client_contact_preferences_method CHECK (method IN ('none', 'telegram', 'call')),
        CONSTRAINT ck_beauty_client_contact_preferences_none_empty CHECK ((method = 'none') = (cardinality(offsets_minutes) = 0)),
        CONSTRAINT ck_beauty_client_contact_preferences_offsets CHECK (offsets_minutes <@ ARRAY[60, 120, 1440]::smallint[] AND cardinality(offsets_minutes) <= 3),
        CONSTRAINT ck_beauty_client_contact_preferences_source CHECK (source IN ('chat', 'admin', 'stop', 'web')),
        CONSTRAINT ck_beauty_client_contact_preferences_updated_by CHECK (updated_by_user_id IS NULL OR source = 'admin'),
        CONSTRAINT fk_beauty_client_contact_preferences_beauty_clients_tenant_id_ FOREIGN KEY (tenant_id, client_id) REFERENCES beauty_clients (tenant_id, id) ON DELETE CASCADE,
        CONSTRAINT fk_beauty_client_contact_preferences_beauty_conversations_tena FOREIGN KEY (tenant_id, telegram_conversation_id) REFERENCES beauty_conversations (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_client_contact_preferences_users_tenant_id_updated_b FOREIGN KEY (tenant_id, updated_by_user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    CREATE TABLE beauty_reminder_settings (
        tenant_id uuid NOT NULL,
        telegram_enabled boolean NOT NULL DEFAULT TRUE,
        call_enabled boolean NOT NULL DEFAULT TRUE,
        quiet_start time without time zone NOT NULL DEFAULT ('22:00'),
        quiet_end time without time zone NOT NULL DEFAULT ('08:00'),
        updated_by_user_id uuid,
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_reminder_settings PRIMARY KEY (tenant_id),
        CONSTRAINT ck_beauty_reminder_settings_quiet CHECK (quiet_start <> quiet_end),
        CONSTRAINT fk_beauty_reminder_settings_users_tenant_id_updated_by_user_id FOREIGN KEY (tenant_id, updated_by_user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    CREATE INDEX ix_beauty_reminders_tenant_id_message_id ON beauty_reminders (tenant_id, message_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    CREATE UNIQUE INDEX ux_beauty_reminders_point ON beauty_reminders (tenant_id, appointment_id, offset_minutes) WHERE status = 'scheduled' AND offset_minutes IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_reminders ADD CONSTRAINT ck_beauty_reminders_method CHECK (method IS NULL OR method IN ('telegram', 'auto'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_reminders ADD CONSTRAINT ck_beauty_reminders_status CHECK (status IN ('scheduled', 'sent', 'failed', 'cancelled', 'skipped'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    CREATE INDEX ix_beauty_messages_tenant_id_sender_user_id ON beauty_messages (tenant_id, sender_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_messages ADD CONSTRAINT ck_beauty_messages_sender_user CHECK (sender_user_id IS NULL OR sender_type = 'staff');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    CREATE INDEX ix_beauty_conversations_awaiting_staff ON beauty_conversations (tenant_id, awaiting_staff_since) WHERE awaiting_staff_since IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    CREATE INDEX ix_beauty_conversations_last_message_desc ON beauty_conversations (tenant_id, last_message_at DESC);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    CREATE INDEX ix_beauty_conversations_tenant_id_pending_prompt_appointment_id ON beauty_conversations (tenant_id, pending_prompt_appointment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_conversations ADD CONSTRAINT ck_beauty_conversations_pending_prompt CHECK (pending_prompt IS NULL OR pending_prompt IN ('reminder_choice'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_conversations ADD CONSTRAINT ck_beauty_conversations_pending_prompt_all_or_none CHECK ((pending_prompt IS NULL) = (pending_prompt_appointment_id IS NULL) AND (pending_prompt IS NULL) = (pending_prompt_expires_at IS NULL));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_appointments ADD CONSTRAINT ck_beauty_appointments_reminder_call_single CHECK (reminder_method <> 'call' OR cardinality(reminder_offsets) = 1);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_appointments ADD CONSTRAINT ck_beauty_appointments_reminder_method CHECK (reminder_method IN ('none', 'auto', 'telegram', 'call'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_appointments ADD CONSTRAINT ck_beauty_appointments_reminder_none_empty CHECK ((reminder_method = 'none') = (cardinality(reminder_offsets) = 0));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_appointments ADD CONSTRAINT ck_beauty_appointments_reminder_offsets CHECK (reminder_offsets <@ ARRAY[60, 120, 1440]::smallint[] AND cardinality(reminder_offsets) <= 3);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_appointments ADD CONSTRAINT ck_beauty_appointments_reminder_option CHECK (reminder_option IN ('none', '1h', '2h', 'custom'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    CREATE INDEX ix_beauty_call_tasks_tenant_id_client_id ON beauty_call_tasks (tenant_id, client_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    CREATE INDEX ix_beauty_call_tasks_tenant_id_completed_by_user_id ON beauty_call_tasks (tenant_id, completed_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    CREATE INDEX ix_beauty_call_tasks_tenant_id_location_id_due_at ON beauty_call_tasks (tenant_id, location_id, due_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    CREATE INDEX ix_beauty_call_tasks_tenant_id_status_due_at ON beauty_call_tasks (tenant_id, status, due_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    CREATE UNIQUE INDEX ux_beauty_call_tasks_open_reminder ON beauty_call_tasks (tenant_id, appointment_id) WHERE status = 'open' AND reason = 'reminder';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    CREATE UNIQUE INDEX ux_beauty_call_tasks_reminder ON beauty_call_tasks (tenant_id, reminder_id) WHERE reminder_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    CREATE INDEX ix_beauty_client_contact_preferences_tenant_id_telegram_conver ON beauty_client_contact_preferences (tenant_id, telegram_conversation_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    CREATE INDEX ix_beauty_client_contact_preferences_tenant_id_updated_by_user ON beauty_client_contact_preferences (tenant_id, updated_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    CREATE INDEX ix_beauty_reminder_settings_tenant_id_updated_by_user_id ON beauty_reminder_settings (tenant_id, updated_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_conversations ADD CONSTRAINT fk_beauty_conversations_beauty_appointments_tenant_id_pending_ FOREIGN KEY (tenant_id, pending_prompt_appointment_id) REFERENCES beauty_appointments (tenant_id, id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_messages ADD CONSTRAINT fk_beauty_messages_users_tenant_id_sender_user_id FOREIGN KEY (tenant_id, sender_user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_reminders ADD CONSTRAINT fk_beauty_reminders_beauty_messages_tenant_id_message_id FOREIGN KEY (tenant_id, message_id) REFERENCES beauty_messages (tenant_id, id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_appointments NO FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_reminders NO FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN

                    UPDATE beauty_appointments
                    SET reminder_method = 'auto',
                        reminder_offsets = CASE reminder_option WHEN '1h' THEN ARRAY[60]::smallint[] ELSE ARRAY[120]::smallint[] END
                    WHERE reminder_option IN ('1h', '2h') AND reminder_method = 'none';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN

                    UPDATE beauty_reminders r
                    SET method = 'auto', planned_starts_at = a.starts_at
                    FROM beauty_appointments a
                    WHERE r.status = 'scheduled' AND r.method IS NULL
                      AND a.tenant_id = r.tenant_id AND a.id = r.appointment_id;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN

                    WITH pick AS (
                        SELECT DISTINCT ON (r.tenant_id, r.appointment_id)
                               r.tenant_id, r.id,
                               (CASE a.reminder_option WHEN '1h' THEN 60 ELSE 120 END)::smallint AS off
                        FROM beauty_reminders r
                        JOIN beauty_appointments a ON a.tenant_id = r.tenant_id AND a.id = r.appointment_id
                        WHERE r.status = 'scheduled' AND r.offset_minutes IS NULL AND a.reminder_option IN ('1h', '2h')
                        ORDER BY r.tenant_id, r.appointment_id, r.scheduled_at DESC, r.id)
                    UPDATE beauty_reminders r
                    SET offset_minutes = pick.off
                    FROM pick
                    WHERE r.tenant_id = pick.tenant_id AND r.id = pick.id;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_appointments FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_reminders FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_client_contact_preferences ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_client_contact_preferences FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN

                    CREATE POLICY tenant_isolation ON beauty_client_contact_preferences
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_call_tasks ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_call_tasks FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN

                    CREATE POLICY tenant_isolation ON beauty_call_tasks
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_reminder_settings ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    ALTER TABLE beauty_reminder_settings FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN

                    CREATE POLICY tenant_isolation ON beauty_reminder_settings
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN

    DO $$
    DECLARE
        t text;
    BEGIN
        FOREACH t IN ARRAY ARRAY['beauty_client_contact_preferences', 'beauty_call_tasks', 'beauty_reminder_settings',
                                 'beauty_appointments', 'beauty_reminders', 'beauty_conversations', 'beauty_messages', 'beauty_clients']
        LOOP
            IF NOT EXISTS (
                SELECT 1 FROM pg_class c
                WHERE c.oid = to_regclass('public.' || t) AND c.relrowsecurity AND c.relforcerowsecurity) THEN
                RAISE EXCEPTION 'FORCE ROW LEVEL SECURITY is not enabled on %', t;
            END IF;
        END LOOP;
    END
    $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261024193048_beauty_reminders_contacts_inbox') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20261024193048_beauty_reminders_contacts_inbox', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261025080340_beauty_chat_buttons') THEN
    ALTER TABLE beauty_messages DROP CONSTRAINT ck_beauty_messages_kind;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261025080340_beauty_chat_buttons') THEN
    ALTER TABLE beauty_messages ADD actions jsonb;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261025080340_beauty_chat_buttons') THEN
    ALTER TABLE beauty_messages ADD callback_data character varying(64);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261025080340_beauty_chat_buttons') THEN
    ALTER TABLE beauty_messages ADD callback_message_ref character varying(32);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261025080340_beauty_chat_buttons') THEN
    ALTER TABLE beauty_appointments ADD client_confirmed_at timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261025080340_beauty_chat_buttons') THEN
    CREATE TABLE beauty_chat_keyboards (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        conversation_id uuid NOT NULL,
        message_id uuid NOT NULL,
        kind character varying(24) NOT NULL,
        token character varying(22) NOT NULL,
        options jsonb NOT NULL,
        proposal_id uuid,
        appointment_id uuid,
        status character varying(16) NOT NULL DEFAULT 'active',
        expires_at timestamp with time zone NOT NULL,
        used_at timestamp with time zone,
        used_option smallint,
        used_by_message_id uuid,
        markup_cleared_at timestamp with time zone,
        clear_attempts smallint NOT NULL DEFAULT 0,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_chat_keyboards PRIMARY KEY (id),
        CONSTRAINT ak_beauty_chat_keyboards_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_chat_keyboards_appointment CHECK (kind NOT IN ('appointment_actions', 'cancel_confirm', 'reminder_choice') OR appointment_id IS NOT NULL),
        CONSTRAINT ck_beauty_chat_keyboards_clear_attempts CHECK (clear_attempts >= 0),
        CONSTRAINT ck_beauty_chat_keyboards_kind CHECK (kind IN ('booking_confirm', 'slot_pick', 'reminder_choice', 'appointment_list', 'appointment_actions', 'cancel_confirm')),
        CONSTRAINT ck_beauty_chat_keyboards_options CHECK (jsonb_typeof(options) = 'array' AND jsonb_array_length(options) <= 10),
        CONSTRAINT ck_beauty_chat_keyboards_proposal CHECK (kind <> 'booking_confirm' OR proposal_id IS NOT NULL),
        CONSTRAINT ck_beauty_chat_keyboards_status CHECK (status IN ('active', 'used', 'expired', 'revoked')),
        CONSTRAINT ck_beauty_chat_keyboards_token CHECK (token ~ '^[A-Za-z0-9_-]{22}$'),
        CONSTRAINT ck_beauty_chat_keyboards_used CHECK ((status = 'used' AND used_at IS NOT NULL AND used_by_message_id IS NOT NULL) OR (status <> 'used' AND used_at IS NULL AND used_by_message_id IS NULL)),
        CONSTRAINT ck_beauty_chat_keyboards_used_option CHECK (used_option IS NULL OR (status = 'used' AND used_option BETWEEN 0 AND 9)),
        CONSTRAINT fk_beauty_chat_keyboards_beauty_appointments_tenant_id_appoint FOREIGN KEY (tenant_id, appointment_id) REFERENCES beauty_appointments (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_chat_keyboards_beauty_chat_booking_proposals_tenant_ FOREIGN KEY (tenant_id, proposal_id) REFERENCES beauty_chat_booking_proposals (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_chat_keyboards_beauty_conversations_tenant_id_conver FOREIGN KEY (tenant_id, conversation_id) REFERENCES beauty_conversations (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_chat_keyboards_beauty_messages_tenant_id_message_id FOREIGN KEY (tenant_id, message_id) REFERENCES beauty_messages (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_chat_keyboards_beauty_messages_tenant_id_used_by_mes FOREIGN KEY (tenant_id, used_by_message_id) REFERENCES beauty_messages (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261025080340_beauty_chat_buttons') THEN
    ALTER TABLE beauty_messages ADD CONSTRAINT ck_beauty_messages_actions CHECK (actions IS NULL OR (direction = 'outbound' AND jsonb_typeof(actions) = 'object' AND octet_length(actions::text) <= 4096));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261025080340_beauty_chat_buttons') THEN
    ALTER TABLE beauty_messages ADD CONSTRAINT ck_beauty_messages_callback_fields CHECK (kind = 'callback' OR (callback_data IS NULL AND callback_message_ref IS NULL));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261025080340_beauty_chat_buttons') THEN
    ALTER TABLE beauty_messages ADD CONSTRAINT ck_beauty_messages_kind CHECK (kind IN ('text', 'command', 'unsupported', 'callback'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261025080340_beauty_chat_buttons') THEN
    CREATE INDEX ix_beauty_chat_keyboards_active ON beauty_chat_keyboards (tenant_id, conversation_id) WHERE status = 'active';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261025080340_beauty_chat_buttons') THEN
    CREATE INDEX ix_beauty_chat_keyboards_sweeper ON beauty_chat_keyboards (tenant_id, updated_at) WHERE status <> 'active' AND markup_cleared_at IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261025080340_beauty_chat_buttons') THEN
    CREATE INDEX ix_beauty_chat_keyboards_tenant_id_appointment_id ON beauty_chat_keyboards (tenant_id, appointment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261025080340_beauty_chat_buttons') THEN
    CREATE INDEX ix_beauty_chat_keyboards_tenant_id_conversation_id ON beauty_chat_keyboards (tenant_id, conversation_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261025080340_beauty_chat_buttons') THEN
    CREATE UNIQUE INDEX ix_beauty_chat_keyboards_tenant_id_message_id ON beauty_chat_keyboards (tenant_id, message_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261025080340_beauty_chat_buttons') THEN
    CREATE INDEX ix_beauty_chat_keyboards_tenant_id_proposal_id ON beauty_chat_keyboards (tenant_id, proposal_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261025080340_beauty_chat_buttons') THEN
    CREATE UNIQUE INDEX ix_beauty_chat_keyboards_tenant_id_token ON beauty_chat_keyboards (tenant_id, token);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261025080340_beauty_chat_buttons') THEN
    CREATE INDEX ix_beauty_chat_keyboards_tenant_id_used_by_message_id ON beauty_chat_keyboards (tenant_id, used_by_message_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261025080340_beauty_chat_buttons') THEN
    ALTER TABLE beauty_chat_keyboards ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261025080340_beauty_chat_buttons') THEN
    ALTER TABLE beauty_chat_keyboards FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261025080340_beauty_chat_buttons') THEN

                    CREATE POLICY tenant_isolation ON beauty_chat_keyboards
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261025080340_beauty_chat_buttons') THEN

    DO $$
    DECLARE
        t text;
    BEGIN
        FOREACH t IN ARRAY ARRAY['beauty_chat_keyboards', 'beauty_messages', 'beauty_appointments']
        LOOP
            IF NOT EXISTS (
                SELECT 1 FROM pg_class c
                WHERE c.oid = to_regclass('public.' || t) AND c.relrowsecurity AND c.relforcerowsecurity) THEN
                RAISE EXCEPTION 'FORCE ROW LEVEL SECURITY is not enabled on %', t;
            END IF;
        END LOOP;
    END
    $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261025080340_beauty_chat_buttons') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20261025080340_beauty_chat_buttons', '8.0.11');
    END IF;
END $EF$;
COMMIT;

