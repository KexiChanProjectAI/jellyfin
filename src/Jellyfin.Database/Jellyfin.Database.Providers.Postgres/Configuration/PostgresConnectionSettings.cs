using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Database.Implementations.DbConfiguration;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Jellyfin.Database.Providers.Postgres.Configuration;

/// <summary>
/// Resolves the PostgreSQL connection and the settings around it from every place they can be given.
/// </summary>
internal sealed class PostgresConnectionSettings
{
    private PostgresConnectionSettings(NpgsqlConnectionStringBuilder builder, PostgresDatabaseOptions options, IReadOnlyList<string> sources)
    {
        Builder = builder;
        Options = options;
        Sources = sources;
    }

    /// <summary>
    /// Gets the resolved connection string builder.
    /// </summary>
    public NpgsqlConnectionStringBuilder Builder { get; }

    /// <summary>
    /// Gets the resolved behavior settings.
    /// </summary>
    public PostgresDatabaseOptions Options { get; }

    /// <summary>
    /// Gets a description of where each setting came from, for logging. Never contains a secret.
    /// </summary>
    public IReadOnlyList<string> Sources { get; }

    /// <summary>
    /// Gets the connection string.
    /// </summary>
    public string ConnectionString => Builder.ToString();

    /// <summary>
    /// Gets the connection string with every secret removed, safe to log.
    /// </summary>
    public string RedactedConnectionString
    {
        get
        {
            var copy = new NpgsqlConnectionStringBuilder(Builder.ConnectionString)
            {
                Password = null,
                SslPassword = null
            };
            return copy.ToString();
        }
    }

    /// <summary>
    /// Builds a connection string for a different database on the same server, without pooling.
    /// </summary>
    /// <param name="database">The database to connect to.</param>
    /// <returns>The connection string.</returns>
    /// <remarks>
    /// Used for <c>CREATE DATABASE</c> and <c>ALTER DATABASE ... RENAME</c>, neither of which can run
    /// from inside the database they act on. Pooling is off so the connection is gone the moment it
    /// is disposed, which matters because a lingering session blocks both statements.
    /// </remarks>
    public string BuildConnectionStringFor(string database)
    {
        var copy = new NpgsqlConnectionStringBuilder(Builder.ConnectionString)
        {
            Database = database,
            Pooling = false,
            ApplicationName = Builder.ApplicationName
        };
        return copy.ToString();
    }

    /// <summary>
    /// Resolves the settings.
    /// </summary>
    /// <param name="databaseConfiguration">The database configuration from <c>database.xml</c>.</param>
    /// <param name="configuration">The startup configuration, which carries the environment. May be null at design time.</param>
    /// <param name="applicationVersion">The Jellyfin version, reported to the server as the application name.</param>
    /// <returns>The resolved settings.</returns>
    public static PostgresConnectionSettings Resolve(
        DatabaseConfigurationOptions databaseConfiguration,
        IConfiguration? configuration,
        string applicationVersion)
    {
        ArgumentNullException.ThrowIfNull(databaseConfiguration);

        var sources = new List<string>();
        var options = Clone(databaseConfiguration.PostgreSql);
        var legacy = databaseConfiguration.CustomProviderOptions;

        // Lowest tier first, so that each following tier overwrites what the one below it set.
        ApplyContainerConventions(options, sources);
        ApplyLegacyOptions(options, legacy, sources);
        ApplyXmlOptions(options, databaseConfiguration.PostgreSql, sources);
        ApplyEnvironment(options, configuration, sources);

        var builder = options.ConnectionString is { Length: > 0 } explicitConnectionString
            ? new NpgsqlConnectionStringBuilder(explicitConnectionString)
            : new NpgsqlConnectionStringBuilder();

        if (options.ConnectionString is { Length: > 0 })
        {
            sources.Add("connection string given verbatim");
        }

        // A field set anywhere still wins over the same field inside a verbatim connection string,
        // so that an operator can point a stock connection string at another database or user
        // without rewriting it.
        SetText(options.Host, v => builder.Host = v);
        SetNumber(options.Port, v => builder.Port = v);
        SetText(options.Database, v => builder.Database = v);
        SetText(options.Username, v => builder.Username = v);
        SetText(ReadPassword(options, sources), v => builder.Password = v);
        SetText(options.SslMode, v => builder.SslMode = Enum.Parse<SslMode>(v, true));
        SetText(options.RootCertificate, v => builder.RootCertificate = v);
        SetNumber(options.CommandTimeout, v => builder.CommandTimeout = v);
        SetNumber(options.MaxPoolSize, v => builder.MaxPoolSize = v);

        foreach (var option in options.AdditionalOptions)
        {
            if (!string.IsNullOrWhiteSpace(option.Key))
            {
                // Verbatim, so that any Npgsql keyword is reachable without this class growing a
                // property for it.
                builder[option.Key] = option.Value;
            }
        }

        builder.Host ??= "localhost";
        builder.Database ??= "jellyfin";
        builder.Username ??= "jellyfin";

        if (!builder.ContainsKey("Application Name") || string.IsNullOrEmpty(builder.ApplicationName))
        {
            // The prefix is load bearing: the backup code only ever terminates sessions whose
            // application name starts with "jellyfin", so it cannot disturb anyone else's.
            builder.ApplicationName = "jellyfin+" + applicationVersion;
        }

        if (!builder.ContainsKey("Options") || string.IsNullOrEmpty(builder.Options))
        {
            // Jellyfin's folder filters produce row estimates in the millions for queries that
            // return a page, so the planner opts into JIT compilation and spends seconds compiling a
            // query that runs in milliseconds.
            builder.Options = "-c jit=off";
        }

        if (!builder.ContainsKey("Timezone") || string.IsNullOrEmpty(builder.Timezone))
        {
            // Some queries compare against DateTime constants whose Kind is Unspecified; those are
            // cast using the session time zone, so it must not vary with the server's locale.
            builder.Timezone = "UTC";
        }

        if (!builder.ContainsKey("Command Timeout"))
        {
            builder.CommandTimeout = 60;
        }

        if (!builder.ContainsKey("Maximum Pool Size"))
        {
            // PostgreSQL ships with max_connections = 100 and Npgsql would otherwise open up to 100
            // on its own, leaving nothing for anybody else including our own maintenance
            // connections.
            builder.MaxPoolSize = 50;
        }

        builder.IncludeErrorDetail = options.EnableSensitiveDataLogging;
        builder.PersistSecurityInfo = false;

        return new PostgresConnectionSettings(builder, options, sources);

        static void SetText(string? value, Action<string> apply)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                apply(value);
            }
        }

        static void SetNumber(int? value, Action<int> apply)
        {
            if (value.HasValue)
            {
                apply(value.Value);
            }
        }
    }

    private static PostgresDatabaseOptions Clone(PostgresDatabaseOptions? source)
    {
        var clone = new PostgresDatabaseOptions();
        if (source is null)
        {
            return clone;
        }

        clone.MigrationBackupPolicy = source.MigrationBackupPolicy;
        clone.BackupRetention = source.BackupRetention;
        clone.KeepFailedMigrationDatabase = source.KeepFailedMigrationDatabase;
        clone.SerializeWrites = source.SerializeWrites;
        clone.EnableSensitiveDataLogging = source.EnableSensitiveDataLogging;
        clone.MaintenanceDatabase = source.MaintenanceDatabase;
        clone.ClientToolsPath = source.ClientToolsPath;
        foreach (var option in source.AdditionalOptions)
        {
            clone.AdditionalOptions.Add(option);
        }

        return clone;
    }

    private static void ApplyContainerConventions(PostgresDatabaseOptions target, List<string> sources)
    {
        var applied = false;
        applied |= Take(target, "POSTGRES_HOST", (t, v) => t.Host = v);
        applied |= Take(target, "POSTGRES_PORT", (t, v) => t.Port = ParsePort(v));
        applied |= Take(target, "POSTGRES_DB", (t, v) => t.Database = v);
        applied |= Take(target, "POSTGRES_USER", (t, v) => t.Username = v);
        applied |= Take(target, "POSTGRES_PASSWORD_FILE", (t, v) => t.PasswordFile = v);
        applied |= Take(target, "POSTGRES_PASSWORD", (t, v) => t.Password = v);
        applied |= Take(target, "POSTGRES_SSLMODE", (t, v) => t.SslMode = v);

        if (applied)
        {
            sources.Add("POSTGRES_* environment variables");
        }

        static bool Take(PostgresDatabaseOptions target, string name, Action<PostgresDatabaseOptions, string> apply)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            apply(target, value);
            return true;
        }
    }

    private static void ApplyLegacyOptions(PostgresDatabaseOptions target, CustomDatabaseOptions? legacy, List<string> sources)
    {
        if (legacy is null)
        {
            return;
        }

        var applied = false;
        if (!string.IsNullOrWhiteSpace(legacy.ConnectionString))
        {
            target.ConnectionString = legacy.ConnectionString;
            applied = true;
        }

        foreach (var option in legacy.Options)
        {
            if (string.IsNullOrWhiteSpace(option.Key))
            {
                continue;
            }

            applied = true;
            switch (option.Key.ToUpperInvariant())
            {
                case "ENABLESENSITIVEDATALOGGING":
                    target.EnableSensitiveDataLogging = IsTrue(option.Value);
                    break;
                default:
                    target.AdditionalOptions.Add(option);
                    break;
            }
        }

        if (applied)
        {
            sources.Add("CustomProviderOptions in database.xml");
        }
    }

    private static void ApplyXmlOptions(PostgresDatabaseOptions target, PostgresDatabaseOptions? source, List<string> sources)
    {
        if (source is null)
        {
            return;
        }

        var applied = false;
        applied |= CopyText(source.Host, v => target.Host = v);
        applied |= CopyNumber(source.Port, v => target.Port = v);
        applied |= CopyText(source.Database, v => target.Database = v);
        applied |= CopyText(source.Username, v => target.Username = v);
        applied |= CopyText(source.Password, v => target.Password = v);
        applied |= CopyText(source.PasswordFile, v => target.PasswordFile = v);
        applied |= CopyText(source.ConnectionString, v => target.ConnectionString = v);
        applied |= CopyText(source.SslMode, v => target.SslMode = v);
        applied |= CopyText(source.RootCertificate, v => target.RootCertificate = v);
        applied |= CopyNumber(source.CommandTimeout, v => target.CommandTimeout = v);
        applied |= CopyNumber(source.MaxPoolSize, v => target.MaxPoolSize = v);

        if (applied)
        {
            sources.Add("PostgreSql section in database.xml");
        }

        static bool CopyText(string? value, Action<string> apply)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            apply(value);
            return true;
        }

        static bool CopyNumber(int? value, Action<int> apply)
        {
            if (!value.HasValue)
            {
                return false;
            }

            apply(value.Value);
            return true;
        }
    }

    private static void ApplyEnvironment(PostgresDatabaseOptions target, IConfiguration? configuration, List<string> sources)
    {
        if (configuration is null)
        {
            return;
        }

        var section = configuration.GetSection("database:postgres");
        if (!section.Exists())
        {
            return;
        }

        var applied = false;
        applied |= Take(section, "host", v => target.Host = v);
        applied |= Take(section, "port", v => target.Port = ParsePort(v));
        applied |= Take(section, "database", v => target.Database = v);
        applied |= Take(section, "username", v => target.Username = v);
        applied |= Take(section, "password", v => target.Password = v);
        applied |= Take(section, "passwordfile", v => target.PasswordFile = v);
        applied |= Take(section, "connectionstring", v => target.ConnectionString = v);
        applied |= Take(section, "sslmode", v => target.SslMode = v);
        applied |= Take(section, "rootcertificate", v => target.RootCertificate = v);
        applied |= Take(section, "commandtimeout", v => target.CommandTimeout = ParseInt(v));
        applied |= Take(section, "maxpoolsize", v => target.MaxPoolSize = ParseInt(v));
        applied |= Take(section, "maintenancedatabase", v => target.MaintenanceDatabase = v);
        applied |= Take(section, "clienttoolspath", v => target.ClientToolsPath = v);
        applied |= Take(section, "serializewrites", v => target.SerializeWrites = IsTrue(v));
        applied |= Take(section, "enablesensitivedatalogging", v => target.EnableSensitiveDataLogging = IsTrue(v));
        applied |= Take(section, "backupretention", v => target.BackupRetention = ParseInt(v));
        applied |= Take(section, "migrationbackuppolicy", v => target.MigrationBackupPolicy = Enum.Parse<PostgresMigrationBackupPolicy>(v, true));

        if (applied)
        {
            sources.Add("JELLYFIN_DATABASE__POSTGRES__* environment variables");
        }

        static bool Take(IConfiguration section, string key, Action<string> apply)
        {
            var value = section[key];
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            apply(value);
            return true;
        }
    }

    private static string? ReadPassword(PostgresDatabaseOptions options, List<string> sources)
    {
        // A file wins over an inline value within the same tier: it is how secrets are mounted, and
        // an install that has both almost certainly left the inline one behind by accident.
        if (!string.IsNullOrWhiteSpace(options.PasswordFile))
        {
            if (!File.Exists(options.PasswordFile))
            {
                throw new Jellyfin.Database.Implementations.DatabaseProviderStartupException(
                    $"The configured PostgreSQL password file '{options.PasswordFile}' does not exist.");
            }

            sources.Add("password read from file");
            return File.ReadAllText(options.PasswordFile).TrimEnd('\r', '\n');
        }

        if (!string.IsNullOrEmpty(options.Password))
        {
            sources.Add("password given directly");
            return options.Password;
        }

        // Not an error. Peer, certificate and .pgpass authentication all leave it empty, and the
        // connection attempt is where a genuinely missing password surfaces.
        return null;
    }

    private static bool IsTrue(string? value)
        => value is not null && (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("1", StringComparison.Ordinal));

    private static int ParsePort(string value)
        => ParseInt(value);

    private static int ParseInt(string value)
        => int.Parse(value, CultureInfo.InvariantCulture);
}
