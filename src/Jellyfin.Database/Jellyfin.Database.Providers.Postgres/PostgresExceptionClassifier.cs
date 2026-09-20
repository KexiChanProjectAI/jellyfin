using System;
using Jellyfin.Database.Implementations;
using Npgsql;

namespace Jellyfin.Database.Providers.Postgres;

/// <summary>
/// Maps PostgreSQL SQLSTATE codes onto <see cref="DatabaseErrorKind"/>.
/// </summary>
internal static class PostgresExceptionClassifier
{
    /// <summary>
    /// Classifies an exception.
    /// </summary>
    /// <param name="exception">The exception, possibly wrapped in a <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>.</param>
    /// <returns>The kind of failure.</returns>
    public static DatabaseErrorKind Classify(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres.SqlState switch
                {
                    // serialization_failure and deadlock_detected. Both mean the work can succeed if
                    // the whole transaction is replayed; neither means this statement can be retried.
                    PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected => DatabaseErrorKind.TransientLock,
                    PostgresErrorCodes.UniqueViolation => DatabaseErrorKind.UniqueViolation,
                    PostgresErrorCodes.ForeignKeyViolation => DatabaseErrorKind.ForeignKeyViolation,
                    _ => DatabaseErrorKind.Unknown
                };
            }
        }

        return DatabaseErrorKind.Unknown;
    }
}
