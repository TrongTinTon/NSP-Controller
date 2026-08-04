# Local PostgreSQL bootstrap

Controller requires `PostgreSqlConnectionString`. The optional `PostgreSqlAdminConnectionString` is used only on first start to create the application role/database when needed.

Recommended deployment:

1. Create a dedicated PostgreSQL role and database.
2. Grant the application role ownership or required schema privileges.
3. Supply connection strings through environment variables or deployment-specific configuration.
4. Start Controller; `LocalStore.EnsureSchema()` applies `database/init_database.sql`.

Environment variables:

```text
NSP_POSTGRES_CONNECTION
NSP_POSTGRES_ADMIN_CONNECTION
```

The 1.4.8 schema stores Reader technical configuration without business Port lists. A local database created by a legacy Antenna-based Controller should be recreated or migrated explicitly before using this source. An existing `ports_json` column may remain unused, but fresh installs do not create it in `controller_reader`.

Tables:

- `controller_reader`
- `controller_reader_runtime_status`
- `controller_parking_outbox`
- `controller_lane_calibration_outbox`
