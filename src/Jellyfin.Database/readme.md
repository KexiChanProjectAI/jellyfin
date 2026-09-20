# Jellyfin's database layer

Jellyfin talks to its database through EF Core behind `IJellyfinDatabaseProvider`. Two providers ship
in the server:

| Project | Key | Notes |
| --- | --- | --- |
| `Jellyfin.Database.Providers.Postgres` | `Jellyfin-PgSql` | The default for new installations. See [docs/postgresql.md](docs/postgresql.md). |
| `Jellyfin.Database.Providers.Sqlite` | `Jellyfin-SQLite` | Embedded, needs no server. The default for anything that already has a `jellyfin.db`. |

A third party provider can still be loaded by setting `DatabaseType` to `PLUGIN_PROVIDER`.

## Which provider a server uses

`config/database.xml` decides. When it has no `DatabaseType`:

1. `JELLYFIN_DATABASE__TYPE` is used when set. This is the documented way to ask for SQLite.
2. Otherwise, a `data/jellyfin.db` or `data/library.db` means this is an upgrade, and SQLite is kept.
3. Otherwise it is a new installation, and PostgreSQL is used.

A PostgreSQL choice is only written to `database.xml` once the connection has been proven, so a
server that cannot reach its database can still be pointed at SQLite on the next start.

## Writing migrations

Each provider owns its own migration set, because migrations contain provider specific DDL. **A
schema change needs a migration in every provider**, and the tests fail otherwise:
`CheckForUnappliedMigrations_SqLite` and `CheckForUnappliedMigrations_Postgres` both compare the model
against the migrations.

```shell
dotnet tool restore

# SQLite
dotnet ef migrations add {NAME} \
  --project "src/Jellyfin.Database/Jellyfin.Database.Providers.Sqlite" \
  --output-dir Migrations

# PostgreSQL
dotnet ef migrations add {NAME} \
  --project "src/Jellyfin.Database/Jellyfin.Database.Providers.Postgres" \
  --output-dir Migrations
```

The provider is chosen by `--project`, which is the project whose `IDesignTimeDbContextFactory` is
found. The `-- --migration-provider` argument that older instructions mention is not read by either
factory.

### Two rules the migration ordering depends on

`JellyfinMigrationService` runs EF migrations and the code migrations in
`Jellyfin.Server/Migrations/Routines` interleaved, ordered by identifier as plain text. So:

- **Give the same change the same identifier in both providers.** Otherwise a code migration that has
  to run after it runs after it on one provider and before it on the other.
- **Raw SQL in a code migration has to be guarded**, with `Database.IsSqlite()` or equivalent. A
  routine that reaches a server of another type must skip rather than fail.

PostgreSQL's `InitialCreate` is deliberately dated `20250101000000`, before every code migration,
because some code migrations run on a fresh install and query tables that have to exist by then.

## Running the tests

The SQLite tests need nothing. The PostgreSQL tests skip unless a server is configured:

```shell
export JELLYFIN_POSTGRES_TEST_CONNECTION="Host=localhost;Username=jellyfin_test;Password=...;Database=postgres"
dotnet test tests/Jellyfin.Server.Implementations.Tests --filter "FullyQualifiedName~Postgres"
```

The user in that connection string needs `CREATEDB`, because each test runs against a database of its
own. `CREATEROLE` additionally lets the test that covers the `pg_dump` fallback run; without it that
one test skips.

Set `JELLYFIN_POSTGRES_TEST_REQUIRED=1` to turn skips into failures, which is what CI does so that a
misconfigured runner cannot report success by skipping everything.
