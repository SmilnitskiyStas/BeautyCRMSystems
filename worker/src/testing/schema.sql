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

