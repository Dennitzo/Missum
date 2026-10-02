using Missum.Core.Contracts;
using Missum.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Missum.Tests;

internal sealed class TestEnvironment : IAsyncDisposable
{
    private readonly HashSet<string> _additionalDatabasePaths = [];
    private TestEnvironment(string directory, ServiceProvider services)
    {
        Directory = directory;
        Services = services;
        DatabasePath = services.GetRequiredService<IMissumDatabase>().DatabasePath;
    }

    internal string Directory { get; }
    internal string DatabasePath { get; }
    internal ServiceProvider Services { get; }
    internal T Get<T>() where T : notnull => Services.GetRequiredService<T>();
    internal void TrackDatabase(string databasePath) => _additionalDatabasePaths.Add(databasePath);

    internal static async Task<TestEnvironment> CreateAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"Missum-tests-{Guid.NewGuid():N}");
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddMissumInfrastructure(options => options.DataDirectory = directory);
        var services = collection.BuildServiceProvider(validateScopes: true);
        var environment = new TestEnvironment(directory, services);
        await environment.Get<IMissumDatabase>().InitializeAsync().ConfigureAwait(false);
        return environment;
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync().ConfigureAwait(false);
        TestSqlitePools.ClearDatabasePool(DatabasePath);
        try
        {
            var root = Path.GetFullPath(Directory);
            if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())
                || !Path.GetFileName(root).StartsWith("Missum-tests-", StringComparison.Ordinal)) throw new IOException("Unexpected test directory.");
            TestSqlitePools.ClearDatabaseBackups(DatabasePath);
            foreach (var databasePath in _additionalDatabasePaths)
            {
                TestSqlitePools.ClearDatabasePool(databasePath);
                TestSqlitePools.ClearDatabaseBackups(databasePath);
            }
            foreach (var file in System.IO.Directory.EnumerateFiles(root, "*", new EnumerationOptions
                { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            System.IO.Directory.Delete(root, recursive: true);
        }
        catch (IOException) { }
    }
}
