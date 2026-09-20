using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Database.Providers.Postgres.Locking;

/// <summary>
/// Takes a transaction scoped advisory lock when a transaction starts, so that only one Jellyfin
/// transaction writes at a time.
/// </summary>
/// <remarks>
/// This exists for installs that hit unique constraint races the codebase never had to handle under
/// SQLite's single writer. It is off by default: it makes every writer queue behind the slowest one,
/// and a long read transaction such as a full backup holds the lock for its whole run.
/// <para>
/// The lock is transaction scoped, so PostgreSQL releases it on commit or rollback even if the
/// process dies. A <c>lock_timeout</c> is set first so that a second transaction opened on another
/// pooled connection inside the first one fails with an error instead of hanging forever.
/// </para>
/// </remarks>
internal sealed class WriteSerialisingTransactionInterceptor : DbTransactionInterceptor
{
    private const string LockSql = "SET LOCAL lock_timeout = '30s'; SELECT pg_advisory_xact_lock(hashtext('jellyfin'));";

    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WriteSerialisingTransactionInterceptor"/> class.
    /// </summary>
    /// <param name="logger">A logger.</param>
    public WriteSerialisingTransactionInterceptor(ILogger logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public override async ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection,
        TransactionEndEventData eventData,
        DbTransaction result,
        CancellationToken cancellationToken = default)
    {
        await TakeLockAsync(result, cancellationToken).ConfigureAwait(false);
        return await base.TransactionStartedAsync(connection, eventData, result, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override DbTransaction TransactionStarted(
        DbConnection connection,
        TransactionEndEventData eventData,
        DbTransaction result)
    {
        TakeLock(result);
        return base.TransactionStarted(connection, eventData, result);
    }

    /// <inheritdoc />
    public override async ValueTask<DbTransaction> TransactionUsedAsync(
        DbConnection connection,
        TransactionEventData eventData,
        DbTransaction result,
        CancellationToken cancellationToken = default)
    {
        await TakeLockAsync(result, cancellationToken).ConfigureAwait(false);
        return await base.TransactionUsedAsync(connection, eventData, result, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override DbTransaction TransactionUsed(
        DbConnection connection,
        TransactionEventData eventData,
        DbTransaction result)
    {
        TakeLock(result);
        return base.TransactionUsed(connection, eventData, result);
    }

    private async Task TakeLockAsync(DbTransaction transaction, CancellationToken cancellationToken)
    {
        var command = transaction.Connection!.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText = LockSql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void TakeLock(DbTransaction transaction)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = LockSql;
        command.ExecuteNonQuery();
    }
}
