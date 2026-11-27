\set ON_ERROR_STOP on
-- Dedicated objects only. Existing roles/databases are never changed.
SELECT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='telemetry_ingest')
    OR EXISTS(SELECT 1 FROM pg_database WHERE datname='telemetry_history') AS history_exists \gset
\if :history_exists
\echo Existing history database or role found. Nothing has been changed.
\echo Use its existing credentials in the application history storage settings.
\else
BEGIN;
CREATE ROLE telemetry_ingest LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;
\echo Set the NEW application database password below. This is not the postgres administrator password.
\password telemetry_ingest
COMMIT;
CREATE DATABASE telemetry_history OWNER telemetry_ingest ENCODING 'UTF8' TEMPLATE template0;
\echo Created telemetry_history with dedicated owner telemetry_ingest.
\echo Enter these credentials in the application history storage settings.
\endif
