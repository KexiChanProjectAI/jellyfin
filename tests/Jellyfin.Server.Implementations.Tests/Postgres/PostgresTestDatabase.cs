using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Postgres;
using MediaBrowser.Common.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Postgres;

/// <summary>
/// A throwaway PostgreSQL database with Jellyfin's schema already migrated into it.
/// </summary>
/// <remarks>
/// Gated on <c>JELLYFIN_POSTGRES_TEST_CONNECTION</c>. Without it the tests skip, so the suite still
/// runs on a machine, or a CI runner, that has no PostgreSQL. Setting
/// <c>JELLYFIN_POSTGRES_TEST_REQUIRED</c> turns the skip into a failure, which is what the
/// PostgreSQL CI job sets so that a misconfigured runner cannot report green by skipping everything.
/// </remarks>
public sealed class PostgresTestDatabase : IAsyncDisposable
{
    private const string ConnectionVariable = "JELLYFIN_POSTGRES_TEST_CONNECTION";
    private const string RequiredVariable = "JELLYFIN_POSTGRES_TEST_REQUIRED";

    private readonly string _adminConnectionString;
    private readonly string _databaseName;
    private readonly Lazy<(DbContextOptions<JellyfinDbContext> Options, PostgresDatabaseProvider Provider)> _shared;

    private PostgresTestDatabase(string adminConnectionString, string databaseName, PostgresDatabaseOptions options)
    {
        _adminConnectionString = adminConnectionString;
        _databaseName = databaseName;
        Options = options;
        ApplicationPaths = new Mock<IApplicationPaths>().Object;

        // Built once, as the server builds them once in AddPooledDbContextFactory. A fresh options
        // object per context would make EF build a new internal service provider every time.
        _shared = new Lazy<(DbContextOptions<JellyfinDbContext>, PostgresDatabaseProvider)>(() =>
        {
            var provider = new PostgresDatabaseProvider(
                ApplicationPaths,
                NullLogger<PostgresDatabaseProvider>.Instance);
            var builder = new DbContextOptionsBuilder<JellyfinDbContext>();
            provider.Initialise(builder, BuildConfiguration());
            return (builder.Options, provider);
        });
    }

    /// <summary>
    /// Gets the settings pointing at this database.
    /// </summary>
    public PostgresDatabaseOptions Options { get; }

    /// <summary>
    /// Gets application paths stubbed for the provider.
    /// </summary>
    public IApplicationPaths ApplicationPaths { get; }

    /// <summary>
    /// Gets the name of the database.
    /// </summary>
    public string DatabaseName => _databaseName;

    /// <summary>
    /// Reports whether a PostgreSQL server was configured for the tests.
    /// </summary>
    public static bool IsConfigured
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable));

    /// <summary>
    /// Skips the calling test when no PostgreSQL server is configured, unless one is required.
    /// </summary>
    public static void SkipIfUnavailable()
    {
        if (IsConfigured)
        {
            return;
        }

        var required = Environment.GetEnvironmentVariable(RequiredVariable);
        Assert.False(
            required is "1" or "true",
            $"{RequiredVariable} is set but {ConnectionVariable} is not, so the PostgreSQL tests cannot run.");

        Assert.Skip($"Set {ConnectionVariable} to run the PostgreSQL tests.");
    }

    /// <summary>
    /// Creates a new empty database and migrates Jellyfin's schema into it.
    /// </summary>
    /// <param name="migrate">Whether to apply the migrations. False leaves the database empty.</param>
    /// <returns>The database.</returns>
    public static async Task<PostgresTestDatabase> CreateAsync(bool migrate = true)
    {
        SkipIfUnavailable();

        var adminConnectionString = Environment.GetEnvironmentVariable(ConnectionVariable)!;
        var name = "jf_t_" + Guid.NewGuid().ToString("N")[..16];

        var admin = new NpgsqlConnection(adminConnectionString);
        await using (admin.ConfigureAwait(false))
        {
            await admin.OpenAsync().ConfigureAwait(false);
            var command = admin.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
#pragma warning disable CA2100 // A generated name, and an identifier cannot be a parameter.
                command.CommandText = FormattableString.Invariant($"CREATE DATABASE \"{name}\" ENCODING 'UTF8' TEMPLATE template0");
#pragma warning restore CA2100
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        var source = new NpgsqlConnectionStringBuilder(adminConnectionString);
        var options = new PostgresDatabaseOptions
        {
            Host = source.Host,
            Port = source.Port,
            Username = source.Username,
            Password = source.Password,
            Database = name,
            MaintenanceDatabase = source.Database
        };

        var database = new PostgresTestDatabase(adminConnectionString, name, options);

        if (migrate)
        {
            var context = database.CreateDbContext();
            await using (context.ConfigureAwait(false))
            {
                await context.Database.MigrateAsync().ConfigureAwait(false);
            }
        }

        return database;
    }

    /// <summary>
    /// Creates a provider bound to this database.
    /// </summary>
    /// <returns>The provider, already initialised.</returns>
    public PostgresDatabaseProvider CreateProvider()
    {
        var (options, provider) = _shared.Value;
        provider.DbContextFactory = new PostgresTestDbContextFactory(options, provider);
        return provider;
    }

    /// <summary>
    /// Creates a context against this database.
    /// </summary>
    /// <returns>The context.</returns>
    public JellyfinDbContext CreateDbContext()
    {
        var (options, provider) = _shared.Value;
        return new JellyfinDbContext(
            options,
            NullLogger<JellyfinDbContext>.Instance,
            provider,
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }

    /// <summary>
    /// Builds the database configuration pointing at this database.
    /// </summary>
    /// <returns>The configuration.</returns>
    public DatabaseConfigurationOptions BuildConfiguration()
        => new() { DatabaseType = "Jellyfin-PgSql", PostgreSql = Options };

    /// <summary>
    /// Opens a raw connection to this database.
    /// </summary>
    /// <returns>An open connection.</returns>
    public async Task<NpgsqlConnection> OpenAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Database = _databaseName,
            Pooling = false
        };
        var connection = new NpgsqlConnection(builder.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    /// <summary>
    /// Runs a scalar query against this database.
    /// </summary>
    /// <param name="sql">The query.</param>
    /// <returns>The first column of the first row.</returns>
    public async Task<object?> ScalarAsync(string sql)
    {
        var connection = await OpenAsync().ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
#pragma warning disable CA2100 // Test-owned SQL.
                command.CommandText = sql;
#pragma warning restore CA2100
                return await command.ExecuteScalarAsync().ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();

        var admin = new NpgsqlConnection(_adminConnectionString);
        await using (admin.ConfigureAwait(false))
        {
            await admin.OpenAsync().ConfigureAwait(false);
            var command = admin.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
#pragma warning disable CA2100 // A generated name, and an identifier cannot be a parameter.
                command.CommandText = FormattableString.Invariant($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)");
#pragma warning restore CA2100
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class PostgresTestDbContextFactory : IDbContextFactory<JellyfinDbContext>
    {
        private readonly DbContextOptions<JellyfinDbContext> _options;
        private readonly IJellyfinDatabaseProvider _provider;

        public PostgresTestDbContextFactory(DbContextOptions<JellyfinDbContext> options, IJellyfinDatabaseProvider provider)
        {
            _options = options;
            _provider = provider;
        }

        public JellyfinDbContext CreateDbContext() => new(
            _options,
            NullLogger<JellyfinDbContext>.Instance,
            _provider,
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }
}
