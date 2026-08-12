using System.Collections.Generic;

namespace PDFReader.Models;

public sealed class AutomationImportRequest
{
    public string PdfPath { get; set; } = string.Empty;
    public List<AutomationOcrRecord> Records { get; set; } = new();
}

public sealed class AutomationOcrRecord
{
    public int Page { get; set; }
    public AutomationRegion Region { get; set; } = new();
    public double CaptureZoom { get; set; } = 1.0;
    public string Title { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string? AudioFile { get; set; }
}

public sealed class AutomationRegion
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}
