namespace PDFReader.Models;

public sealed class PdfOutlineEntry
{
    public int Level { get; set; }
    public string Title { get; set; } = string.Empty;
    public int PageNumber { get; set; }
}
