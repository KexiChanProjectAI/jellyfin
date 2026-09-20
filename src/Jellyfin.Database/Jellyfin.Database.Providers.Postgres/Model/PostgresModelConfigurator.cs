using System;
using Jellyfin.Database.Implementations.ValueConverters;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Database.Providers.Postgres.Model;

/// <summary>
/// Applies PostgreSQL specific mapping to Jellyfin's shared entity model.
/// </summary>
internal static class PostgresModelConfigurator
{
    private static readonly DateTimeKindValueConverter _utcConverter = new(DateTimeKind.Utc);

    /// <summary>
    /// Applies the mapping.
    /// </summary>
    /// <param name="modelBuilder">The model builder.</param>
    /// <remarks>
    /// Jellyfin calls the provider hook before <c>ApplyConfigurationsFromAssembly</c>, but entity
    /// discovery runs ahead of both, so every entity and property the model will ever have is
    /// already present here. That is verified by a test which asserts the converter reached all of
    /// them.
    /// </remarks>
    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                var clrType = property.ClrType;

                if (clrType == typeof(string))
                {
                    // SQLite compares text byte by byte. PostgreSQL would use the database's
                    // collation instead, and a typical en_US.UTF-8 cluster ignores punctuation and
                    // spacing at the primary level, which reshuffles SortName ordering and the A-Z
                    // jump bar relative to every other Jellyfin install. "C" also makes lower() and
                    // ILIKE fold ASCII only, which is exactly what SQLite does, so the People name
                    // lookups and the search filters keep their meaning.
                    // Set per column rather than per model so it does not depend on how the database
                    // was created.
                    property.SetCollation("C");

                    // SQLite ignores a declared length, PostgreSQL enforces it. Device names, ids and
                    // app versions come straight from a client header and are never shortened before
                    // being saved, so a varchar column here turns an overlong header into a failed
                    // login (22001) rather than a long row.
                    property.SetMaxLength(null);
                }
                else if ((clrType == typeof(DateTime) || clrType == typeof(DateTime?))
                         && property.GetValueConverter() is null)
                {
                    // Npgsql maps DateTime to timestamp with time zone and refuses any value whose
                    // Kind is not Utc. This matches what the SQLite provider does, so Local and
                    // Unspecified values keep being interpreted in the process time zone rather than
                    // silently changing meaning between providers.
                    property.SetValueConverter(_utcConverter);
                }
            }
        }
    }
}
