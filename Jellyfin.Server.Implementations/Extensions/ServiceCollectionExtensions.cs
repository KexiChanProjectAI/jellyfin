using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Postgres;
using Jellyfin.Database.Providers.Sqlite;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using JellyfinDbProviderFactory = System.Func<System.IServiceProvider, Jellyfin.Database.Implementations.IJellyfinDatabaseProvider>;

namespace Jellyfin.Server.Implementations.Extensions;

/// <summary>
/// Extensions for the <see cref="IServiceCollection"/> interface.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// The provider a fresh installation uses when nothing says otherwise.
    /// </summary>
    public const string DefaultDatabaseProviderKey = "Jellyfin-PgSql";

    /// <summary>
    /// The provider that needs no server of its own.
    /// </summary>
    public const string SqliteDatabaseProviderKey = "Jellyfin-SQLite";

    private static IEnumerable<Type> DatabaseProviderTypes()
    {
        yield return typeof(SqliteDatabaseProvider);
        yield return typeof(PostgresDatabaseProvider);
    }

    private static IDictionary<string, JellyfinDbProviderFactory> GetSupportedDbProviders()
    {
        var items = new Dictionary<string, JellyfinDbProviderFactory>(StringComparer.InvariantCultureIgnoreCase);
        foreach (var providerType in DatabaseProviderTypes())
        {
            var keyAttribute = providerType.GetCustomAttribute<JellyfinDatabaseProviderKeyAttribute>();
            if (keyAttribute is null || string.IsNullOrWhiteSpace(keyAttribute.DatabaseProviderKey))
            {
                continue;
            }

            var provider = providerType;
            items[keyAttribute.DatabaseProviderKey] = (services) => (IJellyfinDatabaseProvider)ActivatorUtilities.CreateInstance(services, providerType);
        }

        return items;
    }

    /// <summary>
    /// Reports whether this data directory already holds a database from an earlier installation.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <returns>True when an upgrade, false when a new installation.</returns>
    /// <remarks>
    /// jellyfin.db is the current database and library.db is the one from 10.10 and earlier, which
    /// the startup migrations import. Either means this server has data and has to keep reading it
    /// from SQLite.
    /// </remarks>
    private static bool HasExistingSqliteDatabase(IApplicationPaths applicationPaths)
        => File.Exists(Path.Combine(applicationPaths.DataPath, "jellyfin.db"))
            || File.Exists(Path.Combine(applicationPaths.DataPath, "library.db"));

    private static JellyfinDbProviderFactory? LoadDatabasePlugin(CustomDatabaseOptions customProviderOptions, IApplicationPaths applicationPaths)
    {
        if (string.IsNullOrWhiteSpace(customProviderOptions.PluginName)
            || string.IsNullOrWhiteSpace(customProviderOptions.PluginAssembly))
        {
            throw new InvalidOperationException(
                "A PLUGIN_PROVIDER database needs both PluginName and PluginAssembly to be set in database.xml.");
        }

        var plugin = Directory.EnumerateDirectories(applicationPaths.PluginsPath)
            .Where(e => Path.GetFileName(e)!.StartsWith(customProviderOptions.PluginName, StringComparison.OrdinalIgnoreCase))
            .Order()
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"The requested custom database plugin with the name '{customProviderOptions.PluginName}' could not been found in '{applicationPaths.PluginsPath}'");

        var dbProviderAssembly = Path.Combine(plugin, Path.ChangeExtension(customProviderOptions.PluginAssembly, "dll"));
        if (!File.Exists(dbProviderAssembly))
        {
            throw new InvalidOperationException($"Could not find the requested assembly at '{dbProviderAssembly}'");
        }

        // we have to load the assembly without proxy to ensure maximum performance for this.
        var assembly = Assembly.LoadFrom(dbProviderAssembly);
        var dbProviderType = assembly.GetExportedTypes().FirstOrDefault(f => f.IsAssignableTo(typeof(IJellyfinDatabaseProvider)))
            ?? throw new InvalidOperationException($"Could not find any type implementing the '{nameof(IJellyfinDatabaseProvider)}' interface.");

        return (services) => (IJellyfinDatabaseProvider)ActivatorUtilities.CreateInstance(services, dbProviderType);
    }

    /// <summary>
    /// Adds the <see cref="IDbContextFactory{TContext}"/> interface to the service collection with second level caching enabled.
    /// </summary>
    /// <param name="serviceCollection">An instance of the <see cref="IServiceCollection"/> interface.</param>
    /// <param name="configurationManager">The server configuration manager.</param>
    /// <param name="configuration">The startup Configuration.</param>
    /// <returns>The updated service collection.</returns>
    public static IServiceCollection AddJellyfinDbContext(
        this IServiceCollection serviceCollection,
        IServerConfigurationManager configurationManager,
        IConfiguration configuration)
    {
        var efCoreConfiguration = configurationManager.GetConfiguration<DatabaseConfigurationOptions>("database");
        JellyfinDbProviderFactory? providerFactory = null;

        if (efCoreConfiguration?.DatabaseType is null)
        {
            var cmdMigrationArgument = configuration.GetValue<string>("migration-provider");
            if (!string.IsNullOrWhiteSpace(cmdMigrationArgument))
            {
                efCoreConfiguration = new DatabaseConfigurationOptions()
                {
                    DatabaseType = cmdMigrationArgument,
                };
            }
            else
            {
                var databaseType = configuration.GetValue<string>("database:type");
                var isNewInstallation = !HasExistingSqliteDatabase(configurationManager.ApplicationPaths);

                if (string.IsNullOrWhiteSpace(databaseType))
                {
                    // An installation that predates database.xml has no DatabaseType but does have a
                    // database, so a missing file cannot be read as "new installation". Only an
                    // installation with neither gets the new default.
                    databaseType = isNewInstallation ? DefaultDatabaseProviderKey : SqliteDatabaseProviderKey;
                }

                efCoreConfiguration = new DatabaseConfigurationOptions()
                {
                    DatabaseType = databaseType,
                    LockingBehavior = DatabaseLockingBehaviorTypes.NoLock
                };

                // A PostgreSQL choice is not written out here. It is only known to be usable once the
                // connection has been proven, and persisting it before that would turn one failed
                // start into a permanent one, with no way back to SQLite that does not involve
                // editing the file by hand.
                if (!databaseType.Equals(DefaultDatabaseProviderKey, StringComparison.OrdinalIgnoreCase))
                {
                    configurationManager.SaveConfiguration("database", efCoreConfiguration);
                }
            }
        }

        if (efCoreConfiguration.DatabaseType.Equals("PLUGIN_PROVIDER", StringComparison.OrdinalIgnoreCase))
        {
            if (efCoreConfiguration.CustomProviderOptions is null)
            {
                throw new InvalidOperationException("The custom database provider must declare the custom provider options to work");
            }

            providerFactory = LoadDatabasePlugin(efCoreConfiguration.CustomProviderOptions, configurationManager.ApplicationPaths);
        }
        else
        {
            var providers = GetSupportedDbProviders();
            if (!providers.TryGetValue(efCoreConfiguration.DatabaseType.ToUpperInvariant(), out providerFactory!))
            {
                throw new InvalidOperationException($"Jellyfin cannot find the database provider of type '{efCoreConfiguration.DatabaseType}'. Supported types are {string.Join(", ", providers.Keys)}");
            }
        }

        serviceCollection.AddSingleton<IJellyfinDatabaseProvider>(providerFactory!);

        // The provider gets to reject a behavior that is meaningless or unsafe on it, rather than
        // leaving the operator with a setting that silently does nothing or deadlocks.
        serviceCollection.AddSingleton<IEntityFrameworkCoreLockingBehavior>(serviceProvider =>
        {
            var provider = serviceProvider.GetRequiredService<IJellyfinDatabaseProvider>();
            var behavior = provider.NormalizeLockingBehavior(efCoreConfiguration.LockingBehavior);
            return behavior switch
            {
                DatabaseLockingBehaviorTypes.Pessimistic =>
                    ActivatorUtilities.CreateInstance<PessimisticLockBehavior>(serviceProvider),
                DatabaseLockingBehaviorTypes.Optimistic =>
                    ActivatorUtilities.CreateInstance<OptimisticLockBehavior>(serviceProvider),
                _ => ActivatorUtilities.CreateInstance<NoLockBehavior>(serviceProvider)
            };
        });

        serviceCollection.AddPooledDbContextFactory<JellyfinDbContext>((serviceProvider, opt) =>
        {
            var provider = serviceProvider.GetRequiredService<IJellyfinDatabaseProvider>();
            provider.Initialise(opt, efCoreConfiguration);
            var lockingBehavior = serviceProvider.GetRequiredService<IEntityFrameworkCoreLockingBehavior>();
            lockingBehavior.Initialise(opt);
        });

        return serviceCollection;
    }
}
