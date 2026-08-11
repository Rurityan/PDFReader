using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Pdf.IO;
using PDFReader.Models;
using PdfSharpDocument = PdfSharp.Pdf.PdfDocument;

namespace PDFReader.Services;

public sealed class PdfEditingService
{
    public void AddTextAnnotation(
        string sourcePath,
        string outputPath,
        int pageIndex,
        double x,
        double y,
        double width,
        double height,
        double zoom,
        string title,
        string contents,
        string? annotationId = null)
    {
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(outputPath);
        var temporaryPath = string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)
            ? $"{source}.{Guid.NewGuid():N}.tmp.pdf"
            : destination;

        try
        {
            using (var document = PdfReader.Open(source, PdfDocumentOpenMode.Modify))
            {
                if (pageIndex < 0 || pageIndex >= document.Pages.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(pageIndex));
                }

                var page = document.Pages[pageIndex];
                var pageWidth = page.Width.Point;
                var pageHeight = page.Height.Point;
                var scale = zoom > 0 ? zoom : 1.0;
                var left = Math.Clamp(x / scale, 0, pageWidth);
                var top = Math.Clamp(y / scale, 0, pageHeight);
                var right = Math.Clamp((x + width) / scale, left, pageWidth);
                var bottom = Math.Clamp(pageHeight - ((y + height) / scale), 0, pageHeight);

                var annotation = new PdfTextAnnotation
                {
                    Rectangle = new PdfRectangle(
                        new XPoint(left, bottom),
                        new XPoint(right, pageHeight - top)),
                    Title = string.IsNullOrWhiteSpace(title) ? "PDF Reader" : title.Trim(),
                    Subject = "PDF Reader 标注",
                    Contents = contents.Trim(),
                    Icon = PdfTextAnnotationIcon.Note,
                    Open = false,
                    Color = XColor.FromArgb(255, 247, 196),
                };
                annotation.Elements.SetString("/NM", annotationId ?? Guid.NewGuid().ToString("N"));
                page.Annotations.Add(annotation);
                document.Save(temporaryPath);
            }

            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            {
                File.Move(temporaryPath, source, true);
            }
        }
        catch
        {
            if (File.Exists(temporaryPath)
                && !string.Equals(temporaryPath, destination, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }

    public void AddLineAnnotation(
        string sourcePath,
        string outputPath,
        int pageIndex,
        double startX,
        double startY,
        double endX,
        double endY,
        double zoom,
        string? annotationId = null,
        string colorHex = "#2B6CB0",
        double strokeWidth = 2)
    {
        var pageBounds = GetPageBounds(sourcePath, pageIndex);
        var scale = zoom > 0 ? zoom : 1.0;
        var x1 = Math.Clamp(startX / scale, 0, pageBounds.Width);
        var y1 = Math.Clamp(startY / scale, 0, pageBounds.Height);
        var x2 = Math.Clamp(endX / scale, 0, pageBounds.Width);
        var y2 = Math.Clamp(endY / scale, 0, pageBounds.Height);
        var left = Math.Min(x1, x2);
        var right = Math.Max(x1, x2);
        var top = Math.Min(y1, y2);
        var bottom = Math.Max(y1, y2);

        WriteGenericAnnotation(sourcePath, outputPath, pageIndex, "/Line",
            new PdfRectangle(new XPoint(left, pageBounds.Height - bottom), new XPoint(right, pageBounds.Height - top)),
            (annotation, document) =>
            {
                annotation.Elements.SetString("/NM", annotationId ?? Guid.NewGuid().ToString("N"));
                annotation.Elements.SetString("/Contents", "PDF Reader 线条");
                annotation.Elements.SetObject("/L", CreateArray(document,
                    new PdfReal(x1), new PdfReal(pageBounds.Height - y1),
                    new PdfReal(x2), new PdfReal(pageBounds.Height - y2)));
                annotation.Elements.SetObject("/C", CreateColorArray(document, colorHex));
                annotation.Elements.SetObject("/BS", CreateDictionary(document, ("/W", new PdfReal(Math.Max(0.1, strokeWidth)))));
            });
    }

    public void AddHighlightAnnotation(
        string sourcePath,
        string outputPath,
        int pageIndex,
        double x,
        double y,
        double width,
        double height,
        double zoom,
        string? annotationId = null)
    {
        var pageBounds = GetPageBounds(sourcePath, pageIndex);
        var scale = zoom > 0 ? zoom : 1.0;
        var left = Math.Clamp(x / scale, 0, pageBounds.Width);
        var top = Math.Clamp(y / scale, 0, pageBounds.Height);
        var right = Math.Clamp((x + width) / scale, left, pageBounds.Width);
        var bottom = Math.Clamp(pageBounds.Height - ((y + height) / scale), 0, pageBounds.Height);
        var topPdf = pageBounds.Height - top;

        WriteGenericAnnotation(sourcePath, outputPath, pageIndex, "/Highlight",
            new PdfRectangle(new XPoint(left, bottom), new XPoint(right, topPdf)),
            (annotation, document) =>
            {
                annotation.Elements.SetString("/NM", annotationId ?? Guid.NewGuid().ToString("N"));
                annotation.Elements.SetObject("/C", CreateArray(document,
                    new PdfReal(1), new PdfReal(0.84), new PdfReal(0.08)));
                annotation.Elements.SetReal("/CA", 0.35);
                annotation.Elements.SetObject("/QuadPoints", CreateArray(document,
                    new PdfReal(left), new PdfReal(topPdf),
                    new PdfReal(right), new PdfReal(topPdf),
                    new PdfReal(left), new PdfReal(bottom),
                    new PdfReal(right), new PdfReal(bottom)));
            });
    }

    public void AddRectangleAnnotation(
        string sourcePath,
        string outputPath,
        int pageIndex,
        double x,
        double y,
        double width,
        double height,
        double zoom,
        string? annotationId = null,
        string colorHex = "#2B6CB0",
        double strokeWidth = 2)
    {
        var pageBounds = GetPageBounds(sourcePath, pageIndex);
        var scale = zoom > 0 ? zoom : 1.0;
        var left = Math.Clamp(x / scale, 0, pageBounds.Width);
        var top = Math.Clamp(y / scale, 0, pageBounds.Height);
        var right = Math.Clamp((x + width) / scale, left, pageBounds.Width);
        var bottom = Math.Clamp(pageBounds.Height - ((y + height) / scale), 0, pageBounds.Height);
        var topPdf = pageBounds.Height - top;

        WriteGenericAnnotation(sourcePath, outputPath, pageIndex, "/Square",
            new PdfRectangle(new XPoint(left, bottom), new XPoint(right, topPdf)),
            (annotation, document) =>
            {
                annotation.Elements.SetString("/NM", annotationId ?? Guid.NewGuid().ToString("N"));
                annotation.Elements.SetObject("/C", CreateColorArray(document, colorHex));
                annotation.Elements.SetObject("/BS", CreateDictionary(document, ("/W", new PdfReal(Math.Max(0.1, strokeWidth)))));
            });
    }

    public void AddFreehandAnnotation(
        string sourcePath,
        string outputPath,
        int pageIndex,
        IReadOnlyList<PdfAnnotationPoint> points,
        double zoom,
        string? annotationId = null,
        string colorHex = "#2B6CB0",
        double strokeWidth = 2)
    {
        if (points.Count < 2)
        {
            throw new ArgumentException("自由绘制至少需要两个点", nameof(points));
        }

        var pageBounds = GetPageBounds(sourcePath, pageIndex);
        var scale = zoom > 0 ? zoom : 1.0;
        var pdfPoints = points
            .Select(point => new PdfAnnotationPoint(
                Math.Clamp(point.X / scale, 0, pageBounds.Width),
                Math.Clamp(point.Y / scale, 0, pageBounds.Height)))
            .ToList();
        var left = pdfPoints.Min(point => point.X);
        var right = pdfPoints.Max(point => point.X);
        var top = pdfPoints.Min(point => point.Y);
        var bottom = pdfPoints.Max(point => point.Y);

        WriteGenericAnnotation(sourcePath, outputPath, pageIndex, "/Ink",
            new PdfRectangle(new XPoint(left, pageBounds.Height - bottom),
                new XPoint(right, pageBounds.Height - top)),
            (annotation, document) =>
            {
                annotation.Elements.SetString("/NM", annotationId ?? Guid.NewGuid().ToString("N"));
                annotation.Elements.SetObject("/C", CreateColorArray(document, colorHex));
                annotation.Elements.SetObject("/BS", CreateDictionary(document, ("/W", new PdfReal(Math.Max(0.1, strokeWidth)))));
                var stroke = CreateArray(document, pdfPoints.SelectMany(point => new PdfItem[]
                {
                    new PdfReal(point.X),
                    new PdfReal(pageBounds.Height - point.Y),
                }).ToArray());
                annotation.Elements.SetObject("/InkList", CreateArray(document, stroke));
            });
    }

    public IReadOnlyList<PdfAnnotationInfo> GetAnnotations(string sourcePath, int pageIndex)
    {
        using var document = PdfReader.Open(Path.GetFullPath(sourcePath), PdfDocumentOpenMode.Import);
        if (pageIndex < 0 || pageIndex >= document.Pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        var page = document.Pages[pageIndex];
        var pageHeight = page.Height.Point;
        var result = new List<PdfAnnotationInfo>();
        for (var index = 0; index < page.Annotations.Count; index++)
        {
            var annotation = page.Annotations[index];
            var subtype = annotation.Elements.GetName("/Subtype");
            var type = subtype switch
            {
                "/Text" => PdfAnnotationType.Text,
                "/Line" => PdfAnnotationType.Line,
                "/Ink" => PdfAnnotationType.Freehand,
                "/Square" => PdfAnnotationType.Rectangle,
                "/Highlight" => PdfAnnotationType.Highlight,
                _ => PdfAnnotationType.Unknown,
            };
            var rectangle = annotation.Rectangle;
            var x = rectangle.X1;
            var right = rectangle.X2;
            var top = pageHeight - rectangle.Y2;
            var bottom = pageHeight - rectangle.Y1;
            var id = annotation.Elements.GetString("/NM");
            if (string.IsNullOrWhiteSpace(id))
            {
                id = $"object:{index}";
            }

            var startX = x;
            var startY = top;
            var endX = right;
            var endY = bottom;
            var line = annotation.Elements.GetArray("/L");
            if (line is not null && line.Elements.Count >= 4)
            {
                startX = line.Elements.GetReal(0);
                startY = pageHeight - line.Elements.GetReal(1);
                endX = line.Elements.GetReal(2);
                endY = pageHeight - line.Elements.GetReal(3);
            }

            var points = Array.Empty<PdfAnnotationPoint>();
            var inkList = annotation.Elements.GetArray("/InkList");
            var stroke = inkList?.Elements.GetArray(0);
            if (stroke is not null)
            {
                var pointList = new List<PdfAnnotationPoint>();
                for (var pointIndex = 0; pointIndex + 1 < stroke.Elements.Count; pointIndex += 2)
                {
                    pointList.Add(new PdfAnnotationPoint(
                        stroke.Elements.GetReal(pointIndex),
                        pageHeight - stroke.Elements.GetReal(pointIndex + 1)));
                }

                points = pointList.ToArray();
                if (points.Length > 0)
                {
                    startX = points[0].X;
                    startY = points[0].Y;
                    endX = points[^1].X;
                    endY = points[^1].Y;
                }
            }

            var strokeColor = ReadColorHex(annotation.Elements.GetArray("/C"));
            var borderStyle = annotation.Elements.GetDictionary("/BS");
            var strokeWidth = borderStyle?.Elements.GetReal("/W") ?? 2;

            result.Add(new PdfAnnotationInfo
            {
                Id = id,
                PageNumber = pageIndex + 1,
                Type = type,
                Title = annotation.Title,
                Contents = annotation.Contents,
                X = x,
                Y = top,
                Width = Math.Max(1, right - x),
                Height = Math.Max(1, bottom - top),
                StartX = startX,
                StartY = startY,
                EndX = endX,
                EndY = endY,
                Points = points,
                StrokeColor = strokeColor,
                StrokeWidth = strokeWidth > 0 ? strokeWidth : 2,
            });
        }

        return result;
    }

    public void DeleteAnnotation(string sourcePath, string outputPath, int pageIndex, string annotationId)
    {
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(outputPath);
        var temporaryPath = string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)
            ? $"{source}.{Guid.NewGuid():N}.tmp.pdf"
            : destination;
        try
        {
            using (var document = PdfReader.Open(source, PdfDocumentOpenMode.Modify))
            {
                if (pageIndex < 0 || pageIndex >= document.Pages.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(pageIndex));
                }

                var page = document.Pages[pageIndex];
                PdfAnnotation? annotation = null;
                for (var index = 0; index < page.Annotations.Count; index++)
                {
                    var candidate = page.Annotations[index];
                    if (GetAnnotationId(candidate, index) == annotationId)
                    {
                        annotation = candidate;
                        break;
                    }
                }
                if (annotation is null)
                {
                    throw new InvalidOperationException("找不到要删除的 PDF 标注");
                }

                page.Annotations.Remove(annotation);
                document.Save(temporaryPath);
            }

            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            {
                File.Move(temporaryPath, source, true);
            }
        }
        catch
        {
            if (File.Exists(temporaryPath) && !string.Equals(temporaryPath, destination, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }

    private static PdfRectangle GetPageBounds(string sourcePath, int pageIndex)
    {
        using var document = PdfReader.Open(Path.GetFullPath(sourcePath), PdfDocumentOpenMode.Import);
        if (pageIndex < 0 || pageIndex >= document.Pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        var page = document.Pages[pageIndex];
        return new PdfRectangle(
            new XPoint(0, 0),
            new XPoint(page.Width.Point, page.Height.Point));
    }

    private static void WriteGenericAnnotation(
        string sourcePath,
        string outputPath,
        int pageIndex,
        string subtype,
        PdfRectangle rectangle,
        Action<PdfAnnotation, PdfSharpDocument> configure)
    {
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(outputPath);
        var temporaryPath = string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)
            ? $"{source}.{Guid.NewGuid():N}.tmp.pdf"
            : destination;
        try
        {
            using (var document = PdfReader.Open(source, PdfDocumentOpenMode.Modify))
            {
                if (pageIndex < 0 || pageIndex >= document.Pages.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(pageIndex));
                }

                var annotation = new GenericPdfAnnotation(document)
                {
                    Rectangle = rectangle,
                };
                annotation.Elements.SetName("/Type", "/Annot");
                annotation.Elements.SetName("/Subtype", subtype);
                configure(annotation, document);
                document.Pages[pageIndex].Annotations.Add(annotation);
                document.Save(temporaryPath);
            }

            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            {
                File.Move(temporaryPath, source, true);
            }
        }
        catch
        {
            if (File.Exists(temporaryPath) && !string.Equals(temporaryPath, destination, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }

    private static PdfArray CreateArray(PdfSharpDocument document, params PdfItem[] items) => new(document, items);

    private static PdfArray CreateColorArray(PdfSharpDocument document, string colorHex)
    {
        var (red, green, blue) = ParseColor(colorHex);
        return CreateArray(document, new PdfReal(red), new PdfReal(green), new PdfReal(blue));
    }

    private static string ReadColorHex(PdfArray? color)
    {
        if (color is null || color.Elements.Count < 3)
        {
            return "#2B6CB0";
        }

        var red = (byte)Math.Clamp(Math.Round(color.Elements.GetReal(0) * 255), 0, 255);
        var green = (byte)Math.Clamp(Math.Round(color.Elements.GetReal(1) * 255), 0, 255);
        var blue = (byte)Math.Clamp(Math.Round(color.Elements.GetReal(2) * 255), 0, 255);
        return $"#{red:X2}{green:X2}{blue:X2}";
    }

    private static (double Red, double Green, double Blue) ParseColor(string colorHex)
    {
        var value = colorHex.Trim().TrimStart('#');
        if (value.Length == 3)
        {
            value = string.Concat(value.Select(character => new string(character, 2)));
        }

        if (value.Length != 6 || !uint.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
        {
            rgb = 0x2B6CB0;
        }

        return ((rgb >> 16 & 0xFF) / 255.0, (rgb >> 8 & 0xFF) / 255.0, (rgb & 0xFF) / 255.0);
    }

    private static PdfDictionary CreateDictionary(PdfSharpDocument document, params (string Key, PdfItem Value)[] values)
    {
        var dictionary = new PdfDictionary(document);
        foreach (var (key, value) in values)
        {
            dictionary.Elements.SetValue(key, value);
        }

        return dictionary;
    }

    private static string GetAnnotationId(PdfAnnotation annotation, int index)
    {
        var id = annotation.Elements.GetString("/NM");
        return string.IsNullOrWhiteSpace(id) ? $"object:{index}" : id;
    }

    private sealed class GenericPdfAnnotation : PdfAnnotation
    {
        public GenericPdfAnnotation(PdfSharpDocument document)
            : base(document)
        {
        }
    }

    public void SaveCopy(string sourcePath, string destinationPath)
    {
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            var temporaryPath = $"{source}.{Guid.NewGuid():N}.tmp.pdf";
            try
            {
                using (var document = PdfReader.Open(source, PdfDocumentOpenMode.Modify))
                {
                    document.Save(temporaryPath);
                }

                File.Move(temporaryPath, source, true);
            }
            catch
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }

                throw;
            }

            return;
        }

        using var copy = PdfReader.Open(source, PdfDocumentOpenMode.Modify);
        copy.Save(destination);
    }
}
