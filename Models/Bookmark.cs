using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations.Schema;

namespace PDFReader.Models;

public sealed class Bookmark
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PdfDocumentId { get; set; }
    public Guid? ParentId { get; set; }
    public int PageNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public PdfDocument? PdfDocument { get; set; }
    public Bookmark? Parent { get; set; }
    public ObservableCollection<Bookmark> Children { get; } = new();
    public ICollection<OcrRecord> OcrRecords { get; } = new Collection<OcrRecord>();

    [NotMapped]
    public bool IsPersisted { get; set; }
}
