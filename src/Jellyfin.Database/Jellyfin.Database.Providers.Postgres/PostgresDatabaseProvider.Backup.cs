using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Providers.Postgres.Backup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jellyfin.Database.Providers.Postgres;

/// <content>
/// Migration backup and restore.
/// </content>
public sealed partial class PostgresDatabaseProvider
{
    /// <inheritdoc />
    public async Task<string> MigrationBackupFast(CancellationToken cancellationToken)
    {
        if (await IsDatabaseEmptyAsync(cancellationToken).ConfigureAwait(false))
        {
            // A first start has nothing to lose yet, and demanding a backup here would make a fresh
            // install impossible for a role that can neither copy a database nor reach pg_dump.
            _logger.LogDebug("The database holds no Jellyfin tables yet, so no migration backup is needed");
            return new BackupKey(BackupStrategyKind.None, "empty").ToString();
        }

        var template = new TemplateDatabaseBackupStrategy(Settings, _logger);
        var key = await template.TryBackupAsync(cancellationToken).ConfigureAwait(false);
        if (key is not null)
        {
            return key.Value.ToString();
        }

        try
        {
            var serverVersion = await GetServerMajorVersionAsync(cancellationToken).ConfigureAwait(false);
            var dump = new PgDumpBackupStrategy(Settings, _applicationPaths, new PostgresToolRunner(Settings, _logger), _logger);
            var dumpKey = await dump.BackupAsync(serverVersion, cancellationToken).ConfigureAwait(false);
            return dumpKey.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (Settings.Options.MigrationBackupPolicy == PostgresMigrationBackupPolicy.Skip)
            {
                _logger.LogCritical(
                    ex,
                    "No migration backup could be taken and the configured policy is Skip, so the migration will run "
                    + "with no way back. If it fails, the database has to be restored from your own backup.");
                return new BackupKey(BackupStrategyKind.None, "skipped").ToString();
            }

            throw new DatabaseProviderStartupException(
                "Jellyfin has to back the database up before migrating it, and neither method worked."
                + Environment.NewLine
                + "  - Copying the database needs the CREATEDB privilege: ALTER ROLE "
                + Settings.Builder.Username
                + " CREATEDB;"
                + Environment.NewLine
                + "  - pg_dump was not usable: "
                + ex.Message
                + Environment.NewLine
                + "Install the matching postgresql-client package or set PostgreSql/ClientToolsPath in database.xml."
                + Environment.NewLine
                + "To migrate anyway, having taken your own backup, set PostgreSql/MigrationBackupPolicy to Skip.",
                ex);
        }
    }

    /// <inheritdoc />
    public async Task RestoreBackupFast(string key, CancellationToken cancellationToken)
    {
        var parsed = BackupKey.Parse(key);
        switch (parsed.Kind)
        {
            case BackupStrategyKind.Template:
                await new TemplateDatabaseBackupStrategy(Settings, _logger)
                    .RestoreAsync(parsed.Value, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case BackupStrategyKind.Dump:
                var serverVersion = await GetServerMajorVersionAsync(cancellationToken).ConfigureAwait(false);
                await new PgDumpBackupStrategy(Settings, _applicationPaths, new PostgresToolRunner(Settings, _logger), _logger)
                    .RestoreAsync(parsed.Value, serverVersion, await GetModelTablesAsync(cancellationToken).ConfigureAwait(false), cancellationToken)
                    .ConfigureAwait(false);
                break;

            default:
                _logger.LogCritical(
                    "The migration failed and no backup was taken ({Reason}), so the database is in whatever state the "
                    + "migration left it. Restore it from your own backup.",
                    parsed.Value);
                break;
        }
    }

    /// <inheritdoc />
    public async Task DeleteBackup(string key)
    {
        var parsed = BackupKey.Parse(key);
        switch (parsed.Kind)
        {
            case BackupStrategyKind.Template:
                await new TemplateDatabaseBackupStrategy(Settings, _logger)
                    .DeleteAsync(parsed.Value, CancellationToken.None)
                    .ConfigureAwait(false);
                break;

            case BackupStrategyKind.Dump:
                new PgDumpBackupStrategy(Settings, _applicationPaths, new PostgresToolRunner(Settings, _logger), _logger)
                    .Delete(parsed.Value);
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Reports whether the database holds anything worth backing up.
    /// </summary>
    private async Task<bool> IsDatabaseEmptyAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(Settings.BuildConnectionStringFor(Settings.Builder.Database!));
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                // The history table on its own is not data: Entity Framework creates it before the
                // first migration runs.
                command.CommandText = """
                    SELECT count(*)
                    FROM information_schema.tables
                    WHERE table_schema = current_schema()
                      AND table_type = 'BASE TABLE'
                      AND table_name <> '__EFMigrationsHistory'
                    """;
                var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture) == 0;
            }
        }
    }

    private async Task<int> GetServerMajorVersionAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(Settings.BuildConnectionStringFor(Settings.Builder.Database!));
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "SELECT current_setting('server_version_num')::int";
                var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture) / 10000;
            }
        }
    }

    private async Task<IReadOnlyList<string>> GetModelTablesAsync(CancellationToken cancellationToken)
    {
        if (DbContextFactory is null)
        {
            return [];
        }

        var context = await DbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            return GetModelTables(context).ToArray();
        }
    }
}
