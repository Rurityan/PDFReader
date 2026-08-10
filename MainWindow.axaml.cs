using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using PDFReader.Models;
using PDFReader.ViewModels;

namespace PDFReader;

public partial class MainWindow : Window
{
    private Point _selectionStart;
    private bool _isSelecting;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
        ViewModel.PropertyChanged += ViewModelPropertyChanged;
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

    private async void OpenSettingsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var window = new SettingsWindow(ViewModel.GetSettings(), ViewModel.ApplySettingsAsync);
        await window.ShowDialog(this);
    }

    private async void BookmarkDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox listBox && listBox.SelectedItem is Bookmark bookmark)
        {
            await ViewModel.GoToBookmarkAsync(bookmark);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModelPropertyChanged;
        ViewModel.Dispose();
        base.OnClosed(e);
    }
}
