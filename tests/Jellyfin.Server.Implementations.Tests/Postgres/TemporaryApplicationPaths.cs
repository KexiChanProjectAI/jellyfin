using System;
using System.IO;
using MediaBrowser.Common.Configuration;
using Moq;

namespace Jellyfin.Server.Implementations.Tests.Postgres;

/// <summary>
/// Application paths rooted in a temporary directory that is removed on dispose.
/// </summary>
/// <remarks>
/// The dump strategy writes real files under <see cref="IApplicationPaths.DataPath"/>, so a test
/// that exercises it needs somewhere real to put them.
/// </remarks>
internal sealed class TemporaryApplicationPaths : IDisposable
{
    private readonly string _root;

    /// <summary>
    /// Initializes a new instance of the <see cref="TemporaryApplicationPaths"/> class.
    /// </summary>
    public TemporaryApplicationPaths()
    {
        _root = Path.Combine(Path.GetTempPath(), "jellyfin-pg-test-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);

        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(e => e.DataPath).Returns(_root);
        Paths = paths.Object;
    }

    /// <summary>
    /// Gets the paths.
    /// </summary>
    public IApplicationPaths Paths { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a test over.
        }
    }
}
