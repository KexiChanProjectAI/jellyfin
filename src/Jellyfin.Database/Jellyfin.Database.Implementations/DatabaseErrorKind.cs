namespace Jellyfin.Database.Implementations;

/// <summary>
/// Provider independent classification of a database error.
/// </summary>
/// <remarks>
/// Error reporting is provider specific: SQLite reports a locked database through
/// <c>SqliteErrorCode</c>, PostgreSQL reports a serialization failure through a SQLSTATE. Code that
/// has to react to such a failure asks the provider to classify the exception instead of matching
/// on a message, which breaks as soon as the provider or its language changes.
/// </remarks>
public enum DatabaseErrorKind
{
    /// <summary>
    /// The error has no meaning that callers can act on.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The write could not proceed because another writer held the database, and retrying the same
    /// work may succeed.
    /// </summary>
    TransientLock = 1,

    /// <summary>
    /// The write violated a unique constraint, typically because a concurrent writer inserted the
    /// same row first.
    /// </summary>
    UniqueViolation = 2,

    /// <summary>
    /// The write violated a foreign key constraint.
    /// </summary>
    ForeignKeyViolation = 3
}
