// NpgsqlQuerySqlGeneratorFactory and NpgsqlQuerySqlGenerator live in an .Internal namespace
// (EF1001), but they are what Npgsql itself registers, and the generator's constructor is the only
// place the null ordering flag can be set from outside. A package bump that changes either
// signature breaks the build rather than the behavior, and the resulting SQL is pinned by a test.
#pragma warning disable EF1001

using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Internal;

namespace Jellyfin.Database.Providers.Postgres.Query;

/// <summary>
/// Creates Npgsql's query SQL generator with reverse null ordering on, so that every <c>ORDER BY</c>
/// carries <c>NULLS FIRST</c> ascending or <c>NULLS LAST</c> descending and a null sorts where SQLite
/// puts it.
/// </summary>
/// <remarks>
/// SQLite treats null as smaller than every value and PostgreSQL as larger, so on the same query an
/// unrated item opens a rating descending list on PostgreSQL instead of closing it. That reaches
/// users as Next Up picking a different episode and as reshuffled library pages. Npgsql already
/// implements the SQLite placement but keeps the switch internal, because it does not rebuild
/// indexes to match: an affected b-tree index can still filter but may no longer supply the order,
/// so a sort may examine every qualifying row even for one page. The follow up, if plans justify it,
/// is <c>NULLS FIRST</c> on the sort bearing indexes plus a migration.
/// <para>
/// The alternative, adding an explicit null ordering key to every Jellyfin query, was rejected: it
/// doubles the sort terms, defeats index ordered scans on SQLite as well, and silently misses every
/// ordering expression outside the central mapper.
/// </para>
/// </remarks>
internal sealed class NullPlacementQuerySqlGeneratorFactory : NpgsqlQuerySqlGeneratorFactory
{
    private readonly QuerySqlGeneratorDependencies _dependencies;
    private readonly IRelationalTypeMappingSource _typeMappingSource;
    private readonly INpgsqlSingletonOptions _npgsqlSingletonOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="NullPlacementQuerySqlGeneratorFactory"/> class.
    /// </summary>
    /// <param name="dependencies">Dependencies supplied by EF Core.</param>
    /// <param name="typeMappingSource">The type mapping source supplied by EF Core.</param>
    /// <param name="npgsqlSingletonOptions">Npgsql's provider options.</param>
    public NullPlacementQuerySqlGeneratorFactory(
        QuerySqlGeneratorDependencies dependencies,
        IRelationalTypeMappingSource typeMappingSource,
        INpgsqlSingletonOptions npgsqlSingletonOptions)
        : base(dependencies, typeMappingSource, npgsqlSingletonOptions)
    {
        _dependencies = dependencies;
        _typeMappingSource = typeMappingSource;
        _npgsqlSingletonOptions = npgsqlSingletonOptions;
    }

    /// <inheritdoc />
    public override QuerySqlGenerator Create()
        => new NpgsqlQuerySqlGenerator(
            _dependencies,
            _typeMappingSource,
            reverseNullOrderingEnabled: true,
            _npgsqlSingletonOptions.PostgresVersion);
}
