using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.DbConfiguration;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Database.Implementations;

/// <summary>
/// Defines the type and extension points for multi database support.
/// </summary>
public interface IJellyfinDatabaseProvider
{
    /// <summary>
    /// Gets or Sets the Database Factory when initialisaition is done.
    /// </summary>
    IDbContextFactory<JellyfinDbContext>? DbContextFactory { get; set; }

    /// <summary>
    /// Gets a value indicating whether retrying a failed command on the same connection can succeed
    /// while a transaction is open.
    /// </summary>
    /// <remarks>
    /// SQLite leaves the transaction usable after a busy failure, so the command itself can be
    /// retried. PostgreSQL aborts the whole transaction on a serialization failure or deadlock and
    /// fails every following command with <c>25P02</c> until it is rolled back, so there the unit of
    /// retry has to be the transaction, never the command.
    /// </remarks>
    bool CanRetryInsideTransaction => true;

    /// <summary>
    /// Initialises jellyfins EFCore database access.
    /// </summary>
    /// <param name="options">The EFCore database options.</param>
    /// <param name="databaseConfiguration">The Jellyfin database options.</param>
    void Initialise(DbContextOptionsBuilder options, DatabaseConfigurationOptions databaseConfiguration);

    /// <summary>
    /// Will be invoked when EFCore wants to build its model.
    /// </summary>
    /// <param name="modelBuilder">The ModelBuilder from EFCore.</param>
    void OnModelCreating(ModelBuilder modelBuilder);

    /// <summary>
    /// Will be invoked when EFCore wants to configure its model.
    /// </summary>
    /// <param name="configurationBuilder">The ModelConfigurationBuilder from EFCore.</param>
    void ConfigureConventions(ModelConfigurationBuilder configurationBuilder);

    /// <summary>
    /// If supported this should run any periodic maintaince tasks, reclaiming unused space and refreshing the query
    /// planner statistics. Also used after migrations have modified the database.
    /// </summary>
    /// <param name="cancellationToken">The token to abort the operation.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task RunScheduledOptimisation(CancellationToken cancellationToken);

    /// <summary>
    /// If supported this should perform any actions that are required on stopping the jellyfin server. This runs
    /// against a deadline imposed by the service manager, so unlike
    /// <see cref="RunScheduledOptimisation(CancellationToken)"/> it should only do work whose cost does not grow with
    /// the size of the database.
    /// </summary>
    /// <param name="cancellationToken">The token that will be used to abort the operation.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task RunShutdownTask(CancellationToken cancellationToken);

    /// <summary>
    /// Runs a full Database backup that can later be restored to.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A key to identify the backup.</returns>
    /// <exception cref="NotImplementedException">May throw an NotImplementException if this operation is not supported for this database.</exception>
    Task<string> MigrationBackupFast(CancellationToken cancellationToken);

    /// <summary>
    /// Restores a backup that has been previously created by <see cref="MigrationBackupFast(CancellationToken)"/>.
    /// </summary>
    /// <param name="key">The key to the backup from which the current database should be restored from.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
    Task RestoreBackupFast(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a backup that has been previously created by <see cref="MigrationBackupFast(CancellationToken)"/>.
    /// </summary>
    /// <param name="key">The key to the backup which should be cleaned up.</param>
    /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
    Task DeleteBackup(string key);

    /// <summary>
    /// Removes all contents from the database.
    /// </summary>
    /// <param name="dbContext">The Database context.</param>
    /// <param name="tableNames">The names of the tables to purge or null for all tables to be purged.</param>
    /// <returns>A Task.</returns>
    Task PurgeDatabase(JellyfinDbContext dbContext, IEnumerable<string>? tableNames);

    /// <summary>
    /// Classifies an exception thrown by the database into a provider independent kind.
    /// </summary>
    /// <param name="exception">The exception to classify, which may be a wrapper such as <see cref="DbUpdateException"/>.</param>
    /// <returns>The kind of failure, or <see cref="DatabaseErrorKind.Unknown"/> when it carries no meaning callers can act on.</returns>
    DatabaseErrorKind ClassifyException(Exception exception) => DatabaseErrorKind.Unknown;

    /// <summary>
    /// Returns the locking behavior to actually use, given the one the operator configured.
    /// </summary>
    /// <param name="requested">The configured behavior.</param>
    /// <returns>The behavior to register.</returns>
    /// <remarks>
    /// A behavior can be meaningless or harmful on a given provider; the provider gets to say so
    /// rather than leaving the operator with a setting that silently does nothing or deadlocks.
    /// </remarks>
    DatabaseLockingBehaviorTypes NormalizeLockingBehavior(DatabaseLockingBehaviorTypes requested) => requested;

    /// <summary>
    /// Ensures the database is reachable and usable, before any migration runs.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    /// <remarks>
    /// This is separate from <see cref="Initialise"/> because that also runs at design time, where
    /// no server exists to connect to. Implementations should throw
    /// <see cref="DatabaseProviderStartupException"/> with an actionable message when the database
    /// cannot be used.
    /// </remarks>
    Task EnsureDatabaseReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Called after rows have been imported into an empty database and before the importing
    /// transaction commits.
    /// </summary>
    /// <param name="dbContext">The context holding the open import transaction.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    /// <remarks>
    /// Rows imported with explicit primary keys do not advance the generators backing those keys, so
    /// on PostgreSQL the next insert collides with an imported row. Implementations must do their
    /// work through the supplied context so that a failure rolls the import back.
    /// </remarks>
    Task CompleteDatabaseRestoreAsync(JellyfinDbContext dbContext, CancellationToken cancellationToken) => Task.CompletedTask;
}
