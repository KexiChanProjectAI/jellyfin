using System;

namespace Jellyfin.Server.Implementations.FullSystemBackup;

/// <summary>
/// Manifest type for backups internal structure.
/// </summary>
internal class BackupManifest
{
    public required Version ServerVersion { get; set; }

    public required Version BackupEngineVersion { get; set; }

    public required DateTimeOffset DateCreated { get; set; }

    public required string[] DatabaseTables { get; set; }

    /// <summary>
    /// Gets or sets the database provider this backup was taken from.
    /// </summary>
    /// <remarks>
    /// Null in archives written before this was recorded, which were all SQLite. A restore replaces
    /// the migration history wholesale, and each provider has its own migration identifiers, so an
    /// archive from another provider would leave the database claiming migrations that never ran
    /// against it.
    /// </remarks>
    public string? DatabaseProviderType { get; set; }

    public required BackupOptions Options { get; set; }
}
