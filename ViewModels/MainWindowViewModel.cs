using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PDFReader.Services;

namespace PDFReader.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly PdfDocumentService _pdfService = new();
    private readonly PaddleOcrService _ocrService = new();
    private Bitmap? _pageImage;
    private int _currentPage;
    private double _zoom = 1.25;

    [ObservableProperty]
    private string _documentTitle = "未打开文档";

    [ObservableProperty]
    private string _documentPath = "从文件菜单打开一个 PDF 文档";

    [ObservableProperty]
    private string _statusMessage = "就绪";

    [ObservableProperty]
    private bool _hasDocument;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _pageIndicator = "0 / 0";

    [ObservableProperty]
    private string _zoomIndicator = "125%";

    [ObservableProperty]
    private string _ocrText = "选择当前页面后开始 OCR。";

    [ObservableProperty]
    private bool _isOcrBusy;

    public Bitmap? PageImage
    {
        get => _pageImage;
        private set => SetProperty(ref _pageImage, value);
    }

    public bool CanGoPrevious => HasDocument && _currentPage > 0 && !IsBusy;
    public bool CanGoNext => HasDocument && _currentPage < _pdfService.PageCount - 1 && !IsBusy;

    [RelayCommand]
    private async Task GoPreviousAsync()
    {
        if (CanGoPrevious)
        {
            await ShowPageAsync(_currentPage - 1);
        }
    }

    [RelayCommand]
    private async Task GoNextAsync()
    {
        if (CanGoNext)
        {
            await ShowPageAsync(_currentPage + 1);
        }
    }

    [RelayCommand]
    private async Task ZoomInAsync()
    {
        if (HasDocument)
        {
            _zoom = Math.Min(3.0, _zoom + 0.25);
            await ShowPageAsync(_currentPage);
        }
    }

    [RelayCommand]
    private async Task ZoomOutAsync()
    {
        if (HasDocument)
        {
            _zoom = Math.Max(0.5, _zoom - 0.25);
            await ShowPageAsync(_currentPage);
        }
    }

    [RelayCommand]
    private async Task RunOcrAsync()
    {
        if (!HasDocument || IsBusy || IsOcrBusy)
        {
            return;
        }

        try
        {
            IsOcrBusy = true;
            StatusMessage = "正在运行 PaddleOCR，首次运行可能需要下载模型...";
            await using var imageStream = await _pdfService.RenderPageAsync(_currentPage, _zoom);
            var result = await _ocrService.RecognizeAsync(imageStream);
            OcrText = string.IsNullOrWhiteSpace(result.Text) ? "未识别到文本。" : result.Text;
            StatusMessage = $"OCR 完成，识别到 {result.Lines.Count} 行";
        }
        catch (Exception exception)
        {
            StatusMessage = $"OCR 失败: {exception.Message}";
        }
        finally
        {
            IsOcrBusy = false;
        }
    }

    public async Task OpenDocumentAsync(string filePath)
    {
        try
        {
            IsBusy = true;
            StatusMessage = "正在打开文档...";
            await _pdfService.OpenAsync(filePath);

            DocumentTitle = Path.GetFileName(filePath);
            DocumentPath = filePath;
            HasDocument = true;
            _currentPage = 0;
            _zoom = 1.25;
            await ShowPageAsync(_currentPage);
            StatusMessage = "文档已打开";
        }
        catch (Exception exception)
        {
            HasDocument = false;
            StatusMessage = $"打开失败: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
            NotifyNavigationChanged();
        }
    }

    private async Task ShowPageAsync(int pageIndex)
    {
        try
        {
            IsBusy = true;
            StatusMessage = $"正在渲染第 {pageIndex + 1} 页...";
            await using var stream = await _pdfService.RenderPageAsync(pageIndex, _zoom);
            var image = new Bitmap(stream);
            var oldImage = PageImage;
            PageImage = image;
            oldImage?.Dispose();
            _currentPage = pageIndex;
            PageIndicator = $"{_currentPage + 1} / {_pdfService.PageCount}";
            ZoomIndicator = $"{_zoom:P0}";
            StatusMessage = "就绪";
        }
        catch (Exception exception)
        {
            StatusMessage = $"渲染失败: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
            NotifyNavigationChanged();
        }
    }

    partial void OnHasDocumentChanged(bool value) => NotifyNavigationChanged();
    partial void OnIsBusyChanged(bool value) => NotifyNavigationChanged();

    private void NotifyNavigationChanged()
    {
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
    }

    public void Dispose()
    {
        PageImage?.Dispose();
        _pdfService.Dispose();
    }
}
