#pragma warning disable CA1873

using System;
using System.Data.Common;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Polly;

namespace Jellyfin.Database.Implementations.Locking;

/// <summary>
/// Defines a locking mechanism that will retry any write operation for a few times.
/// </summary>
public class OptimisticLockBehavior : IEntityFrameworkCoreLockingBehavior
{
    private readonly Policy _writePolicy;
    private readonly AsyncPolicy _writeAsyncPolicy;
    private readonly ILogger<OptimisticLockBehavior> _logger;
    private readonly IJellyfinDatabaseProvider _databaseProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="OptimisticLockBehavior"/> class.
    /// </summary>
    /// <param name="logger">The application logger.</param>
    /// <param name="databaseProvider">The database provider, which classifies the failures worth retrying.</param>
    public OptimisticLockBehavior(ILogger<OptimisticLockBehavior> logger, IJellyfinDatabaseProvider databaseProvider)
    {
        ArgumentNullException.ThrowIfNull(databaseProvider);
        _databaseProvider = databaseProvider;
        TimeSpan[] sleepDurations = [
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(150),
            TimeSpan.FromMilliseconds(150),
            TimeSpan.FromMilliseconds(150),
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(150),
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(150),
            TimeSpan.FromSeconds(3)
        ];

        Func<int, Context, TimeSpan> backoffProvider = (index, context) =>
        {
            var backoff = sleepDurations[index];
            return backoff + TimeSpan.FromMilliseconds(RandomNumberGenerator.GetInt32(0, (int)(backoff.TotalMilliseconds * .5)));
        };

        _logger = logger;

        // Ask the provider rather than matching on the message. SQLite says "database is locked" in
        // English; PostgreSQL reports a serialization failure or a deadlock as a SQLSTATE, and no
        // message match would ever fire there, leaving a retry policy that silently never retries.
        bool ShouldRetry(Exception exception)
            => databaseProvider.ClassifyException(exception) == DatabaseErrorKind.TransientLock;

        _writePolicy = Policy
            .Handle<Exception>(ShouldRetry)
            .WaitAndRetry(sleepDurations.Length, backoffProvider, RetryHandle);
        _writeAsyncPolicy = Policy
            .Handle<Exception>(ShouldRetry)
            .WaitAndRetryAsync(sleepDurations.Length, backoffProvider, RetryHandle);

        void RetryHandle(Exception exception, TimeSpan timespan, int retryNo, Context context)
        {
            if (retryNo < sleepDurations.Length)
            {
                _logger.LogWarning("Operation failed retry {RetryNo}", retryNo);
            }
            else
            {
                _logger.LogError(exception, "Operation failed retry {RetryNo}", retryNo);
            }
        }
    }

    /// <inheritdoc/>
    public void Initialise(DbContextOptionsBuilder optionsBuilder)
    {
        _logger.LogInformation("The database locking mode has been set to: Optimistic.");
        optionsBuilder.AddInterceptors(new RetryInterceptor(_writeAsyncPolicy, _writePolicy, _databaseProvider.CanRetryInsideTransaction));
        optionsBuilder.AddInterceptors(new TransactionLockingInterceptor(_writeAsyncPolicy, _writePolicy));
    }

    /// <inheritdoc/>
    public void OnSaveChanges(JellyfinDbContext context, Action saveChanges)
    {
        _writePolicy.Execute(saveChanges);
    }

    /// <inheritdoc/>
    public async Task OnSaveChangesAsync(JellyfinDbContext context, Func<Task> saveChanges)
    {
        await _writeAsyncPolicy.ExecuteAsync(saveChanges).ConfigureAwait(false);
    }

    private sealed class TransactionLockingInterceptor : DbTransactionInterceptor
    {
        private readonly AsyncPolicy _asyncRetryPolicy;
        private readonly Policy _retryPolicy;

        public TransactionLockingInterceptor(AsyncPolicy asyncRetryPolicy, Policy retryPolicy)
        {
            _asyncRetryPolicy = asyncRetryPolicy;
            _retryPolicy = retryPolicy;
        }

        public override InterceptionResult<DbTransaction> TransactionStarting(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
        {
            return InterceptionResult<DbTransaction>.SuppressWithResult(_retryPolicy.Execute(() => connection.BeginTransaction(eventData.IsolationLevel)));
        }

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            return InterceptionResult<DbTransaction>.SuppressWithResult(await _asyncRetryPolicy.ExecuteAsync(async () => await connection.BeginTransactionAsync(eventData.IsolationLevel, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false));
        }
    }

    private sealed class RetryInterceptor : DbCommandInterceptor
    {
        private readonly AsyncPolicy _asyncRetryPolicy;
        private readonly Policy _retryPolicy;
        private readonly bool _canRetryInsideTransaction;

        public RetryInterceptor(AsyncPolicy asyncRetryPolicy, Policy retryPolicy, bool canRetryInsideTransaction)
        {
            _asyncRetryPolicy = asyncRetryPolicy;
            _retryPolicy = retryPolicy;
            _canRetryInsideTransaction = canRetryInsideTransaction;
        }

        /// <summary>
        /// Reports whether this command can be retried on its own.
        /// </summary>
        /// <remarks>
        /// PostgreSQL aborts the whole transaction on a serialization failure or a deadlock, so
        /// replaying the statement only produces 25P02 until the transaction is rolled back. There
        /// the unit of retry is the transaction, which the surrounding SaveChanges policy owns.
        /// </remarks>
        private bool CanRetry(DbCommand command)
            => _canRetryInsideTransaction || command.Transaction is null;

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            if (!CanRetry(command))
            {
                return result;
            }

            return InterceptionResult<int>.SuppressWithResult(_retryPolicy.Execute(command.ExecuteNonQuery));
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!CanRetry(command))
            {
                return result;
            }

            return InterceptionResult<int>.SuppressWithResult(await _asyncRetryPolicy.ExecuteAsync(async () => await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false));
        }

        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            if (!CanRetry(command))
            {
                return result;
            }

            return InterceptionResult<object>.SuppressWithResult(_retryPolicy.Execute(() => command.ExecuteScalar()!));
        }

        public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            if (!CanRetry(command))
            {
                return result;
            }

            return InterceptionResult<object>.SuppressWithResult((await _asyncRetryPolicy.ExecuteAsync(async () => await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)!).ConfigureAwait(false))!);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            if (!CanRetry(command))
            {
                return result;
            }

            return InterceptionResult<DbDataReader>.SuppressWithResult(_retryPolicy.Execute(command.ExecuteReader));
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!CanRetry(command))
            {
                return result;
            }

            return InterceptionResult<DbDataReader>.SuppressWithResult(await _asyncRetryPolicy.ExecuteAsync(async () => await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false));
        }
    }
}
