using System;

namespace PDFReader.Models;

public sealed class Bookmark
{
    public long Id { get; set; }
    public string DocumentPath { get; set; } = string.Empty;
    public int PageNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}
