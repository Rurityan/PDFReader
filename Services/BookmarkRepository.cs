using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PDFReader.Models;

namespace PDFReader.Services;

public sealed class BookmarkRepository
{
    private readonly ReaderDbContextFactory _contextFactory = new();

    public async Task<IReadOnlyList<Bookmark>> GetForDocumentAsync(
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        await using var database = _contextFactory.Create();
        return await database.Bookmarks
            .AsNoTracking()
            .Where(bookmark => bookmark.PdfDocumentId == documentId)
            .OrderBy(bookmark => bookmark.ParentId)
            .ThenBy(bookmark => bookmark.SortOrder)
            .ThenBy(bookmark => bookmark.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task SaveAsync(
        Bookmark bookmark,
        CancellationToken cancellationToken = default)
    {
        await using var database = _contextFactory.Create();
        var existing = await database.Bookmarks
            .SingleOrDefaultAsync(item => item.Id == bookmark.Id, cancellationToken);

        if (existing is null)
        {
            database.Bookmarks.Add(new Bookmark
            {
                Id = bookmark.Id,
                PdfDocumentId = bookmark.PdfDocumentId,
                ParentId = bookmark.ParentId,
                PageNumber = bookmark.PageNumber,
                Title = bookmark.Title,
                SortOrder = bookmark.SortOrder,
                CreatedAtUtc = bookmark.CreatedAtUtc,
                UpdatedAtUtc = bookmark.UpdatedAtUtc,
            });
        }
        else
        {
            existing.PdfDocumentId = bookmark.PdfDocumentId;
            existing.ParentId = bookmark.ParentId;
            existing.PageNumber = bookmark.PageNumber;
            existing.Title = bookmark.Title;
            existing.SortOrder = bookmark.SortOrder;
            existing.UpdatedAtUtc = bookmark.UpdatedAtUtc;
        }

        await database.SaveChangesAsync(cancellationToken);
        bookmark.IsPersisted = true;
    }

    public async Task DeleteSubtreeAsync(
        Guid bookmarkId,
        CancellationToken cancellationToken = default)
    {
        await using var database = _contextFactory.Create();
        var root = await database.Bookmarks
            .SingleOrDefaultAsync(bookmark => bookmark.Id == bookmarkId, cancellationToken);
        if (root is null)
        {
            return;
        }

        var allBookmarks = await database.Bookmarks
            .Where(bookmark => bookmark.PdfDocumentId == root.PdfDocumentId)
            .ToListAsync(cancellationToken);
        var subtreeIds = new HashSet<Guid> { bookmarkId };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var bookmark in allBookmarks)
            {
                if (bookmark.ParentId is Guid parentId
                    && subtreeIds.Contains(parentId)
                    && subtreeIds.Add(bookmark.Id))
                {
                    changed = true;
                }
            }
        }

        var ocrRecords = await database.OcrRecords
            .Where(record => record.BookmarkId != null && subtreeIds.Contains(record.BookmarkId.Value))
            .ToListAsync(cancellationToken);
        foreach (var record in ocrRecords)
        {
            record.BookmarkId = null;
        }

        database.Bookmarks.RemoveRange(
            allBookmarks.Where(bookmark => subtreeIds.Contains(bookmark.Id)));
        await database.SaveChangesAsync(cancellationToken);
    }
}
