using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PDFReader.Data;
using PDFReader.Models;

namespace PDFReader.Services;

public sealed class BookmarkRepository
{
    private readonly DbContextOptions<ReaderDbContext> _options;

    public BookmarkRepository()
    {
        var appDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PDFReader");
        Directory.CreateDirectory(appDirectory);

        _options = new DbContextOptionsBuilder<ReaderDbContext>()
            .UseSqlite($"Data Source={Path.Combine(appDirectory, "reader.db")}")
            .Options;

        using var database = new ReaderDbContext(_options);
        database.Database.EnsureCreated();
        database.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS Bookmarks (
                Id INTEGER NOT NULL CONSTRAINT PK_Bookmarks PRIMARY KEY AUTOINCREMENT,
                DocumentPath TEXT NOT NULL,
                PageNumber INTEGER NOT NULL,
                Title TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL
            );
            """);
        database.Database.ExecuteSqlRaw("""
            CREATE INDEX IF NOT EXISTS IX_Bookmarks_DocumentPath_PageNumber
            ON Bookmarks (DocumentPath, PageNumber);
            """);
    }

    public async Task AddAsync(Bookmark bookmark, CancellationToken cancellationToken = default)
    {
        await using var database = new ReaderDbContext(_options);
        database.Bookmarks.Add(bookmark);
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Bookmark>> GetForDocumentAsync(
        string documentPath,
        CancellationToken cancellationToken = default)
    {
        await using var database = new ReaderDbContext(_options);
        return await database.Bookmarks
            .AsNoTracking()
            .Where(bookmark => bookmark.DocumentPath == documentPath)
            .OrderBy(bookmark => bookmark.PageNumber)
            .ThenBy(bookmark => bookmark.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }
}
