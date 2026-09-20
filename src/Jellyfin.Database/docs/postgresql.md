# Running Jellyfin on PostgreSQL

New installations use PostgreSQL by default. An existing installation keeps whatever its
`config/database.xml` already says, so upgrading never moves your data on its own.

PostgreSQL **15 or newer** is required. The provider is tested against 15, 16, 17 and 18.

## Quick start with Docker Compose

```yaml
services:
  db:
    image: postgres:17
    environment:
      POSTGRES_DB: jellyfin
      POSTGRES_USER: jellyfin
      POSTGRES_PASSWORD_FILE: /run/secrets/jellyfin_db
    secrets: [jellyfin_db]
    volumes:
      - pgdata:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U jellyfin -d jellyfin"]
      interval: 5s
      timeout: 5s
      retries: 20

  jellyfin:
    image: jellyfin/jellyfin
    depends_on:
      db: { condition: service_healthy }
    environment:
      JELLYFIN_DATABASE__POSTGRES__HOST: db
      JELLYFIN_DATABASE__POSTGRES__DATABASE: jellyfin
      JELLYFIN_DATABASE__POSTGRES__USERNAME: jellyfin
      JELLYFIN_DATABASE__POSTGRES__PASSWORDFILE: /run/secrets/jellyfin_db
    secrets: [jellyfin_db]
    volumes:
      - config:/config
      - cache:/cache
      - /path/to/media:/media
    ports: ["8096:8096"]

secrets:
  jellyfin_db:
    file: ./jellyfin_db_password.txt

volumes: { pgdata: {}, config: {}, cache: {} }
```

Jellyfin waits up to 30 seconds for the database to start accepting connections, so a database that
is still coming up is not a failure.

## Settings

Every setting can come from the environment or from `config/database.xml`. The environment wins, so a
container can supply a password without it ever being written to disk.

| Environment | `database.xml` | Default |
| --- | --- | --- |
| `JELLYFIN_DATABASE__POSTGRES__HOST` | `Host` | `localhost` |
| `JELLYFIN_DATABASE__POSTGRES__PORT` | `Port` | `5432` |
| `JELLYFIN_DATABASE__POSTGRES__DATABASE` | `Database` | `jellyfin` |
| `JELLYFIN_DATABASE__POSTGRES__USERNAME` | `Username` | `jellyfin` |
| `JELLYFIN_DATABASE__POSTGRES__PASSWORD` | `Password` | none |
| `JELLYFIN_DATABASE__POSTGRES__PASSWORDFILE` | `PasswordFile` | none |
| `JELLYFIN_DATABASE__POSTGRES__CONNECTIONSTRING` | `ConnectionString` | none |
| `JELLYFIN_DATABASE__POSTGRES__SSLMODE` | `SslMode` | Npgsql's default |
| `JELLYFIN_DATABASE__POSTGRES__ROOTCERTIFICATE` | `RootCertificate` | none |
| `JELLYFIN_DATABASE__POSTGRES__COMMANDTIMEOUT` | `CommandTimeout` | `60` |
| `JELLYFIN_DATABASE__POSTGRES__MAXPOOLSIZE` | `MaxPoolSize` | `50` |
| `JELLYFIN_DATABASE__POSTGRES__MAINTENANCEDATABASE` | `MaintenanceDatabase` | `postgres` |
| `JELLYFIN_DATABASE__POSTGRES__CLIENTTOOLSPATH` | `ClientToolsPath` | searched |
| `JELLYFIN_DATABASE__POSTGRES__MIGRATIONBACKUPPOLICY` | `MigrationBackupPolicy` | `Required` |
| `JELLYFIN_DATABASE__POSTGRES__SERIALIZEWRITES` | `SerializeWrites` | `false` |

The `POSTGRES_HOST`, `POSTGRES_PORT`, `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD` and
`POSTGRES_PASSWORD_FILE` names the official `postgres` image uses are also honoured, at lower
precedence.

`AdditionalOptions` in `database.xml` passes any other Npgsql keyword through verbatim:

```xml
<PostgreSql>
  <Host>db</Host>
  <PasswordFile>/run/secrets/jellyfin_db</PasswordFile>
  <AdditionalOptions>
    <CustomDatabaseOption><Key>Keepalive</Key><Value>30</Value></CustomDatabaseOption>
  </AdditionalOptions>
</PostgreSql>
```

Passwords are never logged and never appear on a command line; the client tools receive them through
`PGPASSWORD`.

### TLS

```yaml
JELLYFIN_DATABASE__POSTGRES__SSLMODE: VerifyFull
JELLYFIN_DATABASE__POSTGRES__ROOTCERTIFICATE: /etc/ssl/certs/pg-ca.pem
```

`VerifyFull` checks the certificate and the host name and is the right choice over an untrusted
network. `Require` encrypts without verifying, which stops passive eavesdropping only.

## Privileges

The simplest setup is a database owned by the Jellyfin user:

```sql
CREATE ROLE jellyfin LOGIN PASSWORD '...';
CREATE DATABASE jellyfin OWNER jellyfin ENCODING 'UTF8' TEMPLATE template0;
```

Jellyfin creates the database itself if the user holds `CREATEDB` and it does not exist yet.

`CREATEDB` also decides how migration backups are taken:

- **With `CREATEDB`**, the database is copied with `CREATE DATABASE ... TEMPLATE` before a migration
  runs. Nothing external is needed, and a rollback renames the copy back into place.
- **Without it**, `pg_dump` and `psql` are used instead, and a client package matching the server's
  major version has to be installed, or `ClientToolsPath` set to where it lives.

If neither works, the migration is refused and the server stops before touching the schema. Setting
`MigrationBackupPolicy` to `Skip` runs it anyway, with no way back, and only makes sense if you take
your own backups.

Backups are pruned to `BackupRetention` copies, one by default. They are named
`<database>_jfbak_<timestamp>`, and only databases matching that pattern and owned by the Jellyfin
user are ever dropped.

## Tuning

`jit=off` is set by default. Jellyfin's folder filters make the planner estimate millions of rows for
queries that return a page, and it then spends seconds compiling a query that runs in milliseconds.

`MaxPoolSize` defaults to 50 against PostgreSQL's default `max_connections` of 100. Raise both
together if you run several servers against one instance.

`SerializeWrites` puts every transaction behind one advisory lock. It is off by default and trades
throughput for the single writer behaviour SQLite had; a long transaction such as a backup stalls
every writer behind it while it runs.

## Moving an existing library

There is no in-place conversion. A server with a `jellyfin.db` keeps using it, and pointing that
installation at PostgreSQL would start from an empty library.

Backup archives do not bridge the two either: restoring replaces the migration history wholesale, and
each provider numbers its migrations differently, so an archive records which database it came from
and a restore onto the other one is refused rather than silently corrupting it.

Note also that a plugin which opens `jellyfin.db` directly keeps reading that file and will not see
anything on PostgreSQL.

## Troubleshooting

**`Jellyfin could not use the PostgreSQL database ...`** — the message names the host, port, database
and user that were tried and what went wrong. Nothing has been written at that point, and
`database.xml` is left unwritten, so fixing the settings and starting again is enough. To fall back to
the embedded database, set `JELLYFIN_DATABASE__TYPE=Jellyfin-SQLite`.

**`... may not create tables in the 'public' schema`** — PostgreSQL 15 stopped granting that to
everyone. Make the Jellyfin user the database owner, as above.

**`... already contains an Entity Framework migration history that Jellyfin did not create`** — the
database belongs to one of the third party PostgreSQL plugins. Their schemas are close enough to look
plausible and different enough to corrupt, so point Jellyfin at an empty database instead.

**`No 'pg_dump' matching PostgreSQL N could be found`** — install `postgresql-client-N`, or set
`ClientToolsPath`. Granting the user `CREATEDB` avoids needing the client tools at all.
