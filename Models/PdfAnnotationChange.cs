namespace PDFReader.Models;

public enum PdfAnnotationChangeKind
{
    Add,
    Delete,
}

public sealed record PdfAnnotationChange(
    PdfAnnotationChangeKind Kind,
    PdfAnnotationInfo Annotation);
