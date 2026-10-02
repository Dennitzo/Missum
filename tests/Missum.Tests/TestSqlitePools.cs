using Microsoft.Data.Sqlite;

namespace Missum.Tests;

internal static class TestSqlitePools
{
    internal static void ClearDatabasePool(string databasePath)
    {
        // Match SqliteDatabase and SqliteProjectMemoryStore exactly. Other test
        // classes are using independent databases concurrently.
        using var pool = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString());
        SqliteConnection.ClearPool(pool);
    }

    internal static void ClearBackupPool(string backupPath)
    {
        // SqliteDatabase's migration backup connections use this separate
        // connection string. Only this fixture's explicit backup is affected.
        using var pool = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        SqliteConnection.ClearPool(pool);
    }

    internal static void ClearDatabaseBackups(string databasePath)
    {
        var backupDirectory = Path.Combine(Path.GetDirectoryName(databasePath)!, "DatabaseBackups");
        if (!Directory.Exists(backupDirectory)) return;
        foreach (var backup in Directory.EnumerateFiles(backupDirectory, Path.GetFileName(databasePath) + ".pre-v*.bak"))
            ClearBackupPool(backup);
    }
}
