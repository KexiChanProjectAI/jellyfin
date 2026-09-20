using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Postgres;
using Jellyfin.Database.Providers.Sqlite;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Postgres;

/// <summary>
/// The locking behaviours depend on the provider classifying its own failures, so both providers are
/// checked rather than only the one the change was made for.
/// </summary>
public class PostgresLockingTests
{
    /// <summary>
    /// Selecting a retrying mode has to actually retry on SQLite. The classifier replaced a message
    /// match, and getting it wrong would turn the mode into a silent no-op.
    /// </summary>
    [Fact]
    public void Sqlite_ClassifiesItsOwnLockErrors()
    {
        var provider = new SqliteDatabaseProvider(
            new Mock<IApplicationPaths>().Object,
            NullLogger<SqliteDatabaseProvider>.Instance);

        Assert.Equal(DatabaseErrorKind.TransientLock, provider.ClassifyException(MakeSqliteError(5)));
        Assert.Equal(DatabaseErrorKind.TransientLock, provider.ClassifyException(MakeSqliteError(6)));
        Assert.Equal(DatabaseErrorKind.UniqueViolation, provider.ClassifyException(MakeSqliteError(19)));
        Assert.Equal(DatabaseErrorKind.Unknown, provider.ClassifyException(new InvalidOperationException("nothing to do with the database")));

        // Wrapped the way SaveChanges surfaces it.
        Assert.Equal(
            DatabaseErrorKind.TransientLock,
            provider.ClassifyException(new DbUpdateException("save failed", MakeSqliteError(5))));
    }

    /// <summary>
    /// SQLite can resume a transaction after a busy failure, so a single statement is still worth
    /// retrying there. PostgreSQL cannot.
    /// </summary>
    [Fact]
    public void RetryScope_DiffersByProvider()
    {
        // Declared as default interface members, so they are only reachable through the interface,
        // which is also how every caller reaches them.
        IJellyfinDatabaseProvider sqlite = new SqliteDatabaseProvider(
            new Mock<IApplicationPaths>().Object,
            NullLogger<SqliteDatabaseProvider>.Instance);
        IJellyfinDatabaseProvider postgres = new PostgresDatabaseProvider(
            new Mock<IApplicationPaths>().Object,
            NullLogger<PostgresDatabaseProvider>.Instance);

        Assert.True(sqlite.CanRetryInsideTransaction);
        Assert.False(postgres.CanRetryInsideTransaction);
    }

    /// <summary>
    /// The pessimistic behaviour holds a thread affine lock across an await, which only survives
    /// because SQLite's async completes synchronously. It has to be refused rather than left to throw
    /// at the first real write.
    /// </summary>
    [Fact]
    public void PessimisticLocking_IsRefusedOnPostgres()
    {
        IJellyfinDatabaseProvider postgres = new PostgresDatabaseProvider(
            new Mock<IApplicationPaths>().Object,
            NullLogger<PostgresDatabaseProvider>.Instance);
        IJellyfinDatabaseProvider sqlite = new SqliteDatabaseProvider(
            new Mock<IApplicationPaths>().Object,
            NullLogger<SqliteDatabaseProvider>.Instance);

        Assert.Equal(
            DatabaseLockingBehaviorTypes.NoLock,
            postgres.NormalizeLockingBehavior(DatabaseLockingBehaviorTypes.Pessimistic));
        Assert.Equal(
            DatabaseLockingBehaviorTypes.Optimistic,
            postgres.NormalizeLockingBehavior(DatabaseLockingBehaviorTypes.Optimistic));

        // SQLite keeps whatever was asked for.
        Assert.Equal(
            DatabaseLockingBehaviorTypes.Pessimistic,
            sqlite.NormalizeLockingBehavior(DatabaseLockingBehaviorTypes.Pessimistic));
    }

    /// <summary>
    /// The optimistic behaviour has to survive a real write on PostgreSQL, since its interceptors sit
    /// on every command and transaction.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task OptimisticLocking_WorksAgainstPostgres()
    {
        var database = await PostgresTestDatabase.CreateAsync().ConfigureAwait(true);
        await using (database.ConfigureAwait(true))
        {
            var provider = database.CreateProvider();
            var behavior = new OptimisticLockBehavior(NullLogger<OptimisticLockBehavior>.Instance, provider);

            var builder = new DbContextOptionsBuilder<JellyfinDbContext>();
            provider.Initialise(builder, database.BuildConfiguration());
            behavior.Initialise(builder);

            var context = new JellyfinDbContext(
                builder.Options,
                NullLogger<JellyfinDbContext>.Instance,
                provider,
                behavior);

            await using (context.ConfigureAwait(true))
            {
                var id = Guid.NewGuid();
                context.BaseItems.Add(new BaseItemEntity { Id = id, Type = "Movie", Name = "optimistic" });
                await context.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

                Assert.True(await context.BaseItems
                    .AnyAsync(e => e.Id.Equals(id), TestContext.Current.CancellationToken)
                    .ConfigureAwait(true));

                // An explicit transaction exercises the transaction interceptor as well.
                var transaction = await context.Database
                    .BeginTransactionAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
                await using (transaction.ConfigureAwait(true))
                {
                    context.BaseItems.Add(new BaseItemEntity { Id = Guid.NewGuid(), Type = "Movie" });
                    await context.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
                    await transaction.CommitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
                }

                Assert.Equal(3, await context.BaseItems.CountAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
            }
        }
    }

    /// <summary>
    /// The advisory lock interceptor is opt in but shipped, so it has to at least be able to write.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task SerializeWrites_StillAllowsWrites()
    {
        var database = await PostgresTestDatabase.CreateAsync().ConfigureAwait(true);
        await using (database.ConfigureAwait(true))
        {
            var options = database.Options;
            options.SerializeWrites = true;

            var provider = database.CreateProvider(options);
            var context = provider.DbContextFactory!.CreateDbContext();
            await using (context.ConfigureAwait(true))
            {
                var id = Guid.NewGuid();
                context.BaseItems.Add(new BaseItemEntity { Id = id, Type = "Movie", Name = "serialised" });
                await context.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

                Assert.True(await context.BaseItems
                    .AnyAsync(e => e.Id.Equals(id), TestContext.Current.CancellationToken)
                    .ConfigureAwait(true));
            }

            options.SerializeWrites = false;
        }
    }

    private static SqliteException MakeSqliteError(int errorCode)
        => new("simulated", errorCode, errorCode);
}
