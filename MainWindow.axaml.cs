using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using PDFReader.Models;
using PDFReader.ViewModels;

namespace PDFReader;

public partial class MainWindow : Window
{
    private Point _selectionStart;
    private bool _isSelecting;
    private bool _isOpeningDocument;
    private readonly List<Point> _annotationStrokePoints = new();
    private Bookmark? _bookmarkDragCandidate;
    private Point _bookmarkDragStart;
    private bool _isDraggingBookmark;
    private Border? _bookmarkDragGhost;
    private Border? _bookmarkDropZone;
    private Border? _bookmarkDropIndicator;
    private Control? _bookmarkDropTargetVisual;
    private bool _sidebarCollapsed;
    private double _sidebarWidth = 230;
    private bool _closingAfterAnnotationDecision;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
        ViewModel.CurrentPageOcrRecords.CollectionChanged += CurrentPageOcrRecordsChanged;
        ViewModel.CurrentPageAnnotations.CollectionChanged += CurrentPageAnnotationsChanged;
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
        AddHandler(
            InputElement.PointerPressedEvent,
            WindowPointerPressed,
            Avalonia.Interactivity.RoutingStrategies.Tunnel,
            true);
        ViewModel.PropertyChanged += ViewModelPropertyChanged;
        _ = ViewModel.InitializeAsync();
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    private ColumnDefinition SidebarColumn => ContentLayout.ColumnDefinitions[0];
    private ColumnDefinition SidebarSplitterColumn => ContentLayout.ColumnDefinitions[1];

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
                layer.Width = width + 8;
                layer.Height = height + 8;
                for (var pointIndex = 1; pointIndex < points.Count; pointIndex++)
                {
                    layer.Children.Add(new Line
                    {
                        StartPoint = new Point(points[pointIndex - 1].X * scale - left + 4, points[pointIndex - 1].Y * scale - top + 4),
                        EndPoint = new Point(points[pointIndex].X * scale - left + 4, points[pointIndex].Y * scale - top + 4),
                        Stroke = new SolidColorBrush(Color.Parse("#446FAD")),
                        StrokeThickness = 3,
                    });
                }
            }
            else if (annotation.Type == PdfAnnotationType.Rectangle)
            {
                layer.Children.Add(new Border
                {
                    Width = width,
                    Height = height,
                    BorderBrush = new SolidColorBrush(Color.Parse("#446FAD")),
                    BorderThickness = new Thickness(2),
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
            else
            {
                var note = new Border
                {
                    Width = 26,
                    Height = 26,
                    Background = new SolidColorBrush(Color.Parse("#E6D49A")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#AA8B5A")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Child = new TextBlock
                    {
                        Text = "✎",
                        FontSize = 16,
                        Foreground = new SolidColorBrush(Color.Parse("#5A4524")),
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    },
                };
                ToolTip.SetTip(note, string.IsNullOrWhiteSpace(annotation.Contents) ? "文本标注" : annotation.Contents);
                layer.Children.Add(note);
            }

            var deleteItem = new MenuItem
            {
                Header = "删除标注",
                Tag = annotation,
            };
            deleteItem.Click += DeletePdfAnnotationClick;
            layer.ContextMenu = new ContextMenu
            {
                ItemsSource = new[] { deleteItem },
            };
            Canvas.SetLeft(layer, left);
            Canvas.SetTop(layer, top);
            PdfAnnotationOverlay.Children.Add(layer);
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

    private void UpdateAnnotationToolVisuals()
    {
        var selectedBrush = new SolidColorBrush(Color.Parse("#E6A23C"));
        var normalBrush = new SolidColorBrush(Color.Parse("#F1F3F5"));
        var selectedForeground = new SolidColorBrush(Color.Parse("#20252B"));
        var normalForeground = new SolidColorBrush(Color.Parse("#3B454F"));
        var buttons = new (Button Button, AnnotationTool Tool)[]
        {
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
            : new SolidColorBrush(Color.Parse("#2B6CB0"));
        for (var index = 1; index < _annotationStrokePoints.Count; index++)
        {
            AnnotationStrokePreview.Children.Add(new Line
            {
                StartPoint = _annotationStrokePoints[index - 1],
                EndPoint = _annotationStrokePoints[index],
                Stroke = strokeBrush,
                StrokeThickness = ViewModel.AnnotationTool == AnnotationTool.Eraser ? 10 : 3,
                Opacity = 0.7,
            });
        }
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
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开 PDF 文档",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("PDF 文档") { Patterns = new[] { "*.pdf" } }
            }
        });

        var path = files.FirstOrDefault()?.Path.LocalPath;
        if (!string.IsNullOrWhiteSpace(path))
        {
            await ViewModel.OpenDocumentAsync(path);
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
        if (bookmark is null)
        {
            return;
        }

        _bookmarkDragCandidate = bookmark;
        _bookmarkDragStart = e.GetPosition(BookmarkTree);
        _isDraggingBookmark = false;
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
        if (_bookmarkDragCandidate is null)
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
            BeginBookmarkDragVisual(_bookmarkDragCandidate, current);
        }

        if (_isDraggingBookmark)
        {
            UpdateBookmarkDragVisual(current);
            e.Handled = true;
        }
    }

    private async void BookmarkTreePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_bookmarkDragCandidate is null)
        {
            return;
        }

        var dragged = _bookmarkDragCandidate;
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
        await ViewModel.MoveBookmarkAsync(dragged, target, asChild);
        e.Handled = true;
    }

    private void ResetBookmarkDrag(IPointer? pointer)
    {
        pointer?.Capture(null);
        ClearBookmarkDragVisual();
        _bookmarkDragCandidate = null;
        _isDraggingBookmark = false;
    }

    private void BeginBookmarkDragVisual(Bookmark bookmark, Point point)
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
                Text = $"{bookmark.PageNumber}  {bookmark.Title}",
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
        ViewModel.PropertyChanged -= ViewModelPropertyChanged;
        ViewModel.CurrentPageOcrRecords.CollectionChanged -= CurrentPageOcrRecordsChanged;
        ViewModel.CurrentPageAnnotations.CollectionChanged -= CurrentPageAnnotationsChanged;
        ViewModel.Dispose();
        base.OnClosed(e);
    }
}
