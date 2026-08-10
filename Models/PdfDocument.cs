using System;
using System.Collections.Generic;

namespace PDFReader.Models;

public sealed class PdfDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FilePath { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime LastOpenedAtUtc { get; set; }

    public ICollection<Bookmark> Bookmarks { get; } = new List<Bookmark>();
    public ICollection<OcrRecord> OcrRecords { get; } = new List<OcrRecord>();
}
