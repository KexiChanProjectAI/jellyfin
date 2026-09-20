using System.Collections.Generic;

namespace Jellyfin.Database.Implementations.DbConfiguration;

/// <summary>
/// Options to configure jellyfins managed database.
/// </summary>
public class DatabaseConfigurationOptions
{
    /// <summary>
    /// Gets or Sets the type of database jellyfin should use.
    /// </summary>
    public required string DatabaseType { get; set; }

    /// <summary>
    /// Gets or sets the options required to use a custom database provider.
    /// </summary>
    public CustomDatabaseOptions? CustomProviderOptions { get; set; }

    /// <summary>
    /// Gets or Sets the kind of locking behavior jellyfin should perform. Possible options are "NoLock", "Pessimistic", "Optimistic".
    /// Defaults to "NoLock".
    /// </summary>
    public DatabaseLockingBehaviorTypes LockingBehavior { get; set; }

    /// <summary>
    /// Gets or sets the settings for the built in PostgreSQL provider.
    /// </summary>
    /// <remarks>
    /// Absent from configurations written before PostgreSQL support existed, and absent on any
    /// install that does not use it, so it stays optional.
    /// </remarks>
    public PostgresDatabaseOptions? PostgreSql { get; set; }
}
