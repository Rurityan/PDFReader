using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PDFReader.Models;

namespace PDFReader.Services;

public sealed class PdfDocumentRepository
{
    private readonly ReaderDbContextFactory _contextFactory = new();

    public async Task<IReadOnlyList<PdfDocument>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await using var database = _contextFactory.Create();
        return await database.PdfDocuments
            .AsNoTracking()
            .OrderByDescending(document => document.LastOpenedAtUtc)
            .ThenBy(document => document.Title)
            .ToListAsync(cancellationToken);
    }

    public async Task<PdfDocument> GetOrCreateAsync(
        string filePath,
        string title,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        await using var database = _contextFactory.Create();
        var document = await database.PdfDocuments
            .SingleOrDefaultAsync(item => item.FilePath == normalizedPath, cancellationToken);
        var now = DateTime.UtcNow;

        if (document is null)
        {
            document = new PdfDocument
            {
                FilePath = normalizedPath,
                Title = title,
                CreatedAtUtc = now,
                LastOpenedAtUtc = now,
            };
            database.PdfDocuments.Add(document);
        }
        else
        {
            document.Title = title;
            document.LastOpenedAtUtc = now;
        }

        await database.SaveChangesAsync(cancellationToken);
        return document;
    }

    public async Task MarkOpenedAsync(
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        await using var database = _contextFactory.Create();
        var document = await database.PdfDocuments
            .SingleOrDefaultAsync(item => item.Id == documentId, cancellationToken);
        if (document is null)
        {
            return;
        }

        document.LastOpenedAtUtc = DateTime.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task SetArchivedAsync(Guid documentId, bool archived, CancellationToken cancellationToken = default)
    {
        await using var database = _contextFactory.Create();
        var document = await database.PdfDocuments.SingleOrDefaultAsync(item => item.Id == documentId, cancellationToken);
        if (document is null) return;
        document.IsArchived = archived;
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task RebindAsync(
        Guid documentId,
        string newFilePath,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = Path.GetFullPath(newFilePath);
        await using var database = _contextFactory.Create();
        var conflict = await database.PdfDocuments
            .AnyAsync(item => item.FilePath == normalizedPath && item.Id != documentId, cancellationToken);
        if (conflict)
        {
            throw new InvalidOperationException("该 PDF 文件已经绑定到其他文档对象。");
        }

        var document = await database.PdfDocuments
            .SingleOrDefaultAsync(item => item.Id == documentId, cancellationToken);
        if (document is null)
        {
            throw new InvalidOperationException("找不到要重新绑定的 PDF 对象。");
        }

        document.FilePath = normalizedPath;
        document.Title = Path.GetFileName(normalizedPath);
        document.LastOpenedAtUtc = DateTime.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> DeleteAsync(
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        await using var database = _contextFactory.Create();
        var document = await database.PdfDocuments
            .SingleOrDefaultAsync(item => item.Id == documentId, cancellationToken);
        if (document is null)
        {
            return Array.Empty<string>();
        }

        var ocrRecords = await database.OcrRecords
            .Include(record => record.TtsAudios)
            .Where(record => record.PdfDocumentId == documentId)
            .ToListAsync(cancellationToken);
        var bookmarks = await database.Bookmarks
            .Where(bookmark => bookmark.PdfDocumentId == documentId)
            .ToListAsync(cancellationToken);
        var resources = ocrRecords
            .SelectMany(record => new[] { record.CapturePath }
                .Concat(record.TtsAudios.Select(audio => audio.FilePath)))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        database.TtsAudioRecords.RemoveRange(ocrRecords.SelectMany(record => record.TtsAudios));
        database.OcrRecords.RemoveRange(ocrRecords);
        database.Bookmarks.RemoveRange(bookmarks);
        database.PdfDocuments.Remove(document);
        await database.SaveChangesAsync(cancellationToken);
        return resources;
    }
}
