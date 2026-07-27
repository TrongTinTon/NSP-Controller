-- NSP Gatekeeper Controller - deployment/bootstrap reference.
--
-- Runtime does NOT execute this file and does NOT invoke psql.exe.
-- Controller first-start bootstrap performs the same operations through Npgsql.
--
-- Manual deployment example:
--   psql -U postgres -v app_password='YOUR_DEPLOYMENT_SECRET' -f database/00_create_database.sql
--
-- No production password is stored in this source file.

\if :{?app_password}
\else
\echo 'ERROR: app_password is required. Use -v app_password=...'
\quit 3
\endif

SELECT format(
    'CREATE ROLE %I LOGIN PASSWORD %L',
    'parking_log_user',
    :'app_password'
)
WHERE NOT EXISTS (
    SELECT 1 FROM pg_roles WHERE rolname = 'parking_log_user'
)\gexec

SELECT format(
    'CREATE DATABASE %I OWNER %I ENCODING %L TEMPLATE template0',
    'nsp_db',
    'parking_log_user',
    'UTF8'
)
WHERE NOT EXISTS (
    SELECT 1 FROM pg_database WHERE datname = 'nsp_db'
)\gexec

GRANT CONNECT ON DATABASE nsp_db TO parking_log_user;
