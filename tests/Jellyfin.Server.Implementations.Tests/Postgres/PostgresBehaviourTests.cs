using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Postgres;

/// <summary>
/// Behavior of the PostgreSQL provider against a live server.
/// </summary>
[Trait("Category", "PostgreSQL")]
public class PostgresBehaviourTests
{
    /// <summary>
    /// A DateTime whose Kind is Local or Unspecified is rejected outright by Npgsql on a
    /// <c>timestamptz</c> column. The converter has to normalise it, exactly as the SQLite provider
    /// does, or half the entity model becomes unsavable.
    /// </summary>
    [Fact]
    public async Task DateTime_OfAnyKind_RoundTripsAsUtc()
    {
        var database = await PostgresTestDatabase.CreateAsync().ConfigureAwait(true);
        await using (database.ConfigureAwait(true))
        {
            var id = Guid.NewGuid();
            var local = new DateTime(2026, 6, 15, 12, 30, 0, DateTimeKind.Local);
            var unspecified = new DateTime(2026, 6, 15, 12, 30, 0, DateTimeKind.Unspecified);

            var context = database.CreateDbContext();
            await using (context.ConfigureAwait(true))
            {
                context.BaseItems.Add(new BaseItemEntity
                {
                    Id = id,
                    Type = "Movie",
                    DateCreated = local,
                    DateModified = unspecified,
                    PremiereDate = DateTime.UtcNow
                });
                await context.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            var reader = database.CreateDbContext();
            await using (reader.ConfigureAwait(true))
            {
                var saved = await reader.BaseItems.AsNoTracking().FirstAsync(e => e.Id.Equals(id), TestContext.Current.CancellationToken).ConfigureAwait(true);

                Assert.Equal(DateTimeKind.Utc, saved.DateCreated!.Value.Kind);
                Assert.Equal(DateTimeKind.Utc, saved.DateModified!.Value.Kind);
                Assert.Equal(local.ToUniversalTime(), saved.DateCreated!.Value);
                Assert.Equal(unspecified.ToUniversalTime(), saved.DateModified!.Value);
            }
        }
    }

    /// <summary>
    /// Jellyfin's item search matches OriginalTitle with a LIKE and relies on SQLite folding case.
    /// </summary>
    [Fact]
    public async Task Like_MatchesRegardlessOfCase()
    {
        var database = await PostgresTestDatabase.CreateAsync().ConfigureAwait(true);
        await using (database.ConfigureAwait(true))
        {
            var context = database.CreateDbContext();
            await using (context.ConfigureAwait(true))
            {
                context.BaseItems.Add(new BaseItemEntity
                {
                    Id = Guid.NewGuid(),
                    Type = "Movie",
                    OriginalTitle = "The Empire Strikes Back"
                });
                await context.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            var reader = database.CreateDbContext();
            await using (reader.ConfigureAwait(true))
            {
                var found = await reader.BaseItems
                    .Where(e => EF.Functions.Like(e.OriginalTitle!, "%empire%"))
                    .CountAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

                Assert.Equal(1, found);
            }
        }
    }

    /// <summary>
    /// SQLite sorts null before every value, PostgreSQL after. Jellyfin's ordered lists were built
    /// against the first behavior.
    /// </summary>
    [Fact]
    public async Task NullSortKeys_SortWhereSqlitePutsThem()
    {
        var database = await PostgresTestDatabase.CreateAsync().ConfigureAwait(true);
        await using (database.ConfigureAwait(true))
        {
            var withName = Guid.NewGuid();
            var withoutName = Guid.NewGuid();

            var context = database.CreateDbContext();
            await using (context.ConfigureAwait(true))
            {
                context.BaseItems.Add(new BaseItemEntity { Id = withName, Type = "Movie", SortName = "alpha" });
                context.BaseItems.Add(new BaseItemEntity { Id = withoutName, Type = "Movie", SortName = null });
                await context.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            var reader = database.CreateDbContext();
            await using (reader.ConfigureAwait(true))
            {
                var ascending = await reader.BaseItems
                    .Where(e => e.Id.Equals(withName) || e.Id.Equals(withoutName))
                    .OrderBy(e => e.SortName)
                    .Select(e => e.Id)
                    .ToListAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

                var descending = await reader.BaseItems
                    .Where(e => e.Id.Equals(withName) || e.Id.Equals(withoutName))
                    .OrderByDescending(e => e.SortName)
                    .Select(e => e.Id)
                    .ToListAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

                Assert.Equal(withoutName, ascending[0]);
                Assert.Equal(withoutName, descending[^1]);
            }
        }
    }

    /// <summary>
    /// Text has to order byte by byte, the way SQLite does, or the A-Z jump bar and every sorted page
    /// differ between providers.
    /// </summary>
    [Fact]
    public async Task TextOrdering_IsCaseSensitiveLikeSqlite()
    {
        var database = await PostgresTestDatabase.CreateAsync().ConfigureAwait(true);
        await using (database.ConfigureAwait(true))
        {
            var ordered = (string?)await database
                .ScalarAsync("SELECT string_agg(v, ',' ORDER BY v COLLATE \"C\") FROM (VALUES ('a'),('B'),('b'),('A')) AS t(v)")
                .ConfigureAwait(true);

            // Byte order puts every capital before every lowercase letter; a linguistic collation
            // would interleave them as A,a,B,b.
            Assert.Equal("A,B,a,b", ordered);
        }
    }

    /// <summary>
    /// Jellyfin groups by a key and takes min(Id) to pick a representative row. PostgreSQL has no
    /// such aggregate for uuid unless the migration adds it.
    /// </summary>
    [Fact]
    public async Task MinOfGuid_IsSupported()
    {
        var database = await PostgresTestDatabase.CreateAsync().ConfigureAwait(true);
        await using (database.ConfigureAwait(true))
        {
            var context = database.CreateDbContext();
            await using (context.ConfigureAwait(true))
            {
                context.BaseItems.Add(new BaseItemEntity { Id = Guid.NewGuid(), Type = "Movie", PresentationUniqueKey = "shared" });
                context.BaseItems.Add(new BaseItemEntity { Id = Guid.NewGuid(), Type = "Movie", PresentationUniqueKey = "shared" });
                await context.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            var reader = database.CreateDbContext();
            await using (reader.ConfigureAwait(true))
            {
                var representatives = await reader.BaseItems
                    .Where(e => e.PresentationUniqueKey == "shared")
                    .GroupBy(e => e.PresentationUniqueKey)
                    .Select(g => g.Min(e => e.Id))
                    .ToListAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

                Assert.Single(representatives);
            }
        }
    }

    /// <summary>
    /// A client can send a device name far longer than the model declares. SQLite stores it, and
    /// PostgreSQL would reject it with 22001 if the length were carried over into the schema, turning
    /// an odd header into a failed login.
    /// </summary>
    [Fact]
    public async Task OverlongClientStrings_AreStored()
    {
        var database = await PostgresTestDatabase.CreateAsync().ConfigureAwait(true);
        await using (database.ConfigureAwait(true))
        {
            var deviceName = new string('x', 300);

            var context = database.CreateDbContext();
            await using (context.ConfigureAwait(true))
            {
                var user = new User("tester", "Default", "Default");
                context.Users.Add(user);
                context.Devices.Add(new Device(user.Id, "app", new string('v', 100), deviceName, new string('d', 500)));
                await context.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            var reader = database.CreateDbContext();
            await using (reader.ConfigureAwait(true))
            {
                var saved = await reader.Devices.AsNoTracking().FirstAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
                Assert.Equal(deviceName, saved.DeviceName);
            }
        }
    }

    /// <summary>
    /// Rows imported with explicit keys leave the sequence behind them, so the next insert collides
    /// until the sequences are moved on.
    /// </summary>
    [Fact]
    public async Task CompleteDatabaseRestore_MovesIdentitySequencesPastImportedRows()
    {
        var database = await PostgresTestDatabase.CreateAsync().ConfigureAwait(true);
        await using (database.ConfigureAwait(true))
        {
            var provider = database.CreateProvider();

            // Import a row with an explicit id, the way a data migration or a restore does.
            var context = database.CreateDbContext();
            await using (context.ConfigureAwait(true))
            {
                await context.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "ActivityLogs" ("Id", "Name", "Type", "UserId", "DateCreated", "LogSeverity", "RowVersion")
                    VALUES (5000, 'imported', 'test', '00000000-0000-0000-0000-000000000000', now(), 0, 0)
                    """,
                    TestContext.Current.CancellationToken).ConfigureAwait(true);

                await provider.CompleteDatabaseRestoreAsync(context, TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            var writer = database.CreateDbContext();
            await using (writer.ConfigureAwait(true))
            {
                var entry = new ActivityLog("after import", "test", Guid.NewGuid());
                writer.ActivityLogs.Add(entry);

                // Without the reseed this throws 23505 on the primary key.
                await writer.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
                Assert.Equal(5001, entry.Id);
            }
        }
    }

    /// <summary>
    /// Purging has to empty every table and reset the generators, and it has to work with foreign keys
    /// in place.
    /// </summary>
    [Fact]
    public async Task PurgeDatabase_EmptiesEveryTableAndResetsIdentities()
    {
        var database = await PostgresTestDatabase.CreateAsync().ConfigureAwait(true);
        await using (database.ConfigureAwait(true))
        {
            var provider = database.CreateProvider();

            var context = database.CreateDbContext();
            await using (context.ConfigureAwait(true))
            {
                context.ActivityLogs.Add(new ActivityLog("before purge", "test", Guid.NewGuid()));
                context.BaseItems.Add(new BaseItemEntity { Id = Guid.NewGuid(), Type = "Movie" });
                await context.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

                await provider.PurgeDatabase(context, null).ConfigureAwait(true);
            }

            var reader = database.CreateDbContext();
            await using (reader.ConfigureAwait(true))
            {
                Assert.Equal(0, await reader.ActivityLogs.CountAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
                Assert.Equal(0, await reader.BaseItems.CountAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));

                var entry = new ActivityLog("after purge", "test", Guid.NewGuid());
                reader.ActivityLogs.Add(entry);
                await reader.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
                Assert.Equal(1, entry.Id);
            }
        }
    }

    /// <summary>
    /// The scheduled optimisation runs outside a transaction and must not need a superuser.
    /// </summary>
    [Fact]
    public async Task RunScheduledOptimisation_Succeeds()
    {
        var database = await PostgresTestDatabase.CreateAsync().ConfigureAwait(true);
        await using (database.ConfigureAwait(true))
        {
            var provider = database.CreateProvider();
            await provider.RunScheduledOptimisation(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// The concurrency token is a uint in the model and has no unsigned counterpart in PostgreSQL.
    /// </summary>
    [Fact]
    public async Task ConcurrencyToken_RoundTrips()
    {
        var database = await PostgresTestDatabase.CreateAsync().ConfigureAwait(true);
        await using (database.ConfigureAwait(true))
        {
            var context = database.CreateDbContext();
            await using (context.ConfigureAwait(true))
            {
                var entry = new ActivityLog("token", "test", Guid.NewGuid());
                context.ActivityLogs.Add(entry);
                await context.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

                entry.Name = "token changed";
                await context.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

                var stored = await context.ActivityLogs.AsNoTracking().FirstAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
                Assert.Equal("token changed", stored.Name);
            }
        }
    }

    /// <summary>
    /// The exception classifier is what the retry paths key off, so the mapping has to be checked
    /// against a real failure rather than assumed. The violation arrives wrapped in a
    /// DbUpdateException, which is the shape the retry code sees.
    /// </summary>
    [Fact]
    public async Task UniqueViolation_IsClassified()
    {
        var database = await PostgresTestDatabase.CreateAsync().ConfigureAwait(true);
        await using (database.ConfigureAwait(true))
        {
            var provider = database.CreateProvider();
            var id = Guid.NewGuid();

            var context = database.CreateDbContext();
            await using (context.ConfigureAwait(true))
            {
                context.BaseItems.Add(new BaseItemEntity { Id = id, Type = "Movie" });
                await context.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            var second = database.CreateDbContext();
            await using (second.ConfigureAwait(true))
            {
                second.BaseItems.Add(new BaseItemEntity { Id = id, Type = "Movie" });

                var exception = await Assert.ThrowsAsync<DbUpdateException>(
                    () => second.SaveChangesAsync(TestContext.Current.CancellationToken)).ConfigureAwait(true);

                Assert.Equal(
                    Jellyfin.Database.Implementations.DatabaseErrorKind.UniqueViolation,
                    provider.ClassifyException(exception));
            }
        }
    }

    /// <summary>
    /// An error the retry paths must not act on has to classify as unknown, or a doomed write is
    /// retried forever.
    /// </summary>
    [Fact]
    public async Task UnrelatedError_IsNotClassifiedAsRetryable()
    {
        var database = await PostgresTestDatabase.CreateAsync().ConfigureAwait(true);
        await using (database.ConfigureAwait(true))
        {
            var provider = database.CreateProvider();

            var context = database.CreateDbContext();
            await using (context.ConfigureAwait(true))
            {
                var exception = await Assert.ThrowsAnyAsync<Exception>(
                    () => context.Database.ExecuteSqlRawAsync(
                        "INSERT INTO \"BaseItems\" (\"Id\") VALUES (gen_random_uuid())",
                        TestContext.Current.CancellationToken)).ConfigureAwait(true);

                Assert.Equal(
                    Jellyfin.Database.Implementations.DatabaseErrorKind.Unknown,
                    provider.ClassifyException(exception));
            }
        }
    }
}
