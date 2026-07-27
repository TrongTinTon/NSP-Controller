CREATE TABLE IF NOT EXISTS controller_reader (
    serial_number       VARCHAR(128) PRIMARY KEY,
    driver_key          VARCHAR(64) NOT NULL,
    device_name         VARCHAR(256),
    model               VARCHAR(128),
    endpoint            VARCHAR(256),
    port                INTEGER NOT NULL DEFAULT 0,
    enabled             BOOLEAN NOT NULL DEFAULT TRUE,
    config_revision     INTEGER NOT NULL DEFAULT 0,
    config_hash         VARCHAR(128),
    power_dbm           INTEGER NOT NULL DEFAULT 30,
    read_interval_ms    INTEGER NOT NULL DEFAULT 200,
    tid_start_address   INTEGER NOT NULL DEFAULT 2,
    tid_length          INTEGER NOT NULL DEFAULT 4,
    antennas_json       JSONB NOT NULL DEFAULT '[]'::jsonb,
    options_json        JSONB NOT NULL DEFAULT '{}'::jsonb,
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS controller_reader_runtime_status (
    serial_number       VARCHAR(128) PRIMARY KEY,
    driver_key          VARCHAR(64),
    model               VARCHAR(128),
    endpoint            VARCHAR(256),
    online              BOOLEAN NOT NULL DEFAULT FALSE,
    message             TEXT,
    firmware_version    VARCHAR(128),
    antennas_json       JSONB NOT NULL DEFAULT '[]'::jsonb,
    config_revision     INTEGER NOT NULL DEFAULT 0,
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS controller_parking_outbox (
    id                  BIGSERIAL PRIMARY KEY,
    event_uid           VARCHAR(200) NOT NULL UNIQUE,
    controller_code     VARCHAR(128) NOT NULL,
    serial_number       VARCHAR(128) NOT NULL,
    antenna_no          INTEGER NOT NULL,
    tid                 VARCHAR(256) NOT NULL,
    detected_at         TIMESTAMPTZ NOT NULL,
    status              VARCHAR(16) NOT NULL DEFAULT 'pending',
    attempts            INTEGER NOT NULL DEFAULT 0,
    next_attempt_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_error          TEXT,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    sent_at             TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS ix_controller_parking_outbox_pending
    ON controller_parking_outbox(status, next_attempt_at, id);

CREATE TABLE IF NOT EXISTS controller_measurement_outbox (
    id                  BIGSERIAL PRIMARY KEY,
    event_uid           VARCHAR(200) NOT NULL UNIQUE,
    measurement_code    VARCHAR(128) NOT NULL,
    serial_number       VARCHAR(128) NOT NULL,
    antenna_no          INTEGER NOT NULL,
    tid                 VARCHAR(256) NOT NULL,
    rssi_dbm            DOUBLE PRECISION,
    read_at             TIMESTAMPTZ NOT NULL,
    status              VARCHAR(16) NOT NULL DEFAULT 'pending',
    attempts            INTEGER NOT NULL DEFAULT 0,
    next_attempt_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_error          TEXT,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    sent_at             TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS ix_controller_measurement_outbox_pending
    ON controller_measurement_outbox(status, next_attempt_at, id);
