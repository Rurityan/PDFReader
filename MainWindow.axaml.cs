using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PDFReader.Models;
using PDFReader.Controls;
using PDFReader.ViewModels;

namespace PDFReader;

public partial class MainWindow : Window
{
    private Point _selectionStart;
    private bool _isSelecting;
    private bool _isOpeningDocument;
    private readonly List<Point> _annotationStrokePoints = new();
    private Bookmark? _bookmarkDragCandidate;
    private OcrRecord? _ocrDragCandidate;
    private Point _bookmarkDragStart;
    private bool _isDraggingBookmark;
    private Border? _bookmarkDragGhost;
    private Border? _bookmarkDropZone;
    private Border? _bookmarkDropIndicator;
    private Control? _bookmarkDropTargetVisual;
    private bool _sidebarCollapsed;
    private double _sidebarWidth = 230;
    private bool _closingAfterAnnotationDecision;
    private TextResizeHandle? _activeTextResizeHandle;
    private Canvas? _activeTextResizeLayer;
    private Point _textResizeStart;
    private CancellationTokenSource? _continuousReadingRenderDelay;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
        ViewModel.CurrentPageOcrRecords.CollectionChanged += CurrentPageOcrRecordsChanged;
        ViewModel.CurrentPageAnnotations.CollectionChanged += CurrentPageAnnotationsChanged;
        ViewModel.Bookmarks.CollectionChanged += BookmarkTreeDataChanged;
        ViewModel.ContinuousReadingPageRequested += ScrollContinuousReadingToPage;
        ContinuousReadingList.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            ContinuousReadingListScrollChanged,
            Avalonia.Interactivity.RoutingStrategies.Bubble,
            true);
        ContinuousReadingList.AddHandler(
            OcrOverlayControl.AudioRequestedEvent,
            ContinuousReadingOcrAudioRequested,
            Avalonia.Interactivity.RoutingStrategies.Bubble,
            true);
        BookmarkTree.AddHandler(
            InputElement.PointerPressedEvent,
            BookmarkTreePointerPressed,
            Avalonia.Interactivity.RoutingStrategies.Tunnel,
            true);
        BookmarkTree.AddHandler(
            InputElement.PointerMovedEvent,
            BookmarkTreePointerMoved,
            Avalonia.Interactivity.RoutingStrategies.Tunnel,
            true);
        BookmarkTree.AddHandler(
            InputElement.PointerReleasedEvent,
            BookmarkTreePointerReleased,
            Avalonia.Interactivity.RoutingStrategies.Tunnel,
            true);
        BookmarkTree.AddHandler(
            InputElement.PointerReleasedEvent,
            BookmarkTreeExpansionCachePointerReleased,
            Avalonia.Interactivity.RoutingStrategies.Bubble,
            true);
        AddHandler(
            InputElement.PointerPressedEvent,
            WindowPointerPressed,
            Avalonia.Interactivity.RoutingStrategies.Tunnel,
            true);
        ViewModel.PropertyChanged += ViewModelPropertyChanged;
        _ = ViewModel.InitializeAsync();
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    private ColumnDefinition SidebarColumn => ContentLayout.ColumnDefinitions[1];
    private ColumnDefinition SidebarSplitterColumn => ContentLayout.ColumnDefinitions[2];

    private void ToggleSidebarClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_sidebarCollapsed)
        {
            var width = _sidebarWidth > 0 ? _sidebarWidth : 230;
            SidebarColumn.MinWidth = 180;
            SidebarColumn.Width = new GridLength(Math.Clamp(width, SidebarColumn.MinWidth, SidebarColumn.MaxWidth));
            SidebarSplitterColumn.Width = new GridLength(5);
            SidebarToggleButton.Content = "◀";
            ToolTip.SetTip(SidebarToggleButton, "折叠左侧栏");
            _sidebarCollapsed = false;
        }
        else
        {
            if (SidebarColumn.Width.IsAbsolute && SidebarColumn.Width.Value > 0)
            {
                _sidebarWidth = SidebarColumn.Width.Value;
            }

            SidebarColumn.Width = new GridLength(0);
            SidebarColumn.MinWidth = 0;
            SidebarSplitterColumn.Width = new GridLength(0);
            SidebarToggleButton.Content = "▶";
            ToolTip.SetTip(SidebarToggleButton, "展开左侧栏");
            _sidebarCollapsed = true;
        }

        e.Handled = true;
    }

    private void SidebarSplitterPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_sidebarCollapsed && SidebarColumn.Width.IsAbsolute && SidebarColumn.Width.Value > 0)
        {
            _sidebarWidth = SidebarColumn.Width.Value;
        }
    }

    private void WindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsWithin(e.Source as Visual, OcrHistoryList))
        {
            return;
        }

        ViewModel.ClearOcrHistorySelection();
        if (IsWithin(e.Source as Visual, PageSurface)
            && !IsWithin(e.Source as Visual, PdfAnnotationOverlay))
        {
            ViewModel.ClearPdfAnnotationSelection();
        }
    }

    private static bool IsWithin(Visual? source, Visual ancestor)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, ancestor))
            {
                return true;
            }

            source = source.GetVisualParent();
        }

        return false;
    }

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.HasPendingOcr) && !ViewModel.HasPendingOcr)
        {
            SelectionBox.IsVisible = false;
        }

        if (e.PropertyName == nameof(MainWindowViewModel.IsCurrentPageOcrVisible)
            || e.PropertyName == nameof(MainWindowViewModel.CurrentPageOcrButtonText))
        {
            RenderCurrentPageOcrOverlay();
        }

        if (e.PropertyName == nameof(MainWindowViewModel.CurrentZoom))
        {
            RenderPdfAnnotationOverlay();
        }

        if (e.PropertyName == nameof(MainWindowViewModel.SelectedPdfAnnotation))
        {
            RenderPdfAnnotationOverlay();
        }

        if (e.PropertyName == nameof(MainWindowViewModel.AnnotationTool)
            || e.PropertyName == nameof(MainWindowViewModel.IsAnnotationMode))
        {
            UpdateAnnotationToolVisuals();
            UpdateAnnotationCursor();
            if (!ViewModel.IsAnnotationMode)
            {
                ClearAnnotationStrokePreview();
            }
        }

        if (e.PropertyName == nameof(MainWindowViewModel.AnnotationColor)
            || e.PropertyName == nameof(MainWindowViewModel.AnnotationStrokeWidth))
        {
            RenderAnnotationStrokePreview();
        }

    }

    private void CurrentPageOcrRecordsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RenderCurrentPageOcrOverlay();
    }

    private void CurrentPageAnnotationsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RenderPdfAnnotationOverlay();
    }

    private void RenderPdfAnnotationOverlay()
    {
        PdfAnnotationOverlay.Children.Clear();
        if (!ViewModel.HasDocument)
        {
            return;
        }

        var scale = ViewModel.CurrentZoom;
        foreach (var annotation in ViewModel.CurrentPageAnnotations)
        {
            var annotationBrush = new SolidColorBrush(Color.Parse(annotation.StrokeColor));
            var left = annotation.X * scale;
            var top = annotation.Y * scale;
            var width = Math.Max(1, annotation.Width * scale);
            var height = Math.Max(1, annotation.Height * scale);
            var layer = new Canvas
            {
                Tag = annotation,
                Width = Math.Max(width, 26),
                Height = Math.Max(height, 26),
                IsHitTestVisible = true,
            };
            layer.PointerPressed += PdfAnnotationPointerPressed;
            layer.Children.Add(new Border
            {
                Width = layer.Width,
                Height = layer.Height,
                Background = new SolidColorBrush(Color.Parse("#01000000")),
            });

            if (annotation.Type is PdfAnnotationType.Line or PdfAnnotationType.Freehand)
            {
                var points = annotation.Type == PdfAnnotationType.Freehand
                    ? annotation.Points
                    : new[]
                    {
                        new PdfAnnotationPoint(annotation.StartX, annotation.StartY),
                        new PdfAnnotationPoint(annotation.EndX, annotation.EndY),
                    };
                if (points.Count < 2)
                {
                    continue;
                }

                left = points.Min(point => point.X) * scale;
                top = points.Min(point => point.Y) * scale;
                width = Math.Max(2, (points.Max(point => point.X) - points.Min(point => point.X)) * scale);
                height = Math.Max(2, (points.Max(point => point.Y) - points.Min(point => point.Y)) * scale);
                layer.Width = width + 20;
                layer.Height = height + 20;
                for (var pointIndex = 1; pointIndex < points.Count; pointIndex++)
                {
                    layer.Children.Add(new Line
                    {
                        StartPoint = new Point(points[pointIndex - 1].X * scale - left + 10, points[pointIndex - 1].Y * scale - top + 10),
                        EndPoint = new Point(points[pointIndex].X * scale - left + 10, points[pointIndex].Y * scale - top + 10),
                        Stroke = annotationBrush,
                        StrokeThickness = Math.Max(1, annotation.StrokeWidth * scale),
                    });
                }
            }
            else if (annotation.Type == PdfAnnotationType.Rectangle)
            {
                layer.Children.Add(new Border
                {
                    Width = width,
                    Height = height,
                    BorderBrush = annotationBrush,
                    BorderThickness = new Thickness(Math.Max(1, annotation.StrokeWidth * scale)),
                    Background = Brushes.Transparent,
                });
            }
            else if (annotation.Type == PdfAnnotationType.Highlight)
            {
                var highlight = new Border
                {
                    Width = width,
                    Height = height,
                    Background = new SolidColorBrush(Color.Parse("#66F2D34E")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#99D4A82E")),
                    BorderThickness = new Thickness(1),
                };
                layer.Children.Add(highlight);
            }
            else if (annotation.Type == PdfAnnotationType.Text)
            {
                layer.Children.Add(new Border
                {
                    Width = width,
                    Height = height,
                    Padding = new Thickness(3),
                    Background = new SolidColorBrush(Color.Parse("#14FFFFFF")),
                    BorderBrush = annotationBrush,
                    BorderThickness = new Thickness(1),
                    Child = new TextBlock
                    {
                        Text = annotation.Contents ?? string.Empty,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = annotationBrush,
                        FontSize = Math.Max(6, annotation.FontSize * scale),
                    },
                });
            }
            else
            {
                var generic = new Border
                {
                    Width = width,
                    Height = height,
                    Background = new SolidColorBrush(Color.Parse("#18D99726")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#B87800")),
                    BorderThickness = new Thickness(1),
                };
                ToolTip.SetTip(generic, string.IsNullOrWhiteSpace(annotation.Subtype)
                    ? "通用 PDF 标注"
                    : $"PDF 标注: {annotation.Subtype}");
                layer.Children.Add(generic);
            }

            if (ReferenceEquals(ViewModel.SelectedPdfAnnotation, annotation))
            {
                layer.Children.Add(new Border
                {
                    Width = layer.Width,
                    Height = layer.Height,
                    BorderBrush = new SolidColorBrush(Color.Parse("#D79A00")),
                    BorderThickness = new Thickness(2),
                    IsHitTestVisible = false,
                });
                if (annotation.Type is PdfAnnotationType.Text or PdfAnnotationType.Rectangle)
                {
                    AddTextResizeHandles(layer, annotation);
                }
                else if (annotation.Type == PdfAnnotationType.Line)
                {
                    AddLineResizeHandles(layer, annotation, left, top, scale);
                }
            }

            var deleteItem = new MenuItem
            {
                Header = "删除标注",
                Tag = annotation,
            };
            deleteItem.Click += DeletePdfAnnotationClick;
            var menuItems = new List<MenuItem>();
            if (annotation.Type == PdfAnnotationType.Text)
            {
                var editItem = new MenuItem { Header = "编辑文本", Tag = annotation };
                editItem.Click += EditPdfAnnotationClick;
                menuItems.Add(editItem);
            }
            menuItems.Add(deleteItem);
            layer.ContextMenu = new ContextMenu
            {
                ItemsSource = menuItems,
            };
            Canvas.SetLeft(layer, annotation.Type is PdfAnnotationType.Line or PdfAnnotationType.Freehand ? left - 10 : left);
            Canvas.SetTop(layer, annotation.Type is PdfAnnotationType.Line or PdfAnnotationType.Freehand ? top - 10 : top);
            PdfAnnotationOverlay.Children.Add(layer);
        }
    }

    private async void PdfAnnotationPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { Tag: PdfAnnotationInfo annotation })
        {
            ViewModel.SelectPdfAnnotation(annotation);
            if (e.ClickCount >= 2 && annotation.Type == PdfAnnotationType.Text)
            {
                await EditPdfAnnotationAsync(annotation);
            }
            e.Handled = true;
        }
    }

    private void AddTextResizeHandles(Canvas layer, PdfAnnotationInfo annotation)
    {
        foreach (var (name, x, y) in new[]
        {
            ("LT", 0d, 0d), ("T", layer.Width / 2, 0d), ("RT", layer.Width, 0d),
            ("R", layer.Width, layer.Height / 2), ("RB", layer.Width, layer.Height),
            ("B", layer.Width / 2, layer.Height), ("LB", 0d, layer.Height), ("L", 0d, layer.Height / 2),
        })
        {
            var handle = new Border
            {
                Width = 8, Height = 8, Tag = new TextResizeHandle(annotation, name),
                Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.Parse("#D79A00")),
                BorderThickness = new Thickness(1),
                Cursor = new Cursor(StandardCursorType.SizeAll),
            };
            handle.PointerPressed += TextResizeHandlePointerPressed;
            handle.PointerMoved += TextResizeHandlePointerMoved;
            handle.PointerReleased += TextResizeHandlePointerReleased;
            Canvas.SetLeft(handle, x - 4);
            Canvas.SetTop(handle, y - 4);
            layer.Children.Add(handle);
        }
    }

    private void AddLineResizeHandles(Canvas layer, PdfAnnotationInfo annotation, double left, double top, double scale)
    {
        foreach (var (handle, point) in new[] { ("P1", new PdfAnnotationPoint(annotation.StartX, annotation.StartY)), ("P2", new PdfAnnotationPoint(annotation.EndX, annotation.EndY)) })
        {
            var control = new Border { Width = 10, Height = 10, Tag = new TextResizeHandle(annotation, handle), Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.Parse("#D79A00")), BorderThickness = new Thickness(1), Cursor = new Cursor(StandardCursorType.SizeAll) };
            control.PointerPressed += TextResizeHandlePointerPressed;
            control.PointerMoved += TextResizeHandlePointerMoved;
            control.PointerReleased += TextResizeHandlePointerReleased;
            Canvas.SetLeft(control, point.X * scale - left + 5);
            Canvas.SetTop(control, point.Y * scale - top + 5);
            layer.Children.Add(control);
        }
    }

    private void TextResizeHandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control control && control.Tag is TextResizeHandle handle)
        {
            _activeTextResizeHandle = handle;
            _activeTextResizeLayer = control.Parent as Canvas;
            if (_activeTextResizeLayer is not null)
            {
                _activeTextResizeLayer.Opacity = 0.25;
            }
            _textResizeStart = e.GetPosition(PageSurface);
            e.Pointer.Capture(control);
            e.Handled = true;
        }
    }

    private void TextResizeHandlePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_activeTextResizeHandle is { } handle)
        {
            var end = e.GetPosition(PageSurface);
            var deltaX = (end.X - _textResizeStart.X) / ViewModel.CurrentZoom;
            var deltaY = (end.Y - _textResizeStart.Y) / ViewModel.CurrentZoom;
            if (handle.Handle is "P1" or "P2") ViewModel.ResizeLineAnnotation(handle.Annotation, handle.Handle, deltaX, deltaY);
            else ViewModel.ResizeTextAnnotation(handle.Annotation, handle.Handle, deltaX, deltaY);
            _activeTextResizeHandle = null;
            if (_activeTextResizeLayer is not null)
            {
                _activeTextResizeLayer.Opacity = 1;
                _activeTextResizeLayer = null;
            }
            ClearAnnotationStrokePreview();
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void TextResizeHandlePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_activeTextResizeHandle is not { } handle)
        {
            return;
        }

        var point = e.GetPosition(PageSurface);
        var deltaX = (point.X - _textResizeStart.X) / ViewModel.CurrentZoom;
        var deltaY = (point.Y - _textResizeStart.Y) / ViewModel.CurrentZoom;
        if (handle.Handle is "P1" or "P2")
        {
            var startX = handle.Annotation.StartX + (handle.Handle == "P1" ? deltaX : 0);
            var startY = handle.Annotation.StartY + (handle.Handle == "P1" ? deltaY : 0);
            var endX = handle.Annotation.EndX + (handle.Handle == "P2" ? deltaX : 0);
            var endY = handle.Annotation.EndY + (handle.Handle == "P2" ? deltaY : 0);
            RenderLineAnnotationPreview(
                new Point(startX * ViewModel.CurrentZoom, startY * ViewModel.CurrentZoom),
                new Point(endX * ViewModel.CurrentZoom, endY * ViewModel.CurrentZoom),
                handle.Annotation);
            return;
        }

        var left = handle.Annotation.X;
        var top = handle.Annotation.Y;
        var right = left + handle.Annotation.Width;
        var bottom = top + handle.Annotation.Height;
        if (handle.Handle.Contains('L')) left += deltaX;
        if (handle.Handle.Contains('R')) right += deltaX;
        if (handle.Handle.Contains('T')) top += deltaY;
        if (handle.Handle.Contains('B')) bottom += deltaY;
        if (right - left < 24) { if (handle.Handle.Contains('L')) left = right - 24; else right = left + 24; }
        if (bottom - top < 20) { if (handle.Handle.Contains('T')) top = bottom - 20; else bottom = top + 20; }
        AnnotationStrokePreview.Children.Clear();
        var annotationBrush = new SolidColorBrush(Color.Parse(handle.Annotation.StrokeColor));
        var preview = new Border
        {
            Width = (right - left) * ViewModel.CurrentZoom,
            Height = (bottom - top) * ViewModel.CurrentZoom,
            BorderBrush = annotationBrush,
            BorderThickness = new Thickness(Math.Max(1, handle.Annotation.StrokeWidth * ViewModel.CurrentZoom)),
            Background = new SolidColorBrush(Color.Parse("#16D79A00")),
        };
        if (handle.Annotation.Type == PdfAnnotationType.Text)
        {
            preview.Padding = new Thickness(3);
            preview.Child = new TextBlock
            {
                Text = handle.Annotation.Contents ?? string.Empty,
                TextWrapping = TextWrapping.Wrap,
                Foreground = annotationBrush,
                FontSize = Math.Max(6, handle.Annotation.FontSize * ViewModel.CurrentZoom),
            };
        }
        Canvas.SetLeft(preview, left * ViewModel.CurrentZoom);
        Canvas.SetTop(preview, top * ViewModel.CurrentZoom);
        AnnotationStrokePreview.Children.Add(preview);
    }

    private sealed record TextResizeHandle(PdfAnnotationInfo Annotation, string Handle);

    private async void EditPdfAnnotationClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is PdfAnnotationInfo annotation)
        {
            await EditPdfAnnotationAsync(annotation);
            e.Handled = true;
        }
    }

    private async Task EditPdfAnnotationAsync(PdfAnnotationInfo annotation)
    {
        var dialog = new AnnotationCreateWindow(annotation.Title, annotation.Contents);
        var request = await dialog.ShowDialog<AnnotationCreationRequest?>(this);
        if (request is not null)
        {
            await ViewModel.UpdateTextAnnotationAsync(annotation, request.Title, request.Contents);
        }
    }

    private async void DeletePdfAnnotationClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is PdfAnnotationInfo annotation)
        {
            await ViewModel.DeleteAnnotationAsync(annotation);
            e.Handled = true;
        }
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Delete && ViewModel.SelectedPdfAnnotation is not null)
        {
            await ViewModel.DeleteAnnotationAsync(ViewModel.SelectedPdfAnnotation);
            e.Handled = true;
            return;
        }

        if (!e.Handled
            && !ViewModel.IsAnnotationMode
            && !ViewModel.CanCapture
            && !IsTextInputFocused(e.Source as Visual))
        {
            switch (e.Key)
            {
                case Key.Left:
                case Key.Up:
                    await ViewModel.GoPreviousCommand.ExecuteAsync(null);
                    e.Handled = true;
                    break;
                case Key.Right:
                case Key.Down:
                    await ViewModel.GoNextCommand.ExecuteAsync(null);
                    e.Handled = true;
                    break;
            }
        }
    }

    private static bool IsTextInputFocused(Visual? source)
    {
        while (source is not null)
        {
            if (source is TextBox or NumericUpDown or ComboBox)
            {
                return true;
            }

            source = source.GetVisualParent();
        }

        return false;
    }

    private void DocumentScrollViewerScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.OffsetDelta.X != 0 || e.OffsetDelta.Y != 0)
        {
            ViewModel.PreloadAdjacentPages();
        }
    }

    private void DocumentScrollViewerPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!ViewModel.IsCurrentPageOcrVisible || ViewModel.IsAnnotationMode || ViewModel.CanCapture)
        {
            return;
        }

        const double threshold = 2;
        var atTop = DocumentScrollViewer.Offset.Y <= threshold;
        var atBottom = DocumentScrollViewer.Extent.Height - DocumentScrollViewer.Viewport.Height - DocumentScrollViewer.Offset.Y <= threshold;
        if (e.Delta.Y > 0 && atTop && ViewModel.CurrentPageNumber > 1)
        {
            ViewModel.ResumeContinuousReadingAtPage(ViewModel.CurrentPageNumber - 1);
            e.Handled = true;
        }
        else if (e.Delta.Y < 0 && atBottom && ViewModel.CurrentPageNumber < ViewModel.DocumentPageCount)
        {
            ViewModel.ResumeContinuousReadingAtPage(ViewModel.CurrentPageNumber + 1);
            e.Handled = true;
        }
    }

    private void ContinuousReadingListScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.OffsetDelta.Y == 0 || !ViewModel.IsContinuousReadingMode)
        {
            return;
        }

        var viewportCenter = ContinuousReadingList.Bounds.Height / 2;
        ReadingPage? currentPage = null;
        var nearestDistance = double.MaxValue;
        foreach (var container in ContinuousReadingList.GetVisualDescendants().OfType<ListBoxItem>())
        {
            if (container.DataContext is not ReadingPage page)
            {
                continue;
            }

            var position = container.TranslatePoint(new Point(), ContinuousReadingList);
            if (position is null)
            {
                continue;
            }

            var pageCenter = position.Value.Y + container.Bounds.Height / 2;
            var distance = Math.Abs(pageCenter - viewportCenter);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                currentPage = page;
            }
        }

        if (currentPage is not null)
        {
            ViewModel.SetCurrentReadingPage(currentPage.PageNumber);
        }

        ScheduleVisibleReadingPageRenders();
    }

    private async void ContinuousReadingOcrAudioRequested(object? sender, OcrAudioRequestedEventArgs e)
    {
        await ViewModel.PlayOrGenerateOcrAudioAsync(e.Record);
        e.Handled = true;
    }

    private void ScrollContinuousReadingToPage(int pageNumber)
    {
        var page = ViewModel.ReadingPages.FirstOrDefault(item => item.PageNumber == pageNumber);
        if (page is not null)
        {
            Dispatcher.UIThread.Post(
                () => ContinuousReadingList.ScrollIntoView(page),
                DispatcherPriority.Loaded);
        }
    }

    private void RenderCurrentPageOcrOverlay()
    {
        CurrentPageOcrOverlay.Children.Clear();
        if (!ViewModel.HasDocument || !ViewModel.IsCurrentPageOcrVisible)
        {
            return;
        }

        foreach (var record in ViewModel.CurrentPageOcrRecords)
        {
            var layer = new Canvas
            {
                Width = record.OverlayWidth,
                Height = Math.Max(record.DisplayHeight, 30),
            };
            Canvas.SetLeft(layer, record.DisplayX);
            Canvas.SetTop(layer, record.DisplayY);

            var box = new Border
            {
                Width = record.DisplayWidth,
                Height = record.DisplayHeight,
                Background = new SolidColorBrush(Color.Parse("#223B82C4")),
                BorderBrush = new SolidColorBrush(Color.Parse("#5570A9D8")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                IsHitTestVisible = false,
            };
            layer.Children.Add(box);

            var audioButton = new Button
            {
                Width = 30,
                Height = 28,
                Content = "🔊",
                Tag = record,
                Background = new SolidColorBrush(Color.Parse("#AA2B6CB0")),
                Foreground = Brushes.White,
                Opacity = 0.78,
                Padding = new Thickness(4, 2),
            };
            ToolTip.SetTip(audioButton, "生成或播放 OCR 音频");
            Canvas.SetLeft(audioButton, record.DisplayWidth);
            Canvas.SetTop(audioButton, 0);
            audioButton.PointerPressed += CurrentPageOcrAudioPointerPressed;
            audioButton.Click += CurrentPageOcrAudioClick;
            layer.Children.Add(audioButton);

            CurrentPageOcrOverlay.Children.Add(layer);
        }
    }

    private void PageSurfacePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(PageSurface).Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
        {
            ViewModel.CancelCaptureMode();
            ViewModel.CancelAnnotationMode();
            _isSelecting = false;
            _annotationStrokePoints.Clear();
            ClearAnnotationStrokePreview();
            SelectionBox.IsVisible = false;
            e.Handled = true;
            return;
        }

        if ((!ViewModel.CanCapture && !ViewModel.CanAnnotate)
            || e.GetCurrentPoint(PageSurface).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
        {
            return;
        }

        if (ViewModel.IsAnnotationMode && ViewModel.AnnotationTool == AnnotationTool.Select)
        {
            ViewModel.ClearPdfAnnotationSelection();
            e.Handled = true;
            return;
        }

        _selectionStart = e.GetPosition(PageSurface);
        _isSelecting = true;
        if (ViewModel.IsAnnotationMode
            && ViewModel.AnnotationTool is AnnotationTool.Freehand or AnnotationTool.Eraser)
        {
            _annotationStrokePoints.Clear();
            _annotationStrokePoints.Add(_selectionStart);
            SelectionBox.IsVisible = false;
            RenderAnnotationStrokePreview();
            e.Pointer.Capture(PageSurface);
            e.Handled = true;
            return;
        }

        SelectionBox.IsVisible = true;
        UpdateSelectionBox(_selectionStart);
        e.Pointer.Capture(PageSurface);
    }

    private void PageSurfacePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isSelecting
            && ViewModel.IsAnnotationMode
            && ViewModel.AnnotationTool is AnnotationTool.Freehand or AnnotationTool.Eraser)
        {
            var point = e.GetPosition(PageSurface);
            if (_annotationStrokePoints.Count == 0
                || Distance(_annotationStrokePoints[^1], point) >= 2)
            {
                _annotationStrokePoints.Add(point);
                RenderAnnotationStrokePreview();
            }
        }
        else if (_isSelecting && ViewModel.IsAnnotationMode && ViewModel.AnnotationTool == AnnotationTool.Line)
        {
            RenderLineAnnotationPreview(_selectionStart, e.GetPosition(PageSurface));
        }
        else if (_isSelecting)
        {
            UpdateSelectionBox(e.GetPosition(PageSurface));
        }
    }

    private async void PageSurfacePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isSelecting)
        {
            return;
        }

        var end = e.GetPosition(PageSurface);
        var selection = GetSelectionRect(end);
        _isSelecting = false;
        e.Pointer.Capture(null);
        ClearAnnotationStrokePreview();

        if (ViewModel.IsAnnotationMode
            && ViewModel.AnnotationTool is AnnotationTool.Freehand or AnnotationTool.Eraser)
        {
            var stroke = _annotationStrokePoints.ToArray();
            _annotationStrokePoints.Clear();
            ClearAnnotationStrokePreview();
            if (ViewModel.AnnotationTool == AnnotationTool.Freehand && stroke.Length >= 2)
            {
                await ViewModel.AddFreehandAnnotationAsync(
                    stroke.Select(point => new PdfAnnotationPoint(point.X, point.Y)).ToArray());
            }
            else if (ViewModel.AnnotationTool == AnnotationTool.Eraser && stroke.Length > 0)
            {
                await ViewModel.EraseAnnotationsAsync(
                    stroke.Select(point => new PdfAnnotationPoint(point.X, point.Y)).ToArray());
            }

            return;
        }

        var validSelection = ViewModel.AnnotationTool == AnnotationTool.Line
            ? selection.Width >= 4 || selection.Height >= 4
            : selection.Width >= 8 && selection.Height >= 8;
        if (validSelection)
        {
            if (ViewModel.CanAnnotate)
            {
                SelectionBox.IsVisible = false;
                switch (ViewModel.AnnotationTool)
                {
                    case AnnotationTool.Text:
                    {
                        var dialog = new AnnotationCreateWindow();
                        var request = await dialog.ShowDialog<AnnotationCreationRequest?>(this);
                        if (request is not null)
                        {
                            await ViewModel.AddAnnotationAsync(
                                selection.X,
                                selection.Y,
                                selection.Width,
                                selection.Height,
                                request.Title,
                                request.Contents);
                        }

                        break;
                    }
                    case AnnotationTool.Line:
                        await ViewModel.AddLineAnnotationAsync(
                            _selectionStart.X,
                            _selectionStart.Y,
                            end.X,
                            end.Y);
                        break;
                    case AnnotationTool.Highlight:
                        await ViewModel.AddHighlightAnnotationAsync(
                            selection.X,
                            selection.Y,
                            selection.Width,
                            selection.Height);
                        break;
                    case AnnotationTool.Rectangle:
                        await ViewModel.AddRectangleAnnotationAsync(
                            selection.X,
                            selection.Y,
                            selection.Width,
                            selection.Height);
                        break;
                }
            }
            else
            {
                await ViewModel.RunOcrSelectionAsync(selection.X, selection.Y, selection.Width, selection.Height);
            }
        }
        else if (ViewModel.IsAnnotationMode)
        {
            SelectionBox.IsVisible = false;
        }
    }

    private void UpdateSelectionBox(Point current)
    {
        var selection = GetSelectionRect(current);
        Canvas.SetLeft(SelectionBox, selection.X);
        Canvas.SetTop(SelectionBox, selection.Y);
        SelectionBox.Width = selection.Width;
        SelectionBox.Height = selection.Height;
    }

    private void CancelOcrClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _isSelecting = false;
        _annotationStrokePoints.Clear();
        ClearAnnotationStrokePreview();
        SelectionBox.IsVisible = false;
    }

    private void AnnotationToolClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string toolName
            && Enum.TryParse<AnnotationTool>(toolName, out var tool))
        {
            ViewModel.SelectAnnotationTool(tool);
            e.Handled = true;
        }
    }

    private void AnnotationColorClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string colorHex
            && Color.TryParse(colorHex, out var color))
        {
            ViewModel.SetAnnotationColor(color);
            e.Handled = true;
        }
    }

    private async void OpenAnnotationColorPickerClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var dialog = new AnnotationColorPickerWindow(ViewModel.AnnotationColor);
        var color = await dialog.ShowDialog<Color?>(this);
        if (color is Color selectedColor)
        {
            ViewModel.SetAnnotationColor(selectedColor);
        }

        e.Handled = true;
    }

    private void UpdateAnnotationToolVisuals()
    {
        var selectedBrush = new SolidColorBrush(Color.Parse("#E6A23C"));
        var normalBrush = new SolidColorBrush(Color.Parse("#F1F3F5"));
        var selectedForeground = new SolidColorBrush(Color.Parse("#20252B"));
        var normalForeground = new SolidColorBrush(Color.Parse("#3B454F"));
        var buttons = new (Button Button, AnnotationTool Tool)[]
        {
            (SelectAnnotationToolButton, AnnotationTool.Select),
            (TextAnnotationToolButton, AnnotationTool.Text),
            (LineAnnotationToolButton, AnnotationTool.Line),
            (FreehandAnnotationToolButton, AnnotationTool.Freehand),
            (RectangleAnnotationToolButton, AnnotationTool.Rectangle),
            (HighlightAnnotationToolButton, AnnotationTool.Highlight),
            (EraserAnnotationToolButton, AnnotationTool.Eraser),
        };
        foreach (var (button, tool) in buttons)
        {
            var selected = ViewModel.IsAnnotationMode && ViewModel.AnnotationTool == tool;
            button.Background = selected ? selectedBrush : normalBrush;
            button.Foreground = selected ? selectedForeground : normalForeground;
        }
    }

    private void UpdateAnnotationCursor()
    {
        if (!ViewModel.IsAnnotationMode)
        {
            PageSurface.Cursor = null;
            return;
        }

        PageSurface.Cursor = new Cursor(ViewModel.AnnotationTool switch
        {
            AnnotationTool.Select => StandardCursorType.Hand,
            AnnotationTool.Text => StandardCursorType.Ibeam,
            AnnotationTool.Eraser => StandardCursorType.Hand,
            _ => StandardCursorType.Cross,
        });
    }

    private void RenderAnnotationStrokePreview()
    {
        AnnotationStrokePreview.Children.Clear();
        if (_annotationStrokePoints.Count < 2)
        {
            return;
        }

        var strokeBrush = ViewModel.AnnotationTool == AnnotationTool.Eraser
            ? new SolidColorBrush(Color.Parse("#AA8B5A"))
            : ViewModel.AnnotationColorBrush;
        for (var index = 1; index < _annotationStrokePoints.Count; index++)
        {
            AnnotationStrokePreview.Children.Add(new Line
            {
                StartPoint = _annotationStrokePoints[index - 1],
                EndPoint = _annotationStrokePoints[index],
                Stroke = strokeBrush,
                StrokeThickness = ViewModel.AnnotationTool == AnnotationTool.Eraser
                    ? Math.Max(8, (double)ViewModel.AnnotationStrokeWidth * ViewModel.CurrentZoom * 2)
                    : Math.Max(1, (double)ViewModel.AnnotationStrokeWidth * ViewModel.CurrentZoom),
                Opacity = 0.7,
            });
        }
    }

    private void RenderLineAnnotationPreview(Point start, Point end, PdfAnnotationInfo? annotation = null)
    {
        AnnotationStrokePreview.Children.Clear();
        AnnotationStrokePreview.Children.Add(new Line
        {
            StartPoint = start,
            EndPoint = end,
            Stroke = annotation is null
                ? ViewModel.AnnotationColorBrush
                : new SolidColorBrush(Color.Parse(annotation.StrokeColor)),
            StrokeThickness = Math.Max(1, (annotation?.StrokeWidth ?? (double)ViewModel.AnnotationStrokeWidth) * ViewModel.CurrentZoom),
        });
    }

    private void ClearAnnotationStrokePreview()
    {
        AnnotationStrokePreview.Children.Clear();
    }

    private static double Distance(Point first, Point second)
    {
        return Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));
    }

    private Rect GetSelectionRect(Point current)
    {
        current = new Point(
            Math.Clamp(current.X, 0, PageSurface.Bounds.Width),
            Math.Clamp(current.Y, 0, PageSurface.Bounds.Height));
        return new Rect(
            Math.Min(_selectionStart.X, current.X),
            Math.Min(_selectionStart.Y, current.Y),
            Math.Abs(current.X - _selectionStart.X),
            Math.Abs(current.Y - _selectionStart.Y));
    }

    private async void OpenFileClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var picker = new PdfDocumentPickerWindow(
            ViewModel.AvailableDocuments,
            ViewModel.ArchivedDocuments,
            ViewModel.AddPdfFilesAsync,
            ViewModel.SetDocumentArchivedAsync,
            ViewModel.DeleteDocumentAsync);
        var selectedDocuments = await picker.ShowDialog<IReadOnlyList<PdfDocument>?>(this);
        if (selectedDocuments is not null && selectedDocuments.Count > 0)
        {
            await ViewModel.ImportDocumentsToWorkspaceAsync(selectedDocuments);
        }
    }

    private async void DocumentSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isOpeningDocument || sender is not ListBox listBox || listBox.SelectedItem is not PdfDocument document)
        {
            return;
        }

        _isOpeningDocument = true;
        try
        {
            document.RefreshPathStatus();
            if (!document.IsMissing)
            {
                await ViewModel.OpenStoredDocumentAsync(document);
                return;
            }

            var dialog = new MissingDocumentWindow(document.Title, document.FilePath);
            var action = await dialog.ShowDialog<MissingDocumentAction>(this);
            switch (action)
            {
                case MissingDocumentAction.Rebind:
                    await RebindDocumentAsync(document);
                    break;
                case MissingDocumentAction.Delete:
                    await ViewModel.DeleteDocumentAsync(document);
                    break;
                case MissingDocumentAction.Postpone:
                    ViewModel.SetStatus("已暂时搁置缺失的 PDF 文档");
                    break;
            }
        }
        finally
        {
            _isOpeningDocument = false;
        }
    }

    private async void PagePreviewSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox listBox && listBox.SelectedItem is PagePreview preview)
        {
            await ViewModel.GoToPageAsync(preview.PageNumber);
        }
    }

    private void PagePreviewImageAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Image image && image.DataContext is PagePreview preview)
        {
            ViewModel.LoadPagePreviewImage(preview);
        }
    }

    private void PagePreviewImageDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Image image && image.DataContext is PagePreview preview)
        {
            ViewModel.UnloadPagePreviewImage(preview);
        }
    }

    private void ReadingPageImageAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Image image && image.DataContext is ReadingPage page)
        {
            ViewModel.ActivateReadingPage(page);
            _ = ViewModel.LoadReadingPageAnnotationsAsync(page);
            ScheduleVisibleReadingPageRenders();
        }
    }

    private void ReadingPageImageDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Image image && image.DataContext is ReadingPage page)
        {
            ViewModel.UnloadReadingPageImage(page);
        }
    }

    private void ScheduleVisibleReadingPageRenders()
    {
        _continuousReadingRenderDelay?.Cancel();
        _continuousReadingRenderDelay?.Dispose();
        var cancellation = new CancellationTokenSource();
        _continuousReadingRenderDelay = cancellation;
        _ = QueueVisibleReadingPageRendersAsync(cancellation.Token);
    }

    private async Task QueueVisibleReadingPageRendersAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(220, cancellationToken);
            if (cancellationToken.IsCancellationRequested || !ViewModel.IsContinuousReadingMode)
            {
                return;
            }

            foreach (var page in ContinuousReadingList.GetVisualDescendants()
                         .OfType<Image>()
                         .Select(image => image.DataContext)
                         .OfType<ReadingPage>()
                         .Distinct())
            {
                ViewModel.QueueReadingPageRender(page);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RebindDocumentAsync(PdfDocument document)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "重新绑定 PDF 文件",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("PDF 文档") { Patterns = new[] { "*.pdf" } }
            }
        });

        var path = files.FirstOrDefault()?.Path.LocalPath;
        if (!string.IsNullOrWhiteSpace(path))
        {
            await ViewModel.RebindDocumentAsync(document, path);
        }
    }

    private async void OpenSettingsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var window = new SettingsWindow(ViewModel.GetSettings(), ViewModel.ApplySettingsAsync);
        await window.ShowDialog(this);
    }

    private void OpenFileMenuClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        FileMenuButton.ContextMenu?.Open(FileMenuButton);
        e.Handled = true;
    }

    private async void SavePdfClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await ViewModel.SavePdfAsync();
        e.Handled = true;
    }

    private async void SavePdfAsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "另存为 PDF",
            SuggestedFileName = System.IO.Path.GetFileName(ViewModel.DocumentPath),
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PDF 文档") { Patterns = new[] { "*.pdf" } },
            },
        });

        var path = file?.Path.LocalPath;
        if (!string.IsNullOrWhiteSpace(path))
        {
            await ViewModel.SavePdfAsAsync(path);
        }

        e.Handled = true;
    }

    private async void ExportPortablePdfClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "全量导出 PDF",
            SuggestedFileName = $"{System.IO.Path.GetFileNameWithoutExtension(ViewModel.DocumentPath)}-export.pdf",
            FileTypeChoices = new[] { new FilePickerFileType("PDF 文档") { Patterns = new[] { "*.pdf" } } },
        });
        var path = file?.Path.LocalPath;
        if (!string.IsNullOrWhiteSpace(path)) await ViewModel.ExportPortablePdfAsync(path);
        e.Handled = true;
    }

    private async void ExportAcrobatRichMediaPdfClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出 Adobe Acrobat 富媒体 PDF",
            SuggestedFileName = $"{System.IO.Path.GetFileNameWithoutExtension(ViewModel.DocumentPath)}-acrobat-rich-media.pdf",
            FileTypeChoices = new[] { new FilePickerFileType("PDF 文档") { Patterns = new[] { "*.pdf" } } },
        });
        var path = file?.Path.LocalPath;
        if (!string.IsNullOrWhiteSpace(path)) await ViewModel.ExportAcrobatRichMediaPdfAsync(path);
        e.Handled = true;
    }

    private async void CreateBookmarkClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!ViewModel.HasDocument)
        {
            ViewModel.SetStatus("请先打开一个 PDF 文档");
            return;
        }

        var dialog = new BookmarkCreateWindow(
            ViewModel.CurrentPageNumber,
            ViewModel.DocumentPageCount);
        var request = await dialog.ShowDialog<BookmarkCreationRequest?>(this);
        if (request is not null)
        {
            await ViewModel.CreateBookmarkAsync(request.Title, request.PageNumber);
        }
    }

    private void FindCurrentPageBookmarkClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel.FindCurrentPageBookmark();
        e.Handled = true;
    }

    private async void BookmarkDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is TreeView treeView && treeView.SelectedItem is Bookmark bookmark)
        {
            await ViewModel.GoToBookmarkAsync(bookmark);
        }
    }

    private async void BookmarkTitlePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.GetCurrentPoint(BookmarkTree).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonReleased
            || _isDraggingBookmark)
        {
            return;
        }

        var bookmark = FindBookmark(sender);
        if (bookmark is null)
        {
            return;
        }

        ViewModel.SelectedBookmark = bookmark;
        await ViewModel.GoToBookmarkAsync(bookmark);
        e.Handled = true;
    }

    private void BookmarkTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(BookmarkTree).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
        {
            return;
        }

        // Leave TreeView's built-in expander toggle untouched.
        if (IsTreeExpander(e.Source))
        {
            return;
        }

        var bookmark = FindBookmark(e.Source);
        var ocrRecord = FindOcrRecord(e.Source);
        if (bookmark is null && ocrRecord is null)
        {
            return;
        }

        _bookmarkDragCandidate = bookmark;
        _ocrDragCandidate = ocrRecord;
        _bookmarkDragStart = e.GetPosition(BookmarkTree);
        _isDraggingBookmark = false;
    }

    private void BookmarkTreeExpansionCachePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        Dispatcher.UIThread.Post(SaveBookmarkExpansionCache, DispatcherPriority.Background);
    }

    private void BookmarkContextMenuOpened(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Context-menu actions can rebuild the tree immediately; capture the visual state first.
        SaveBookmarkExpansionCache();
    }

    private void BookmarkTreeDataChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(RestoreBookmarkExpansionCache, DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(SaveBookmarkExpansionCache, DispatcherPriority.Background);
    }

    private void SaveBookmarkExpansionCache()
    {
        var expandedIds = BookmarkTree.GetVisualDescendants()
            .OfType<TreeViewItem>()
            .Where(item => item.IsExpanded && item.DataContext is Bookmark)
            .Select(item => ((Bookmark)item.DataContext!).Id);
        ViewModel.SaveBookmarkExpansionCache(expandedIds);
    }

    private void RestoreBookmarkExpansionCache()
    {
        foreach (var item in BookmarkTree.GetVisualDescendants().OfType<TreeViewItem>())
        {
            if (item.DataContext is Bookmark bookmark)
            {
                item.IsExpanded = ViewModel.IsBookmarkExpansionCached(bookmark.Id);
            }
        }
    }

    private static bool IsTreeExpander(object? source)
    {
        var visual = source as Visual;
        while (visual is not null)
        {
            if (visual is ToggleButton)
            {
                return true;
            }

            visual = visual.GetVisualParent();
        }

        return false;
    }

    private void BookmarkTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_bookmarkDragCandidate is null && _ocrDragCandidate is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(BookmarkTree);
        if (!point.Properties.IsLeftButtonPressed)
        {
            ResetBookmarkDrag(e.Pointer);
            return;
        }

        var current = e.GetPosition(BookmarkTree);
        if (!_isDraggingBookmark
            && (Math.Abs(current.X - _bookmarkDragStart.X) > 6
                || Math.Abs(current.Y - _bookmarkDragStart.Y) > 6))
        {
            _isDraggingBookmark = true;
            e.Pointer.Capture(BookmarkTree);
            BeginBookmarkDragVisual(_bookmarkDragCandidate ?? (object)_ocrDragCandidate!, current);
        }

        if (_isDraggingBookmark)
        {
            UpdateBookmarkDragVisual(current);
            e.Handled = true;
        }
    }

    private async void BookmarkTreePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_bookmarkDragCandidate is null && _ocrDragCandidate is null)
        {
            return;
        }

        var dragged = _bookmarkDragCandidate;
        var draggedOcr = _ocrDragCandidate;
        var wasDragging = _isDraggingBookmark;
        var point = e.GetPosition(BookmarkTree);
        ResetBookmarkDrag(e.Pointer);
        if (!wasDragging)
        {
            return;
        }

        var targetVisual = FindBookmarkVisual(BookmarkTree.InputHitTest(point));
        var target = targetVisual is null ? null : FindBookmark(targetVisual);
        if (targetVisual is null || target is null || ReferenceEquals(dragged, target))
        {
            return;
        }

        var targetOrigin = targetVisual.TranslatePoint(new Point(0, 0), BookmarkTree);
        if (targetOrigin is null)
        {
            return;
        }

        var targetY = point.Y - targetOrigin.Value.Y;
        var asChild = targetY >= targetVisual.Bounds.Height * 0.65;
        if (dragged is not null)
        {
            await ViewModel.MoveBookmarkAsync(dragged, target, asChild);
        }
        else if (draggedOcr is not null)
        {
            await ViewModel.MoveOcrToBookmarkAsync(draggedOcr, target);
        }
        e.Handled = true;
    }

    private void ResetBookmarkDrag(IPointer? pointer)
    {
        pointer?.Capture(null);
        ClearBookmarkDragVisual();
        _bookmarkDragCandidate = null;
        _ocrDragCandidate = null;
        _isDraggingBookmark = false;
    }

    private void BeginBookmarkDragVisual(object dragItem, Point point)
    {
        BookmarkDragOverlay.Children.Clear();

        _bookmarkDropIndicator = new Border
        {
            Height = 3,
            Background = new SolidColorBrush(Color.Parse("#2B6CB0")),
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        _bookmarkDropZone = new Border
        {
            Height = 10,
            Background = new SolidColorBrush(Color.Parse("#102B6CB0")),
            BorderBrush = new SolidColorBrush(Color.Parse("#332B6CB0")),
            BorderThickness = new Thickness(0, 1),
            CornerRadius = new CornerRadius(2),
            IsVisible = false,
            Child = _bookmarkDropIndicator,
        };
        _bookmarkDragGhost = new Border
        {
            Width = 220,
            Background = new SolidColorBrush(Color.Parse("#F8FAFC")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2B6CB0")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 5),
            Opacity = 0.72,
            Child = new TextBlock
            {
                Text = dragItem switch
                {
                    Bookmark bookmark => $"{bookmark.PageNumber}  {bookmark.Title}",
                    OcrRecord record => $"OCR  {record.Title}",
                    _ => string.Empty,
                },
                Foreground = new SolidColorBrush(Color.Parse("#263746")),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 42,
            },
        };

        BookmarkDragOverlay.Children.Add(_bookmarkDropZone);
        BookmarkDragOverlay.Children.Add(_bookmarkDragGhost);
        _bookmarkDropTargetVisual = null;
        UpdateBookmarkDragVisual(point);
    }

    private void UpdateBookmarkDragVisual(Point point)
    {
        if (_bookmarkDragGhost is null || _bookmarkDropZone is null || _bookmarkDropIndicator is null)
        {
            return;
        }

        var overlayWidth = BookmarkDragOverlay.Bounds.Width;
        var overlayHeight = BookmarkDragOverlay.Bounds.Height;
        var overlayPoint = BookmarkTree.TranslatePoint(point, BookmarkDragOverlay)
            ?? new Point(point.X, point.Y);
        var ghostX = overlayPoint.X + 14;
        var ghostY = overlayPoint.Y + 12;
        if (overlayWidth > 0)
        {
            ghostX = Math.Clamp(ghostX, 4, Math.Max(4, overlayWidth - _bookmarkDragGhost.Width - 4));
        }

        if (overlayHeight > 0 && _bookmarkDragGhost.Bounds.Height > 0)
        {
            ghostY = Math.Clamp(ghostY, 4, Math.Max(4, overlayHeight - _bookmarkDragGhost.Bounds.Height - 4));
        }

        Canvas.SetLeft(_bookmarkDragGhost, ghostX);
        Canvas.SetTop(_bookmarkDragGhost, ghostY);

        var hitVisual = FindBookmarkVisual(BookmarkTree.InputHitTest(point));
        if (hitVisual is not null)
        {
            var hitTarget = hitVisual.DataContext as Bookmark;
            _bookmarkDropTargetVisual = hitTarget is not null
                && !ReferenceEquals(hitTarget, _bookmarkDragCandidate)
                ? hitVisual
                : null;
        }
        else if (!new Rect(BookmarkTree.Bounds.Size).Contains(point))
        {
            _bookmarkDropTargetVisual = null;
        }

        var targetVisual = _bookmarkDropTargetVisual;
        var target = targetVisual?.DataContext as Bookmark;
        if (targetVisual is null || target is null)
        {
            _bookmarkDropZone.IsVisible = false;
            return;
        }

        var targetOriginInTree = targetVisual.TranslatePoint(new Point(0, 0), BookmarkTree);
        var targetOrigin = targetVisual.TranslatePoint(new Point(0, 0), BookmarkDragOverlay);
        if (targetOriginInTree is null || targetOrigin is null || targetVisual.Bounds.Height <= 0)
        {
            _bookmarkDropZone.IsVisible = false;
            return;
        }

        var targetY = point.Y - targetOriginInTree.Value.Y;
        var asChild = targetY >= targetVisual.Bounds.Height * 0.65;
        const double dropZoneHeight = 10;
        var lineY = targetOrigin.Value.Y + targetVisual.Bounds.Height - 2;
        var zoneWidth = overlayWidth > 20
            ? overlayWidth
            : Math.Max(100, targetVisual.Bounds.Width + targetOrigin.Value.X);
        _bookmarkDropZone.Width = zoneWidth;
        Canvas.SetLeft(_bookmarkDropZone, 0);
        Canvas.SetTop(_bookmarkDropZone, Math.Max(0, lineY - dropZoneHeight / 2));
        if (asChild)
        {
            const double childIndicatorInset = 12;
            var lineWidth = Math.Clamp(targetVisual.Bounds.Width * 0.35, 30, 72);
            _bookmarkDropIndicator.Width = lineWidth;
            _bookmarkDropIndicator.Margin = new Thickness(targetOrigin.Value.X + childIndicatorInset, 0, 0, 0);
        }
        else
        {
            var lineWidth = Math.Max(48, zoneWidth - 16);
            _bookmarkDropIndicator.Width = lineWidth;
            _bookmarkDropIndicator.Margin = new Thickness(8, 0, 0, 0);
        }

        _bookmarkDropZone.IsVisible = true;
    }

    private void ClearBookmarkDragVisual()
    {
        BookmarkDragOverlay.Children.Clear();
        _bookmarkDragGhost = null;
        _bookmarkDropZone = null;
        _bookmarkDropIndicator = null;
        _bookmarkDropTargetVisual = null;
    }

    private static Bookmark? FindBookmark(object? source)
    {
        return FindBookmarkVisual(source)?.DataContext as Bookmark;
    }

    private static OcrRecord? FindOcrRecord(object? source)
    {
        return FindOcrRecordVisual(source)?.DataContext as OcrRecord;
    }

    private static Control? FindBookmarkVisual(object? source)
    {
        var visual = source as Visual;
        while (visual is not null)
        {
            if (visual is Control control && control.Classes.Contains("bookmark-row"))
            {
                return control;
            }

            visual = visual.GetVisualParent();
        }

        return null;
    }

    private static Control? FindOcrRecordVisual(object? source)
    {
        var visual = source as Visual;
        while (visual is not null)
        {
            if (visual is Control control && control.Classes.Contains("ocr-row"))
            {
                return control;
            }

            visual = visual.GetVisualParent();
        }

        return null;
    }

    private static Bookmark? GetBookmarkFromMenu(object? sender)
    {
        if (sender is not MenuItem menuItem)
        {
            return null;
        }

        return menuItem.Tag as Bookmark
            ?? (menuItem.Parent as ContextMenu)?.DataContext as Bookmark;
    }

    private static OcrRecord? GetOcrFromMenu(object? sender)
    {
        if (sender is not Control control)
        {
            return null;
        }

        return control.Tag as OcrRecord
            ?? (control.Parent as ContextMenu)?.DataContext as OcrRecord;
    }

    private async void JumpToBookmarkClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var bookmark = GetBookmarkFromMenu(sender);
        if (bookmark is not null)
        {
            ViewModel.SelectedBookmark = bookmark;
            await ViewModel.GoToBookmarkAsync(bookmark);
        }
    }

    private async void RenameBookmarkClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var bookmark = GetBookmarkFromMenu(sender);
        if (bookmark is null)
        {
            return;
        }

        var dialog = new BookmarkNameWindow(bookmark.Title);
        var title = await dialog.ShowDialog<string?>(this);
        if (title is not null)
        {
            ViewModel.SelectedBookmark = bookmark;
            await ViewModel.RenameBookmarkAsync(bookmark, title);
        }
    }

    private async void ReattachBookmarkOcrClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var bookmark = GetBookmarkFromMenu(sender);
        if (bookmark is not null)
        {
            await ViewModel.ReattachCurrentPageOcrAsync(bookmark);
        }
    }

    private async void DetachBookmarkClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var bookmark = GetBookmarkFromMenu(sender);
        if (bookmark is not null)
        {
            ViewModel.SelectedBookmark = bookmark;
            await ViewModel.DetachBookmarkAsync(bookmark);
        }
    }

    private async void DeleteBookmarkClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var bookmark = GetBookmarkFromMenu(sender);
        if (bookmark is null)
        {
            return;
        }

        if (bookmark.Children.Count > 0)
        {
            var dialog = new BookmarkDeleteConfirmWindow(bookmark.Title, bookmark.Children.Count);
            if (!await dialog.ShowDialog<bool>(this))
            {
                return;
            }
        }

        ViewModel.SelectedBookmark = bookmark;
        await ViewModel.DeleteBookmarkAsync(bookmark);
    }

    private void PlayOcrAudioClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var record = GetOcrFromMenu(sender);
        if (record is not null)
        {
            ViewModel.PlayOcrAudio(record);
            e.Handled = true;
        }
    }

    private async void ViewOcrTextClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var record = GetOcrFromMenu(sender);
        if (record is not null)
        {
            var dialog = new OcrTextWindow(record.Title, record.Text);
            await dialog.ShowDialog(this);
            e.Handled = true;
        }
    }

    private void CurrentPageOcrAudioPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }

    private async void CurrentPageOcrAudioClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var record = (sender as Control)?.Tag as OcrRecord;
        if (record is not null)
        {
            await ViewModel.PlayOrGenerateOcrAudioAsync(record);
            e.Handled = true;
        }
    }

    private async void GenerateOcrAudioClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var record = GetOcrFromMenu(sender);
        if (record is not null)
        {
            await ViewModel.GenerateSpeechForRecordAsync(record);
            e.Handled = true;
        }
    }

    private void GenerateOcrWithVoiceSubmenuOpened(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        PopulateOcrVoiceSubmenu(sender, regenerate: false);
    }

    private void RegenerateOcrWithVoiceSubmenuOpened(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        PopulateOcrVoiceSubmenu(sender, regenerate: true);
    }

    private void PopulateOcrVoiceSubmenu(object? sender, bool regenerate)
    {
        if (sender is not MenuItem menuItem)
        {
            return;
        }

        menuItem.Items.Clear();
        var record = GetOcrFromMenu(menuItem);
        var voiceModels = ViewModel.GetConfiguredVoiceModels();
        if (record is null || voiceModels.Count == 0)
        {
            menuItem.Items.Add(new MenuItem { Header = "未配置 Voice Model", IsEnabled = false });
            return;
        }

        foreach (var voiceModel in voiceModels)
        {
            var voiceItem = new MenuItem
            {
                Header = voiceModel.Name,
                Tag = (record, voiceModel.Name, regenerate),
            };
            voiceItem.Click += GenerateOcrAudioWithVoiceClick;
            menuItem.Items.Add(voiceItem);
        }
    }

    private async void GenerateOcrAudioWithVoiceClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is ValueTuple<OcrRecord, string, bool> request)
        {
            if (request.Item3)
            {
                await ViewModel.RegenerateSpeechForRecordAsync(request.Item1, request.Item2);
            }
            else
            {
                await ViewModel.GenerateSpeechForRecordAsync(request.Item1, request.Item2);
            }
            e.Handled = true;
        }
    }

    private async void RemoveOcrRecordClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var record = GetOcrFromMenu(sender);
        if (record is null)
        {
            return;
        }

        var dialog = new OcrDeleteConfirmWindow(
            record.Title,
            "移除会同时删除 OCR 正文、已生成音频和关联截图资源，是否继续？");
        if (await dialog.ShowDialog<bool>(this))
        {
            await ViewModel.DeleteOcrRecordAsync(record);
        }

        e.Handled = true;
    }

    private async void ClearOcrRecordClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var record = ViewModel.SelectedOcrRecord;
        if (record is null)
        {
            return;
        }

        if (record.BookmarkId is null)
        {
            var dialog = new OcrDeleteConfirmWindow(
                record.Title,
                "尚未挂载到书签。强制删除会同时删除正文、音频和截图资源，是否继续？");
            if (!await dialog.ShowDialog<bool>(this))
            {
                return;
            }
        }

        await ViewModel.DeleteOcrRecordAsync(record);
        e.Handled = true;
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (_closingAfterAnnotationDecision || !ViewModel.HasPendingAnnotationChanges)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        var dialog = new AnnotationUnsavedChangesWindow();
        var action = await dialog.ShowDialog<UnsavedAnnotationAction>(this);
        switch (action)
        {
            case UnsavedAnnotationAction.Save:
                await ViewModel.SaveAnnotationsAsync();
                if (!ViewModel.HasPendingAnnotationChanges)
                {
                    _closingAfterAnnotationDecision = true;
                    Close();
                }

                break;
            case UnsavedAnnotationAction.Discard:
                ViewModel.DiscardPendingAnnotations();
                _closingAfterAnnotationDecision = true;
                Close();
                break;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _continuousReadingRenderDelay?.Cancel();
        _continuousReadingRenderDelay?.Dispose();
        ViewModel.PropertyChanged -= ViewModelPropertyChanged;
        ViewModel.CurrentPageOcrRecords.CollectionChanged -= CurrentPageOcrRecordsChanged;
        ViewModel.CurrentPageAnnotations.CollectionChanged -= CurrentPageAnnotationsChanged;
        ViewModel.Bookmarks.CollectionChanged -= BookmarkTreeDataChanged;
        ViewModel.Dispose();
        base.OnClosed(e);
    }
}
