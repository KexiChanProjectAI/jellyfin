using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Emby.Server.Implementations.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Postgres;

/// <summary>
/// Runs the real item repository against PostgreSQL and against SQLite with the same data, and
/// compares the answers.
/// </summary>
/// <remarks>
/// The unit tests above cover the provider's own behaviour. This covers the queries Jellyfin actually
/// issues, which is where a provider difference would reach users: the repository groups by a key and
/// takes min(Id), orders by nullable columns, and matches text with LIKE, and every one of those
/// behaves differently on PostgreSQL unless the provider corrects it.
/// </remarks>
[Trait("Category", "PostgreSQL")]
public sealed class PostgresRepositoryParityTests
{
    private static readonly Guid _folderId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid _primaryId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid _mergedVersionId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid _noSortNameId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid _upperCaseId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

    /// <summary>
    /// The queries to compare, each returning an ordered list of item ids.
    /// </summary>
    public static TheoryData<string> QueryNames =>
    [
        "children-of-folder",
        "children-including-alternate-versions",
        "sort-by-name-ascending",
        "sort-by-name-descending",
        "sort-by-premiere-date-ascending",
        "search-lowercase-term",
        "search-uppercase-term",
        "name-starts-with"
    ];

    [Theory]
    [MemberData(nameof(QueryNames))]
    public async Task Query_AnswersTheSameOnBothProviders(string queryName)
    {
        var postgres = await PostgresTestDatabase.CreateAsync().ConfigureAwait(true);
        await using (postgres.ConfigureAwait(true))
        {
            using var sqlite = new SqliteParityHarness();

            var postgresContext = postgres.CreateDbContext();
            await using (postgresContext.ConfigureAwait(true))
            {
                Seed(postgresContext);
                await postgresContext.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            using (var sqliteContext = sqlite.CreateDbContext())
            {
                Seed(sqliteContext);
                await sqliteContext.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            var lookup = new ItemTypeLookup();
            var postgresResult = Run(CreateRepository(postgres.CreateProvider().DbContextFactory!, lookup), queryName);
            var sqliteResult = Run(CreateRepository(sqlite.ContextFactory, lookup), queryName);

            // Two empty lists are equal and prove nothing, so require the query to have matched.
            Assert.NotEmpty(sqliteResult);
            Assert.Equal(sqliteResult, postgresResult);
        }
    }

    private static IReadOnlyList<Guid> Run(BaseItemRepository repository, string queryName)
    {
        var query = queryName switch
        {
            "children-of-folder" => new InternalItemsQuery { ParentId = _folderId },
            "children-including-alternate-versions" => new InternalItemsQuery { ParentId = _folderId, IncludeAlternateVersions = true },
            "sort-by-name-ascending" => new InternalItemsQuery { OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)] },
            "sort-by-name-descending" => new InternalItemsQuery { OrderBy = [(ItemSortBy.SortName, SortOrder.Descending)] },
            "sort-by-premiere-date-ascending" => new InternalItemsQuery { OrderBy = [(ItemSortBy.PremiereDate, SortOrder.Ascending)] },
            "search-lowercase-term" => new InternalItemsQuery { SearchTerm = "bunny" },
            "search-uppercase-term" => new InternalItemsQuery { SearchTerm = "BUNNY" },
            "name-starts-with" => new InternalItemsQuery { NameStartsWith = "B" },
            _ => throw new ArgumentOutOfRangeException(nameof(queryName), queryName, "Unknown query.")
        };

        return repository.GetItemList(query).Select(e => e.Id).ToArray();
    }

    private static void Seed(JellyfinDbContext context)
    {
        var lookup = new ItemTypeLookup();
        var movie = lookup.BaseItemKindNames[BaseItemKind.Movie]!;

        context.BaseItems.Add(new BaseItemEntity
        {
            Id = _folderId,
            Type = lookup.BaseItemKindNames[BaseItemKind.Folder]!,
            Name = "Movies",
            Path = "/movies",
            IsFolder = true
        });

        context.BaseItems.Add(Movie(_primaryId, movie, "Big Buck Bunny", "big buck bunny", "/m/bbb-1080p.mp4", null, new DateTime(2008, 4, 10, 0, 0, 0, DateTimeKind.Utc)));
        context.BaseItems.Add(Movie(_mergedVersionId, movie, "Big Buck Bunny", "big buck bunny", "/m/bbb-2160p.mp4", _primaryId, new DateTime(2008, 4, 10, 0, 0, 0, DateTimeKind.Utc)));

        // A null sort key, which the two engines order differently unless the provider corrects it.
        var noSortName = Movie(_noSortNameId, movie, "Zeta", "zeta", "/m/zeta.mp4", null, null);
        noSortName.SortName = null;
        context.BaseItems.Add(noSortName);

        // The search term appears only in OriginalTitle, and only in a different case. CleanName is
        // pre-lowercased by the caller, so it is the OriginalTitle half of the search that depends on
        // LIKE folding case, and this row is the one that distinguishes the two.
        var originalTitleOnly = Movie(_upperCaseId, movie, "Une Aventure", "une aventure", "/m/upper.mp4", null, new DateTime(2010, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        originalTitleOnly.OriginalTitle = "BUNNY Uppercase";
        context.BaseItems.Add(originalTitleOnly);
    }

    private static BaseItemEntity Movie(Guid id, string type, string name, string cleanName, string path, Guid? primaryVersionId, DateTime? premiereDate)
        => new()
        {
            Id = id,
            Type = type,
            Name = name,
            CleanName = cleanName,
            SortName = name,
            OriginalTitle = name,
            Path = path,
            ParentId = _folderId,
            TopParentId = _folderId,
            PrimaryVersionId = primaryVersionId,
            PresentationUniqueKey = name,
            PremiereDate = premiereDate,
            IsFolder = false,
            IsVirtualItem = false
        };

    private static BaseItemRepository CreateRepository(IDbContextFactory<JellyfinDbContext> factory, ItemTypeLookup lookup)
    {
        var configuration = new Mock<IServerConfigurationManager>();
        configuration.Setup(c => c.Configuration).Returns(new ServerConfiguration());

        return new BaseItemRepository(
            factory,
            new Mock<IServerApplicationHost>().Object,
            lookup,
            configuration.Object,
            NullLogger<BaseItemRepository>.Instance);
    }
}
