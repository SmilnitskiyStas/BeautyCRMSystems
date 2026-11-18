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

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026091203_beauty_service_costs') THEN
    ALTER TABLE beauty_appointments ADD cost_captured_at timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026091203_beauty_service_costs') THEN
    ALTER TABLE beauty_appointments ADD cost_snapshot numeric(12,2);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026091203_beauty_service_costs') THEN
    ALTER TABLE beauty_appointments ADD master_payout_snapshot numeric(12,2);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026091203_beauty_service_costs') THEN
    ALTER TABLE beauty_appointments ADD master_percent_snapshot numeric(5,2);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026091203_beauty_service_costs') THEN
    CREATE TABLE beauty_service_cost_audit (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        tenant_id uuid NOT NULL,
        service_id uuid NOT NULL,
        location_id uuid,
        user_id uuid NOT NULL,
        action character varying(8) NOT NULL,
        changed_fields text[] NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_service_cost_audit PRIMARY KEY (id),
        CONSTRAINT ak_beauty_service_cost_audit_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_service_cost_audit_action CHECK (action IN ('set', 'delete')),
        CONSTRAINT ck_beauty_service_cost_audit_changed_fields CHECK (changed_fields <@ ARRAY['cost_amount', 'master_percent']::text[]),
        CONSTRAINT fk_beauty_service_cost_audit_beauty_locations_tenant_id_locati FOREIGN KEY (tenant_id, location_id) REFERENCES beauty_locations (tenant_id, id) ON DELETE CASCADE,
        CONSTRAINT fk_beauty_service_cost_audit_beauty_services_tenant_id_service FOREIGN KEY (tenant_id, service_id) REFERENCES beauty_services (tenant_id, id) ON DELETE CASCADE,
        CONSTRAINT fk_beauty_service_cost_audit_users_tenant_id_user_id FOREIGN KEY (tenant_id, user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026091203_beauty_service_costs') THEN
    CREATE TABLE beauty_service_costs (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        service_id uuid NOT NULL,
        location_id uuid,
        cost_amount numeric(12,2) NOT NULL,
        master_percent numeric(5,2),
        updated_by_user_id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_service_costs PRIMARY KEY (id),
        CONSTRAINT ak_beauty_service_costs_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_service_costs_cost CHECK (cost_amount >= 0 AND cost_amount <= 1000000),
        CONSTRAINT ck_beauty_service_costs_master_percent CHECK (master_percent IS NULL OR master_percent BETWEEN 0 AND 100),
        CONSTRAINT fk_beauty_service_costs_beauty_locations_tenant_id_location_id FOREIGN KEY (tenant_id, location_id) REFERENCES beauty_locations (tenant_id, id) ON DELETE CASCADE,
        CONSTRAINT fk_beauty_service_costs_beauty_services_tenant_id_service_id FOREIGN KEY (tenant_id, service_id) REFERENCES beauty_services (tenant_id, id) ON DELETE CASCADE,
        CONSTRAINT fk_beauty_service_costs_users_tenant_id_updated_by_user_id FOREIGN KEY (tenant_id, updated_by_user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026091203_beauty_service_costs') THEN
    ALTER TABLE beauty_appointments ADD CONSTRAINT ck_beauty_appointments_cost_consistency CHECK ((cost_snapshot IS NULL OR cost_captured_at IS NOT NULL) AND (master_payout_snapshot IS NULL OR cost_snapshot IS NOT NULL) AND (master_percent_snapshot IS NULL OR master_payout_snapshot IS NOT NULL));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026091203_beauty_service_costs') THEN
    ALTER TABLE beauty_appointments ADD CONSTRAINT ck_beauty_appointments_cost_status CHECK ((cost_snapshot IS NULL AND master_percent_snapshot IS NULL AND master_payout_snapshot IS NULL AND cost_captured_at IS NULL) OR status = 'completed');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026091203_beauty_service_costs') THEN
    ALTER TABLE beauty_appointments ADD CONSTRAINT ck_beauty_appointments_cost_values CHECK ((cost_snapshot IS NULL OR cost_snapshot >= 0) AND (master_payout_snapshot IS NULL OR master_payout_snapshot >= 0) AND (master_percent_snapshot IS NULL OR master_percent_snapshot BETWEEN 0 AND 100));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026091203_beauty_service_costs') THEN
    CREATE INDEX ix_beauty_service_cost_audit_tenant_id_location_id ON beauty_service_cost_audit (tenant_id, location_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026091203_beauty_service_costs') THEN
    CREATE INDEX ix_beauty_service_cost_audit_tenant_id_service_id_created_at ON beauty_service_cost_audit (tenant_id, service_id, created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026091203_beauty_service_costs') THEN
    CREATE INDEX ix_beauty_service_cost_audit_tenant_id_user_id ON beauty_service_cost_audit (tenant_id, user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026091203_beauty_service_costs') THEN
    CREATE INDEX ix_beauty_service_costs_tenant_id_location_id ON beauty_service_costs (tenant_id, location_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026091203_beauty_service_costs') THEN
    CREATE INDEX ix_beauty_service_costs_tenant_id_updated_by_user_id ON beauty_service_costs (tenant_id, updated_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026091203_beauty_service_costs') THEN
    CREATE UNIQUE INDEX ux_beauty_service_costs_scope ON beauty_service_costs (tenant_id, service_id, location_id) NULLS NOT DISTINCT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026091203_beauty_service_costs') THEN
    ALTER TABLE beauty_service_costs ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026091203_beauty_service_costs') THEN
    ALTER TABLE beauty_service_costs FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026091203_beauty_service_costs') THEN

                    CREATE POLICY tenant_isolation ON beauty_service_costs
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026091203_beauty_service_costs') THEN
    ALTER TABLE beauty_service_cost_audit ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026091203_beauty_service_costs') THEN
    ALTER TABLE beauty_service_cost_audit FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026091203_beauty_service_costs') THEN

                    CREATE POLICY tenant_isolation ON beauty_service_cost_audit
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026091203_beauty_service_costs') THEN

    DO $$
    DECLARE
        t text;
    BEGIN
        FOREACH t IN ARRAY ARRAY['beauty_service_costs', 'beauty_service_cost_audit', 'beauty_appointments', 'beauty_services', 'beauty_service_prices']
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
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026091203_beauty_service_costs') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20261026091203_beauty_service_costs', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026093343_beauty_client_phone_capture') THEN
    ALTER TABLE beauty_messages DROP CONSTRAINT ck_beauty_messages_kind;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026093343_beauty_client_phone_capture') THEN
    ALTER TABLE beauty_conversations DROP CONSTRAINT ck_beauty_conversations_pending_prompt;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026093343_beauty_client_phone_capture') THEN
    ALTER TABLE beauty_conversations DROP CONSTRAINT ck_beauty_conversations_pending_prompt_all_or_none;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026093343_beauty_client_phone_capture') THEN
    ALTER TABLE beauty_messages ADD contact_phone character varying(16);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026093343_beauty_client_phone_capture') THEN
    ALTER TABLE beauty_messages ADD contact_verdict character varying(8);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026093343_beauty_client_phone_capture') THEN
    ALTER TABLE beauty_conversations ADD reply_keyboard_open_at timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026093343_beauty_client_phone_capture') THEN
    ALTER TABLE beauty_clients ADD call_consent_at timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026093343_beauty_client_phone_capture') THEN
    ALTER TABLE beauty_clients ADD duplicate_of_client_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026093343_beauty_client_phone_capture') THEN
    ALTER TABLE beauty_clients ADD phone_pending character varying(16);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026093343_beauty_client_phone_capture') THEN
    ALTER TABLE beauty_clients ADD phone_pending_at timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026093343_beauty_client_phone_capture') THEN
    ALTER TABLE beauty_clients ADD phone_pending_source character varying(16);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026093343_beauty_client_phone_capture') THEN
    ALTER TABLE beauty_clients ADD phone_requested_at timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026093343_beauty_client_phone_capture') THEN
    ALTER TABLE beauty_clients ADD phone_source character varying(16);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026093343_beauty_client_phone_capture') THEN
    CREATE INDEX ix_beauty_messages_contact_phone ON beauty_messages (tenant_id, created_at) WHERE contact_phone IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026093343_beauty_client_phone_capture') THEN
    ALTER TABLE beauty_messages ADD CONSTRAINT ck_beauty_messages_contact_fields CHECK (kind = 'contact' OR (contact_phone IS NULL AND contact_verdict IS NULL));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026093343_beauty_client_phone_capture') THEN
    ALTER TABLE beauty_messages ADD CONSTRAINT ck_beauty_messages_contact_phone CHECK (contact_phone IS NULL OR (contact_verdict IS NOT NULL AND contact_verdict = 'own' AND contact_phone ~ '^\+[1-9][0-9]{7,14}$'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026093343_beauty_client_phone_capture') THEN
    ALTER TABLE beauty_messages ADD CONSTRAINT ck_beauty_messages_contact_verdict CHECK (contact_verdict IS NULL OR contact_verdict IN ('own', 'foreign', 'invalid'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026093343_beauty_client_phone_capture') THEN
    ALTER TABLE beauty_messages ADD CONSTRAINT ck_beauty_messages_kind CHECK (kind IN ('text', 'command', 'unsupported', 'callback', 'contact'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026093343_beauty_client_phone_capture') THEN
    ALTER TABLE beauty_conversations ADD CONSTRAINT ck_beauty_conversations_pending_prompt CHECK (pending_prompt IS NULL OR pending_prompt IN ('reminder_choice', 'phone_request'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026093343_beauty_client_phone_capture') THEN
    ALTER TABLE beauty_conversations ADD CONSTRAINT ck_beauty_conversations_pending_prompt_all_or_none CHECK ((pending_prompt IS NULL AND pending_prompt_appointment_id IS NULL AND pending_prompt_expires_at IS NULL) OR (pending_prompt IS NOT NULL AND pending_prompt = 'reminder_choice' AND pending_prompt_appointment_id IS NOT NULL AND pending_prompt_expires_at IS NOT NULL) OR (pending_prompt IS NOT NULL AND pending_prompt = 'phone_request' AND pending_prompt_expires_at IS NOT NULL));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026093343_beauty_client_phone_capture') THEN
    CREATE INDEX ix_beauty_clients_tenant_id_duplicate_of_client_id ON beauty_clients (tenant_id, duplicate_of_client_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026093343_beauty_client_phone_capture') THEN
    ALTER TABLE beauty_clients ADD CONSTRAINT ck_beauty_clients_duplicate_not_self CHECK (duplicate_of_client_id IS NULL OR duplicate_of_client_id <> id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026093343_beauty_client_phone_capture') THEN
    ALTER TABLE beauty_clients ADD CONSTRAINT ck_beauty_clients_phone_pending_all_or_none CHECK ((phone_pending IS NULL) = (phone_pending_source IS NULL) AND (phone_pending IS NULL) = (phone_pending_at IS NULL));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026093343_beauty_client_phone_capture') THEN
    ALTER TABLE beauty_clients ADD CONSTRAINT ck_beauty_clients_phone_pending_format CHECK (phone_pending IS NULL OR phone_pending ~ '^\+[1-9][0-9]{7,14}$');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026093343_beauty_client_phone_capture') THEN
    ALTER TABLE beauty_clients ADD CONSTRAINT ck_beauty_clients_phone_pending_source CHECK (phone_pending_source IS NULL OR phone_pending_source IN ('telegram_contact', 'manual_chat'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026093343_beauty_client_phone_capture') THEN
    ALTER TABLE beauty_clients ADD CONSTRAINT ck_beauty_clients_phone_source CHECK (phone_source IS NULL OR phone_source IN ('telegram_contact', 'manual_chat', 'web_form', 'staff'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026093343_beauty_client_phone_capture') THEN
    ALTER TABLE beauty_clients ADD CONSTRAINT ck_beauty_clients_phone_source_requires_phone CHECK (phone_source IS NULL OR phone IS NOT NULL);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026093343_beauty_client_phone_capture') THEN
    ALTER TABLE beauty_clients ADD CONSTRAINT fk_beauty_clients_beauty_clients_tenant_id_duplicate_of_client FOREIGN KEY (tenant_id, duplicate_of_client_id) REFERENCES beauty_clients (tenant_id, id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026093343_beauty_client_phone_capture') THEN

    DO $$
    DECLARE
        t text;
    BEGIN
        FOREACH t IN ARRAY ARRAY['beauty_clients', 'beauty_conversations', 'beauty_messages']
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
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026093343_beauty_client_phone_capture') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20261026093343_beauty_client_phone_capture', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_chat_keyboards DROP CONSTRAINT ck_beauty_chat_keyboards_kind;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_promotions ADD access character varying(16) NOT NULL DEFAULT 'public';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_conversations ADD offer_campaign_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_conversations ADD offer_coupon_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_conversations ADD offer_until timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_clients ADD marketing_offered_at timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_chat_keyboards ADD campaign_recipient_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_chat_booking_proposals ADD campaign_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_chat_booking_proposals ADD coupon_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_appointments ADD campaign_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_appointments ADD coupon_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE TABLE beauty_marketing_consents (
        tenant_id uuid NOT NULL,
        client_id uuid NOT NULL,
        channel character varying(16) NOT NULL,
        level character varying(16) NOT NULL,
        source character varying(16) NOT NULL,
        text_version character varying(32),
        updated_by_user_id uuid,
        conversation_id uuid,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_marketing_consents PRIMARY KEY (tenant_id, client_id, channel),
        CONSTRAINT ck_beauty_marketing_consents_admin CHECK ((source = 'admin') = (updated_by_user_id IS NOT NULL)),
        CONSTRAINT ck_beauty_marketing_consents_channel CHECK (channel IN ('telegram', 'push')),
        CONSTRAINT ck_beauty_marketing_consents_level CHECK (level IN ('full', 'selective', 'none')),
        CONSTRAINT ck_beauty_marketing_consents_source CHECK (source IN ('chat', 'admin', 'stop')),
        CONSTRAINT ck_beauty_marketing_consents_version CHECK (source <> 'chat' OR level = 'none' OR text_version IS NOT NULL),
        CONSTRAINT fk_beauty_marketing_consents_beauty_clients_tenant_id_client_id FOREIGN KEY (tenant_id, client_id) REFERENCES beauty_clients (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_marketing_consents_beauty_conversations_tenant_id_co FOREIGN KEY (tenant_id, conversation_id) REFERENCES beauty_conversations (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_marketing_consents_users_tenant_id_updated_by_user_id FOREIGN KEY (tenant_id, updated_by_user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE TABLE beauty_campaign_recipients (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        campaign_id uuid NOT NULL,
        client_id uuid NOT NULL,
        status character varying(8) NOT NULL DEFAULT 'pending',
        skip_reason character varying(32),
        conversation_id uuid,
        message_id uuid,
        coupon_id uuid,
        queued_at timestamp with time zone,
        sent_at timestamp with time zone,
        clicked_at timestamp with time zone,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_campaign_recipients PRIMARY KEY (id),
        CONSTRAINT ak_campaign_recipients_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_campaign_recipients_message CHECK (status NOT IN ('queued', 'sent', 'failed') OR message_id IS NOT NULL),
        CONSTRAINT ck_beauty_campaign_recipients_skip_reason CHECK (status <> 'skipped' OR skip_reason IS NOT NULL),
        CONSTRAINT ck_beauty_campaign_recipients_status CHECK (status IN ('pending', 'queued', 'sent', 'failed', 'skipped')),
        CONSTRAINT fk_beauty_campaign_recipients_beauty_clients_tenant_id_client_ FOREIGN KEY (tenant_id, client_id) REFERENCES beauty_clients (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_campaign_recipients_beauty_conversations_tenant_id_c FOREIGN KEY (tenant_id, conversation_id) REFERENCES beauty_conversations (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_campaign_recipients_beauty_messages_tenant_id_messag FOREIGN KEY (tenant_id, message_id) REFERENCES beauty_messages (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE TABLE beauty_campaigns (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        name character varying(200) NOT NULL,
        type character varying(8) NOT NULL,
        promotion_id uuid,
        segment_kind character varying(16) NOT NULL,
        segment_days smallint,
        segment_service_id uuid,
        segment_min_visits smallint,
        segment_period_days smallint,
        location_id uuid,
        template character varying(1000) NOT NULL,
        coupon_mode character varying(8) NOT NULL DEFAULT 'none',
        shared_coupon_id uuid,
        coupon_valid_from timestamp with time zone,
        coupon_valid_to timestamp with time zone,
        status character varying(12) NOT NULL DEFAULT 'draft',
        revision integer NOT NULL DEFAULT 1,
        scheduled_at timestamp with time zone,
        recipients_total integer,
        created_by_user_id uuid NOT NULL,
        confirmed_by_user_id uuid,
        confirmed_at timestamp with time zone,
        confirm_key_hash character(64),
        cancelled_by_user_id uuid,
        cancelled_at timestamp with time zone,
        started_at timestamp with time zone,
        finished_at timestamp with time zone,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_campaigns PRIMARY KEY (id),
        CONSTRAINT ak_campaigns_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_campaigns_cancelled CHECK ((status = 'cancelled') = (cancelled_at IS NOT NULL)),
        CONSTRAINT ck_beauty_campaigns_confirmed CHECK (status NOT IN ('scheduled', 'sending', 'done') OR (confirmed_by_user_id IS NOT NULL AND confirmed_at IS NOT NULL AND scheduled_at IS NOT NULL)),
        CONSTRAINT ck_beauty_campaigns_coupon_mode CHECK (coupon_mode IN ('none', 'personal', 'shared')),
        CONSTRAINT ck_beauty_campaigns_coupon_period CHECK (coupon_valid_from IS NULL OR coupon_valid_to IS NULL OR coupon_valid_to > coupon_valid_from),
        CONSTRAINT ck_beauty_campaigns_personal_valid_to CHECK (coupon_mode <> 'personal' OR coupon_valid_to IS NOT NULL),
        CONSTRAINT ck_beauty_campaigns_recipients_total CHECK (recipients_total IS NULL OR recipients_total >= 0),
        CONSTRAINT ck_beauty_campaigns_revision CHECK (revision >= 1),
        CONSTRAINT ck_beauty_campaigns_segment_kind CHECK (segment_kind IN ('lapsed', 'frequent', 'new', 'all_consented')),
        CONSTRAINT ck_beauty_campaigns_segment_params CHECK ((segment_kind <> 'lapsed' OR (segment_days IS NOT NULL AND segment_min_visits IS NULL AND segment_period_days IS NULL)) AND (segment_kind <> 'frequent' OR (segment_min_visits IS NOT NULL AND segment_period_days IS NOT NULL AND segment_days IS NULL AND segment_service_id IS NULL)) AND (segment_kind NOT IN ('new', 'all_consented') OR (segment_days IS NULL AND segment_service_id IS NULL AND segment_min_visits IS NULL AND segment_period_days IS NULL))),
        CONSTRAINT ck_beauty_campaigns_segment_ranges CHECK ((segment_days IS NULL OR segment_days BETWEEN 1 AND 730) AND (segment_min_visits IS NULL OR segment_min_visits BETWEEN 1 AND 100) AND (segment_period_days IS NULL OR segment_period_days BETWEEN 1 AND 730)),
        CONSTRAINT ck_beauty_campaigns_shared_coupon CHECK ((coupon_mode = 'shared') = (shared_coupon_id IS NOT NULL)),
        CONSTRAINT ck_beauty_campaigns_status CHECK (status IN ('draft', 'scheduled', 'sending', 'done', 'cancelled')),
        CONSTRAINT ck_beauty_campaigns_type CHECK (type IN ('promo', 'news')),
        CONSTRAINT ck_beauty_campaigns_type_promotion CHECK ((type <> 'news' OR (promotion_id IS NULL AND coupon_mode = 'none')) AND (type <> 'promo' OR promotion_id IS NOT NULL)),
        CONSTRAINT fk_beauty_campaigns_beauty_locations_tenant_id_location_id FOREIGN KEY (tenant_id, location_id) REFERENCES beauty_locations (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_campaigns_beauty_promotions_tenant_id_promotion_id FOREIGN KEY (tenant_id, promotion_id) REFERENCES beauty_promotions (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_campaigns_beauty_services_tenant_id_segment_service_ FOREIGN KEY (tenant_id, segment_service_id) REFERENCES beauty_services (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_campaigns_users_tenant_id_cancelled_by_user_id FOREIGN KEY (tenant_id, cancelled_by_user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_campaigns_users_tenant_id_confirmed_by_user_id FOREIGN KEY (tenant_id, confirmed_by_user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_campaigns_users_tenant_id_created_by_user_id FOREIGN KEY (tenant_id, created_by_user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE TABLE beauty_coupons (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        kind character varying(8) NOT NULL,
        promotion_id uuid NOT NULL,
        campaign_id uuid,
        client_id uuid,
        code_hash character(64) NOT NULL,
        code character varying(20),
        code_hint character varying(4) NOT NULL,
        valid_from timestamp with time zone NOT NULL,
        valid_to timestamp with time zone NOT NULL,
        max_uses integer,
        uses_count integer NOT NULL DEFAULT 0,
        status character varying(8) NOT NULL DEFAULT 'active',
        created_by_user_id uuid,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_coupons PRIMARY KEY (id),
        CONSTRAINT ak_coupons_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_coupons_code_hash CHECK (code_hash ~ '^[0-9a-f]{64}$'),
        CONSTRAINT ck_beauty_coupons_kind CHECK (kind IN ('personal', 'shared')),
        CONSTRAINT ck_beauty_coupons_kind_fields CHECK ((kind <> 'personal' OR (client_id IS NOT NULL AND campaign_id IS NOT NULL AND code IS NULL AND max_uses IS NOT NULL AND max_uses = 1)) AND (kind <> 'shared' OR (client_id IS NULL AND code IS NOT NULL))),
        CONSTRAINT ck_beauty_coupons_max_uses CHECK (max_uses IS NULL OR max_uses BETWEEN 1 AND 100000),
        CONSTRAINT ck_beauty_coupons_period CHECK (valid_to > valid_from),
        CONSTRAINT ck_beauty_coupons_status CHECK (status IN ('active', 'revoked')),
        CONSTRAINT ck_beauty_coupons_uses CHECK (uses_count >= 0 AND (max_uses IS NULL OR uses_count <= max_uses)),
        CONSTRAINT fk_beauty_coupons_beauty_campaigns_tenant_id_campaign_id FOREIGN KEY (tenant_id, campaign_id) REFERENCES beauty_campaigns (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_coupons_beauty_clients_tenant_id_client_id FOREIGN KEY (tenant_id, client_id) REFERENCES beauty_clients (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_coupons_beauty_promotions_tenant_id_promotion_id FOREIGN KEY (tenant_id, promotion_id) REFERENCES beauty_promotions (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_coupons_users_tenant_id_created_by_user_id FOREIGN KEY (tenant_id, created_by_user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE TABLE beauty_marketing_consent_events (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        tenant_id uuid NOT NULL,
        client_id uuid NOT NULL,
        channel character varying(16) NOT NULL,
        level_from character varying(16),
        level_to character varying(16) NOT NULL,
        source character varying(16) NOT NULL,
        via character varying(16),
        text_version character varying(32),
        actor_user_id uuid,
        basis character varying(16),
        basis_note character varying(200),
        campaign_id uuid,
        conversation_id uuid,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_marketing_consent_events PRIMARY KEY (id),
        CONSTRAINT ak_beauty_marketing_consent_events_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_marketing_consent_events_basis CHECK (basis IS NULL OR basis IN ('written', 'in_person', 'other')),
        CONSTRAINT ck_beauty_marketing_consent_events_basis_admin CHECK (basis IS NULL OR source = 'admin'),
        CONSTRAINT ck_beauty_marketing_consent_events_basis_note CHECK (basis IS NULL OR basis_note IS NOT NULL),
        CONSTRAINT ck_beauty_marketing_consent_events_changed CHECK (level_from IS DISTINCT FROM level_to),
        CONSTRAINT ck_beauty_marketing_consent_events_channel CHECK (channel IN ('telegram', 'push')),
        CONSTRAINT ck_beauty_marketing_consent_events_level_from CHECK (level_from IS NULL OR level_from IN ('full', 'selective', 'none')),
        CONSTRAINT ck_beauty_marketing_consent_events_level_to CHECK (level_to IN ('full', 'selective', 'none')),
        CONSTRAINT ck_beauty_marketing_consent_events_source CHECK (source IN ('chat', 'admin', 'stop')),
        CONSTRAINT ck_beauty_marketing_consent_events_via CHECK (via IS NULL OR via IN ('button', 'command', 'text', 'api')),
        CONSTRAINT fk_beauty_marketing_consent_events_beauty_clients_tenant_id_cl FOREIGN KEY (tenant_id, client_id) REFERENCES beauty_clients (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_marketing_consent_events_beauty_conversations_tenant FOREIGN KEY (tenant_id, conversation_id) REFERENCES beauty_conversations (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_marketing_consent_events_campaigns_tenant_id_campaig FOREIGN KEY (tenant_id, campaign_id) REFERENCES beauty_campaigns (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_marketing_consent_events_users_tenant_id_actor_user_ FOREIGN KEY (tenant_id, actor_user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE TABLE beauty_coupon_redemptions (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        tenant_id uuid NOT NULL,
        coupon_id uuid NOT NULL,
        appointment_id uuid NOT NULL,
        client_id uuid NOT NULL,
        discount_amount numeric(12,2) NOT NULL,
        status character varying(8) NOT NULL DEFAULT 'active',
        release_reason character varying(16),
        released_at timestamp with time zone,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_coupon_redemptions PRIMARY KEY (id),
        CONSTRAINT ak_beauty_coupon_redemptions_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_coupon_redemptions_amount CHECK (discount_amount >= 0),
        CONSTRAINT ck_beauty_coupon_redemptions_reason CHECK (release_reason IS NULL OR release_reason IN ('cancelled', 'payment_failed')),
        CONSTRAINT ck_beauty_coupon_redemptions_released CHECK ((status = 'released') = (released_at IS NOT NULL AND release_reason IS NOT NULL)),
        CONSTRAINT ck_beauty_coupon_redemptions_released_pair CHECK ((released_at IS NULL) = (release_reason IS NULL)),
        CONSTRAINT ck_beauty_coupon_redemptions_status CHECK (status IN ('active', 'released')),
        CONSTRAINT fk_beauty_coupon_redemptions_beauty_appointments_tenant_id_app FOREIGN KEY (tenant_id, appointment_id) REFERENCES beauty_appointments (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_coupon_redemptions_beauty_clients_tenant_id_client_id FOREIGN KEY (tenant_id, client_id) REFERENCES beauty_clients (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_coupon_redemptions_beauty_coupons_tenant_id_coupon_id FOREIGN KEY (tenant_id, coupon_id) REFERENCES beauty_coupons (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_promotions ADD CONSTRAINT ck_beauty_promotions_access CHECK (access IN ('public', 'code_only'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE INDEX ix_beauty_conversations_tenant_id_offer_campaign_id ON beauty_conversations (tenant_id, offer_campaign_id) WHERE offer_campaign_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE INDEX ix_beauty_conversations_tenant_id_offer_coupon_id ON beauty_conversations (tenant_id, offer_coupon_id) WHERE offer_coupon_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_conversations ADD CONSTRAINT ck_beauty_conversations_offer_until_null CHECK (offer_until IS NOT NULL OR (offer_campaign_id IS NULL AND offer_coupon_id IS NULL));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_conversations ADD CONSTRAINT ck_beauty_conversations_offer_until_set CHECK (offer_until IS NULL OR offer_campaign_id IS NOT NULL OR offer_coupon_id IS NOT NULL);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE INDEX ix_beauty_chat_keyboards_tenant_id_campaign_recipient_id ON beauty_chat_keyboards (tenant_id, campaign_recipient_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_chat_keyboards ADD CONSTRAINT ck_beauty_chat_keyboards_campaign_recipient CHECK (kind <> 'campaign_offer' OR campaign_recipient_id IS NOT NULL);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_chat_keyboards ADD CONSTRAINT ck_beauty_chat_keyboards_kind CHECK (kind IN ('booking_confirm', 'slot_pick', 'reminder_choice', 'appointment_list', 'appointment_actions', 'cancel_confirm', 'marketing_consent', 'campaign_offer'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE INDEX ix_beauty_chat_booking_proposals_tenant_id_campaign_id ON beauty_chat_booking_proposals (tenant_id, campaign_id) WHERE campaign_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE INDEX ix_beauty_chat_booking_proposals_tenant_id_coupon_id ON beauty_chat_booking_proposals (tenant_id, coupon_id) WHERE coupon_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE INDEX ix_beauty_appointments_tenant_id_campaign_id ON beauty_appointments (tenant_id, campaign_id) WHERE campaign_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE INDEX ix_beauty_appointments_tenant_id_coupon_id ON beauty_appointments (tenant_id, coupon_id) WHERE coupon_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE INDEX ix_beauty_campaign_recipients_tenant_id_campaign_id_status ON beauty_campaign_recipients (tenant_id, campaign_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE INDEX ix_beauty_campaign_recipients_tenant_id_client_id_queued_at ON beauty_campaign_recipients (tenant_id, client_id, queued_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE INDEX ix_beauty_campaign_recipients_tenant_id_conversation_id ON beauty_campaign_recipients (tenant_id, conversation_id) WHERE conversation_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE INDEX ix_beauty_campaign_recipients_tenant_id_coupon_id ON beauty_campaign_recipients (tenant_id, coupon_id) WHERE coupon_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE INDEX ix_beauty_campaign_recipients_tenant_id_message_id ON beauty_campaign_recipients (tenant_id, message_id) WHERE message_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE UNIQUE INDEX ux_beauty_campaign_recipients_campaign_client ON beauty_campaign_recipients (tenant_id, campaign_id, client_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE INDEX ix_beauty_campaigns_tenant_id_cancelled_by_user_id ON beauty_campaigns (tenant_id, cancelled_by_user_id) WHERE cancelled_by_user_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE INDEX ix_beauty_campaigns_tenant_id_confirmed_by_user_id ON beauty_campaigns (tenant_id, confirmed_by_user_id) WHERE confirmed_by_user_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE INDEX ix_beauty_campaigns_tenant_id_created_by_user_id ON beauty_campaigns (tenant_id, created_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE INDEX ix_beauty_campaigns_tenant_id_location_id ON beauty_campaigns (tenant_id, location_id) WHERE location_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE INDEX ix_beauty_campaigns_tenant_id_promotion_id ON beauty_campaigns (tenant_id, promotion_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE INDEX ix_beauty_campaigns_tenant_id_segment_service_id ON beauty_campaigns (tenant_id, segment_service_id) WHERE segment_service_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE INDEX ix_beauty_campaigns_tenant_id_shared_coupon_id ON beauty_campaigns (tenant_id, shared_coupon_id) WHERE shared_coupon_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE INDEX ix_beauty_campaigns_tenant_id_status_scheduled_at ON beauty_campaigns (tenant_id, status, scheduled_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE INDEX ix_beauty_coupon_redemptions_tenant_id_client_id ON beauty_coupon_redemptions (tenant_id, client_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE INDEX ix_beauty_coupon_redemptions_tenant_id_coupon_id ON beauty_coupon_redemptions (tenant_id, coupon_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE UNIQUE INDEX ux_beauty_coupon_redemptions_active ON beauty_coupon_redemptions (tenant_id, coupon_id, client_id) WHERE status = 'active';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE UNIQUE INDEX ux_beauty_coupon_redemptions_appointment ON beauty_coupon_redemptions (tenant_id, appointment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE INDEX ix_beauty_coupons_tenant_id_campaign_id ON beauty_coupons (tenant_id, campaign_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE INDEX ix_beauty_coupons_tenant_id_client_id ON beauty_coupons (tenant_id, client_id) WHERE client_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE INDEX ix_beauty_coupons_tenant_id_created_by_user_id ON beauty_coupons (tenant_id, created_by_user_id) WHERE created_by_user_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE INDEX ix_beauty_coupons_tenant_id_promotion_id ON beauty_coupons (tenant_id, promotion_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE UNIQUE INDEX ux_beauty_coupons_code_hash ON beauty_coupons (tenant_id, code_hash);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE INDEX ix_beauty_marketing_consent_events_tenant_id_actor_user_id ON beauty_marketing_consent_events (tenant_id, actor_user_id) WHERE actor_user_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE INDEX ix_beauty_marketing_consent_events_tenant_id_campaign_id_creat ON beauty_marketing_consent_events (tenant_id, campaign_id, created_at) WHERE campaign_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE INDEX ix_beauty_marketing_consent_events_tenant_id_client_id_created ON beauty_marketing_consent_events (tenant_id, client_id, created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE INDEX ix_beauty_marketing_consent_events_tenant_id_conversation_id ON beauty_marketing_consent_events (tenant_id, conversation_id) WHERE conversation_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE INDEX ix_beauty_marketing_consents_tenant_id_conversation_id ON beauty_marketing_consents (tenant_id, conversation_id) WHERE conversation_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    CREATE INDEX ix_beauty_marketing_consents_tenant_id_updated_by_user_id ON beauty_marketing_consents (tenant_id, updated_by_user_id) WHERE updated_by_user_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_appointments ADD CONSTRAINT fk_beauty_appointments_campaigns_tenant_id_campaign_id FOREIGN KEY (tenant_id, campaign_id) REFERENCES beauty_campaigns (tenant_id, id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_appointments ADD CONSTRAINT fk_beauty_appointments_coupons_tenant_id_coupon_id FOREIGN KEY (tenant_id, coupon_id) REFERENCES beauty_coupons (tenant_id, id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_chat_booking_proposals ADD CONSTRAINT fk_beauty_chat_booking_proposals_campaigns_tenant_id_campaign_ FOREIGN KEY (tenant_id, campaign_id) REFERENCES beauty_campaigns (tenant_id, id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_chat_booking_proposals ADD CONSTRAINT fk_beauty_chat_booking_proposals_coupons_tenant_id_coupon_id FOREIGN KEY (tenant_id, coupon_id) REFERENCES beauty_coupons (tenant_id, id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_chat_keyboards ADD CONSTRAINT fk_beauty_chat_keyboards_campaign_recipients_tenant_id_campaig FOREIGN KEY (tenant_id, campaign_recipient_id) REFERENCES beauty_campaign_recipients (tenant_id, id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_conversations ADD CONSTRAINT fk_beauty_conversations_campaigns_tenant_id_offer_campaign_id FOREIGN KEY (tenant_id, offer_campaign_id) REFERENCES beauty_campaigns (tenant_id, id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_conversations ADD CONSTRAINT fk_beauty_conversations_coupons_tenant_id_offer_coupon_id FOREIGN KEY (tenant_id, offer_coupon_id) REFERENCES beauty_coupons (tenant_id, id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_campaign_recipients ADD CONSTRAINT fk_beauty_campaign_recipients_beauty_campaigns_tenant_id_campa FOREIGN KEY (tenant_id, campaign_id) REFERENCES beauty_campaigns (tenant_id, id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_campaign_recipients ADD CONSTRAINT fk_beauty_campaign_recipients_coupons_tenant_id_coupon_id FOREIGN KEY (tenant_id, coupon_id) REFERENCES beauty_coupons (tenant_id, id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_campaigns ADD CONSTRAINT fk_beauty_campaigns_coupons_tenant_id_shared_coupon_id FOREIGN KEY (tenant_id, shared_coupon_id) REFERENCES beauty_coupons (tenant_id, id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_clients NO FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    UPDATE beauty_clients SET marketing_consent = false WHERE marketing_consent;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_clients FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_marketing_consents ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_marketing_consents FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN

                    CREATE POLICY tenant_isolation ON beauty_marketing_consents
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_marketing_consent_events ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_marketing_consent_events FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN

                    CREATE POLICY tenant_isolation ON beauty_marketing_consent_events
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_campaigns ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_campaigns FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN

                    CREATE POLICY tenant_isolation ON beauty_campaigns
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_campaign_recipients ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_campaign_recipients FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN

                    CREATE POLICY tenant_isolation ON beauty_campaign_recipients
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_coupons ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_coupons FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN

                    CREATE POLICY tenant_isolation ON beauty_coupons
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_coupon_redemptions ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    ALTER TABLE beauty_coupon_redemptions FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN

                    CREATE POLICY tenant_isolation ON beauty_coupon_redemptions
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN

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
    $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261026203521_beauty_marketing') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20261026203521_beauty_marketing', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN
    ALTER TABLE users ALTER COLUMN password_hash DROP NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN
    ALTER TABLE users ADD state character varying(16) NOT NULL DEFAULT 'active';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN
    ALTER TABLE tenants ADD external_ref character varying(64);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN
    ALTER TABLE tenants ADD external_source character varying(16);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN
    ALTER TABLE tenants ADD suspended_at timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN
    ALTER TABLE tenants ADD suspended_reason character varying(500);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN
    CREATE TABLE beauty_platform_feedback (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        user_id uuid NOT NULL,
        type character varying(16) NOT NULL,
        severity character varying(8) NOT NULL DEFAULT 'normal',
        message character varying(2000) NOT NULL,
        page_url character varying(500),
        delivery_status character varying(16) NOT NULL DEFAULT 'pending',
        attempts integer NOT NULL DEFAULT 0,
        next_attempt_at timestamp with time zone NOT NULL DEFAULT (now()),
        last_error character varying(200),
        delivered_at timestamp with time zone,
        external_lead_id character varying(64),
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_platform_feedback PRIMARY KEY (id),
        CONSTRAINT ak_beauty_platform_feedback_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_platform_feedback_attempts CHECK (attempts >= 0),
        CONSTRAINT ck_beauty_platform_feedback_delivered CHECK ((delivery_status = 'delivered') = (delivered_at IS NOT NULL)),
        CONSTRAINT ck_beauty_platform_feedback_delivery_status CHECK (delivery_status IN ('pending', 'delivered', 'failed')),
        CONSTRAINT ck_beauty_platform_feedback_lead CHECK (external_lead_id IS NULL OR delivery_status = 'delivered'),
        CONSTRAINT ck_beauty_platform_feedback_message CHECK (char_length(message) BETWEEN 10 AND 2000),
        CONSTRAINT ck_beauty_platform_feedback_severity CHECK (severity IN ('low', 'normal', 'high', 'critical')),
        CONSTRAINT ck_beauty_platform_feedback_type CHECK (type IN ('cooperation', 'improvement', 'bug')),
        CONSTRAINT fk_beauty_platform_feedback_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_platform_feedback_users_tenant_id_user_id FOREIGN KEY (tenant_id, user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN
    CREATE TABLE control_plane_nonces (
        key_id character varying(32) NOT NULL,
        nonce character varying(64) NOT NULL,
        seen_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_control_plane_nonces PRIMARY KEY (key_id, nonce),
        CONSTRAINT ck_control_plane_nonces_key_id CHECK (key_id ~ '^[a-z0-9-]{3,32}$'),
        CONSTRAINT ck_control_plane_nonces_nonce CHECK (nonce ~ '^[A-Za-z0-9_-]{22,64}$')
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN
    CREATE TABLE platform_audit_log (
        id bigint GENERATED ALWAYS AS IDENTITY,
        occurred_at timestamp with time zone NOT NULL DEFAULT (now()),
        action character varying(40) NOT NULL,
        tenant_id uuid,
        actor_type character varying(20) NOT NULL,
        actor_id character varying(64),
        actor_display character varying(120),
        key_id character varying(32),
        request_id character varying(64),
        ip inet,
        details jsonb NOT NULL DEFAULT ('{}'::jsonb),
        CONSTRAINT pk_platform_audit_log PRIMARY KEY (id),
        CONSTRAINT ck_platform_audit_log_action CHECK (action IN ('tenant.created', 'tenant.create_replayed', 'tenant.suspended', 'tenant.resumed', 'tenant.modules_changed', 'owner_invite.issued', 'owner_invite.revoked', 'owner_invite.accepted', 'control_auth.failed')),
        CONSTRAINT ck_platform_audit_log_actor_type CHECK (actor_type IN ('shelfguard_admin', 'platform_key', 'owner', 'system')),
        CONSTRAINT ck_platform_audit_log_details CHECK (jsonb_typeof(details) = 'object')
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN
    CREATE TABLE platform_owner_invites (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        tenant_id uuid NOT NULL,
        user_id uuid NOT NULL,
        token_hash bytea NOT NULL,
        expires_at timestamp with time zone NOT NULL,
        used_at timestamp with time zone,
        revoked_at timestamp with time zone,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        created_by_actor character varying(130) NOT NULL,
        CONSTRAINT pk_platform_owner_invites PRIMARY KEY (id),
        CONSTRAINT ck_platform_owner_invites_expires CHECK (expires_at > created_at),
        CONSTRAINT ck_platform_owner_invites_token_hash CHECK (octet_length(token_hash) = 32),
        CONSTRAINT ck_platform_owner_invites_used_or_revoked CHECK (used_at IS NULL OR revoked_at IS NULL),
        CONSTRAINT fk_platform_owner_invites_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT,
        CONSTRAINT fk_platform_owner_invites_users_tenant_id_user_id FOREIGN KEY (tenant_id, user_id) REFERENCES users (tenant_id, id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN
    ALTER TABLE users ADD CONSTRAINT ck_users_password_or_invited CHECK (password_hash IS NOT NULL OR state = 'invited');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN
    ALTER TABLE users ADD CONSTRAINT ck_users_state CHECK (state IN ('active', 'invited'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN
    CREATE UNIQUE INDEX ux_tenants_external_ref ON tenants (external_source, external_ref) WHERE external_ref IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN
    ALTER TABLE tenants ADD CONSTRAINT ck_tenants_external_pair CHECK ((external_ref IS NULL) = (external_source IS NULL));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN
    ALTER TABLE tenants ADD CONSTRAINT ck_tenants_external_ref CHECK (external_ref IS NULL OR external_ref ~ '^[A-Za-z0-9_-]{1,64}$');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN
    ALTER TABLE tenants ADD CONSTRAINT ck_tenants_external_source CHECK (external_source IS NULL OR external_source IN ('shelfguard'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN
    ALTER TABLE tenants ADD CONSTRAINT ck_tenants_suspended CHECK (status = 'suspended' OR (suspended_at IS NULL AND suspended_reason IS NULL));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN
    CREATE INDEX ix_beauty_platform_feedback_pending ON beauty_platform_feedback (tenant_id, next_attempt_at) WHERE delivery_status = 'pending';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN
    CREATE INDEX ix_beauty_platform_feedback_tenant_id_user_id_created_at ON beauty_platform_feedback (tenant_id, user_id, created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN
    CREATE INDEX ix_control_plane_nonces_seen_at ON control_plane_nonces (seen_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN
    CREATE INDEX ix_platform_audit_log_occurred_at ON platform_audit_log (occurred_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN
    CREATE INDEX ix_platform_audit_log_tenant_id_occurred_at ON platform_audit_log (tenant_id, occurred_at DESC) WHERE tenant_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN
    CREATE INDEX ix_platform_owner_invites_tenant_id_user_id ON platform_owner_invites (tenant_id, user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN
    CREATE UNIQUE INDEX ix_platform_owner_invites_token_hash ON platform_owner_invites (token_hash);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN
    CREATE UNIQUE INDEX ux_platform_owner_invites_active_user ON platform_owner_invites (user_id) WHERE used_at IS NULL AND revoked_at IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN
    ALTER TABLE beauty_platform_feedback ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN
    ALTER TABLE beauty_platform_feedback FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN

                    CREATE POLICY tenant_isolation ON beauty_platform_feedback
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN

                    CREATE POLICY tenants_control_lookup ON tenants
                        FOR SELECT
                        USING (external_source = 'shelfguard'
                               AND external_ref = NULLIF(current_setting('app.tenant_external_ref', true), ''));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN

                    CREATE FUNCTION platform_audit_log_block_mutation() RETURNS trigger
                    LANGUAGE plpgsql AS $fn$
                    BEGIN
                        RAISE EXCEPTION 'platform_audit_log is append-only' USING ERRCODE = '42501';
                    END
                    $fn$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN

                    CREATE TRIGGER platform_audit_log_append_only
                        BEFORE UPDATE OR DELETE ON platform_audit_log
                        FOR EACH ROW EXECUTE FUNCTION platform_audit_log_block_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN

                    CREATE TRIGGER platform_audit_log_no_truncate
                        BEFORE TRUNCATE ON platform_audit_log
                        FOR EACH STATEMENT EXECUTE FUNCTION platform_audit_log_block_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN

    DO $$
    DECLARE
        t text;
    BEGIN
        FOREACH t IN ARRAY ARRAY['beauty_platform_feedback', 'users', 'tenants']
        LOOP
            IF NOT EXISTS (
                SELECT 1 FROM pg_class c
                WHERE c.oid = to_regclass('public.' || t) AND c.relrowsecurity AND c.relforcerowsecurity) THEN
                RAISE EXCEPTION 'FORCE ROW LEVEL SECURITY is not enabled on %', t;
            END IF;
        END LOOP;
        IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE tablename = 'beauty_platform_feedback' AND policyname = 'tenant_isolation') THEN
            RAISE EXCEPTION 'tenant_isolation policy is missing on beauty_platform_feedback';
        END IF;
        IF (SELECT count(*) FROM pg_trigger WHERE tgrelid = to_regclass('public.platform_audit_log') AND NOT tgisinternal) <> 2 THEN
            RAISE EXCEPTION 'append-only triggers are missing on platform_audit_log';
        END IF;
    END
    $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261104182836_platform_control_plane') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20261104182836_platform_control_plane', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107134706_beauty_chat_booking_flow') THEN
    ALTER TABLE beauty_messages DROP CONSTRAINT ck_beauty_messages_kind;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107134706_beauty_chat_booking_flow') THEN
    ALTER TABLE beauty_chat_keyboards DROP CONSTRAINT ck_beauty_chat_keyboards_kind;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107134706_beauty_chat_booking_flow') THEN
    ALTER TABLE beauty_locations ADD latitude numeric(9,6);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107134706_beauty_chat_booking_flow') THEN
    ALTER TABLE beauty_locations ADD longitude numeric(9,6);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107134706_beauty_chat_booking_flow') THEN
    ALTER TABLE beauty_chat_keyboards ADD flow_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107134706_beauty_chat_booking_flow') THEN
    ALTER TABLE beauty_chat_keyboards ADD flow_version integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107134706_beauty_chat_booking_flow') THEN
    ALTER TABLE beauty_chat_booking_proposals ADD flow_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107134706_beauty_chat_booking_flow') THEN
    CREATE TABLE beauty_chat_booking_flows (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        conversation_id uuid NOT NULL,
        status character varying(16) NOT NULL DEFAULT 'active',
        step character varying(16) NOT NULL,
        version integer NOT NULL DEFAULT 0,
        service_id uuid,
        specialist_id uuid,
        location_id uuid,
        suggested_last_location_id uuid,
        day date,
        preferred_time time without time zone,
        cursor_at timestamp with time zone,
        list_cursor character varying(64),
        skipped_steps character varying(16)[] NOT NULL DEFAULT ('{}'),
        proposal_id uuid,
        origin character varying(16) NOT NULL DEFAULT 'button',
        cancel_reason character varying(32),
        expires_at timestamp with time zone NOT NULL,
        finished_at timestamp with time zone,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_chat_booking_flows PRIMARY KEY (id),
        CONSTRAINT ak_chat_booking_flows_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_chat_booking_flows_cancel CHECK ((status = 'cancelled') = (cancel_reason IS NOT NULL)),
        CONSTRAINT ck_beauty_chat_booking_flows_cancel_reason CHECK (cancel_reason IS NULL OR cancel_reason IN ('client', 'declined', 'handoff', 'no_services')),
        CONSTRAINT ck_beauty_chat_booking_flows_finished CHECK ((status = 'active') = (finished_at IS NULL)),
        CONSTRAINT ck_beauty_chat_booking_flows_origin CHECK (origin IN ('button', 'command', 'llm', 'text')),
        CONSTRAINT ck_beauty_chat_booking_flows_skipped_steps CHECK (skipped_steps <@ ARRAY['last_location','location','specialist']::varchar[]),
        CONSTRAINT ck_beauty_chat_booking_flows_status CHECK (status IN ('active', 'completed', 'cancelled', 'expired', 'superseded')),
        CONSTRAINT ck_beauty_chat_booking_flows_step CHECK (step IN ('last_location', 'location', 'service', 'specialist', 'time', 'day', 'confirm')),
        CONSTRAINT ck_beauty_chat_booking_flows_step_confirm CHECK (step <> 'confirm' OR proposal_id IS NOT NULL),
        CONSTRAINT ck_beauty_chat_booking_flows_step_last_location CHECK (step <> 'last_location' OR suggested_last_location_id IS NOT NULL),
        CONSTRAINT ck_beauty_chat_booking_flows_step_location CHECK (step NOT IN ('service', 'specialist', 'time', 'day', 'confirm') OR location_id IS NOT NULL),
        CONSTRAINT ck_beauty_chat_booking_flows_step_service CHECK (step NOT IN ('specialist', 'time', 'day', 'confirm') OR service_id IS NOT NULL),
        CONSTRAINT ck_beauty_chat_booking_flows_step_specialist CHECK (step NOT IN ('time', 'day', 'confirm') OR specialist_id IS NOT NULL),
        CONSTRAINT ck_beauty_chat_booking_flows_version CHECK (version >= 0),
        CONSTRAINT fk_beauty_chat_booking_flows_beauty_chat_booking_proposals_ten FOREIGN KEY (tenant_id, proposal_id) REFERENCES beauty_chat_booking_proposals (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_chat_booking_flows_beauty_conversations_tenant_id_co FOREIGN KEY (tenant_id, conversation_id) REFERENCES beauty_conversations (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_chat_booking_flows_beauty_locations_tenant_id_locati FOREIGN KEY (tenant_id, location_id) REFERENCES beauty_locations (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_chat_booking_flows_beauty_locations_tenant_id_sugges FOREIGN KEY (tenant_id, suggested_last_location_id) REFERENCES beauty_locations (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_chat_booking_flows_beauty_services_tenant_id_service FOREIGN KEY (tenant_id, service_id) REFERENCES beauty_services (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_chat_booking_flows_beauty_specialists_tenant_id_spec FOREIGN KEY (tenant_id, specialist_id) REFERENCES beauty_specialists (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107134706_beauty_chat_booking_flow') THEN
    ALTER TABLE beauty_messages ADD CONSTRAINT ck_beauty_messages_kind CHECK (kind IN ('text', 'command', 'unsupported', 'callback', 'contact', 'location'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107134706_beauty_chat_booking_flow') THEN
    ALTER TABLE beauty_locations ADD CONSTRAINT ck_beauty_locations_coordinates CHECK ((latitude IS NULL) = (longitude IS NULL) AND latitude BETWEEN -90 AND 90 AND longitude BETWEEN -180 AND 180);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107134706_beauty_chat_booking_flow') THEN
    CREATE INDEX ix_beauty_chat_keyboards_tenant_id_flow_id ON beauty_chat_keyboards (tenant_id, flow_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107134706_beauty_chat_booking_flow') THEN
    ALTER TABLE beauty_chat_keyboards ADD CONSTRAINT ck_beauty_chat_keyboards_flow_link CHECK (kind NOT LIKE 'flow\_%' OR (flow_version IS NOT NULL AND (flow_id IS NOT NULL OR status <> 'active')));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107134706_beauty_chat_booking_flow') THEN
    ALTER TABLE beauty_chat_keyboards ADD CONSTRAINT ck_beauty_chat_keyboards_flow_none CHECK (kind LIKE 'flow\_%' OR (flow_id IS NULL AND flow_version IS NULL));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107134706_beauty_chat_booking_flow') THEN
    ALTER TABLE beauty_chat_keyboards ADD CONSTRAINT ck_beauty_chat_keyboards_kind CHECK (kind IN ('booking_confirm', 'slot_pick', 'reminder_choice', 'appointment_list', 'appointment_actions', 'cancel_confirm', 'marketing_consent', 'campaign_offer', 'flow_last_location', 'flow_location', 'flow_service', 'flow_specialist', 'flow_time', 'flow_day'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107134706_beauty_chat_booking_flow') THEN
    CREATE INDEX ix_beauty_chat_booking_proposals_tenant_id_flow_id ON beauty_chat_booking_proposals (tenant_id, flow_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107134706_beauty_chat_booking_flow') THEN
    CREATE INDEX ix_beauty_chat_booking_flows_retention ON beauty_chat_booking_flows (tenant_id, finished_at) WHERE status <> 'active';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107134706_beauty_chat_booking_flow') THEN
    CREATE INDEX ix_beauty_chat_booking_flows_sweeper ON beauty_chat_booking_flows (tenant_id, expires_at) WHERE status = 'active';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107134706_beauty_chat_booking_flow') THEN
    CREATE INDEX ix_beauty_chat_booking_flows_tenant_id_conversation_id_created ON beauty_chat_booking_flows (tenant_id, conversation_id, created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107134706_beauty_chat_booking_flow') THEN
    CREATE INDEX ix_beauty_chat_booking_flows_tenant_id_location_id ON beauty_chat_booking_flows (tenant_id, location_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107134706_beauty_chat_booking_flow') THEN
    CREATE INDEX ix_beauty_chat_booking_flows_tenant_id_proposal_id ON beauty_chat_booking_flows (tenant_id, proposal_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107134706_beauty_chat_booking_flow') THEN
    CREATE INDEX ix_beauty_chat_booking_flows_tenant_id_service_id ON beauty_chat_booking_flows (tenant_id, service_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107134706_beauty_chat_booking_flow') THEN
    CREATE INDEX ix_beauty_chat_booking_flows_tenant_id_specialist_id ON beauty_chat_booking_flows (tenant_id, specialist_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107134706_beauty_chat_booking_flow') THEN
    CREATE INDEX ix_beauty_chat_booking_flows_tenant_id_suggested_last_location ON beauty_chat_booking_flows (tenant_id, suggested_last_location_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107134706_beauty_chat_booking_flow') THEN
    CREATE UNIQUE INDEX ux_beauty_chat_booking_flows_active ON beauty_chat_booking_flows (tenant_id, conversation_id) WHERE status = 'active';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107134706_beauty_chat_booking_flow') THEN

    ALTER TABLE beauty_chat_booking_proposals
        ADD CONSTRAINT fk_beauty_chat_booking_proposals_chat_booking_flows_tenant_id_
        FOREIGN KEY (tenant_id, flow_id) REFERENCES beauty_chat_booking_flows (tenant_id, id) ON DELETE SET NULL (flow_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107134706_beauty_chat_booking_flow') THEN

    ALTER TABLE beauty_chat_keyboards
        ADD CONSTRAINT fk_beauty_chat_keyboards_chat_booking_flows_tenant_id_flow_id
        FOREIGN KEY (tenant_id, flow_id) REFERENCES beauty_chat_booking_flows (tenant_id, id) ON DELETE SET NULL (flow_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107134706_beauty_chat_booking_flow') THEN
    ALTER TABLE beauty_chat_booking_flows ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107134706_beauty_chat_booking_flow') THEN
    ALTER TABLE beauty_chat_booking_flows FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107134706_beauty_chat_booking_flow') THEN

                    CREATE POLICY tenant_isolation ON beauty_chat_booking_flows
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107134706_beauty_chat_booking_flow') THEN

    DO $$
    DECLARE
        t text;
    BEGIN
        FOREACH t IN ARRAY ARRAY['beauty_chat_booking_flows', 'beauty_chat_keyboards', 'beauty_messages', 'beauty_locations', 'beauty_chat_booking_proposals']
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
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107134706_beauty_chat_booking_flow') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20261107134706_beauty_chat_booking_flow', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107145615_beauty_chat_booking_flow_step_checks') THEN
    ALTER TABLE beauty_chat_booking_flows DROP CONSTRAINT ck_beauty_chat_booking_flows_step_location;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107145615_beauty_chat_booking_flow_step_checks') THEN
    ALTER TABLE beauty_chat_booking_flows DROP CONSTRAINT ck_beauty_chat_booking_flows_step_service;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107145615_beauty_chat_booking_flow_step_checks') THEN
    ALTER TABLE beauty_chat_booking_flows DROP CONSTRAINT ck_beauty_chat_booking_flows_step_specialist;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107145615_beauty_chat_booking_flow_step_checks') THEN
    ALTER TABLE beauty_chat_booking_flows ADD CONSTRAINT ck_beauty_chat_booking_flows_step_location CHECK (step NOT IN ('time', 'day', 'confirm') OR location_id IS NOT NULL);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107145615_beauty_chat_booking_flow_step_checks') THEN
    ALTER TABLE beauty_chat_booking_flows ADD CONSTRAINT ck_beauty_chat_booking_flows_step_service CHECK (step NOT IN ('specialist', 'last_location', 'location', 'time', 'day', 'confirm') OR service_id IS NOT NULL);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107145615_beauty_chat_booking_flow_step_checks') THEN
    ALTER TABLE beauty_chat_booking_flows ADD CONSTRAINT ck_beauty_chat_booking_flows_step_specialist CHECK (step NOT IN ('last_location', 'location', 'time', 'day', 'confirm') OR specialist_id IS NOT NULL);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261107145615_beauty_chat_booking_flow_step_checks') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20261107145615_beauty_chat_booking_flow_step_checks', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109075625_beauty_public_location_catalog') THEN
    CREATE OR REPLACE FUNCTION public.beauty_generate_public_code() RETURNS text
        LANGUAGE sql VOLATILE PARALLEL SAFE SET search_path = pg_catalog, pg_temp AS $fn$
        SELECT string_agg(substr('0123456789ABCDEFGHJKMNPQRSTVWXYZ', (get_byte(s.b, u.i) & 31) + 1, 1), '' ORDER BY u.n)
        FROM (SELECT decode(replace(gen_random_uuid()::text, '-', ''), 'hex') AS b) AS s,
             unnest(ARRAY[0, 1, 2, 3, 4, 5, 7, 9]) WITH ORDINALITY AS u(i, n)
    $fn$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109075625_beauty_public_location_catalog') THEN
    ALTER TABLE beauty_locations ADD city character varying(100);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109075625_beauty_public_location_catalog') THEN
    ALTER TABLE beauty_locations ADD is_discoverable boolean NOT NULL DEFAULT FALSE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109075625_beauty_public_location_catalog') THEN
    ALTER TABLE beauty_locations ADD public_code character varying(8) NOT NULL DEFAULT (public.beauty_generate_public_code());
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109075625_beauty_public_location_catalog') THEN
    CREATE INDEX ix_beauty_locations_discoverable_coords ON beauty_locations (latitude, longitude) WHERE is_discoverable AND is_active AND latitude IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109075625_beauty_public_location_catalog') THEN
    CREATE UNIQUE INDEX ux_beauty_locations_public_code ON beauty_locations (public_code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109075625_beauty_public_location_catalog') THEN
    ALTER TABLE beauty_locations ADD CONSTRAINT ck_beauty_locations_public_code CHECK (public_code ~ '^[0-9A-HJKMNP-TV-Z]{8}$');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109075625_beauty_public_location_catalog') THEN
    CREATE INDEX ix_beauty_locations_discoverable_city ON beauty_locations (lower(city))
        WHERE is_discoverable AND is_active AND city IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109075625_beauty_public_location_catalog') THEN
    DO $do$
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'beautycrm_catalog') THEN
            BEGIN
                CREATE ROLE beautycrm_catalog NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT;
            EXCEPTION
                WHEN duplicate_object THEN NULL; -- concurrent creation by another migration
                WHEN insufficient_privilege THEN
                RAISE EXCEPTION 'Role beautycrm_catalog does not exist and % cannot create it (needs CREATEROLE). Create it as ops: CREATE ROLE beautycrm_catalog NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT; GRANT beautycrm_catalog TO %;', current_user, current_user;
            END;
        END IF;

        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'beautycrm_catalog'
                   AND (rolcanlogin OR rolsuper OR rolbypassrls)) THEN
            RAISE EXCEPTION 'Role beautycrm_catalog must be NOLOGIN NOSUPERUSER NOBYPASSRLS (ADR-022)';
        END IF;

        -- The migrator must be able to hand ownership of the functions to the role (needs the SET option,
        -- PG16). The membership MUST be INHERIT FALSE: the role-scoped policies are TO beautycrm_catalog, and
        -- a policy applies to every role that inherits the privileges of that role - so an inheriting
        -- migrator would see the catalog rows directly (security review M1).
        IF NOT EXISTS (SELECT 1
                       FROM pg_auth_members m
                       JOIN pg_roles r ON r.oid = m.roleid AND r.rolname = 'beautycrm_catalog'
                       JOIN pg_roles u ON u.oid = m.member AND u.rolname = current_user
                       WHERE m.set_option AND NOT m.inherit_option) THEN
            BEGIN
                EXECUTE format('GRANT beautycrm_catalog TO %I WITH INHERIT FALSE, SET TRUE', current_user);
            EXCEPTION WHEN insufficient_privilege THEN
                RAISE EXCEPTION 'The migrator % must be a member of beautycrm_catalog WITH INHERIT FALSE, SET TRUE and cannot change it. Ops: GRANT beautycrm_catalog TO % WITH INHERIT FALSE, SET TRUE;', current_user, current_user;
            END;
        END IF;
    END
    $do$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109075625_beauty_public_location_catalog') THEN
    GRANT USAGE ON SCHEMA public TO beautycrm_catalog;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109075625_beauty_public_location_catalog') THEN
    GRANT SELECT (id, tenant_id, name, address, city, timezone, is_active,
                  public_code, latitude, longitude, is_discoverable)
        ON beauty_locations TO beautycrm_catalog;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109075625_beauty_public_location_catalog') THEN
    GRANT SELECT (id, slug, status, modules) ON tenants TO beautycrm_catalog;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109075625_beauty_public_location_catalog') THEN
    DROP POLICY IF EXISTS public_catalog_read ON beauty_locations;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109075625_beauty_public_location_catalog') THEN
    CREATE POLICY public_catalog_read ON beauty_locations
        FOR SELECT TO beautycrm_catalog
        USING (is_active);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109075625_beauty_public_location_catalog') THEN
    DROP POLICY IF EXISTS public_catalog_read ON tenants;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109075625_beauty_public_location_catalog') THEN
    CREATE POLICY public_catalog_read ON tenants
        FOR SELECT TO beautycrm_catalog
        USING (status = 'active' AND 'beauty_booking' = ANY (modules));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109075625_beauty_public_location_catalog') THEN
    CREATE OR REPLACE FUNCTION public.beauty_public_resolve_location(p_code text)
        RETURNS TABLE (public_code text, tenant_slug text, location_id uuid, name text, address text,
                       city text, timezone text)
        LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
        SELECT l.public_code::text, t.slug::text, l.id, l.name::text, l.address::text, l.city::text, l.timezone::text
        FROM public.beauty_locations AS l
        JOIN public.tenants AS t ON t.id = l.tenant_id
        WHERE l.public_code = translate(upper(regexp_replace(coalesce(p_code, ''), '[\s-]', '', 'g')), 'OIL', '011')
          AND l.is_active
          AND t.status = 'active' AND 'beauty_booking' = ANY (t.modules)
        LIMIT 1
    $fn$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109075625_beauty_public_location_catalog') THEN
    CREATE OR REPLACE FUNCTION public.beauty_public_nearby_locations(
            p_lat double precision, p_lng double precision, p_radius_km double precision, p_limit integer)
        RETURNS TABLE (public_code text, tenant_slug text, location_id uuid, name text, address text,
                       city text, timezone text, distance_km numeric)
        LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
        WITH q AS (
            -- invalid / NaN / out-of-range coordinates -> no rows; input is rounded to 3 decimals (~110 m);
            -- radius is clamped to (0, 30] km (default 30), limit to 1..10
            SELECT round(p_lat::numeric, 3)::double precision AS lat,
                   round(p_lng::numeric, 3)::double precision AS lng,
                   CASE WHEN p_radius_km IS NULL OR p_radius_km <= 0 THEN 30.0
                        ELSE least(p_radius_km, 30.0) END AS r,
                   greatest(1, least(coalesce(p_limit, 5), 10)) AS lim
            WHERE p_lat BETWEEN -90 AND 90 AND p_lng BETWEEN -180 AND 180
        ),
        b AS (
            SELECT q.lat, q.lng, q.r, q.lim,
                   q.r / 111.195 AS dlat,
                   least(180.0, q.r / (111.195 * greatest(cos(radians(q.lat)), 0.01))) AS dlng
            FROM q
        )
        SELECT l.public_code::text, t.slug::text, l.id, l.name::text, l.address::text, l.city::text, l.timezone::text,
               round(d.km::numeric, 1)
        FROM b
        JOIN public.beauty_locations AS l
          ON l.is_discoverable AND l.is_active AND l.latitude IS NOT NULL AND l.longitude IS NOT NULL
         AND l.latitude BETWEEN b.lat - b.dlat AND b.lat + b.dlat
         AND (l.longitude BETWEEN b.lng - b.dlng AND b.lng + b.dlng
              OR b.lng - b.dlng < -180 OR b.lng + b.dlng > 180)
        JOIN public.tenants AS t
          ON t.id = l.tenant_id AND t.status = 'active' AND 'beauty_booking' = ANY (t.modules)
        CROSS JOIN LATERAL (
            SELECT 2 * 6371.0 * asin(least(1.0, sqrt(
                       sin(radians(l.latitude - b.lat) / 2) ^ 2
                       + cos(radians(b.lat)) * cos(radians(l.latitude)) * sin(radians(l.longitude - b.lng) / 2) ^ 2)))
        ) AS d(km)
        WHERE d.km <= b.r
        ORDER BY d.km, l.public_code
        LIMIT (SELECT lim FROM b)
    $fn$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109075625_beauty_public_location_catalog') THEN
    CREATE OR REPLACE FUNCTION public.beauty_public_search_locations_by_city(p_city text, p_limit integer)
        RETURNS TABLE (public_code text, tenant_slug text, location_id uuid, name text, address text,
                       city text, timezone text)
        LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
        WITH q AS (
            -- exact case-insensitive match (no wildcards, so it cannot be used for prefix scraping);
            -- 2..100 characters; limit clamped to 1..10
            SELECT lower(btrim(p_city)) AS c, greatest(1, least(coalesce(p_limit, 5), 10)) AS lim
            WHERE p_city IS NOT NULL AND char_length(btrim(p_city)) BETWEEN 2 AND 100
        )
        SELECT l.public_code::text, t.slug::text, l.id, l.name::text, l.address::text, l.city::text, l.timezone::text
        FROM q
        JOIN public.beauty_locations AS l
          ON lower(l.city) = q.c AND l.is_discoverable AND l.is_active
        JOIN public.tenants AS t
          ON t.id = l.tenant_id AND t.status = 'active' AND 'beauty_booking' = ANY (t.modules)
        ORDER BY l.name, l.public_code
        LIMIT (SELECT lim FROM q)
    $fn$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109075625_beauty_public_location_catalog') THEN
    REVOKE ALL ON FUNCTION public.beauty_public_resolve_location(text) FROM PUBLIC;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109075625_beauty_public_location_catalog') THEN
    REVOKE ALL ON FUNCTION public.beauty_public_nearby_locations(double precision, double precision, double precision, integer) FROM PUBLIC;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109075625_beauty_public_location_catalog') THEN
    REVOKE ALL ON FUNCTION public.beauty_public_search_locations_by_city(text, integer) FROM PUBLIC;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109075625_beauty_public_location_catalog') THEN
    DO $do$
    BEGIN
        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'beautycrm_app') THEN
            GRANT EXECUTE ON FUNCTION public.beauty_public_resolve_location(text) TO beautycrm_app;
            GRANT EXECUTE ON FUNCTION public.beauty_public_nearby_locations(double precision, double precision, double precision, integer) TO beautycrm_app;
            GRANT EXECUTE ON FUNCTION public.beauty_public_search_locations_by_city(text, integer) TO beautycrm_app;
        ELSE
            RAISE NOTICE 'Role beautycrm_app not found: GRANT EXECUTE on beauty_public_* functions to the runtime role manually (ADR-022).';
        END IF;
    END
    $do$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109075625_beauty_public_location_catalog') THEN
    GRANT CREATE ON SCHEMA public TO beautycrm_catalog;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109075625_beauty_public_location_catalog') THEN
    ALTER FUNCTION public.beauty_public_resolve_location(text) OWNER TO beautycrm_catalog;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109075625_beauty_public_location_catalog') THEN
    ALTER FUNCTION public.beauty_public_nearby_locations(double precision, double precision, double precision, integer) OWNER TO beautycrm_catalog;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109075625_beauty_public_location_catalog') THEN
    ALTER FUNCTION public.beauty_public_search_locations_by_city(text, integer) OWNER TO beautycrm_catalog;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109075625_beauty_public_location_catalog') THEN
    REVOKE CREATE ON SCHEMA public FROM beautycrm_catalog;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109075625_beauty_public_location_catalog') THEN
    CREATE OR REPLACE FUNCTION public.beauty_forbid_public_code_change() RETURNS trigger
        LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $fn$
    BEGIN
        RAISE EXCEPTION 'beauty_locations.public_code is immutable' USING ERRCODE = 'restrict_violation';
    END
    $fn$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109075625_beauty_public_location_catalog') THEN
    CREATE TRIGGER trg_beauty_locations_public_code_immutable
        BEFORE UPDATE OF public_code ON beauty_locations
        FOR EACH ROW WHEN (OLD.public_code IS DISTINCT FROM NEW.public_code)
        EXECUTE FUNCTION public.beauty_forbid_public_code_change();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109075625_beauty_public_location_catalog') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20261109075625_beauty_public_location_catalog', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109135452_beauty_specialist_profile_reviews') THEN
    ALTER TABLE beauty_specialists ADD bio character varying(500);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109135452_beauty_specialist_profile_reviews') THEN
    ALTER TABLE beauty_specialists ADD practice_start_year integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109135452_beauty_specialist_profile_reviews') THEN
    CREATE TABLE beauty_specialist_reviews (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        tenant_id uuid NOT NULL,
        specialist_id uuid NOT NULL,
        appointment_id uuid,
        client_id uuid,
        author_name character varying(100) NOT NULL,
        rating smallint NOT NULL,
        text character varying(1000),
        is_published boolean NOT NULL DEFAULT TRUE,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_specialist_reviews PRIMARY KEY (id),
        CONSTRAINT ak_beauty_specialist_reviews_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_specialist_reviews_author_len CHECK (char_length(author_name) BETWEEN 1 AND 100),
        CONSTRAINT ck_beauty_specialist_reviews_rating CHECK (rating BETWEEN 1 AND 5),
        CONSTRAINT ck_beauty_specialist_reviews_text_len CHECK (text IS NULL OR char_length(text) <= 1000),
        CONSTRAINT fk_beauty_specialist_reviews_beauty_appointments_tenant_id_app FOREIGN KEY (tenant_id, appointment_id) REFERENCES beauty_appointments (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_specialist_reviews_beauty_clients_tenant_id_client_id FOREIGN KEY (tenant_id, client_id) REFERENCES beauty_clients (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_specialist_reviews_beauty_specialists_tenant_id_spec FOREIGN KEY (tenant_id, specialist_id) REFERENCES beauty_specialists (tenant_id, id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109135452_beauty_specialist_profile_reviews') THEN
    ALTER TABLE beauty_specialists ADD CONSTRAINT ck_beauty_specialists_bio_len CHECK (bio IS NULL OR char_length(bio) <= 500);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109135452_beauty_specialist_profile_reviews') THEN
    ALTER TABLE beauty_specialists ADD CONSTRAINT ck_beauty_specialists_practice_start_year CHECK (practice_start_year IS NULL OR practice_start_year BETWEEN 1950 AND 2100);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109135452_beauty_specialist_profile_reviews') THEN
    CREATE INDEX ix_beauty_specialist_reviews_specialist_published ON beauty_specialist_reviews (tenant_id, specialist_id, is_published, created_at DESC);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109135452_beauty_specialist_profile_reviews') THEN
    CREATE INDEX ix_beauty_specialist_reviews_tenant_id_client_id ON beauty_specialist_reviews (tenant_id, client_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109135452_beauty_specialist_profile_reviews') THEN
    CREATE UNIQUE INDEX ux_beauty_specialist_reviews_appointment ON beauty_specialist_reviews (tenant_id, appointment_id) WHERE appointment_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109135452_beauty_specialist_profile_reviews') THEN
    ALTER TABLE beauty_specialist_reviews ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109135452_beauty_specialist_profile_reviews') THEN
    ALTER TABLE beauty_specialist_reviews FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109135452_beauty_specialist_profile_reviews') THEN

                    CREATE POLICY tenant_isolation ON beauty_specialist_reviews
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109135452_beauty_specialist_profile_reviews') THEN

    DO $$
    BEGIN
        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'beautycrm_app') THEN
            GRANT SELECT, INSERT, UPDATE, DELETE ON beauty_specialist_reviews TO beautycrm_app;
        END IF;
    END
    $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109135452_beauty_specialist_profile_reviews') THEN

    DO $$
    BEGIN
        IF NOT EXISTS (
            SELECT 1 FROM pg_class c
            WHERE c.oid = to_regclass('public.beauty_specialist_reviews') AND c.relrowsecurity AND c.relforcerowsecurity) THEN
            RAISE EXCEPTION 'FORCE ROW LEVEL SECURITY is not enabled on beauty_specialist_reviews';
        END IF;
    END
    $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109135452_beauty_specialist_profile_reviews') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20261109135452_beauty_specialist_profile_reviews', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109150000_beauty_reminder_24h_email_channel') THEN
    ALTER TABLE beauty_appointments DROP CONSTRAINT IF EXISTS ck_beauty_appointments_reminder_option;
    ALTER TABLE beauty_appointments ADD CONSTRAINT ck_beauty_appointments_reminder_option
        CHECK (reminder_option IN ('none', '1h', '2h', 'custom', '24h'));
    ALTER TABLE beauty_channels DROP CONSTRAINT IF EXISTS ck_beauty_channels_type;
    ALTER TABLE beauty_channels ADD CONSTRAINT ck_beauty_channels_type
        CHECK (type IN ('telegram', 'instagram', 'facebook', 'whatsapp', 'viber', 'widget', 'email'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261109150000_beauty_reminder_24h_email_channel') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20261109150000_beauty_reminder_24h_email_channel', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261110090000_beauty_cancellation_terms_visibility') THEN
    ALTER TABLE beauty_cancellation_settings ADD COLUMN IF NOT EXISTS show_terms_to_client boolean NOT NULL DEFAULT true;
    ALTER TABLE beauty_cancellation_settings ADD COLUMN IF NOT EXISTS custom_terms_text text NULL;
    ALTER TABLE beauty_cancellation_settings DROP CONSTRAINT IF EXISTS ck_beauty_cancellation_settings_terms_text;
    ALTER TABLE beauty_cancellation_settings ADD CONSTRAINT ck_beauty_cancellation_settings_terms_text
        CHECK (custom_terms_text IS NULL OR char_length(custom_terms_text) <= 500);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261110090000_beauty_cancellation_terms_visibility') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20261110090000_beauty_cancellation_terms_visibility', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261115120000_auth_user_permissions') THEN
    -- users / invites
    ALTER TABLE users ADD COLUMN access_version integer NOT NULL DEFAULT 1;
    ALTER TABLE users DROP CONSTRAINT IF EXISTS ck_users_role;
    ALTER TABLE users ADD CONSTRAINT ck_users_role CHECK (role IN ('owner', 'admin', 'deputy', 'specialist'));
    ALTER TABLE invites ADD COLUMN permissions jsonb NULL; -- стане NOT NULL після backfill нижче
    ALTER TABLE invites DROP CONSTRAINT IF EXISTS ck_invites_role;
    ALTER TABLE invites ADD CONSTRAINT ck_invites_role CHECK (role IN ('admin', 'deputy', 'specialist'));

    -- хто створив запис (ADR-023 §30.2: виключає власноруч створені й скасовані записи зі scope own); наявні рядки NULL
    ALTER TABLE beauty_appointments ADD COLUMN created_by_user_id uuid NULL;
    ALTER TABLE beauty_appointments ADD CONSTRAINT fk_beauty_appointments_users_tenant_id_created_by_user_id
        FOREIGN KEY (tenant_id, created_by_user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT;
    CREATE INDEX ix_beauty_appointments_tenant_id_created_by_user_id ON beauty_appointments (tenant_id, created_by_user_id)
        WHERE created_by_user_id IS NOT NULL;

    -- user_permissions
    CREATE TABLE user_permissions (
        tenant_id uuid NOT NULL,
        user_id uuid NOT NULL,
        permission text NOT NULL,
        level text NOT NULL,
        updated_at timestamp with time zone NOT NULL DEFAULT now(),
        updated_by_user_id uuid NULL,
        CONSTRAINT pk_user_permissions PRIMARY KEY (tenant_id, user_id, permission),
        CONSTRAINT ck_user_permissions_permission CHECK (permission ~ '^[a-z_]{1,32}\.[a-z_]{1,32}$'),
        CONSTRAINT ck_user_permissions_level CHECK (level ~ '^[a-z_]{1,16}$'),
        CONSTRAINT fk_user_permissions_users_tenant_id_user_id FOREIGN KEY (tenant_id, user_id)
            REFERENCES users (tenant_id, id) ON DELETE CASCADE,
        CONSTRAINT fk_user_permissions_users_tenant_id_updated_by_user_id FOREIGN KEY (tenant_id, updated_by_user_id)
            REFERENCES users (tenant_id, id) ON DELETE RESTRICT
    );
    CREATE INDEX ix_user_permissions_tenant_id_updated_by_user_id ON user_permissions (tenant_id, updated_by_user_id);

    -- user_access_audit (append-only)
    CREATE TABLE user_access_audit (
        id uuid NOT NULL DEFAULT gen_random_uuid(),
        tenant_id uuid NOT NULL,
        actor_user_id uuid NULL,
        target_user_id uuid NULL,
        invite_id uuid NULL,
        action character varying(32) NOT NULL,
        changes jsonb NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT now(),
        CONSTRAINT pk_user_access_audit PRIMARY KEY (id),
        CONSTRAINT ck_user_access_audit_action CHECK (action IN ('permissions_changed', 'role_changed', 'preset_reset', 'invited', 'invite_revoked', 'invite_accepted', 'activated', 'deactivated', 'migrated')),
        CONSTRAINT ck_user_access_audit_changes CHECK (jsonb_typeof(changes) IN ('array', 'object')),
        CONSTRAINT ck_user_access_audit_target_or_invite CHECK (target_user_id IS NOT NULL OR invite_id IS NOT NULL),
        CONSTRAINT fk_user_access_audit_users_tenant_id_actor_user_id FOREIGN KEY (tenant_id, actor_user_id)
            REFERENCES users (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_user_access_audit_users_tenant_id_target_user_id FOREIGN KEY (tenant_id, target_user_id)
            REFERENCES users (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_user_access_audit_invites_tenant_id_invite_id FOREIGN KEY (tenant_id, invite_id)
            REFERENCES invites (tenant_id, id) ON DELETE RESTRICT
    );
    CREATE INDEX ix_user_access_audit_tenant_id_actor_user_id ON user_access_audit (tenant_id, actor_user_id);
    CREATE INDEX ix_user_access_audit_tenant_id_created_at_id ON user_access_audit (tenant_id, created_at DESC, id DESC);
    CREATE INDEX ix_user_access_audit_tenant_id_invite_id_created_at ON user_access_audit (tenant_id, invite_id, created_at DESC)
        WHERE invite_id IS NOT NULL;
    CREATE INDEX ix_user_access_audit_tenant_id_target_user_id_created_at ON user_access_audit (tenant_id, target_user_id, created_at DESC);

    -- append-only (defense in depth): основний захист - REVOKE UPDATE, DELETE для beautycrm_app (grants.sql / setup-db.ps1)
    -- Єдиний виняток: рядковий DELETE, що виконує власник purge_user_access_audit (current_user всередині SECURITY DEFINER-функції).
    -- Роль застосунку не може стати цим користувачем (немає членства), тож обійти тригер неможливо.
    CREATE FUNCTION user_access_audit_block_mutation() RETURNS trigger
    LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $fn$
    BEGIN
        IF TG_OP = 'DELETE' AND TG_LEVEL = 'ROW' AND current_user = 'beautycrm_audit_purge' THEN
            RETURN OLD;
        END IF;
        RAISE EXCEPTION 'user_access_audit is append-only' USING ERRCODE = '42501';
    END
    $fn$;
    CREATE TRIGGER user_access_audit_append_only
        BEFORE UPDATE OR DELETE ON user_access_audit
        FOR EACH ROW EXECUTE FUNCTION user_access_audit_block_mutation();
    CREATE TRIGGER user_access_audit_no_truncate
        BEFORE TRUNCATE ON user_access_audit
        FOR EACH STATEMENT EXECUTE FUNCTION user_access_audit_block_mutation();

    -- backfill (до ENABLE RLS на нових таблицях). FORCE RLS на users без app.tenant_id приховує всі рядки: знімаємо
    -- FORCE лише на час читання в цій транзакції (прецедент: platform_control_plane Down).
    ALTER TABLE users NO FORCE ROW LEVEL SECURITY;
    ALTER TABLE invites NO FORCE ROW LEVEL SECURITY;
    WITH presets(permission, admin_level, specialist_level) AS (VALUES
        ('calendar.view', 'all', 'own'),
        ('calendar.manage', 'all', 'own'),
        ('clients.view', 'all', 'none'),
        ('clients.edit', 'all', 'none'),
        ('clients.contacts', 'full', 'masked'),
        ('clients.calls', 'all', 'none'),
        ('revenue.view', 'all', 'none'),
        ('revenue.costs', 'edit', 'none'),
        ('channels.inbox', 'reply', 'none'),
        ('channels.settings', 'edit', 'none'),
        ('ai.use', 'use', 'none'),
        ('ai.settings', 'edit', 'none'),
        ('services.catalog', 'edit', 'view'),
        ('marketing.campaigns', 'edit', 'none'),
        ('staff.access', 'edit', 'own'),
        ('locations.access', 'edit', 'own'),
        ('settings.general', 'view', 'none'),
        ('team.manage', 'manage', 'none')),
    ins AS (
        INSERT INTO user_permissions (tenant_id, user_id, permission, level, updated_by_user_id)
        SELECT u.tenant_id, u.id, p.permission,
               CASE u.role WHEN 'admin' THEN p.admin_level ELSE p.specialist_level END, NULL
        FROM users u CROSS JOIN presets p
        WHERE u.role IN ('admin', 'specialist')
        ON CONFLICT (tenant_id, user_id, permission) DO NOTHING
        RETURNING tenant_id, user_id, permission, level
    )
    INSERT INTO user_access_audit (tenant_id, actor_user_id, target_user_id, action, changes)
    SELECT tenant_id, NULL, user_id, 'migrated',
           jsonb_agg(jsonb_build_object('permission', permission, 'from', NULL::text, 'to', level) ORDER BY permission)
    FROM ins
    GROUP BY tenant_id, user_id;
    -- наявні запрошення (pending, прийняті, відкликані): повний знімок = пресет ролі (§30.3); ідемпотентно (лише NULL)
    WITH presets(permission, admin_level, specialist_level) AS (VALUES
        ('calendar.view', 'all', 'own'),
        ('calendar.manage', 'all', 'own'),
        ('clients.view', 'all', 'none'),
        ('clients.edit', 'all', 'none'),
        ('clients.contacts', 'full', 'masked'),
        ('clients.calls', 'all', 'none'),
        ('revenue.view', 'all', 'none'),
        ('revenue.costs', 'edit', 'none'),
        ('channels.inbox', 'reply', 'none'),
        ('channels.settings', 'edit', 'none'),
        ('ai.use', 'use', 'none'),
        ('ai.settings', 'edit', 'none'),
        ('services.catalog', 'edit', 'view'),
        ('marketing.campaigns', 'edit', 'none'),
        ('staff.access', 'edit', 'own'),
        ('locations.access', 'edit', 'own'),
        ('settings.general', 'view', 'none'),
        ('team.manage', 'manage', 'none'))
    UPDATE invites i
       SET permissions = (SELECT jsonb_object_agg(p.permission,
                                 CASE i.role WHEN 'admin' THEN p.admin_level ELSE p.specialist_level END)
                          FROM presets p)
     WHERE i.permissions IS NULL AND i.role IN ('admin', 'specialist');
    ALTER TABLE users FORCE ROW LEVEL SECURITY;
    ALTER TABLE invites FORCE ROW LEVEL SECURITY;

    -- invites.permissions: повний матеріалізований знімок (B1), NOT NULL. DEFAULT '{}' (порожній знімок = найнижчі рівні,
    -- fail-closed) лише щоб наявний код створення запрошень (до TASK-851, який завжди передає повний знімок) не падав.
    ALTER TABLE invites ALTER COLUMN permissions SET DEFAULT '{}'::jsonb;
    ALTER TABLE invites ALTER COLUMN permissions SET NOT NULL;
    ALTER TABLE invites ADD CONSTRAINT ck_invites_permissions CHECK (jsonb_typeof(permissions) = 'object');

    -- Retention (Q7, 3 роки): ЄДИНИЙ шлях видалення з журналу - SECURITY DEFINER purge_user_access_audit, власник - окрема
    -- NOLOGIN-роль beautycrm_audit_purge (за патерном beautycrm_catalog, ADR-022: створюється тут або ops заздалегідь;
    -- членство мігратора INHERIT FALSE, SET TRUE; політики TO цієї ролі діють на всіх, хто її успадковує).
    DO $do$
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'beautycrm_audit_purge') THEN
            BEGIN
                CREATE ROLE beautycrm_audit_purge NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT;
            EXCEPTION
                WHEN duplicate_object THEN NULL;
                WHEN insufficient_privilege THEN
                RAISE EXCEPTION 'Role beautycrm_audit_purge does not exist and % cannot create it (needs CREATEROLE). Create it as ops: CREATE ROLE beautycrm_audit_purge NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT; GRANT beautycrm_audit_purge TO % WITH INHERIT FALSE, SET TRUE;', current_user, current_user;
            END;
        END IF;
        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'beautycrm_audit_purge' AND (rolcanlogin OR rolsuper OR rolbypassrls)) THEN
            RAISE EXCEPTION 'Role beautycrm_audit_purge must be NOLOGIN NOSUPERUSER NOBYPASSRLS (ADR-023)';
        END IF;
        IF NOT EXISTS (SELECT 1 FROM pg_auth_members m
                       JOIN pg_roles r ON r.oid = m.roleid AND r.rolname = 'beautycrm_audit_purge'
                       JOIN pg_roles u ON u.oid = m.member AND u.rolname = current_user
                       WHERE m.set_option AND NOT m.inherit_option) THEN
            BEGIN
                EXECUTE format('GRANT beautycrm_audit_purge TO %I WITH INHERIT FALSE, SET TRUE', current_user);
            EXCEPTION WHEN insufficient_privilege THEN
                RAISE EXCEPTION 'The migrator % must be a member of beautycrm_audit_purge WITH INHERIT FALSE, SET TRUE. Ops: GRANT beautycrm_audit_purge TO % WITH INHERIT FALSE, SET TRUE;', current_user, current_user;
            END;
        END IF;
    END
    $do$;
    GRANT USAGE ON SCHEMA public TO beautycrm_audit_purge;
    GRANT SELECT, DELETE ON user_access_audit TO beautycrm_audit_purge;

    CREATE FUNCTION public.purge_user_access_audit(p_before timestamptz) RETURNS bigint
        LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
    DECLARE
        n bigint;
    BEGIN
        IF p_before IS NULL OR p_before > now() - interval '3 years' THEN
            RAISE EXCEPTION 'purge_user_access_audit: p_before must be at least 3 years in the past' USING ERRCODE = '22023';
        END IF;
        DELETE FROM public.user_access_audit WHERE created_at < p_before;
        GET DIAGNOSTICS n = ROW_COUNT;
        RETURN n;
    END
    $fn$;
    -- EXECUTE: ніхто, крім ролі воркера (GRANT у grants.sql / setup-db.ps1; ролі середовища). REVOKE ДО передачі власності
    REVOKE ALL ON FUNCTION public.purge_user_access_audit(timestamptz) FROM PUBLIC;
    GRANT CREATE ON SCHEMA public TO beautycrm_audit_purge;
    ALTER FUNCTION public.purge_user_access_audit(timestamptz) OWNER TO beautycrm_audit_purge;
    REVOKE CREATE ON SCHEMA public FROM beautycrm_audit_purge;

    -- RLS (ADR-002): ENABLE + FORCE, fail-closed без app.tenant_id
    ALTER TABLE user_permissions ENABLE ROW LEVEL SECURITY;
    ALTER TABLE user_permissions FORCE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON user_permissions
        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    ALTER TABLE user_access_audit ENABLE ROW LEVEL SECURITY;
    ALTER TABLE user_access_audit FORCE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON user_access_audit
        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    -- purge діє між tenant-ами (без app.tenant_id): окремі політики лише для ролі-власника функції, лише SELECT/DELETE
    CREATE POLICY audit_purge_select ON user_access_audit FOR SELECT TO beautycrm_audit_purge USING (true);
    CREATE POLICY audit_purge_delete ON user_access_audit FOR DELETE TO beautycrm_audit_purge USING (true);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261115120000_auth_user_permissions') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20261115120000_auth_user_permissions', '8.0.11');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE TABLE beauty_stocktakes (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        tenant_id uuid NOT NULL,
        location_id uuid NOT NULL,
        status character varying(16) NOT NULL DEFAULT 'draft',
        note character varying(500),
        version integer NOT NULL DEFAULT 1,
        created_by_user_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        posted_at timestamp with time zone,
        posted_by_user_id uuid,
        cancelled_at timestamp with time zone,
        cancelled_by_user_id uuid,
        CONSTRAINT pk_beauty_stocktakes PRIMARY KEY (id),
        CONSTRAINT ak_stocktakes_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_stocktakes_cancelled CHECK ((status = 'cancelled') = (cancelled_at IS NOT NULL)),
        CONSTRAINT ck_beauty_stocktakes_posted CHECK ((status = 'posted') = (posted_at IS NOT NULL AND posted_by_user_id IS NOT NULL)),
        CONSTRAINT ck_beauty_stocktakes_status CHECK (status IN ('draft', 'posted', 'cancelled')),
        CONSTRAINT ck_beauty_stocktakes_version CHECK (version >= 1),
        CONSTRAINT fk_beauty_stocktakes_beauty_locations_tenant_id_location_id FOREIGN KEY (tenant_id, location_id) REFERENCES beauty_locations (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_stocktakes_users_tenant_id_cancelled_by_user_id FOREIGN KEY (tenant_id, cancelled_by_user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_stocktakes_users_tenant_id_created_by_user_id FOREIGN KEY (tenant_id, created_by_user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_stocktakes_users_tenant_id_posted_by_user_id FOREIGN KEY (tenant_id, posted_by_user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE TABLE beauty_suppliers (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        name character varying(200) NOT NULL,
        contact_name character varying(200),
        phone character varying(32),
        email character varying(320),
        note character varying(500),
        is_active boolean NOT NULL DEFAULT TRUE,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_suppliers PRIMARY KEY (id),
        CONSTRAINT ak_beauty_suppliers_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_suppliers_name CHECK (char_length(btrim(name)) BETWEEN 1 AND 200)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE TABLE beauty_products (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        name character varying(200) NOT NULL,
        category character varying(100),
        unit character varying(8) NOT NULL,
        barcode character varying(64),
        purpose character varying(16) NOT NULL,
        supplier_id uuid,
        purchase_price numeric(12,2),
        min_stock numeric(14,3),
        track_expiry boolean NOT NULL DEFAULT FALSE,
        is_active boolean NOT NULL DEFAULT TRUE,
        note character varying(500),
        updated_by_user_id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_products PRIMARY KEY (id),
        CONSTRAINT ak_beauty_products_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_products_barcode CHECK (barcode IS NULL OR barcode ~ '^[0-9A-Za-z._-]{1,64}$'),
        CONSTRAINT ck_beauty_products_min_stock CHECK (min_stock IS NULL OR min_stock >= 0),
        CONSTRAINT ck_beauty_products_purchase_price CHECK (purchase_price IS NULL OR purchase_price BETWEEN 0 AND 1000000),
        CONSTRAINT ck_beauty_products_purpose CHECK (purpose IN ('sale', 'salon_use', 'both')),
        CONSTRAINT ck_beauty_products_unit CHECK (unit IN ('pcs', 'ml', 'g', 'l', 'kg', 'pack')),
        CONSTRAINT fk_beauty_products_beauty_suppliers_tenant_id_supplier_id FOREIGN KEY (tenant_id, supplier_id) REFERENCES beauty_suppliers (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_products_users_tenant_id_updated_by_user_id FOREIGN KEY (tenant_id, updated_by_user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE TABLE beauty_stock_operations (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        tenant_id uuid NOT NULL,
        kind character varying(24) NOT NULL,
        location_id uuid NOT NULL,
        to_location_id uuid,
        supplier_id uuid,
        document_number character varying(64),
        received_on date,
        reason character varying(16),
        stocktake_id uuid,
        note character varying(500),
        created_by_user_id uuid,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        idempotency_key_hash character varying(64),
        idempotency_request_hash character varying(64),
        CONSTRAINT pk_beauty_stock_operations PRIMARY KEY (id),
        CONSTRAINT ak_beauty_stock_operations_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_stock_operations_idempotency CHECK ((idempotency_key_hash IS NULL) = (idempotency_request_hash IS NULL)),
        CONSTRAINT ck_beauty_stock_operations_kind CHECK (kind IN ('receipt', 'write_off', 'transfer', 'stocktake', 'service_consumption', 'sale')),
        CONSTRAINT ck_beauty_stock_operations_reason CHECK (reason IS NULL OR reason IN ('expired', 'damaged', 'lost', 'internal_use', 'correction', 'other')),
        CONSTRAINT ck_beauty_stock_operations_reason_kind CHECK ((kind = 'write_off') = (reason IS NOT NULL)),
        CONSTRAINT ck_beauty_stock_operations_receipt_fields CHECK (kind = 'receipt' OR (supplier_id IS NULL AND document_number IS NULL AND received_on IS NULL)),
        CONSTRAINT ck_beauty_stock_operations_stocktake CHECK ((kind = 'stocktake') = (stocktake_id IS NOT NULL)),
        CONSTRAINT ck_beauty_stock_operations_transfer_distinct CHECK (to_location_id IS NULL OR to_location_id <> location_id),
        CONSTRAINT ck_beauty_stock_operations_transfer_target CHECK ((kind = 'transfer') = (to_location_id IS NOT NULL)),
        CONSTRAINT fk_beauty_stock_operations_beauty_locations_tenant_id_location FOREIGN KEY (tenant_id, location_id) REFERENCES beauty_locations (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_stock_operations_beauty_locations_tenant_id_to_locat FOREIGN KEY (tenant_id, to_location_id) REFERENCES beauty_locations (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_stock_operations_beauty_suppliers_tenant_id_supplier FOREIGN KEY (tenant_id, supplier_id) REFERENCES beauty_suppliers (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_stock_operations_stocktakes_tenant_id_stocktake_id FOREIGN KEY (tenant_id, stocktake_id) REFERENCES beauty_stocktakes (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_stock_operations_users_tenant_id_created_by_user_id FOREIGN KEY (tenant_id, created_by_user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE TABLE beauty_product_location_settings (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        product_id uuid NOT NULL,
        location_id uuid NOT NULL,
        min_stock numeric(14,3),
        is_enabled boolean NOT NULL DEFAULT TRUE,
        updated_by_user_id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_product_location_settings PRIMARY KEY (id),
        CONSTRAINT ak_beauty_product_location_settings_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_product_location_settings_min_stock CHECK (min_stock IS NULL OR min_stock >= 0),
        CONSTRAINT fk_beauty_product_location_settings_beauty_locations_tenant_id FOREIGN KEY (tenant_id, location_id) REFERENCES beauty_locations (tenant_id, id) ON DELETE CASCADE,
        CONSTRAINT fk_beauty_product_location_settings_beauty_products_tenant_id_ FOREIGN KEY (tenant_id, product_id) REFERENCES beauty_products (tenant_id, id) ON DELETE CASCADE,
        CONSTRAINT fk_beauty_product_location_settings_users_tenant_id_updated_by FOREIGN KEY (tenant_id, updated_by_user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE TABLE beauty_stock_levels (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        location_id uuid NOT NULL,
        product_id uuid NOT NULL,
        quantity numeric(14,3) NOT NULL DEFAULT 0.0,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_stock_levels PRIMARY KEY (id),
        CONSTRAINT ak_beauty_stock_levels_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_stock_levels_quantity CHECK (quantity >= 0),
        CONSTRAINT fk_beauty_stock_levels_beauty_locations_tenant_id_location_id FOREIGN KEY (tenant_id, location_id) REFERENCES beauty_locations (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_stock_levels_beauty_products_tenant_id_product_id FOREIGN KEY (tenant_id, product_id) REFERENCES beauty_products (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE TABLE beauty_stocktake_lines (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        stocktake_id uuid NOT NULL,
        product_id uuid NOT NULL,
        counted_quantity numeric(14,3),
        expected_quantity numeric(14,3),
        surplus_expires_on date,
        updated_by_user_id uuid,
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_stocktake_lines PRIMARY KEY (id),
        CONSTRAINT ak_beauty_stocktake_lines_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_stocktake_lines_counted CHECK (counted_quantity IS NULL OR counted_quantity >= 0),
        CONSTRAINT ck_beauty_stocktake_lines_surplus_expires_on CHECK (surplus_expires_on IS NULL OR surplus_expires_on BETWEEN DATE '2000-01-01' AND DATE '2100-12-31'),
        CONSTRAINT fk_beauty_stocktake_lines_beauty_products_tenant_id_product_id FOREIGN KEY (tenant_id, product_id) REFERENCES beauty_products (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_stocktake_lines_beauty_stocktakes_tenant_id_stocktak FOREIGN KEY (tenant_id, stocktake_id) REFERENCES beauty_stocktakes (tenant_id, id) ON DELETE CASCADE,
        CONSTRAINT fk_beauty_stocktake_lines_users_tenant_id_updated_by_user_id FOREIGN KEY (tenant_id, updated_by_user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE TABLE beauty_stock_batches (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        location_id uuid NOT NULL,
        product_id uuid NOT NULL,
        operation_id uuid NOT NULL,
        source_batch_id uuid,
        supplier_id uuid,
        received_at timestamp with time zone NOT NULL,
        expires_on date,
        lot_number character varying(64),
        initial_quantity numeric(14,3) NOT NULL,
        quantity numeric(14,3) NOT NULL,
        unit_cost numeric(12,2),
        tenant_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL DEFAULT (now()),
        updated_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_stock_batches PRIMARY KEY (id),
        CONSTRAINT ak_beauty_stock_batches_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_stock_batches_expires_on CHECK (expires_on IS NULL OR expires_on BETWEEN DATE '2000-01-01' AND DATE '2100-12-31'),
        CONSTRAINT ck_beauty_stock_batches_initial_quantity CHECK (initial_quantity > 0),
        CONSTRAINT ck_beauty_stock_batches_quantity CHECK (quantity >= 0 AND quantity <= initial_quantity),
        CONSTRAINT ck_beauty_stock_batches_unit_cost CHECK (unit_cost IS NULL OR unit_cost BETWEEN 0 AND 1000000),
        CONSTRAINT fk_beauty_stock_batches_beauty_locations_tenant_id_location_id FOREIGN KEY (tenant_id, location_id) REFERENCES beauty_locations (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_stock_batches_beauty_products_tenant_id_product_id FOREIGN KEY (tenant_id, product_id) REFERENCES beauty_products (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_stock_batches_beauty_stock_batches_tenant_id_source_ FOREIGN KEY (tenant_id, source_batch_id) REFERENCES beauty_stock_batches (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_stock_batches_beauty_stock_operations_tenant_id_oper FOREIGN KEY (tenant_id, operation_id) REFERENCES beauty_stock_operations (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_stock_batches_beauty_suppliers_tenant_id_supplier_id FOREIGN KEY (tenant_id, supplier_id) REFERENCES beauty_suppliers (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE TABLE beauty_stock_movements (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        tenant_id uuid NOT NULL,
        operation_id uuid NOT NULL,
        location_id uuid NOT NULL,
        product_id uuid NOT NULL,
        batch_id uuid,
        type character varying(24) NOT NULL,
        quantity numeric(14,3) NOT NULL,
        unit_cost numeric(12,2),
        created_by_user_id uuid,
        occurred_at timestamp with time zone NOT NULL DEFAULT (now()),
        CONSTRAINT pk_beauty_stock_movements PRIMARY KEY (id),
        CONSTRAINT ak_beauty_stock_movements_tenant_id_id UNIQUE (tenant_id, id),
        CONSTRAINT ck_beauty_stock_movements_batch CHECK (batch_id IS NOT NULL OR type = 'service_consumption'),
        CONSTRAINT ck_beauty_stock_movements_quantity_sign CHECK ((type IN ('receipt', 'transfer_in') AND quantity > 0) OR (type IN ('write_off', 'transfer_out', 'service_consumption', 'sale') AND quantity < 0) OR (type = 'stocktake_adjust' AND quantity <> 0)),
        CONSTRAINT ck_beauty_stock_movements_type CHECK (type IN ('receipt', 'write_off', 'transfer_out', 'transfer_in', 'stocktake_adjust', 'service_consumption', 'sale')),
        CONSTRAINT fk_beauty_stock_movements_beauty_locations_tenant_id_location_ FOREIGN KEY (tenant_id, location_id) REFERENCES beauty_locations (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_stock_movements_beauty_products_tenant_id_product_id FOREIGN KEY (tenant_id, product_id) REFERENCES beauty_products (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_stock_movements_beauty_stock_batches_tenant_id_batch FOREIGN KEY (tenant_id, batch_id) REFERENCES beauty_stock_batches (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_stock_movements_beauty_stock_operations_tenant_id_op FOREIGN KEY (tenant_id, operation_id) REFERENCES beauty_stock_operations (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_beauty_stock_movements_users_tenant_id_created_by_user_id FOREIGN KEY (tenant_id, created_by_user_id) REFERENCES users (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE INDEX ix_beauty_product_location_settings_tenant_id_location_id ON beauty_product_location_settings (tenant_id, location_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE INDEX ix_beauty_product_location_settings_tenant_id_updated_by_user_ ON beauty_product_location_settings (tenant_id, updated_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE UNIQUE INDEX ux_beauty_product_location_settings_pair ON beauty_product_location_settings (tenant_id, product_id, location_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE INDEX ix_beauty_products_active_name ON beauty_products (tenant_id, is_active, name);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE INDEX ix_beauty_products_supplier ON beauty_products (tenant_id, supplier_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE INDEX ix_beauty_products_tenant_id_updated_by_user_id ON beauty_products (tenant_id, updated_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE UNIQUE INDEX ux_beauty_products_barcode ON beauty_products (tenant_id, barcode) WHERE barcode IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE INDEX ix_beauty_stock_batches_expiry ON beauty_stock_batches (tenant_id, location_id, expires_on) WHERE quantity > 0 AND expires_on IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE INDEX ix_beauty_stock_batches_fefo ON beauty_stock_batches (tenant_id, location_id, product_id, expires_on, received_at, id) WHERE quantity > 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE INDEX ix_beauty_stock_batches_tenant_id_operation_id ON beauty_stock_batches (tenant_id, operation_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE INDEX ix_beauty_stock_batches_tenant_id_product_id ON beauty_stock_batches (tenant_id, product_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE INDEX ix_beauty_stock_batches_tenant_id_source_batch_id ON beauty_stock_batches (tenant_id, source_batch_id) WHERE source_batch_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE INDEX ix_beauty_stock_batches_tenant_id_supplier_id ON beauty_stock_batches (tenant_id, supplier_id) WHERE supplier_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE INDEX ix_beauty_stock_levels_tenant_id_product_id ON beauty_stock_levels (tenant_id, product_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE UNIQUE INDEX ux_beauty_stock_levels_pair ON beauty_stock_levels (tenant_id, location_id, product_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE INDEX ix_beauty_stock_movements_batch ON beauty_stock_movements (tenant_id, batch_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE INDEX ix_beauty_stock_movements_location_occurred ON beauty_stock_movements (tenant_id, location_id, occurred_at DESC, id DESC);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE INDEX ix_beauty_stock_movements_operation ON beauty_stock_movements (tenant_id, operation_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE INDEX ix_beauty_stock_movements_product_occurred ON beauty_stock_movements (tenant_id, product_id, occurred_at DESC);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE INDEX ix_beauty_stock_movements_tenant_id_created_by_user_id ON beauty_stock_movements (tenant_id, created_by_user_id) WHERE created_by_user_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE INDEX ix_beauty_stock_operations_location_created ON beauty_stock_operations (tenant_id, location_id, created_at DESC, id DESC);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE INDEX ix_beauty_stock_operations_tenant_id_created_by_user_id ON beauty_stock_operations (tenant_id, created_by_user_id) WHERE created_by_user_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE INDEX ix_beauty_stock_operations_tenant_id_supplier_id ON beauty_stock_operations (tenant_id, supplier_id) WHERE supplier_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE INDEX ix_beauty_stock_operations_to_location_created ON beauty_stock_operations (tenant_id, to_location_id, created_at DESC) WHERE to_location_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE UNIQUE INDEX ux_beauty_stock_operations_idempotency ON beauty_stock_operations (tenant_id, idempotency_key_hash) WHERE idempotency_key_hash IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE UNIQUE INDEX ux_beauty_stock_operations_stocktake ON beauty_stock_operations (tenant_id, stocktake_id) WHERE stocktake_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE INDEX ix_beauty_stocktake_lines_tenant_id_product_id ON beauty_stocktake_lines (tenant_id, product_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE INDEX ix_beauty_stocktake_lines_tenant_id_updated_by_user_id ON beauty_stocktake_lines (tenant_id, updated_by_user_id) WHERE updated_by_user_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE UNIQUE INDEX ux_beauty_stocktake_lines_pair ON beauty_stocktake_lines (tenant_id, stocktake_id, product_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE INDEX ix_beauty_stocktakes_location_created ON beauty_stocktakes (tenant_id, location_id, created_at DESC);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE INDEX ix_beauty_stocktakes_tenant_id_cancelled_by_user_id ON beauty_stocktakes (tenant_id, cancelled_by_user_id) WHERE cancelled_by_user_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE INDEX ix_beauty_stocktakes_tenant_id_created_by_user_id ON beauty_stocktakes (tenant_id, created_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE INDEX ix_beauty_stocktakes_tenant_id_posted_by_user_id ON beauty_stocktakes (tenant_id, posted_by_user_id) WHERE posted_by_user_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE UNIQUE INDEX ux_beauty_stocktakes_open ON beauty_stocktakes (tenant_id, location_id) WHERE status = 'draft';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    CREATE UNIQUE INDEX ux_beauty_suppliers_name ON beauty_suppliers (tenant_id, lower(name));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    ALTER TABLE beauty_suppliers ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    ALTER TABLE beauty_suppliers FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN

                    CREATE POLICY tenant_isolation ON beauty_suppliers
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    ALTER TABLE beauty_products ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    ALTER TABLE beauty_products FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN

                    CREATE POLICY tenant_isolation ON beauty_products
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    ALTER TABLE beauty_product_location_settings ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    ALTER TABLE beauty_product_location_settings FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN

                    CREATE POLICY tenant_isolation ON beauty_product_location_settings
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    ALTER TABLE beauty_stock_levels ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    ALTER TABLE beauty_stock_levels FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN

                    CREATE POLICY tenant_isolation ON beauty_stock_levels
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    ALTER TABLE beauty_stock_operations ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    ALTER TABLE beauty_stock_operations FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN

                    CREATE POLICY tenant_isolation ON beauty_stock_operations
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    ALTER TABLE beauty_stock_batches ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    ALTER TABLE beauty_stock_batches FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN

                    CREATE POLICY tenant_isolation ON beauty_stock_batches
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    ALTER TABLE beauty_stock_movements ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    ALTER TABLE beauty_stock_movements FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN

                    CREATE POLICY tenant_isolation ON beauty_stock_movements
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    ALTER TABLE beauty_stocktakes ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    ALTER TABLE beauty_stocktakes FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN

                    CREATE POLICY tenant_isolation ON beauty_stocktakes
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    ALTER TABLE beauty_stocktake_lines ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    ALTER TABLE beauty_stocktake_lines FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN

                    CREATE POLICY tenant_isolation ON beauty_stocktake_lines
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN

    DO $$
    DECLARE
        t text;
    BEGIN
        FOREACH t IN ARRAY ARRAY['beauty_suppliers', 'beauty_products', 'beauty_product_location_settings', 'beauty_stock_levels',
                                 'beauty_stock_operations', 'beauty_stock_batches', 'beauty_stock_movements', 'beauty_stocktakes',
                                 'beauty_stocktake_lines']
        LOOP
            IF NOT EXISTS (
                SELECT 1 FROM pg_class WHERE oid = to_regclass('public.' || t) AND relrowsecurity AND relforcerowsecurity
            ) THEN
                RAISE EXCEPTION 'FORCE ROW LEVEL SECURITY is not enabled on %', t;
            END IF;
        END LOOP;
    END
    $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20261118155039_beauty_inventory') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20261118155039_beauty_inventory', '8.0.11');
    END IF;
END $EF$;
COMMIT;

