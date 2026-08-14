using System;
using System.Collections.Generic;

namespace PDFReader.Models;

public sealed class PdfReaderMetadata
{
    public List<PdfReaderMetadataBookmark> Bookmarks { get; set; } = new();
    public List<PdfReaderMetadataOcrRecord> OcrRecords { get; set; } = new();
}

public sealed class PdfReaderMetadataBookmark
{
    public Guid Id { get; set; }
    public Guid? ParentId { get; set; }
    public int Page { get; set; }
    public int SortOrder { get; set; }
    public string Title { get; set; } = string.Empty;
}

public sealed class PdfReaderMetadataOcrRecord
{
    public Guid Id { get; set; }
    public Guid? BookmarkId { get; set; }
    public bool AllowStandalone { get; set; }
    public int PageNumber { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double CaptureZoom { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public List<PdfReaderMetadataAudio> AudioFiles { get; set; } = new();
}

public sealed class PdfReaderMetadataAudio
{
    public Guid Id { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}
