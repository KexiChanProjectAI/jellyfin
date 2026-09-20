using System;

namespace Jellyfin.Database.Implementations;

/// <summary>
/// Thrown when the configured database cannot be used and the server cannot start.
/// </summary>
/// <remarks>
/// The message is shown to the operator verbatim and without a stack trace, so it has to say what
/// was tried, why it failed, and what to change. Anything that is merely a symptom belongs in the
/// inner exception.
/// </remarks>
public class DatabaseProviderStartupException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseProviderStartupException"/> class.
    /// </summary>
    public DatabaseProviderStartupException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseProviderStartupException"/> class.
    /// </summary>
    /// <param name="message">The operator facing message.</param>
    public DatabaseProviderStartupException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseProviderStartupException"/> class.
    /// </summary>
    /// <param name="message">The operator facing message.</param>
    /// <param name="innerException">The underlying failure.</param>
    public DatabaseProviderStartupException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
