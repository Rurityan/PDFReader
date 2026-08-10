using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PDFReader.Data;
using PDFReader.Models;

namespace PDFReader.Services;

public sealed class OcrResultRepository
{
    private readonly DbContextOptions<ReaderDbContext> _options;

    public OcrResultRepository()
    {
        var databasePath = Path.GetFullPath(ReaderSettings.GetDefaultDatabasePath());
        var appDirectory = Path.GetDirectoryName(databasePath)!;
        Directory.CreateDirectory(appDirectory);

        _options = new DbContextOptionsBuilder<ReaderDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;

        using var database = new ReaderDbContext(_options);
        database.Database.EnsureCreated();
    }

    public async Task AddAsync(OcrRecord record, CancellationToken cancellationToken = default)
    {
        await using var database = new ReaderDbContext(_options);
        database.OcrRecords.Add(record);
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OcrRecord>> GetForDocumentAsync(
        string documentPath,
        CancellationToken cancellationToken = default)
    {
        await using var database = new ReaderDbContext(_options);
        return await database.OcrRecords
            .AsNoTracking()
            .Where(record => record.DocumentPath == documentPath)
            .OrderByDescending(record => record.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }
}
