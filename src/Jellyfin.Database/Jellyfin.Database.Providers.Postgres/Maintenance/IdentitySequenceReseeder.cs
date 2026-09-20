using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Database.Providers.Postgres.Maintenance;

/// <summary>
/// Moves identity sequences past the rows that were just imported.
/// </summary>
/// <remarks>
/// Rows inserted with an explicit primary key do not advance the sequence behind that key, so
/// straight after an import the next insert asks for value one and collides with an imported row.
/// That is what makes a naive copy fail later with a duplicate key on ActivityLogs or UserData
/// rather than at the time of the copy.
/// </remarks>
internal static class IdentitySequenceReseeder
{
    /// <summary>
    /// Reseeds every sequence owned by a column of a table in the model.
    /// </summary>
    /// <param name="dbContext">The context holding the open import transaction.</param>
    /// <param name="logger">A logger.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task ReseedAsync(JellyfinDbContext dbContext, ILogger logger, CancellationToken cancellationToken)
    {
        // ALTER SEQUENCE rather than setval: it is transactional, so a failure later in the import
        // takes the reseed with it instead of leaving the sequences moved on a database that was
        // rolled back.
        //
        // The sequence is found through pg_depend, which is how an identity or serial column records
        // that it owns one. Sequences that belong to nothing, or to a table outside this schema, are
        // left alone.
        const string Sql = """
            DO $$
            DECLARE
                rec record;
                next_value bigint;
            BEGIN
                FOR rec IN
                    SELECT quote_ident(seq_ns.nspname) || '.' || quote_ident(seq.relname) AS sequence_name,
                           quote_ident(tab_ns.nspname) || '.' || quote_ident(tab.relname) AS table_name,
                           quote_ident(att.attname) AS column_name
                    FROM pg_class seq
                    JOIN pg_namespace seq_ns ON seq_ns.oid = seq.relnamespace
                    JOIN pg_depend dep ON dep.objid = seq.oid AND dep.classid = 'pg_class'::regclass
                    JOIN pg_class tab ON tab.oid = dep.refobjid
                    JOIN pg_namespace tab_ns ON tab_ns.oid = tab.relnamespace
                    JOIN pg_attribute att ON att.attrelid = tab.oid AND att.attnum = dep.refobjsubid
                    WHERE seq.relkind = 'S'
                      AND dep.deptype IN ('a', 'i')
                      AND tab_ns.nspname = current_schema()
                LOOP
                    EXECUTE format('SELECT COALESCE(MAX(%s), 0) + 1 FROM %s', rec.column_name, rec.table_name)
                        INTO next_value;
                    EXECUTE format('ALTER SEQUENCE %s RESTART WITH %s', rec.sequence_name, next_value);
                END LOOP;
            END
            $$;
            """;

        await dbContext.Database.ExecuteSqlRawAsync(Sql, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Identity sequences were moved past the imported rows");
    }
}
