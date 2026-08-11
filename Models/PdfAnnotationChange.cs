namespace PDFReader.Models;

public enum PdfAnnotationChangeKind
{
    Add,
    Delete,
    Update,
}

public sealed record PdfAnnotationChange(
    PdfAnnotationChangeKind Kind,
    PdfAnnotationInfo Annotation);
