namespace Jellyfin.Database.Providers.Postgres;

/// <summary>
/// Facts about this provider's migration set that code outside the migrations needs to know.
/// </summary>
internal static class PostgresMigrations
{
    /// <summary>
    /// The identifier of the migration that creates the whole schema.
    /// </summary>
    /// <remarks>
    /// The timestamp is deliberately older than every code migration in
    /// <c>Jellyfin.Server/Migrations/Routines</c>. The migration service runs Entity Framework
    /// migrations and code migrations interleaved in identifier order, and some code migrations run
    /// on a fresh install and query tables, so the schema has to be created before the first of
    /// them. A scaffolded identifier carries the current date and would sort after all of them.
    /// </remarks>
    public const string InitialCreateId = "20250101000000_InitialCreate";
}
