using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Postgres;
using Jellyfin.Database.Providers.Postgres.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.EfMigrations;

/// <summary>
/// Schema level checks for the PostgreSQL provider.
/// </summary>
public class PostgresMigrationTests
{
    private const string InitialCreateId = "20250101000000_InitialCreate";

    /// <summary>
    /// The model the server builds has to match the one the migrations produce, or Entity Framework
    /// refuses to migrate at startup.
    /// </summary>
    [Fact]
    public void CheckForUnappliedMigrations_Postgres()
    {
        var context = new PostgresDesignTimeJellyfinDbFactory().CreateDbContext([]);
        Assert.False(
            context.Database.HasPendingModelChanges(),
            "There are unapplied changes to the EFCore model for PostgreSQL. Please create a Migration.");
    }

    /// <summary>
    /// The schema has to exist before the first code migration that queries a table runs, and the
    /// migration service orders both kinds together by identifier.
    /// </summary>
    [Fact]
    public void InitialCreate_SortsBeforeEveryCodeMigration()
    {
        var context = new PostgresDesignTimeJellyfinDbFactory().CreateDbContext([]);
        var migrations = context.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();

        Assert.Contains(InitialCreateId, migrations);

        // Mirrors JellyfinMigrationService: ordinal ordering over both sets.
        // Built the way CodeMigration.BuildCodeMigrationId builds it, from the earliest routine in
        // Jellyfin.Server/Migrations/Routines. Kept as a literal because referencing Jellyfin.Server
        // from here would be a cycle.
        var earliestCodeMigration = new DateTime(2025, 4, 20, 5, 0, 0, DateTimeKind.Utc)
            .ToString("yyyyMMddHHmmsss", System.Globalization.CultureInfo.InvariantCulture) + "_Earliest";
        Assert.True(
            string.CompareOrdinal(InitialCreateId, earliestCodeMigration) < 0,
            $"InitialCreate ({InitialCreateId}) must sort before the first code migration ({earliestCodeMigration}).");
    }

    /// <summary>
    /// Every DateTime column has to carry the UTC converter, or Npgsql rejects the value outright.
    /// This is what makes the provider's OnModelCreating hook safe despite running before
    /// ApplyConfigurationsFromAssembly.
    /// </summary>
    [Fact]
    public void EveryDateTimeProperty_HasUtcConverter()
    {
        var context = new PostgresDesignTimeJellyfinDbFactory().CreateDbContext([]);

        var missing = context.Model.GetEntityTypes()
            .SelectMany(e => e.GetProperties().Select(p => (Entity: e, Property: p)))
            .Where(x => (x.Property.ClrType == typeof(DateTime) || x.Property.ClrType == typeof(DateTime?))
                        && x.Property.GetValueConverter() is null)
            .Select(x => x.Entity.ShortName() + "." + x.Property.Name)
            .ToList();

        Assert.Empty(missing);
    }

    /// <summary>
    /// A declared length that SQLite ignores is enforced by PostgreSQL, and several of these columns
    /// are filled straight from client headers.
    /// </summary>
    [Fact]
    public void NoStringColumn_HasAMaxLength()
    {
        var context = new PostgresDesignTimeJellyfinDbFactory().CreateDbContext([]);

        var bounded = context.Model.GetEntityTypes()
            .SelectMany(e => e.GetProperties().Select(p => (Entity: e, Property: p)))
            .Where(x => x.Property.ClrType == typeof(string) && x.Property.GetMaxLength() is not null)
            .Select(x => x.Entity.ShortName() + "." + x.Property.Name)
            .ToList();

        Assert.Empty(bounded);
    }

    /// <summary>
    /// Every text column has to sort the way SQLite sorts, or ordering and the A-Z jump bar differ
    /// between providers.
    /// </summary>
    [Fact]
    public void EveryStringColumn_UsesTheCCollation()
    {
        var context = new PostgresDesignTimeJellyfinDbFactory().CreateDbContext([]);

        // Collation is not kept in the runtime model, only in the one migrations are built from.
        var model = context.GetService<IDesignTimeModel>().Model;
        var wrong = model.GetEntityTypes()
            .SelectMany(e => e.GetProperties().Select(p => (Entity: e, Property: p)))
            .Where(x => x.Property.ClrType == typeof(string)
                        && !string.Equals(x.Property.GetCollation(), "C", StringComparison.Ordinal))
            .Select(x => x.Entity.ShortName() + "." + x.Property.Name)
            .ToList();

        Assert.Empty(wrong);
    }

    /// <summary>
    /// Null placement has to match SQLite, or ordered lists differ between providers wherever the
    /// sort key is nullable.
    /// </summary>
    [Fact]
    public void OrderBy_PlacesNullsWhereSqliteDoes()
    {
        var context = new PostgresDesignTimeJellyfinDbFactory().CreateDbContext([]);

        var ascending = context.BaseItems.OrderBy(e => e.SortName).Select(e => e.Id).ToQueryString();
        var descending = context.BaseItems.OrderByDescending(e => e.SortName).Select(e => e.Id).ToQueryString();

        Assert.Contains("NULLS FIRST", ascending, StringComparison.Ordinal);
        Assert.Contains("NULLS LAST", descending, StringComparison.Ordinal);
    }

    /// <summary>
    /// Jellyfin's searches assume the case insensitive LIKE that SQLite provides by default.
    /// </summary>
    [Fact]
    public void Like_IsTranslatedToCaseInsensitiveILike()
    {
        var context = new PostgresDesignTimeJellyfinDbFactory().CreateDbContext([]);

        var sql = context.BaseItems
            .Where(e => EF.Functions.Like(e.OriginalTitle!, "%term%"))
            .Select(e => e.Id)
            .ToQueryString();

        Assert.Contains("ILIKE", sql, StringComparison.Ordinal);
    }
}
