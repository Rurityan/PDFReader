using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PDFReader.Models;

namespace PDFReader.Services;

public sealed class PdfDocumentRepository
{
    private readonly ReaderDbContextFactory _contextFactory = new();

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
}
