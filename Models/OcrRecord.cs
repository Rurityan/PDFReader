using System;
using System.Collections.Generic;

namespace PDFReader.Models;

public sealed class OcrRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PdfDocumentId { get; set; }
    public Guid? BookmarkId { get; set; }
    public int PageNumber { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string Text { get; set; } = string.Empty;
    public string? CapturePath { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public PdfDocument? PdfDocument { get; set; }
    public Bookmark? Bookmark { get; set; }
    public ICollection<TtsAudioRecord> TtsAudios { get; } = new List<TtsAudioRecord>();
}
