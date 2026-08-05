CREATE TABLE IF NOT EXISTS controller_reader (
    serial_number       VARCHAR(128) PRIMARY KEY,
    driver_key          VARCHAR(64) NOT NULL,
    endpoint            VARCHAR(256),
    port                INTEGER NOT NULL DEFAULT 0,
    enabled             BOOLEAN NOT NULL DEFAULT TRUE,
    config_hash         VARCHAR(128),
    power_dbm           INTEGER NOT NULL DEFAULT 30,
    read_interval_ms    INTEGER NOT NULL DEFAULT 200,
    tid_start_address   INTEGER NOT NULL DEFAULT 2,
    tid_length          INTEGER NOT NULL DEFAULT 4,
    options_json        JSONB NOT NULL DEFAULT '{}'::jsonb,
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS controller_reader_runtime_status (
    serial_number       VARCHAR(128) PRIMARY KEY,
    detected_sdk_serial VARCHAR(128),
    detected_endpoint   VARCHAR(256),
    driver_key          VARCHAR(64),
    model               VARCHAR(128),
    endpoint            VARCHAR(256),
    online              BOOLEAN NOT NULL DEFAULT FALSE,
    message             TEXT,
    firmware_version    VARCHAR(128),
    power_dbm           INTEGER NOT NULL DEFAULT 30,
    read_interval_ms    INTEGER NOT NULL DEFAULT 200,
    ports_json          JSONB NOT NULL DEFAULT '[]'::jsonb,
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

ALTER TABLE controller_reader_runtime_status
    ADD COLUMN IF NOT EXISTS detected_sdk_serial VARCHAR(128);
ALTER TABLE controller_reader_runtime_status
    ADD COLUMN IF NOT EXISTS detected_endpoint VARCHAR(256);

CREATE INDEX IF NOT EXISTS ix_controller_reader_runtime_detected_serial
    ON controller_reader_runtime_status(detected_sdk_serial);

CREATE TABLE IF NOT EXISTS controller_runtime_context (
    singleton_id         SMALLINT PRIMARY KEY CHECK (singleton_id = 1),
    parking_layouts_json JSONB NOT NULL DEFAULT '[]'::jsonb,
    updated_at           TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS controller_parking_outbox (
    id                  BIGSERIAL PRIMARY KEY,
    event_uid           VARCHAR(200) NOT NULL UNIQUE,
    serial_number       VARCHAR(128) NOT NULL,
    port_no             INTEGER NOT NULL CHECK (port_no BETWEEN 1 AND 16),
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

CREATE TABLE IF NOT EXISTS controller_lane_calibration_outbox (
    id                  BIGSERIAL PRIMARY KEY,
    event_uid           VARCHAR(200) NOT NULL UNIQUE,
    lane_calibration_code    VARCHAR(128) NOT NULL,
    revision            INTEGER NOT NULL DEFAULT 1,
    power_dbm           INTEGER NOT NULL DEFAULT 30,
    read_interval_ms    INTEGER NOT NULL DEFAULT 200,
    serial_number       VARCHAR(128) NOT NULL,
    port_no             INTEGER NOT NULL CHECK (port_no BETWEEN 1 AND 16),
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

CREATE INDEX IF NOT EXISTS ix_controller_lane_calibration_outbox_pending
    ON controller_lane_calibration_outbox(status, next_attempt_at, id);
