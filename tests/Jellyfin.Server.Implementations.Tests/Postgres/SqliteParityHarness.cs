using System;
using System.Threading;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Server.Implementations.Tests.Postgres;

/// <summary>
/// An in-memory SQLite database to compare PostgreSQL's answers against.
/// </summary>
/// <remarks>
/// SQLite is the reference: its answers are what every existing Jellyfin installation returns, so a
/// difference is a PostgreSQL bug rather than a question of which is more correct.
/// </remarks>
internal sealed class SqliteParityHarness : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JellyfinDbContext> _options;
    private readonly SqliteDatabaseProvider _provider;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteParityHarness"/> class.
    /// </summary>
    public SqliteParityHarness()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _provider = new SqliteDatabaseProvider(
            new Mock<IApplicationPaths>().Object,
            NullLogger<SqliteDatabaseProvider>.Instance);

        _options = new DbContextOptionsBuilder<JellyfinDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = CreateDbContext();
        context.Database.EnsureCreated();

        // The expression index the People lookups rely on is created by a migration, not by the
        // model, so EnsureCreated does not produce it.
        context.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS \"IX_Peoples_NameLower\" ON \"Peoples\" (lower(\"Name\"));");
    }

    /// <summary>
    /// Gets a factory handing out contexts against this database.
    /// </summary>
    public IDbContextFactory<JellyfinDbContext> ContextFactory
    {
        get
        {
            var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
            factory.Setup(f => f.CreateDbContext()).Returns(CreateDbContext);
            factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(CreateDbContext);
            return factory.Object;
        }
    }

    /// <summary>
    /// Creates a context against this database.
    /// </summary>
    /// <returns>The context.</returns>
    public JellyfinDbContext CreateDbContext() => new(
        _options,
        NullLogger<JellyfinDbContext>.Instance,
        _provider,
        new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));

    /// <inheritdoc />
    public void Dispose() => _connection.Dispose();
}
