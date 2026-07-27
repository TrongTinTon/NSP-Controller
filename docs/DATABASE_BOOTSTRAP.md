# NSP Controller Local PostgreSQL Bootstrap

## Runtime sequence

`Controller start -> EnsureDatabase -> EnsureSchema -> Controller runtime`

- `EnsureDatabase` is implemented by `Infrastructure/Database/DatabaseBootstrapper.cs`.
- `EnsureSchema` remains in `Infrastructure/Database/LocalStore.cs`.

### Existing database

If `PostgreSqlConnectionString` can open the configured database, no admin credential is used and no role/database DDL is executed.

### Fresh database

If the application connection cannot be opened, the Controller connects to maintenance database `postgres` using `PostgreSqlAdminConnectionString`. It then:

1. Creates the application login role only if it does not exist.
2. Creates the application database only if it does not exist.
3. Sets the new database owner to the application role.
4. Grants CONNECT.
5. Reconnects using the normal application connection string.
6. Runs `database/init_database.sql` to create only technical tables/indexes.

If `PostgreSqlAdminConnectionString` is blank, the Controller tries the application credentials against database `postgres`. This fallback works only when that role already exists and has enough PostgreSQL privileges.

## Configuration

```xml
<add key="PostgreSqlConnectionString"
     value="Host=127.0.0.1;Port=5432;Database=nsp_db;Username=parking_log_user;Password=...;Pooling=true" />

<add key="PostgreSqlAdminConnectionString"
     value="Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=...;Pooling=false" />
```

The admin connection is used only when role/database bootstrap is needed. Do not commit production passwords into source control.

## Manual deployment reference

`database/00_create_database.sql` provides the equivalent role/database bootstrap for administrators using `psql`. The Controller runtime does not execute this file and does not depend on `psql.exe`.

## Optional admin connection string

`PostgreSqlAdminConnectionString` may be left empty. Empty values, whitespace, `""`, and `''` are normalized as not configured. In that case the Controller derives a maintenance connection from `PostgreSqlConnectionString` and changes only `Database=postgres`. A dedicated PostgreSQL administrator connection is required only when the application role/database still need to be created and the application account does not have the required privileges.

## Invalid optional admin connection value

`PostgreSqlAdminConnectionString` is optional. From Controller 1.1.6, if an existing `.exe.config` contains an invalid or incomplete value, the Controller logs a warning and falls back to the validated `PostgreSqlConnectionString` with `Database=postgres`. An invalid optional admin value no longer stops startup by itself.

If the application role/database do not yet exist and the fallback account cannot create them, configure a valid PostgreSQL administrator connection or provision the role/database during deployment.
