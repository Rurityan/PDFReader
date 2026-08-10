using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
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
    private Bookmark? _bookmarkDragCandidate;
    private Point _bookmarkDragStart;
    private bool _isDraggingBookmark;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
        ViewModel.PropertyChanged += ViewModelPropertyChanged;
        _ = ViewModel.InitializeAsync();
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.HasPendingOcr) && !ViewModel.HasPendingOcr)
        {
            SelectionBox.IsVisible = false;
        }
    }

    private void PageSurfacePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(PageSurface).Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
        {
            ViewModel.CancelCaptureMode();
            _isSelecting = false;
            SelectionBox.IsVisible = false;
            e.Handled = true;
            return;
        }

        if (!ViewModel.CanCapture || e.GetCurrentPoint(PageSurface).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
        {
            return;
        }

        _selectionStart = e.GetPosition(PageSurface);
        _isSelecting = true;
        SelectionBox.IsVisible = true;
        UpdateSelectionBox(_selectionStart);
        e.Pointer.Capture(PageSurface);
    }

    private void PageSurfacePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isSelecting)
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

        if (selection.Width >= 8 && selection.Height >= 8)
        {
            await ViewModel.RunOcrSelectionAsync(selection.X, selection.Y, selection.Width, selection.Height);
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
        SelectionBox.IsVisible = false;
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

        var bookmark = FindBookmark(e.Source);
        if (bookmark is null)
        {
            return;
        }

        _bookmarkDragCandidate = bookmark;
        _bookmarkDragStart = e.GetPosition(BookmarkTree);
        _isDraggingBookmark = false;
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
        _bookmarkDragCandidate = null;
        _isDraggingBookmark = false;
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
            if (visual is Control control && control.DataContext is Bookmark)
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

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModelPropertyChanged;
        ViewModel.Dispose();
        base.OnClosed(e);
    }
}
