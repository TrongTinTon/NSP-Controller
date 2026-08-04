# Connection troubleshooting

## Core API

Check:

- Controller Code;
- Base URL;
- Client ID and Client Secret;
- optional database query value;
- Windows firewall and reverse proxy;
- Edge endpoint registration.

When the configured URL is unreachable and Zeroconf is enabled, Controller accepts exactly one discovered NSP service. Multiple discoveries require an explicit URL.

## Reader

Check the local Reader row:

- Driver key;
- COM/IP endpoint;
- TCP/COM port;
- Reader serial matching Edge configuration;
- configured Reader Port list.

The Controller intentionally does not fall back to Port 1 when no Port is configured.

## PostgreSQL

Check `NSP_POSTGRES_CONNECTION`, role privileges, database reachability and `pg_hba.conf`. No password is included in source defaults.
