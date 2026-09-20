namespace Jellyfin.Database.Implementations.DbConfiguration;

/// <summary>
/// What to do when a migration needs a backup and none can be taken.
/// </summary>
public enum PostgresMigrationBackupPolicy
{
    /// <summary>
    /// Refuse to migrate. The server stops before the schema is touched, with the database intact.
    /// </summary>
    Required = 0,

    /// <summary>
    /// Migrate anyway, with no way back. The operator is expected to hold their own backup.
    /// </summary>
    Skip = 1
}
