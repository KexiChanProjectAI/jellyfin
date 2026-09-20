using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Providers.Postgres.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jellyfin.Database.Providers.Postgres.Startup;

/// <summary>
/// Checks that the configured PostgreSQL database exists and can be used, before anything tries to
/// migrate it.
/// </summary>
internal sealed class PostgresStartupProbe
{
    private static readonly TimeSpan _connectRetryWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _connectRetryDelay = TimeSpan.FromSeconds(2);

    private readonly PostgresConnectionSettings _settings;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresStartupProbe"/> class.
    /// </summary>
    /// <param name="settings">The resolved connection settings.</param>
    /// <param name="logger">A logger.</param>
    public PostgresStartupProbe(PostgresConnectionSettings settings, ILogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    private static int MinimumVersionNumber => PostgresDatabaseProvider.MinimumServerVersion * 10000;

    /// <summary>
    /// Runs every check, creating the database if it is missing and it is allowed to.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    /// <exception cref="DatabaseProviderStartupException">Thrown when the database cannot be used.</exception>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await CheckServerAsync(connection, cancellationToken).ConfigureAwait(false);
            await CheckSchemaPrivilegeAsync(connection, cancellationToken).ConfigureAwait(false);
            await CheckForForeignSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Opens a connection to the Jellyfin database, creating it if the server says it is missing.
    /// </summary>
    private async Task<NpgsqlConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        var createAttempted = false;

        while (true)
        {
            NpgsqlConnection? pending = null;
            try
            {
                pending = new NpgsqlConnection(_settings.ConnectionString);
                await pending.OpenAsync(cancellationToken).ConfigureAwait(false);

                // Hand ownership to the caller, so the cleanup below leaves it alone.
                var opened = pending;
                pending = null;
                return opened;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidCatalogName && !createAttempted)
            {
                // The server is up and answering, it simply has no such database yet. This is the
                // normal first start.
                createAttempted = true;
                await CreateDatabaseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.InvalidPassword or PostgresErrorCodes.InvalidAuthorizationSpecification)
            {
                // Credentials will not fix themselves, so retrying only delays the report.
                throw Fail($"authentication failed ({ex.MessageText})", ex);
            }
            catch (Exception ex) when (IsRetryableStartupFailure(ex) && deadline.Elapsed < _connectRetryWindow)
            {
                // A database started alongside the server in the same compose file is routinely not
                // accepting connections yet.
                _logger.LogInformation(
                    "Waiting for PostgreSQL at {Host}:{Port} to accept connections: {Reason}",
                    _settings.Builder.Host,
                    _settings.Builder.Port,
                    ex.Message);
                await Task.Delay(_connectRetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not DatabaseProviderStartupException and not OperationCanceledException)
            {
                throw Fail(ex.Message, ex);
            }
            finally
            {
                if (pending is not null)
                {
                    await pending.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        var maintenanceDatabase = string.IsNullOrWhiteSpace(_settings.Options.MaintenanceDatabase)
            ? "postgres"
            : _settings.Options.MaintenanceDatabase;
        var database = _settings.Builder.Database!;

        _logger.LogInformation("The database '{Database}' does not exist yet, creating it", database);

        try
        {
            var connection = new NpgsqlConnection(_settings.BuildConnectionStringFor(maintenanceDatabase));
            await using (connection.ConfigureAwait(false))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                // template0 rather than the default template1, so a local addition to template1 does
                // not end up in Jellyfin's database. UTF8 is not negotiable: every other encoding
                // rejects text that clients legitimately send.
                var command = connection.CreateCommand();
                await using (command.ConfigureAwait(false))
                {
                    // An identifier cannot be a parameter in any dialect, so the database name is
                    // escaped instead. It comes from the server's own configuration rather than from
                    // a request, and QuoteIdentifier doubles embedded quotes.
#pragma warning disable CA2100
                    command.CommandText = string.Create(
                        CultureInfo.InvariantCulture,
                        $"CREATE DATABASE {QuoteIdentifier(database)} ENCODING 'UTF8' TEMPLATE template0");
#pragma warning restore CA2100
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            throw Fail(
                $"the database '{database}' does not exist and the user '{_settings.Builder.Username}' is not "
                + "allowed to create it. Ask a database administrator to run:"
                + Environment.NewLine
                + $"    CREATE DATABASE {QuoteIdentifier(database)} OWNER {QuoteIdentifier(_settings.Builder.Username!)} ENCODING 'UTF8' TEMPLATE template0;",
                ex);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DuplicateDatabase)
        {
            // Another instance won the race. That is the outcome we wanted anyway.
            _logger.LogDebug("The database '{Database}' was created concurrently", database);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            throw Fail(
                $"the database '{database}' does not exist, and the maintenance database "
                + $"'{maintenanceDatabase}' used to create it does not exist either. Set "
                + "PostgreSql/MaintenanceDatabase in database.xml to a database this user can connect to.",
                ex);
        }
    }

    private async Task CheckServerAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText =
                "SELECT current_setting('server_version_num')::int, "
                + "current_setting('server_encoding'), "
                + "(SELECT rolcreatedb FROM pg_roles WHERE rolname = current_user)";

            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw Fail("the server did not report its version.", null);
                }

                var versionNumber = reader.GetInt32(0);
                var encoding = reader.GetString(1);
                var canCreateDatabase = !await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)
                    && reader.GetBoolean(2);

                if (versionNumber < MinimumVersionNumber)
                {
                    throw Fail(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"the server is version {versionNumber / 10000}, but Jellyfin requires PostgreSQL {PostgresDatabaseProvider.MinimumServerVersion} or newer."),
                        null);
                }

                if (!encoding.Equals("UTF8", StringComparison.OrdinalIgnoreCase))
                {
                    throw Fail(
                        $"the database's encoding is {encoding}, but Jellyfin requires UTF8. Recreate the database with "
                        + "ENCODING 'UTF8' TEMPLATE template0.",
                        null);
                }

                _logger.LogInformation(
                    "Connected to PostgreSQL {Version} as '{User}' (may create databases: {CanCreateDatabase})",
                    versionNumber / 10000,
                    _settings.Builder.Username,
                    canCreateDatabase);

                if (!canCreateDatabase)
                {
                    // Not fatal, but it decides which backup strategy is available, and finding that
                    // out at the moment a migration needs a backup is too late to be useful.
                    _logger.LogInformation(
                        "The user '{User}' may not create databases, so migration backups will use pg_dump instead of a database copy",
                        _settings.Builder.Username);
                }
            }
        }
    }

    private async Task CheckSchemaPrivilegeAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = "SELECT has_schema_privilege(current_user, current_schema(), 'CREATE'), current_schema()";
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                    && !await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false)
                    && !reader.GetBoolean(0))
                {
                    var schema = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false)
                        ? "public"
                        : reader.GetString(1);

                    // PostgreSQL 15 stopped granting CREATE on public to everyone. On a database the
                    // Jellyfin user does not own, this is the first thing that breaks, and it breaks
                    // as a confusing permission error partway through creating the schema.
                    throw Fail(
                        $"the user '{_settings.Builder.Username}' may not create tables in the '{schema}' schema of "
                        + $"database '{_settings.Builder.Database}'. Ask a database administrator to run:"
                        + Environment.NewLine
                        + $"    ALTER DATABASE {QuoteIdentifier(_settings.Builder.Database!)} OWNER TO {QuoteIdentifier(_settings.Builder.Username!)};"
                        + Environment.NewLine
                        + $"    GRANT CREATE ON SCHEMA {QuoteIdentifier(schema)} TO {QuoteIdentifier(_settings.Builder.Username!)};",
                        null);
                }
            }
        }
    }

    private async Task CheckForForeignSchemaAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            // A database that already carries a migration history from something that is not this
            // provider belongs to one of the community PostgreSQL plugins. Their schemas are close
            // enough to look plausible and different enough to corrupt, so refuse rather than
            // migrate into it.
            command.CommandText = """
                SELECT EXISTS (SELECT 1 FROM "__EFMigrationsHistory"),
                       EXISTS (SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = @initial)
                """;
            command.Parameters.AddWithValue("initial", PostgresMigrations.InitialCreateId);

            try
            {
                var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                        && reader.GetBoolean(0)
                        && !reader.GetBoolean(1))
                    {
                        throw Fail(
                            $"the database '{_settings.Builder.Database}' already contains an Entity Framework migration "
                            + "history that Jellyfin did not create. This is what a third party PostgreSQL plugin leaves "
                            + "behind, and its schema is not compatible. Point Jellyfin at an empty database, or migrate "
                            + "the old one through a SQLite install first.",
                            null);
                    }
                }
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
            {
                // No history table at all is the normal fresh database.
            }
        }
    }

    private static bool IsRetryableStartupFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is System.Net.Sockets.SocketException or TimeoutException)
            {
                return true;
            }

            if (current is PostgresException postgres
                && postgres.SqlState is PostgresErrorCodes.CannotConnectNow or PostgresErrorCodes.TooManyConnections)
            {
                return true;
            }
        }

        return false;
    }

    private DatabaseProviderStartupException Fail(string reason, Exception? inner)
    {
        var message =
            $"Jellyfin could not use the PostgreSQL database (host '{_settings.Builder.Host}', port {_settings.Builder.Port}, "
            + $"database '{_settings.Builder.Database}', user '{_settings.Builder.Username}'): {reason}"
            + Environment.NewLine
            + "New installations use PostgreSQL by default. Either"
            + Environment.NewLine
            + "  1. supply connection settings, through JELLYFIN_DATABASE__POSTGRES__HOST, __PORT, __DATABASE, __USERNAME"
            + Environment.NewLine
            + "     and __PASSWORD or __PASSWORDFILE, or a <PostgreSql> section in database.xml, or"
            + Environment.NewLine
            + "  2. use the embedded SQLite database instead: set JELLYFIN_DATABASE__TYPE=Jellyfin-SQLite and restart.";

        return new DatabaseProviderStartupException(message, inner);
    }

    private static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
