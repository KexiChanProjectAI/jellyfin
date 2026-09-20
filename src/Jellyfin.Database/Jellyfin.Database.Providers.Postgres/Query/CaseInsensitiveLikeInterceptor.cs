using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;

namespace Jellyfin.Database.Providers.Postgres.Query;

/// <summary>
/// Rewrites <see cref="DbFunctionsExtensions.Like(DbFunctions, string, string)"/> to Npgsql's
/// <c>ILIKE</c> so that text matching keeps the case insensitivity Jellyfin's queries assume.
/// </summary>
/// <remarks>
/// SQLite's <c>LIKE</c> folds ASCII case by default and PostgreSQL's does not. Jellyfin relies on
/// the SQLite behavior in the item search against <c>OriginalTitle</c> and in every activity log
/// filter, and the difference raises no error: it silently returns fewer rows. Rewriting once here
/// keeps the call sites provider neutral.
/// <para>
/// This runs as an <see cref="IQueryExpressionInterceptor"/>, a public extension point, rather than
/// by replacing EF's query translation preprocessor, so it does not depend on internals.
/// </para>
/// <para>
/// Cost: <c>ILIKE</c> cannot use a plain b-tree index for a prefix pattern. Most of Jellyfin's
/// patterns are <c>%term%</c>, which no b-tree serves anyway; the answer for a prefix search that
/// matters is the <c>pg_trgm</c> index this provider installs when it is permitted to, not
/// reverting this.
/// </para>
/// </remarks>
internal sealed class CaseInsensitiveLikeInterceptor : IQueryExpressionInterceptor
{
    /// <summary>
    /// The one instance every context shares.
    /// </summary>
    /// <remarks>
    /// EF keys its internal service provider cache partly on the interceptors an options object
    /// carries. Handing it a new instance per context builds a new service provider each time, which
    /// EF eventually reports as an error.
    /// </remarks>
    public static readonly CaseInsensitiveLikeInterceptor Instance = new();

    private CaseInsensitiveLikeInterceptor()
    {
    }

    /// <summary>
    /// Rewrites the query expression before EF translates it.
    /// </summary>
    /// <param name="queryExpression">The query expression, as modified by any earlier interceptor.</param>
    /// <param name="eventData">Contextual information supplied by EF Core.</param>
    /// <returns>The rewritten expression.</returns>
    public Expression QueryCompilationStarting(
        Expression queryExpression,
        QueryExpressionEventData eventData)
        => LikeToILikeRewriter.Instance.Visit(queryExpression);

    private sealed class LikeToILikeRewriter : ExpressionVisitor
    {
        public static readonly LikeToILikeRewriter Instance = new();

        // Exact signatures of methods that exist, so GetMethod cannot return null. A null dance here
        // would silently skip the rewrite, which is the failure this class exists to prevent.
        private static readonly MethodInfo _like = typeof(DbFunctionsExtensions).GetMethod(
            nameof(DbFunctionsExtensions.Like),
            [typeof(DbFunctions), typeof(string), typeof(string)])!;

        private static readonly MethodInfo _likeWithEscape = typeof(DbFunctionsExtensions).GetMethod(
            nameof(DbFunctionsExtensions.Like),
            [typeof(DbFunctions), typeof(string), typeof(string), typeof(string)])!;

        private static readonly MethodInfo _iLike = typeof(NpgsqlDbFunctionsExtensions).GetMethod(
            nameof(NpgsqlDbFunctionsExtensions.ILike),
            [typeof(DbFunctions), typeof(string), typeof(string)])!;

        private static readonly MethodInfo _iLikeWithEscape = typeof(NpgsqlDbFunctionsExtensions).GetMethod(
            nameof(NpgsqlDbFunctionsExtensions.ILike),
            [typeof(DbFunctions), typeof(string), typeof(string), typeof(string)])!;

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node!.Method == _like)
            {
                return Expression.Call(_iLike, Visit(node.Arguments));
            }

            if (node.Method == _likeWithEscape)
            {
                return Expression.Call(_iLikeWithEscape, Visit(node.Arguments));
            }

            return base.VisitMethodCall(node);
        }
    }
}
