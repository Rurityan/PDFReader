using System;
using System.IO;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PDFReader.Models;
using PDFReader.Services;

namespace PDFReader.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly PdfDocumentService _pdfService = new();
    private readonly PaddleOcrService _ocrService = new();
    private readonly OcrResultRepository _ocrRepository = new();
    private readonly BookmarkRepository _bookmarkRepository = new();
    private readonly SettingsService _settingsService = new();
    private readonly TtsService _ttsService = new();
    private readonly AudioPlaybackService _audioPlaybackService = new();
    private CancellationTokenSource? _ocrCancellation;
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
    private string _lastCapturePath = "尚未生成选区截图";

    [ObservableProperty]
    private bool _isOcrBusy;

    [ObservableProperty]
    private bool _isOcrEnabled;

    [ObservableProperty]
    private bool _isContinuousCapture;

    private bool _captureOnce;

    [ObservableProperty]
    private bool _hasPendingOcr;

    private double _pendingOcrX;
    private double _pendingOcrY;
    private double _pendingOcrWidth;
    private double _pendingOcrHeight;

    [ObservableProperty]
    private bool _enableOcrCaptureCache;

    [ObservableProperty]
    private string _ocrCaptureDirectory = ReaderSettings.GetDefaultCaptureDirectory();

    [ObservableProperty]
    private string _ttsBaseUrl = string.Empty;

    [ObservableProperty]
    private string _ttsApiKey = string.Empty;

    [ObservableProperty]
    private string _ttsModelType = string.Empty;

    [ObservableProperty]
    private string _ttsVoiceModel = string.Empty;

    [ObservableProperty]
    private string _generatedAudioPath = "尚未生成音频";

    [ObservableProperty]
    private bool _isTtsBusy;

    [ObservableProperty]
    private string _bookmarkTitle = string.Empty;

    public ObservableCollection<OcrRecord> OcrHistory { get; } = new();
    public ObservableCollection<Bookmark> Bookmarks { get; } = new();

    public MainWindowViewModel()
    {
        var settings = _settingsService.Load();
        EnableOcrCaptureCache = settings.EnableOcrCaptureCache;
        OcrCaptureDirectory = settings.OcrCaptureDirectory;
        TtsBaseUrl = settings.TtsBaseUrl;
        TtsApiKey = settings.TtsApiKey;
        TtsModelType = settings.TtsModelType;
        TtsVoiceModel = settings.TtsVoiceModel;
    }

    public Bitmap? PageImage
    {
        get => _pageImage;
        private set => SetProperty(ref _pageImage, value);
    }

    public bool CanGoPrevious => HasDocument && _currentPage > 0 && !IsBusy;
    public bool CanGoNext => HasDocument && _currentPage < _pdfService.PageCount - 1 && !IsBusy;
    public bool CanCancelOcr => IsOcrBusy || HasPendingOcr;
    public bool CanSelectCaptureMode => HasDocument && IsOcrEnabled && !IsBusy && !IsOcrBusy;
    public bool CanCapture => CanSelectCaptureMode && (_captureOnce || IsContinuousCapture);
    public bool CanCancelCapture => (_captureOnce || IsContinuousCapture) && !IsOcrBusy;

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
    private void StartOcr()
    {
        if (IsOcrEnabled)
        {
            return;
        }

        IsOcrEnabled = true;
        StatusMessage = "OCR 已启动，请选择截取模式";
        NotifyCaptureChanged();
    }

    [RelayCommand]
    private void CaptureOnce()
    {
        if (!CanSelectCaptureMode)
        {
            return;
        }

        _captureOnce = true;
        IsContinuousCapture = false;
        StatusMessage = "请在页面上拖动选择一次 OCR 区域";
        NotifyCaptureChanged();
    }

    [RelayCommand]
    private void ToggleContinuousCapture()
    {
        if (!CanSelectCaptureMode && !IsContinuousCapture)
        {
            return;
        }

        _captureOnce = false;
        IsContinuousCapture = !IsContinuousCapture;
        StatusMessage = IsContinuousCapture
            ? "持续截取已开启，请连续拖动选择 OCR 区域"
            : "持续截取已停止";
        NotifyCaptureChanged();
    }

    [RelayCommand]
    public void CancelCaptureMode()
    {
        if (!CanCancelCapture)
        {
            return;
        }

        _captureOnce = false;
        IsContinuousCapture = false;
        StatusMessage = "截取已取消";
        NotifyCaptureChanged();
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
            await LoadDocumentDataAsync();
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

    private async Task LoadDocumentDataAsync()
    {
        OcrHistory.Clear();
        Bookmarks.Clear();

        var ocrRecords = await _ocrRepository.GetForDocumentAsync(DocumentPath);
        foreach (var record in ocrRecords)
        {
            OcrHistory.Add(record);
        }

        var bookmarks = await _bookmarkRepository.GetForDocumentAsync(DocumentPath);
        foreach (var bookmark in bookmarks)
        {
            Bookmarks.Add(bookmark);
        }
    }

    [RelayCommand]
    private async Task AddBookmarkAsync()
    {
        if (!HasDocument || IsBusy)
        {
            return;
        }

        var bookmark = new Bookmark
        {
            DocumentPath = DocumentPath,
            PageNumber = _currentPage + 1,
            Title = string.IsNullOrWhiteSpace(BookmarkTitle)
                ? $"第 {_currentPage + 1} 页"
                : BookmarkTitle.Trim(),
            CreatedAtUtc = DateTime.UtcNow,
        };
        await _bookmarkRepository.AddAsync(bookmark);
        Bookmarks.Add(bookmark);
        BookmarkTitle = string.Empty;
        StatusMessage = "书签已添加";
    }

    [RelayCommand]
    public async Task GoToBookmarkAsync(Bookmark? bookmark)
    {
        if (bookmark is null || bookmark.PageNumber < 1 || !HasDocument)
        {
            return;
        }

        await ShowPageAsync(bookmark.PageNumber - 1);
    }

    public async Task RunOcrSelectionAsync(double x, double y, double width, double height)
    {
        if (!CanCapture || width < 8 || height < 8)
        {
            return;
        }

        var cancellation = BeginOcrCancellation();
        try
        {
            IsOcrBusy = true;
            StatusMessage = "正在识别选区...";
            await using var imageStream = await _pdfService.RenderPageRegionAsync(
                _currentPage, x, y, width, height, _zoom, cancellation.Token);
            if (EnableOcrCaptureCache)
            {
                LastCapturePath = await SaveDebugCaptureAsync(
                    imageStream,
                    _currentPage + 1,
                    OcrCaptureDirectory,
                    cancellation.Token);
            }
            else
            {
                LastCapturePath = "截图缓存未开启";
            }
            imageStream.Position = 0;
            StatusMessage = EnableOcrCaptureCache
                ? "选区截图已保存，正在识别..."
                : "正在识别选区...";
            var result = await _ocrService.RecognizeAsync(imageStream, cancellation.Token);
            OcrText = string.IsNullOrWhiteSpace(result.Text) ? "选区内未识别到文本。" : result.Text;
            SetPendingOcr(x, y, width, height);
            StatusMessage = $"选区 OCR 完成，识别到 {result.Lines.Count} 行，请确认后保存";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "OCR 已停止";
        }
        catch (Exception exception)
        {
            StatusMessage = $"选区 OCR 失败: {exception.Message}";
        }
        finally
        {
            EndOcrCancellation(cancellation);
            IsOcrBusy = false;
            if (_captureOnce)
            {
                _captureOnce = false;
                NotifyCaptureChanged();
            }
        }
    }

    [RelayCommand]
    private void CancelOcr()
    {
        if (!CanCancelOcr)
        {
            return;
        }

        _ocrCancellation?.Cancel();
        OcrText = "识别结果已取消，未保存任何结果。";
        HasPendingOcr = false;
        StatusMessage = IsOcrBusy ? "正在停止 OCR..." : "OCR 结果已取消";
    }

    private CancellationTokenSource BeginOcrCancellation()
    {
        var cancellation = new CancellationTokenSource();
        _ocrCancellation = cancellation;
        return cancellation;
    }

    private void EndOcrCancellation(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(_ocrCancellation, cancellation))
        {
            _ocrCancellation = null;
        }

        cancellation.Dispose();
    }

    private void NotifyCaptureChanged()
    {
        OnPropertyChanged(nameof(CanSelectCaptureMode));
        OnPropertyChanged(nameof(CanCapture));
        OnPropertyChanged(nameof(CanCancelCapture));
    }

    [RelayCommand]
    private void SaveSettings()
    {
        if (string.IsNullOrWhiteSpace(OcrCaptureDirectory))
        {
            OcrCaptureDirectory = ReaderSettings.GetDefaultCaptureDirectory();
        }

        _settingsService.Save(new ReaderSettings
        {
            EnableOcrCaptureCache = EnableOcrCaptureCache,
            OcrCaptureDirectory = OcrCaptureDirectory.Trim(),
            TtsBaseUrl = TtsBaseUrl.Trim(),
            TtsApiKey = TtsApiKey.Trim(),
            TtsModelType = TtsModelType.Trim(),
            TtsVoiceModel = TtsVoiceModel.Trim(),
        });
        StatusMessage = "设置已保存";
    }

    [RelayCommand]
    private async Task GenerateSpeechAsync()
    {
        if (!HasDocument || IsTtsBusy || string.IsNullOrWhiteSpace(OcrText))
        {
            return;
        }

        try
        {
            IsTtsBusy = true;
            StatusMessage = "正在生成语音...";
            GeneratedAudioPath = await _ttsService.GenerateAsync(
                OcrText,
                CreateReaderSettings(),
                _currentPage + 1);
            StatusMessage = "语音生成完成";
        }
        catch (Exception exception)
        {
            StatusMessage = $"语音生成失败: {exception.Message}";
        }
        finally
        {
            IsTtsBusy = false;
        }
    }

    [RelayCommand]
    private void PlayGeneratedAudio()
    {
        if (string.IsNullOrWhiteSpace(GeneratedAudioPath) || GeneratedAudioPath == "尚未生成音频")
        {
            return;
        }

        try
        {
            _audioPlaybackService.Play(GeneratedAudioPath);
            StatusMessage = "正在播放音频";
        }
        catch (Exception exception)
        {
            StatusMessage = $"播放失败: {exception.Message}";
        }
    }

    [RelayCommand]
    private void StopAudio()
    {
        _audioPlaybackService.Stop();
        StatusMessage = "音频已停止";
    }

    private ReaderSettings CreateReaderSettings()
    {
        return new ReaderSettings
        {
            EnableOcrCaptureCache = EnableOcrCaptureCache,
            OcrCaptureDirectory = OcrCaptureDirectory,
            TtsBaseUrl = TtsBaseUrl,
            TtsApiKey = TtsApiKey,
            TtsModelType = TtsModelType,
            TtsVoiceModel = TtsVoiceModel,
        };
    }

    [RelayCommand]
    private async Task SaveOcrAsync()
    {
        if (!HasPendingOcr || string.IsNullOrWhiteSpace(OcrText) || !HasDocument)
        {
            return;
        }

        try
        {
            IsOcrBusy = true;
            var record = new OcrRecord
            {
                DocumentPath = DocumentPath,
                PageNumber = _currentPage + 1,
                X = _pendingOcrX,
                Y = _pendingOcrY,
                Width = _pendingOcrWidth,
                Height = _pendingOcrHeight,
                Text = OcrText.Trim(),
                CreatedAtUtc = DateTime.UtcNow,
            };
            await _ocrRepository.AddAsync(record);
            OcrHistory.Insert(0, record);
            HasPendingOcr = false;
            StatusMessage = "OCR 结果已保存";
        }
        catch (Exception exception)
        {
            StatusMessage = $"保存 OCR 结果失败: {exception.Message}";
        }
        finally
        {
            IsOcrBusy = false;
        }
    }

    private void SetPendingOcr(double x, double y, double width, double height)
    {
        _pendingOcrX = x;
        _pendingOcrY = y;
        _pendingOcrWidth = width;
        _pendingOcrHeight = height;
        HasPendingOcr = true;
    }

    private static async Task<string> SaveDebugCaptureAsync(
        Stream imageStream,
        int pageNumber,
        string directory,
        CancellationToken cancellationToken)
    {
        directory = string.IsNullOrWhiteSpace(directory)
            ? ReaderSettings.GetDefaultCaptureDirectory()
            : directory.Trim();
        Directory.CreateDirectory(directory);

        var fileName = $"page-{pageNumber}-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.png";
        var path = Path.Combine(directory, fileName);
        await using var file = File.Create(path);
        await imageStream.CopyToAsync(file, cancellationToken);
        return path;
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

    partial void OnHasDocumentChanged(bool value)
    {
        NotifyNavigationChanged();
        NotifyCaptureChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        NotifyNavigationChanged();
        NotifyCaptureChanged();
    }

    partial void OnIsOcrBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCancelOcr));
        NotifyCaptureChanged();
    }
    partial void OnHasPendingOcrChanged(bool value) => OnPropertyChanged(nameof(CanCancelOcr));
    partial void OnIsOcrEnabledChanged(bool value) => NotifyCaptureChanged();
    partial void OnIsContinuousCaptureChanged(bool value) => NotifyCaptureChanged();

    private void NotifyNavigationChanged()
    {
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
    }

    public void Dispose()
    {
        PageImage?.Dispose();
        _pdfService.Dispose();
        _audioPlaybackService.Dispose();
    }
}
