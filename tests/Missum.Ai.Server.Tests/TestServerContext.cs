using Missum.Ai.Server.Core.Configuration;
using Missum.Ai.Server.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Missum.Ai.Server.Tests;

internal sealed class TestServerContext : IDisposable
{
    public TestServerContext()
    {
        Root = Path.Combine(Path.GetTempPath(), "Missum-AI-Server-Tests", Guid.NewGuid().ToString("N"));
        Options = new MissumAiServerOptions
        {
            DataDirectory = Root,
        };
        Database = new MissumAiDatabase(Microsoft.Extensions.Options.Options.Create(Options));
    }

    public string Root { get; }

    public MissumAiServerOptions Options { get; }

    public MissumAiDatabase Database { get; }

    public IOptions<MissumAiServerOptions> WrappedOptions => Microsoft.Extensions.Options.Options.Create(Options);

    public void Dispose()
    {
        Database.Dispose();
        // Other test classes use their own databases concurrently. Clearing all
        // pools races their connection checkout/native command lifetime. Release
        // only this fixture's exact pool before removing its private directory.
        using var pool = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString());
        SqliteConnection.ClearPool(pool);
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
