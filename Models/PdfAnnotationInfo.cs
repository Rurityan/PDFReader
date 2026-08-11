using System;
using System.Collections.Generic;

namespace PDFReader.Models;

public enum PdfAnnotationType
{
    Text,
    Line,
    Freehand,
    Rectangle,
    Highlight,
    Unknown,
}

public sealed class PdfAnnotationInfo
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string? Subtype { get; init; }
    public int PageNumber { get; init; }
    public PdfAnnotationType Type { get; init; }
    public string? Title { get; init; }
    public string? Contents { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public double StartX { get; init; }
    public double StartY { get; init; }
    public double EndX { get; init; }
    public double EndY { get; init; }
    public IReadOnlyList<PdfAnnotationPoint> Points { get; init; } = Array.Empty<PdfAnnotationPoint>();
    public string StrokeColor { get; init; } = "#2B6CB0";
    public double StrokeWidth { get; init; } = 2;
}

public sealed record PdfAnnotationPoint(double X, double Y);
