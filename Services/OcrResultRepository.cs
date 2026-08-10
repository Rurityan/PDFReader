using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PDFReader.Models;

namespace PDFReader.Services;

public sealed class OcrResultRepository
{
    private readonly ReaderDbContextFactory _contextFactory = new();

    public async Task AddAsync(OcrRecord record, CancellationToken cancellationToken = default)
    {
        await using var database = _contextFactory.Create();
        database.OcrRecords.Add(record);
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OcrRecord>> GetForDocumentAsync(
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        await using var database = _contextFactory.Create();
        return await database.OcrRecords
            .AsNoTracking()
            .Include(record => record.TtsAudios)
            .Where(record => record.PdfDocumentId == documentId)
            .OrderByDescending(record => record.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task AttachToBookmarkAsync(
        Guid ocrRecordId,
        Guid bookmarkId,
        CancellationToken cancellationToken = default)
    {
        await using var database = _contextFactory.Create();
        var record = await database.OcrRecords
            .SingleOrDefaultAsync(item => item.Id == ocrRecordId, cancellationToken);
        if (record is null)
        {
            throw new InvalidOperationException("找不到要挂载的 OCR 记录。");
        }

        record.BookmarkId = bookmarkId;
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task AddAudioAsync(
        TtsAudioRecord audio,
        CancellationToken cancellationToken = default)
    {
        await using var database = _contextFactory.Create();
        database.TtsAudioRecords.Add(audio);
        await database.SaveChangesAsync(cancellationToken);
    }

    public IReadOnlyList<string> RemoveUnattachedRecords()
    {
        using var database = _contextFactory.Create();
        var orphanedRecords = database.OcrRecords
            .Include(record => record.TtsAudios)
            .Where(record => record.BookmarkId == null)
            .ToList();
        if (orphanedRecords.Count == 0)
        {
            return Array.Empty<string>();
        }

        var resources = orphanedRecords
            .SelectMany(record => new[] { record.CapturePath }
                .Concat(record.TtsAudios.Select(audio => audio.FilePath)))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        database.OcrRecords.RemoveRange(orphanedRecords);
        database.SaveChanges();
        return resources;
    }
}
