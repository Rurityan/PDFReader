using System;
using System.Data;
using System.IO;
using Microsoft.EntityFrameworkCore;
using PDFReader.Data;
using PDFReader.Models;

namespace PDFReader.Services;

public sealed class ReaderDbContextFactory
{
    private static readonly object InitializationLock = new();
    private readonly DbContextOptions<ReaderDbContext> _options;

    public ReaderDbContextFactory()
    {
        var databasePath = Path.GetFullPath(ReaderSettings.GetDefaultDatabasePath());
        var appDirectory = Path.GetDirectoryName(databasePath)!;
        Directory.CreateDirectory(appDirectory);

        _options = new DbContextOptionsBuilder<ReaderDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;

        lock (InitializationLock)
        {
            using var database = new ReaderDbContext(_options);
            EnsureCurrentSchema(database);
        }
    }

    public ReaderDbContext Create() => new(_options);

    private static void EnsureCurrentSchema(ReaderDbContext database)
    {
        var connection = database.Database.GetDbConnection();
        var wasOpen = connection.State == ConnectionState.Open;
        if (!wasOpen)
        {
            connection.Open();
        }

        bool hasUserTables;
        bool hasCurrentSchema;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT
                    EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'),
                    EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'PdfDocuments')
                        AND EXISTS(SELECT 1 FROM pragma_table_info('OcrRecords') WHERE name = 'Title')
                        AND EXISTS(SELECT 1 FROM pragma_table_info('OcrRecords') WHERE name = 'CaptureZoom');
                """;
            using var reader = command.ExecuteReader();
            reader.Read();
            hasUserTables = reader.GetBoolean(0);
            hasCurrentSchema = reader.GetBoolean(1);
        }

        if (!wasOpen)
        {
            connection.Close();
        }

        if (hasUserTables && !hasCurrentSchema)
        {
            database.Database.EnsureDeleted();
        }

        database.Database.EnsureCreated();
        EnsureColumn(database, "PdfDocuments", "IsArchived", "INTEGER NOT NULL DEFAULT 0");
    }

    private static void EnsureColumn(ReaderDbContext database, string table, string column, string definition)
    {
        var connection = database.Database.GetDbConnection();
        var wasOpen = connection.State == ConnectionState.Open;
        if (!wasOpen) connection.Open();
        try
        {
            using var check = connection.CreateCommand();
            check.CommandText = $"SELECT EXISTS(SELECT 1 FROM pragma_table_info('{table}') WHERE name = '{column}');";
            if (Convert.ToInt64(check.ExecuteScalar()) == 0)
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
                alter.ExecuteNonQuery();
            }
        }
        finally
        {
            if (!wasOpen) connection.Close();
        }
    }
}
