using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Providers.Postgres.Configuration;
using Jellyfin.Database.Providers.Postgres.Model;
using Jellyfin.Database.Providers.Postgres.Query;
using MediaBrowser.Common.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jellyfin.Database.Providers.Postgres;

/// <summary>
/// Configures Jellyfin to use a PostgreSQL database.
/// </summary>
[JellyfinDatabaseProviderKey("Jellyfin-PgSql")]
public sealed partial class PostgresDatabaseProvider : IJellyfinDatabaseProvider
{
    /// <summary>
    /// The lowest PostgreSQL version this provider supports.
    /// </summary>
    /// <remarks>
    /// 14 leaves support in November 2026. 15 is also where the <c>public</c> schema stopped being
    /// writable by every user, which is the behavior the startup probe checks for.
    /// </remarks>
    internal const int MinimumServerVersion = 15;

    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<PostgresDatabaseProvider> _logger;
    private readonly IConfiguration? _configuration;

    private PostgresConnectionSettings? _settings;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresDatabaseProvider"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths, used to place file backups.</param>
    /// <param name="logger">A logger.</param>
    /// <param name="configuration">The startup configuration, which carries environment settings. Null at design time.</param>
    public PostgresDatabaseProvider(
        IApplicationPaths applicationPaths,
        ILogger<PostgresDatabaseProvider> logger,
        IConfiguration? configuration = null)
    {
        _applicationPaths = applicationPaths;
        _logger = logger;
        _configuration = configuration;
    }

    /// <inheritdoc/>
    public IDbContextFactory<JellyfinDbContext>? DbContextFactory { get; set; }

    /// <inheritdoc/>
    public bool CanRetryInsideTransaction => false;

    /// <summary>
    /// Gets the resolved connection settings.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when accessed before <see cref="Initialise"/>.</exception>
    internal PostgresConnectionSettings Settings
        => _settings ?? throw new InvalidOperationException("The PostgreSQL provider has not been initialised yet.");

    /// <inheritdoc/>
    public void Initialise(DbContextOptionsBuilder options, DatabaseConfigurationOptions databaseConfiguration)
    {
        ArgumentNullException.ThrowIfNull(options);

        var settings = PostgresConnectionSettings.Resolve(databaseConfiguration, _configuration, GetApplicationVersion());
        _settings = settings;

        _logger.LogInformation(
            "PostgreSQL connection: {ConnectionString} (settings from: {Sources})",
            settings.RedactedConnectionString,
            settings.Sources.Count == 0 ? "defaults" : string.Join(", ", settings.Sources));

        options
            .UseNpgsql(
                settings.ConnectionString,
                npgsqlOptions => npgsqlOptions
                    .MigrationsAssembly(GetType().Assembly)
                    // Pin the version the SQL is generated for. Letting Npgsql discover it from the
                    // live server would make the model, and therefore whether migrations look
                    // pending, depend on which server happens to be connected.
                    .SetPostgresVersion(MinimumServerVersion, 0))
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.MultipleCollectionIncludeWarning))
            .ReplaceService<IQuerySqlGeneratorFactory, NullPlacementQuerySqlGeneratorFactory>()
            .AddInterceptors(CaseInsensitiveLikeInterceptor.Instance);

        // Deliberately no EnableRetryOnFailure: an execution strategy that retries refuses to run
        // while the caller owns the transaction, and this codebase opens transactions by hand in a
        // dozen places. Resilience comes from the startup probe's connect retry and from the
        // operation level retries around the writes that actually race.
        if (settings.Options.SerializeWrites)
        {
            options.AddInterceptors(new Locking.WriteSerialisingTransactionInterceptor(_logger));
            _logger.LogInformation("PostgreSQL writes are serialised with an advisory lock");
        }

        if (settings.Options.EnableSensitiveDataLogging)
        {
            options.EnableSensitiveDataLogging();
            _logger.LogWarning("EnableSensitiveDataLogging is enabled: query parameters will be written to the log");
        }
    }

    /// <inheritdoc/>
    public void OnModelCreating(ModelBuilder modelBuilder)
    {
        PostgresModelConfigurator.Configure(modelBuilder);
    }

    /// <inheritdoc/>
    public void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // PostgreSQL needs none of the conventions SQLite does; it supports RETURNING natively.
    }

    /// <inheritdoc/>
    public DatabaseErrorKind ClassifyException(Exception exception)
        => PostgresExceptionClassifier.Classify(exception);

    /// <inheritdoc/>
    public DatabaseLockingBehaviorTypes NormalizeLockingBehavior(DatabaseLockingBehaviorTypes requested)
    {
        if (requested == DatabaseLockingBehaviorTypes.Pessimistic)
        {
            // That behavior holds a thread affine reader/writer lock across an await. SQLite's async
            // completes synchronously so the lock is released on the thread that took it; Npgsql's
            // does not, and the release throws SynchronizationLockException. PostgreSQL also does its
            // own row level locking, so the setting has nothing to add.
            _logger.LogWarning(
                "The Pessimistic locking behavior is not supported on PostgreSQL and has been ignored. "
                + "Set PostgreSql/SerializeWrites in database.xml if writes really have to be serialised.");
            return DatabaseLockingBehaviorTypes.NoLock;
        }

        return requested;
    }

    /// <inheritdoc/>
    public async Task EnsureDatabaseReadyAsync(CancellationToken cancellationToken)
    {
        var probe = new Startup.PostgresStartupProbe(Settings, _logger);
        await probe.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task RunScheduledOptimisation(CancellationToken cancellationToken)
    {
        if (DbContextFactory is null)
        {
            return;
        }

        var context = await DbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var tables = GetModelTables(context).ToArray();
            if (tables.Length == 0)
            {
                return;
            }

            // Named tables rather than a bare VACUUM: the bare form also visits shared catalogs and
            // logs a warning for each one it may not touch when the role is not a superuser.
            var sql = "VACUUM (ANALYZE) " + string.Join(", ", tables);
            _logger.LogDebug("Reclaiming space and refreshing planner statistics for {TableCount} tables", tables.Length);

            // VACUUM cannot run inside a transaction, and EF opens one for ExecuteSqlRaw by default.
            await context.Database.ExecuteSqlRawAsync(sql, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("The Jellyfin database was optimized successfully");
        }
    }

    /// <inheritdoc/>
    public Task RunShutdownTask(CancellationToken cancellationToken)
    {
        NpgsqlConnection.ClearAllPools();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task PurgeDatabase(JellyfinDbContext dbContext, IEnumerable<string>? tableNames)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        var helper = dbContext.GetService<ISqlGenerationHelper>();
        var tables = tableNames is null
            ? GetModelTables(dbContext).ToArray()
            : tableNames.Select(e => QualifyAndQuote(e, helper)).ToArray();

        if (tables.Length == 0)
        {
            return;
        }

        // One statement for all of them: TRUNCATE takes an ACCESS EXCLUSIVE lock on each table, and
        // separate statements would take those locks in sequence and can deadlock against anything
        // taking them in another order. CASCADE covers the foreign keys, RESTART IDENTITY puts the
        // generators back so a following import starts from one.
        var sql = "TRUNCATE TABLE " + string.Join(", ", tables) + " RESTART IDENTITY CASCADE";
        await dbContext.Database.ExecuteSqlRawAsync(sql).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task CompleteDatabaseRestoreAsync(JellyfinDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        await Maintenance.IdentitySequenceReseeder
            .ReseedAsync(dbContext, _logger, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Returns every table in the model, schema qualified and quoted.
    /// </summary>
    private static IEnumerable<string> GetModelTables(DbContext context)
    {
        var helper = context.GetService<ISqlGenerationHelper>();
        return context.Model.GetEntityTypes()
            .Select(e => (Schema: e.GetSchema(), Table: e.GetTableName()))
            .Where(e => e.Table is not null)
            .Distinct()
            .Select(e => helper.DelimitIdentifier(e.Table!, e.Schema));
    }

    /// <summary>
    /// Quotes a possibly schema qualified table name that arrived as a single string.
    /// </summary>
    private static string QualifyAndQuote(string tableName, ISqlGenerationHelper helper)
    {
        var separator = tableName.IndexOf('.', StringComparison.Ordinal);
        return separator < 0
            ? helper.DelimitIdentifier(tableName)
            : helper.DelimitIdentifier(tableName[(separator + 1)..], tableName[..separator]);
    }

    private static string GetApplicationVersion()
        => typeof(PostgresDatabaseProvider).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    private static string Invariant(FormattableString value)
        => value.ToString(CultureInfo.InvariantCulture);
}
