using System;

namespace PDFReader.Models;

public sealed class OcrRecord
{
    public long Id { get; set; }
    public string DocumentPath { get; set; } = string.Empty;
    public int PageNumber { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}
