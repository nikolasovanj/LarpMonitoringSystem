CREATE EXTENSION IF NOT EXISTS timescaledb;

-- ---------------------------------------------------------------- series
-- One row per (site, line, device, sensor). Created on first sight by the ingestion worker.
CREATE TABLE series (
    id          integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    site        text NOT NULL,
    line        text NOT NULL,
    device      text NOT NULL,
    sensor      text NOT NULL,
    unit        text,
    first_seen  timestamptz NOT NULL DEFAULT now(),
    UNIQUE (site, line, device, sensor)
);

-- ---------------------------------------------------------------- telemetry
CREATE TABLE telemetry (
    time       timestamptz      NOT NULL,   -- device timestamp
    series_id  integer          NOT NULL REFERENCES series(id),
    value      double precision NOT NULL
);

SELECT create_hypertable('telemetry', 'time', chunk_time_interval => INTERVAL '1 day');

-- "latest value" and "last hour of one sensor" both filter on series first
CREATE UNIQUE INDEX telemetry_series_time_uq ON telemetry (series_id, time);

-- Compress chunks older than 7 days; segmenting by series keeps per-sensor queries fast
ALTER TABLE telemetry SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = 'series_id',
    timescaledb.compress_orderby   = 'time DESC'
);
SELECT add_compression_policy('telemetry', INTERVAL '7 days');
SELECT add_retention_policy('telemetry', INTERVAL '90 days');

-- ---------------------------------------------------------------- 1-minute rollup
CREATE MATERIALIZED VIEW telemetry_1m
WITH (timescaledb.continuous) AS
SELECT time_bucket('1 minute', time) AS bucket,
       series_id,
       avg(value) AS avg,
       min(value) AS min,
       max(value) AS max,
       count(*)   AS samples
FROM telemetry
GROUP BY bucket, series_id
WITH NO DATA;

SELECT add_continuous_aggregate_policy('telemetry_1m',
    start_offset      => INTERVAL '1 hour',
    end_offset        => INTERVAL '1 minute',
    schedule_interval => INTERVAL '1 minute');

-- Keep rollups longer than raw data
SELECT add_retention_policy('telemetry_1m', INTERVAL '2 years');

-- ---------------------------------------------------------------- device status
-- Current state, upserted from factory/{site}/{line}/{device}/status ("online"/"offline")
CREATE TABLE device_status (
    site          text NOT NULL,
    line          text NOT NULL,
    device        text NOT NULL,
    status        text NOT NULL CHECK (status IN ('online', 'offline')),
    status_since  timestamptz NOT NULL DEFAULT now(),
    last_seen     timestamptz NOT NULL DEFAULT now(),   -- last message of any kind
    PRIMARY KEY (site, line, device)
);

-- History of changes only (insert when the status actually flips)
CREATE TABLE device_status_events (
    time    timestamptz NOT NULL DEFAULT now(),
    site    text NOT NULL,
    line    text NOT NULL,
    device  text NOT NULL,
    status  text NOT NULL CHECK (status IN ('online', 'offline'))
);
SELECT create_hypertable('device_status_events', 'time', chunk_time_interval => INTERVAL '30 days');
CREATE INDEX device_status_events_dev_idx ON device_status_events (site, line, device, time DESC);

-- ---------------------------------------------------------------- alerting
CREATE TABLE alert_rules (
    id          integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name        text NOT NULL,
    sensor      text NOT NULL,                              -- matches series.sensor
    site        text,                                       -- NULL = any
    line        text,
    device      text,
    kind        text NOT NULL CHECK (kind IN ('threshold', 'zscore', 'stale')),
    operator    text CHECK (operator IN ('>', '>=', '<', '<=')),
    threshold   double precision,
    for_seconds integer NOT NULL DEFAULT 0 CHECK (for_seconds >= 0),  -- must hold this long
    severity    text NOT NULL CHECK (severity IN ('info', 'warning', 'critical')),
    enabled     boolean NOT NULL DEFAULT true
);

ALTER TABLE alert_rules ADD CONSTRAINT alert_rules_kind_fields CHECK (
    (kind = 'threshold' AND operator IS NOT NULL AND threshold IS NOT NULL) OR
    (kind = 'zscore'    AND threshold IS NOT NULL AND threshold > 0) OR
    (kind = 'stale'     AND for_seconds > 0)
);

-- sensor = '*' matches every sensor. Thresholds are set from the simulator profiles.
INSERT INTO alert_rules (name, sensor, kind, operator, threshold, for_seconds, severity) VALUES
  ('High temperature',    'temperature', 'threshold', '>',  80,  3,  'critical'),
  ('Low pressure',        'pressure',    'threshold', '<',  5.2, 2,  'warning'),
  ('High vibration',      'vibration',   'threshold', '>',  4,   3,  'warning'),
  ('Statistical anomaly', '*',           'zscore',    NULL, 5,   0,  'info'),
  ('Sensor silent',       '*',           'stale',     NULL, NULL, 30, 'warning');

CREATE TABLE alerts (
    id               bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    rule_id          integer REFERENCES alert_rules(id) ON DELETE SET NULL,
    series_id        integer REFERENCES series(id),
    severity         text NOT NULL,
    message          text NOT NULL,
    value            double precision,
    opened_at        timestamptz NOT NULL DEFAULT now(),
    resolved_at      timestamptz,
    acknowledged_at  timestamptz,
    acknowledged_by  text
);
-- at most one OPEN alert per rule and series
CREATE UNIQUE INDEX alerts_one_open_idx ON alerts (rule_id, series_id) WHERE resolved_at IS NULL;
CREATE INDEX alerts_open_idx ON alerts (opened_at DESC) WHERE resolved_at IS NULL;

-- ---------------------------------------------------------------- convenience
CREATE VIEW latest_readings AS
SELECT s.id AS series_id, s.site, s.line, s.device, s.sensor, s.unit, t.time, t.value
FROM series s
JOIN LATERAL (
    SELECT time, value FROM telemetry
    WHERE series_id = s.id ORDER BY time DESC LIMIT 1
) t ON true;