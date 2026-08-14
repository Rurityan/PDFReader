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

    public async Task<OcrRecord?> FindDuplicateAsync(
        Guid documentId,
        int pageNumber,
        string text,
        double x,
        double y,
        double width,
        double height,
        CancellationToken cancellationToken = default)
    {
        await using var database = _contextFactory.Create();
        var candidates = await database.OcrRecords
            .Include(record => record.TtsAudios)
            .Where(record => record.PdfDocumentId == documentId
                && record.PageNumber == pageNumber
                && record.Text == text)
            .OrderBy(record => record.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        const double tolerance = 0.01;
        return candidates.FirstOrDefault(record =>
            Math.Abs(record.X - x) <= tolerance
            && Math.Abs(record.Y - y) <= tolerance
            && Math.Abs(record.Width - width) <= tolerance
            && Math.Abs(record.Height - height) <= tolerance);
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
        record.IsExternalImport = false;
        record.AllowStandalone = false;
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

    public async Task<IReadOnlyList<string>> DeleteAudiosAsync(
        Guid ocrRecordId,
        CancellationToken cancellationToken = default)
    {
        await using var database = _contextFactory.Create();
        var audios = await database.TtsAudioRecords
            .Where(audio => audio.OcrRecordId == ocrRecordId)
            .ToListAsync(cancellationToken);
        var paths = audios
            .Select(audio => audio.FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        database.TtsAudioRecords.RemoveRange(audios);
        await database.SaveChangesAsync(cancellationToken);
        return paths;
    }

    public async Task<IReadOnlyList<string>> DeleteAsync(
        Guid recordId,
        CancellationToken cancellationToken = default)
    {
        await using var database = _contextFactory.Create();
        var record = await database.OcrRecords
            .Include(item => item.TtsAudios)
            .SingleOrDefaultAsync(item => item.Id == recordId, cancellationToken);
        if (record is null)
        {
            return Array.Empty<string>();
        }

        var resources = new[] { record.CapturePath }
            .Concat(record.TtsAudios.Select(audio => audio.FilePath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        database.OcrRecords.Remove(record);
        await database.SaveChangesAsync(cancellationToken);
        return resources;
    }

    public IReadOnlyList<string> RemoveUnattachedRecords()
    {
        using var database = _contextFactory.Create();
        var orphanedRecords = database.OcrRecords
            .Include(record => record.TtsAudios)
            .Where(record => record.BookmarkId == null && !record.IsExternalImport && !record.AllowStandalone)
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

    public IReadOnlyList<string> CleanupOrphanedData()
    {
        using var database = _contextFactory.Create();
        var documents = database.PdfDocuments.Select(document => document.Id).ToHashSet();
        var bookmarks = database.Bookmarks.ToList();
        var bookmarkById = bookmarks.ToDictionary(bookmark => bookmark.Id);
        var records = database.OcrRecords.Include(record => record.TtsAudios).ToList();

        // A record is valid when attached to its document's bookmark, externally imported, or explicitly retained alone.
        var orphanedRecords = records.Where(record =>
                !documents.Contains(record.PdfDocumentId)
                || (record.BookmarkId is not null
                    && (!bookmarkById.TryGetValue(record.BookmarkId.Value, out var bookmark)
                        || bookmark.PdfDocumentId != record.PdfDocumentId))
                || (record.BookmarkId is null && !record.IsExternalImport && !record.AllowStandalone))
            .ToList();
        var orphanedRecordIds = orphanedRecords.Select(record => record.Id).ToHashSet();
        var orphanedAudios = database.TtsAudioRecords
            .Where(audio => !database.OcrRecords.Any(record => record.Id == audio.OcrRecordId))
            .ToList();
        var orphanedBookmarks = bookmarks
            .Where(bookmark => !documents.Contains(bookmark.PdfDocumentId))
            .ToList();

        // Keep a child bookmark when only its parent reference is stale; it becomes a root bookmark.
        foreach (var bookmark in bookmarks.Where(bookmark =>
                     bookmark.ParentId is Guid parentId && !bookmarkById.ContainsKey(parentId)))
        {
            bookmark.ParentId = null;
        }

        var resources = orphanedRecords
            .SelectMany(record => new[] { record.CapturePath }
                .Concat(record.TtsAudios.Select(audio => audio.FilePath)))
            .Concat(orphanedAudios.Select(audio => audio.FilePath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        database.TtsAudioRecords.RemoveRange(orphanedAudios);
        database.TtsAudioRecords.RemoveRange(orphanedRecords.SelectMany(record => record.TtsAudios));
        database.OcrRecords.RemoveRange(orphanedRecords);
        database.Bookmarks.RemoveRange(orphanedBookmarks);
        if (orphanedRecords.Count > 0 || orphanedAudios.Count > 0 || orphanedBookmarks.Count > 0
            || database.ChangeTracker.HasChanges())
        {
            database.SaveChanges();
        }

        return resources;
    }
}
