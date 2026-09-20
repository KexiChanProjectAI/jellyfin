using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Postgres;

/// <summary>
/// Migration backup and restore against a live server.
/// </summary>
/// <remarks>
/// This is the path that decides whether a failed migration can be undone, so each strategy is
/// exercised end to end rather than mocked.
/// </remarks>
[Trait("Category", "PostgreSQL")]
public class PostgresBackupTests
{
    /// <summary>
    /// A first start has an empty database and nothing to lose, and demanding a backup there would
    /// make a fresh install impossible for a role that can neither copy a database nor run pg_dump.
    /// </summary>
    [Fact]
    public async Task EmptyDatabase_NeedsNoBackup()
    {
        var database = await PostgresTestDatabase.CreateAsync(migrate: false).ConfigureAwait(true);
        await using (database.ConfigureAwait(true))
        {
            var provider = database.CreateProvider();
            var key = await provider.MigrationBackupFast(TestContext.Current.CancellationToken).ConfigureAwait(true);

            Assert.StartsWith("none:", key, StringComparison.Ordinal);

            // Restoring a backup that was never taken must not pretend to have worked, but it must
            // also not throw and mask the original migration failure.
            await provider.RestoreBackupFast(key, TestContext.Current.CancellationToken).ConfigureAwait(true);
            await provider.DeleteBackup(key).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// With the CREATEDB privilege the database is copied, and the copy can be put back.
    /// </summary>
    [Fact]
    public async Task TemplateStrategy_BacksUpAndRestores()
    {
        var database = await PostgresTestDatabase.CreateAsync().ConfigureAwait(true);
        await using (database.ConfigureAwait(true))
        {
            var provider = database.CreateProvider();
            var keptId = Guid.NewGuid();

            var seed = database.CreateDbContext();
            await using (seed.ConfigureAwait(true))
            {
                seed.BaseItems.Add(new BaseItemEntity { Id = keptId, Type = "Movie", Name = "before backup" });
                await seed.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            var key = await provider.MigrationBackupFast(TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.StartsWith("tpl:", key, StringComparison.Ordinal);

            // Stand in for a migration that damages the database.
            var damage = database.CreateDbContext();
            await using (damage.ConfigureAwait(true))
            {
                await damage.Database.ExecuteSqlRawAsync(
                    "DELETE FROM \"BaseItems\"",
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
                await damage.Database.ExecuteSqlRawAsync(
                    "DROP TABLE \"ActivityLogs\"",
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            await provider.RestoreBackupFast(key, TestContext.Current.CancellationToken).ConfigureAwait(true);

            var restored = database.CreateDbContext();
            await using (restored.ConfigureAwait(true))
            {
                Assert.True(await restored.BaseItems.AnyAsync(e => e.Id.Equals(keptId), TestContext.Current.CancellationToken).ConfigureAwait(true));

                // The dropped table has to be back too, which a row level restore would not achieve.
                Assert.Equal(0, await restored.ActivityLogs.CountAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
            }
        }
    }

    /// <summary>
    /// The backup is cleaned up when the migration succeeds, and deleting is refused for anything
    /// that is not one of ours.
    /// </summary>
    [Fact]
    public async Task TemplateStrategy_DeletesItsOwnBackupOnly()
    {
        var database = await PostgresTestDatabase.CreateAsync().ConfigureAwait(true);
        await using (database.ConfigureAwait(true))
        {
            var provider = database.CreateProvider();

            var seed = database.CreateDbContext();
            await using (seed.ConfigureAwait(true))
            {
                seed.BaseItems.Add(new BaseItemEntity { Id = Guid.NewGuid(), Type = "Movie" });
                await seed.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            var key = await provider.MigrationBackupFast(TestContext.Current.CancellationToken).ConfigureAwait(true);
            var backupName = key["tpl:".Length..];

            await provider.DeleteBackup(key).ConfigureAwait(true);
            Assert.False(await DatabaseExistsAsync(database, backupName).ConfigureAwait(true));

            // A key naming the live database must not drop it.
            await provider.DeleteBackup("tpl:" + database.DatabaseName).ConfigureAwait(true);
            Assert.True(await DatabaseExistsAsync(database, database.DatabaseName).ConfigureAwait(true));
        }
    }

    /// <summary>
    /// Without CREATEDB the provider has to fall back to pg_dump rather than fail.
    /// </summary>
    [Fact]
    public async Task WithoutCreateDatabasePrivilege_FallsBackToPgDump()
    {
        var database = await PostgresTestDatabase.CreateAsync().ConfigureAwait(true);
        await using (database.ConfigureAwait(true))
        {
            var restricted = await database.CreateRestrictedRoleAsync().ConfigureAwait(true);
            if (restricted is null)
            {
                Assert.Skip("The test role may not create other roles, so the no-CREATEDB path cannot be exercised.");
                return;
            }

            using var temporaryPaths = new TemporaryApplicationPaths();
            var provider = database.CreateProvider(restricted, temporaryPaths.Paths);

            var seed = database.CreateDbContext();
            await using (seed.ConfigureAwait(true))
            {
                seed.BaseItems.Add(new BaseItemEntity { Id = Guid.NewGuid(), Type = "Movie", Name = "dumped" });
                await seed.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            string key;
            try
            {
                key = await provider.MigrationBackupFast(TestContext.Current.CancellationToken).ConfigureAwait(true);
            }
            catch (DatabaseProviderStartupException ex) when (ex.Message.Contains("pg_dump", StringComparison.Ordinal))
            {
                Assert.Skip("No pg_dump matching the server version is installed.");
                return;
            }

            Assert.StartsWith("dump:", key, StringComparison.Ordinal);

            var dump = Directory.GetFiles(
                Path.Combine(temporaryPaths.Paths.DataPath, "PostgresBackups"),
                "*.sql").Single();
            var contents = await File.ReadAllTextAsync(dump, TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Contains("PostgreSQL database dump complete", contents, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A rollback that cannot find its backup has to say so. Returning quietly would tell the
    /// migration service the database was restored when it was not.
    /// </summary>
    [Fact]
    public async Task MissingDumpFile_FailsLoudly()
    {
        var database = await PostgresTestDatabase.CreateAsync().ConfigureAwait(true);
        await using (database.ConfigureAwait(true))
        {
            using var temporaryPaths = new TemporaryApplicationPaths();
            var provider = database.CreateProvider(applicationPaths: temporaryPaths.Paths);

            await Assert.ThrowsAsync<FileNotFoundException>(
                () => provider.RestoreBackupFast("dump:20250101000000", TestContext.Current.CancellationToken))
                .ConfigureAwait(true);
        }
    }

    /// <summary>
    /// A key that came from neither strategy has to be rejected rather than guessed at.
    /// </summary>
    [Fact]
    public async Task UnknownKey_IsRejected()
    {
        var database = await PostgresTestDatabase.CreateAsync().ConfigureAwait(true);
        await using (database.ConfigureAwait(true))
        {
            var provider = database.CreateProvider();
            await Assert.ThrowsAsync<ArgumentException>(
                () => provider.RestoreBackupFast("something-else", TestContext.Current.CancellationToken))
                .ConfigureAwait(true);
        }
    }

    private static async Task<bool> DatabaseExistsAsync(PostgresTestDatabase database, string name)
    {
        var result = await database
            .ScalarOnMaintenanceAsync($"SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = '{name}')")
            .ConfigureAwait(false);
        return result is true;
    }
}
