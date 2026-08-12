using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using PDFReader.Models;

namespace PDFReader.Controls;

public sealed class AnnotationOverlayControl : Canvas
{
    public static readonly StyledProperty<IEnumerable<PdfAnnotationInfo>?> AnnotationsProperty =
        AvaloniaProperty.Register<AnnotationOverlayControl, IEnumerable<PdfAnnotationInfo>?>(nameof(Annotations));

    private INotifyCollectionChanged? _notifier;

    public IEnumerable<PdfAnnotationInfo>? Annotations
    {
        get => GetValue(AnnotationsProperty);
        set => SetValue(AnnotationsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AnnotationsProperty)
        {
            if (_notifier is not null) _notifier.CollectionChanged -= CollectionChanged;
            _notifier = change.GetNewValue<IEnumerable<PdfAnnotationInfo>?>() as INotifyCollectionChanged;
            if (_notifier is not null) _notifier.CollectionChanged += CollectionChanged;
            Rebuild();
        }
    }

    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        Children.Clear();
        if (Annotations is null) return;
        foreach (var annotation in Annotations)
        {
            var brush = new SolidColorBrush(Color.Parse(annotation.StrokeColor));
            if (annotation.Type is PdfAnnotationType.Line or PdfAnnotationType.Freehand)
            {
                var points = annotation.Type == PdfAnnotationType.Freehand
                    ? annotation.Points
                    : new[] { new PdfAnnotationPoint(annotation.StartX, annotation.StartY), new PdfAnnotationPoint(annotation.EndX, annotation.EndY) };
                for (var index = 1; index < points.Count; index++)
                {
                    Children.Add(new Line
                    {
                        StartPoint = new Point(points[index - 1].X, points[index - 1].Y),
                        EndPoint = new Point(points[index].X, points[index].Y),
                        Stroke = brush,
                        StrokeThickness = Math.Max(1, annotation.StrokeWidth),
                        IsHitTestVisible = false,
                    });
                }
                continue;
            }

            Control control = annotation.Type switch
            {
                PdfAnnotationType.Rectangle => new Border
                {
                    Width = annotation.Width, Height = annotation.Height, BorderBrush = brush,
                    BorderThickness = new Thickness(Math.Max(1, annotation.StrokeWidth)),
                },
                PdfAnnotationType.Highlight => new Border
                {
                    Width = annotation.Width, Height = annotation.Height,
                    Background = new SolidColorBrush(Color.Parse("#66F2D34E")),
                },
                PdfAnnotationType.Text => new Border
                {
                    Width = annotation.Width, Height = annotation.Height, Padding = new Thickness(3),
                    BorderBrush = brush, BorderThickness = new Thickness(1),
                    Child = new TextBlock { Text = annotation.Contents ?? string.Empty, TextWrapping = TextWrapping.Wrap, Foreground = brush, FontSize = annotation.FontSize },
                },
                _ => new Border
                {
                    Width = annotation.Width, Height = annotation.Height, BorderBrush = brush, BorderThickness = new Thickness(1),
                },
            };
            control.IsHitTestVisible = false;
            SetLeft(control, annotation.X);
            SetTop(control, annotation.Y);
            Children.Add(control);
        }
    }
}
