-- Telemetry History 0.10.1. Additive and isolated from the existing mature database.
CREATE SCHEMA IF NOT EXISTS td_history;
CREATE TABLE IF NOT EXISTS td_history.schema_version(version integer PRIMARY KEY, installed_at timestamptz NOT NULL DEFAULT now());
INSERT INTO td_history.schema_version(version) VALUES(1) ON CONFLICT DO NOTHING;
CREATE TABLE IF NOT EXISTS td_history.segment(
 id uuid PRIMARY KEY, archive_file text NOT NULL, sha256 text NOT NULL,
 event_count integer NOT NULL, committed_at timestamptz NOT NULL DEFAULT now());
CREATE TABLE IF NOT EXISTS td_history.event(
 id uuid PRIMARY KEY, kind text NOT NULL, experiment_id uuid, session_id uuid, boot_id uuid NOT NULL,
 observed_at timestamptz NOT NULL, received_at timestamptz NOT NULL,
 observed_ticks bigint NOT NULL, received_ticks bigint NOT NULL, monotonic_ticks bigint NOT NULL, ordinal bigint NOT NULL,
 time_basis text NOT NULL, configuration_id uuid, source_ip inet, destination_ip inet,
 source_port integer NOT NULL, destination_port integer NOT NULL, direction text NOT NULL, mode text NOT NULL,
 packet_id text NOT NULL, packet_name text NOT NULL, outer_source_id integer NOT NULL, apid integer NOT NULL, frame_sequence integer NOT NULL,
 status text NOT NULL, reason text NOT NULL, payload_sha256 text, payload_length integer NOT NULL DEFAULT 0,
 segment_id uuid NOT NULL REFERENCES td_history.segment(id), archive_line integer NOT NULL,
 data jsonb NOT NULL, created_at timestamptz NOT NULL DEFAULT now());
ALTER TABLE td_history.event ADD COLUMN IF NOT EXISTS capture_time_confirmed boolean;
ALTER TABLE td_history.event ADD COLUMN IF NOT EXISTS capture_ticks bigint;
ALTER TABLE td_history.event ADD COLUMN IF NOT EXISTS capture_event_id uuid;
CREATE INDEX IF NOT EXISTS history_event_time ON td_history.event(observed_at DESC,observed_ticks DESC,ordinal DESC,id);
CREATE INDEX IF NOT EXISTS history_event_kind ON td_history.event(kind,observed_at DESC,id);
CREATE INDEX IF NOT EXISTS history_event_experiment ON td_history.event(experiment_id,observed_at DESC,id);
CREATE INDEX IF NOT EXISTS history_event_packet ON td_history.event(packet_id,observed_at DESC,id);
CREATE INDEX IF NOT EXISTS history_event_session ON td_history.event(session_id,observed_at DESC,id);
CREATE TABLE IF NOT EXISTS td_history.configuration(
 id uuid PRIMARY KEY, applied_at timestamptz NOT NULL, sha256 text NOT NULL, content jsonb NOT NULL);
CREATE TABLE IF NOT EXISTS td_history.parameter_definition(
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 configuration_id uuid NOT NULL REFERENCES td_history.configuration(id), runtime_id text NOT NULL,
 reference text NOT NULL, namespace text NOT NULL, code text NOT NULL, name text NOT NULL, unit text NOT NULL,
 packet_id text NOT NULL, packet_name text NOT NULL, definition jsonb NOT NULL,
 UNIQUE(configuration_id,runtime_id));
CREATE INDEX IF NOT EXISTS history_parameter_reference ON td_history.parameter_definition(reference,id);
CREATE TABLE IF NOT EXISTS td_history.sample(
 packet_event_id uuid NOT NULL REFERENCES td_history.event(id), parameter_id bigint NOT NULL REFERENCES td_history.parameter_definition(id),
 observed_at timestamptz NOT NULL, value_text text, value_kind text, value_float double precision, value_exact numeric,
 raw_text text, hex_source text, quality text NOT NULL, diagnostic text,
 PRIMARY KEY(packet_event_id,parameter_id));
CREATE INDEX IF NOT EXISTS history_sample_parameter_time ON td_history.sample(parameter_id,observed_at DESC,packet_event_id);
CREATE TABLE IF NOT EXISTS td_history.experiment(
 id uuid PRIMARY KEY, name text NOT NULL, started_at timestamptz NOT NULL, ended_at timestamptz, notes text NOT NULL DEFAULT '');
CREATE TABLE IF NOT EXISTS td_history.command_event(
 event_id uuid PRIMARY KEY REFERENCES td_history.event(id), evidence text NOT NULL, verified boolean NOT NULL,
 codes text[] NOT NULL DEFAULT '{}', names text[] NOT NULL DEFAULT '{}',
 success boolean, actor text NOT NULL DEFAULT '', capture_event_id uuid);
CREATE INDEX IF NOT EXISTS history_command_codes ON td_history.command_event USING gin(codes);
-- V0.10.9: judgment storage retired. Existing tables and rows are left untouched.
CREATE TABLE IF NOT EXISTS td_history.incident(
 event_id uuid PRIMARY KEY REFERENCES td_history.event(id), incident_key text NOT NULL,
 source_kind text NOT NULL, condition text NOT NULL, value_text text NOT NULL,
 released boolean NOT NULL DEFAULT false, release_reason text NOT NULL DEFAULT '', released_at timestamptz, actor text NOT NULL DEFAULT '');
CREATE INDEX IF NOT EXISTS history_incident_key ON td_history.incident(incident_key);
-- Small selection dimensions are maintained while ingesting. Historical discovery runs once
-- in the background, avoiding DISTINCT scans of the full event/sample history on every page.
CREATE TABLE IF NOT EXISTS td_history.query_choice(
 category text NOT NULL,value text NOT NULL,names text[] NOT NULL DEFAULT '{}',
 first_at timestamptz NOT NULL,last_at timestamptz NOT NULL,metadata jsonb NOT NULL DEFAULT '{}',
 PRIMARY KEY(category,value));
CREATE TABLE IF NOT EXISTS td_history.query_metadata_state(
 name text PRIMARY KEY,completed_at timestamptz NOT NULL DEFAULT now());
