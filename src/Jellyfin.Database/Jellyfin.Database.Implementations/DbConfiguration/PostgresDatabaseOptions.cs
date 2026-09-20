using System.Collections.ObjectModel;

namespace Jellyfin.Database.Implementations.DbConfiguration;

/// <summary>
/// Connection and behavior settings for the PostgreSQL provider, as stored in <c>database.xml</c>.
/// </summary>
/// <remarks>
/// This lives in the shared assembly and deliberately holds no Npgsql types, so that reading the
/// configuration does not depend on the provider being loaded. Every value here can also be supplied
/// through the environment, which is how a container hands over a password without writing it to
/// disk.
/// </remarks>
public class PostgresDatabaseOptions
{
    /// <summary>
    /// Gets or sets the host name or address of the PostgreSQL server.
    /// </summary>
    public string? Host { get; set; }

    /// <summary>
    /// Gets or sets the port the PostgreSQL server listens on.
    /// </summary>
    public int? Port { get; set; }

    /// <summary>
    /// Gets or sets the name of the database to use.
    /// </summary>
    public string? Database { get; set; }

    /// <summary>
    /// Gets or sets the user to connect as.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Gets or sets the password to connect with.
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="PasswordFile"/> or the environment. A password here sits in plain text in
    /// the configuration directory.
    /// </remarks>
    public string? Password { get; set; }

    /// <summary>
    /// Gets or sets the path to a file whose contents are the password.
    /// </summary>
    /// <remarks>
    /// This is the shape container secrets take. A single trailing newline is ignored.
    /// </remarks>
    public string? PasswordFile { get; set; }

    /// <summary>
    /// Gets or sets a complete connection string, which replaces the individual settings above.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Gets or sets the TLS mode, as an Npgsql <c>SslMode</c> name such as <c>Require</c> or <c>VerifyFull</c>.
    /// </summary>
    public string? SslMode { get; set; }

    /// <summary>
    /// Gets or sets the path to the CA certificate used to verify the server.
    /// </summary>
    public string? RootCertificate { get; set; }

    /// <summary>
    /// Gets or sets the command timeout in seconds.
    /// </summary>
    public int? CommandTimeout { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of pooled connections.
    /// </summary>
    public int? MaxPoolSize { get; set; }

    /// <summary>
    /// Gets or sets the database to connect to when creating or renaming the Jellyfin database.
    /// </summary>
    /// <remarks>
    /// <c>CREATE DATABASE</c> cannot run from inside the database being created, so it needs a
    /// second database to connect to. Defaults to <c>postgres</c>.
    /// </remarks>
    public string? MaintenanceDatabase { get; set; }

    /// <summary>
    /// Gets or sets what should happen when no migration backup can be taken.
    /// </summary>
    public PostgresMigrationBackupPolicy MigrationBackupPolicy { get; set; } = PostgresMigrationBackupPolicy.Required;

    /// <summary>
    /// Gets or sets the directory holding <c>pg_dump</c> and <c>psql</c>.
    /// </summary>
    /// <remarks>
    /// Only needed when the client tools are not on the path or do not match the server version.
    /// </remarks>
    public string? ClientToolsPath { get; set; }

    /// <summary>
    /// Gets or sets the number of migration backups to keep.
    /// </summary>
    public int BackupRetention { get; set; } = 1;

    /// <summary>
    /// Gets or sets a value indicating whether the database a failed migration was rolled back from is kept for inspection.
    /// </summary>
    public bool KeepFailedMigrationDatabase { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether writes are serialised against each other with an advisory lock.
    /// </summary>
    /// <remarks>
    /// Off by default. It trades throughput for the single writer semantics the codebase grew up
    /// with under SQLite, and a long running transaction such as a backup stalls every writer behind
    /// it.
    /// </remarks>
    public bool SerializeWrites { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether parameter values are written to the log.
    /// </summary>
    public bool EnableSensitiveDataLogging { get; set; }

    /// <summary>
    /// Gets or sets additional Npgsql connection string keywords, applied verbatim.
    /// </summary>
    public Collection<CustomDatabaseOption> AdditionalOptions { get; set; } = [];
}
