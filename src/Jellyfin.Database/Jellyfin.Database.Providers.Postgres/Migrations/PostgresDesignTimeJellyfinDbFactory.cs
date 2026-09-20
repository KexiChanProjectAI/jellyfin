using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Locking;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Database.Providers.Postgres.Migrations;

/// <summary>
/// The design time factory for <see cref="JellyfinDbContext"/> on PostgreSQL.
/// This is only used when creating migrations, never at runtime.
/// </summary>
/// <remarks>
/// It builds the context through the provider's own <see cref="PostgresDatabaseProvider.Initialise"/>
/// rather than calling <c>UseNpgsql</c> directly, so that the model migrations are scaffolded from is
/// the model the server actually runs with. Doing otherwise produces migrations that do not match
/// the runtime model, which Entity Framework then reports as pending changes on every start.
/// No connection is opened.
/// </remarks>
internal sealed class PostgresDesignTimeJellyfinDbFactory : IDesignTimeDbContextFactory<JellyfinDbContext>
{
    public JellyfinDbContext CreateDbContext(string[] args)
    {
        var provider = new PostgresDatabaseProvider(
            null!,
            NullLogger<PostgresDatabaseProvider>.Instance);

        // No settings of its own, so the resolver falls back to its defaults and to the POSTGRES_*
        // environment variables. Scaffolding never connects, but `dotnet ef database update` does,
        // and this is what lets a developer point it at a scratch database.
        var optionsBuilder = new DbContextOptionsBuilder<JellyfinDbContext>();
        provider.Initialise(
            optionsBuilder,
            new DatabaseConfigurationOptions { DatabaseType = "Jellyfin-PgSql" });

        return new JellyfinDbContext(
            optionsBuilder.Options,
            NullLogger<JellyfinDbContext>.Instance,
            provider,
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }
}
