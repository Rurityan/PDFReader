using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PDFReader.Models;
using PDFReader.Services;

namespace PDFReader.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly PdfDocumentService _pdfService = new();
    private readonly OcrService _ocrService = new();
    private readonly SettingsService _settingsService = new();
    private readonly TtsService _ttsService = new();
    private readonly AudioPlaybackService _audioPlaybackService = new();
    private readonly PdfEditingService _pdfEditingService = new();
    private readonly PdfAnnotationService _annotationService = new();
    private readonly Html5ExportService _html5ExportService = new();
    private readonly PdfDocumentRepository _documentRepository = new();
    private readonly LocalAutomationService _automationService;
    private readonly SemaphoreSlim _documentOpenGate = new(1, 1);
    private readonly SemaphoreSlim _pageRenderGate = new(1, 1);
    private readonly SemaphoreSlim _zoomChangeGate = new(1, 1);
    private readonly SemaphoreSlim _readingRenderGate = new(2, 2);
    private readonly Dictionary<Guid, Dictionary<int, IReadOnlyList<PdfAnnotationInfo>>> _annotationCache = new();
    private readonly Dictionary<int, Bitmap> _prefetchedPageImages = new();
    private readonly HashSet<int> _prefetchingPageIndexes = new();
    private OcrResultRepository _ocrRepository;
    private BookmarkRepository _bookmarkRepository;
    private ReaderSettings _settings;
    private Guid _documentId;
    private readonly HashSet<CancellationTokenSource> _ocrCancellations = new();
    private readonly object _ocrCancellationLock = new();
    private readonly SemaphoreSlim _ocrQueueSignal = new(0);
    private readonly CancellationTokenSource _ocrQueueCancellation = new();
    private readonly Task _ocrQueueWorker;
    private readonly ConcurrentQueue<OcrQueueJob> _ocrQueue = new();
    private CancellationTokenSource? _readingCancellation;
    private Bitmap? _pageImage;
    private Bitmap? _previousReadingPageImage;
    private Bitmap? _nextReadingPageImage;
    private int _currentPage;
    private double _zoom = 1.25;
    private PagePreview? _selectedPagePreview;
    private readonly List<PdfAnnotationChange> _pendingAnnotationChanges = new();
    private bool _isSynchronizingTextFontSize;
    private int _pageRenderGeneration;
    private DateTime _lastPageNavigationUtc;
    private int _preloadRadius = 5;

    private sealed record OcrQueueJob(OcrRecord Record, byte[] ImageBytes);

    public event Action<int>? ContinuousReadingPageRequested;
    public event Action<Bookmark>? BookmarkLocationRequested;
    public event Action<OcrRecord>? OcrBookmarkCreationRequested;
    public event Action<OcrRecord, Bookmark>? OcrCrossPageAttachRequested;

    [ObservableProperty]
    private decimal _annotationTextFontSize = 11;

    [ObservableProperty]
    private string _documentTitle = "未打开文档";

    [ObservableProperty]
    private string _documentPath = "从文件菜单打开一个 PDF 文档";

    [ObservableProperty]
    private PdfDocument? _selectedDocument;

    [ObservableProperty]
    private PdfAnnotationInfo? _selectedPdfAnnotation;

    [ObservableProperty]
    private string _statusMessage = "就绪";

    [ObservableProperty]
    private bool _hasDocument;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _pageIndicator = "0 / 0";

    [ObservableProperty]
    private string _pageNumberInput = "1";

    [ObservableProperty]
    private string _zoomIndicator = "125%";

    [ObservableProperty]
    private string _ocrText = "选择当前页面后开始 OCR。";

    [ObservableProperty]
    private string _ocrTitle = string.Empty;

    [ObservableProperty]
    private string _lastCapturePath = "尚未生成选区截图";

    [ObservableProperty]
    private bool _isOcrBusy;

    [ObservableProperty]
    private bool _isOcrEnabled;

    [ObservableProperty]
    private bool _isContinuousCapture;

    [ObservableProperty]
    private bool _isAnnotationMode;

    [ObservableProperty]
    private bool _isReadingCurrentPage;

    [ObservableProperty]
    private bool _isAudioPlaying;

    [ObservableProperty]
    private AnnotationTool _annotationTool = AnnotationTool.Freehand;

    [ObservableProperty]
    private Color _annotationColor = Color.Parse("#2B6CB0");

    [ObservableProperty]
    private decimal _annotationStrokeWidth = 2;

    private bool _captureOnce;

    [ObservableProperty]
    private bool _hasPendingOcr;


    [ObservableProperty]
    private bool _enablePagePreviews = true;

    [ObservableProperty]
    private bool _enableOcrCaptureCache;

    [ObservableProperty]
    private bool _autoGenerateOcrAudio;

    [ObservableProperty]
    private bool _enableTtsAudioNormalization;

    [ObservableProperty]
    private string _ffmpegPath = string.Empty;

    [ObservableProperty]
    private bool _enableLocalApi;

    [ObservableProperty]
    private int _localApiPort = 38421;

    [ObservableProperty]
    private string _localApiToken = string.Empty;

    [ObservableProperty]
    private string _ocrCaptureDirectory = ReaderSettings.GetDefaultCaptureDirectory();

    [ObservableProperty]
    private string _audioDirectory = ReaderSettings.GetDefaultAudioDirectory();

    [ObservableProperty]
    private string _ttsBaseUrl = string.Empty;

    [ObservableProperty]
    private string _ttsApiKey = string.Empty;

    [ObservableProperty]
    private string _ttsModelType = string.Empty;

    [ObservableProperty]
    private string _ttsVoiceModel = string.Empty;
    private List<TtsVoiceModelOption> _ttsVoiceModels = new();

    [ObservableProperty]
    private string _generatedAudioPath = "尚未生成音频";

    [ObservableProperty]
    private bool _isTtsBusy;

    [ObservableProperty]
    private bool _isCurrentPageOcrVisible;

    [ObservableProperty]
    private Bookmark? _selectedBookmark;

    [ObservableProperty]
    private object? _selectedTreeItem;

    [ObservableProperty]
    private OcrRecord? _selectedOcrRecord;

    [ObservableProperty]
    private OcrRecord? _selectedOcrHistoryRecord;

    private readonly Stack<DeletedBookmarkOperation> _deletedBookmarkHistory = new();
    private readonly Stack<DeletedAnnotationOperation> _deletedAnnotationHistory = new();
    private readonly HashSet<Guid> _expandedBookmarkIds = new();

    public ObservableCollection<OcrRecord> OcrHistory { get; } = new();
    public ObservableCollection<OcrRecord> OcrProcessingQueue { get; } = new();
    public ObservableCollection<OcrRecord> CurrentPageOcrRecords { get; } = new();
    public ObservableCollection<PdfAnnotationInfo> CurrentPageAnnotations { get; } = new();
    public ObservableCollection<PagePreview> PagePreviews { get; } = new();
    public ObservableCollection<ReadingPage> ReadingPages { get; } = new();
    public ObservableCollection<PdfDocument> AvailableDocuments { get; } = new();
    public ObservableCollection<PdfDocument> ArchivedDocuments { get; } = new();
    public ObservableCollection<Bookmark> Bookmarks { get; } = new();
    public ObservableCollection<PdfDocument> Documents { get; } = new();

    public PagePreview? SelectedPagePreview
    {
        get => _selectedPagePreview;
        private set => SetProperty(ref _selectedPagePreview, value);
    }

    public MainWindowViewModel()
    {
        _audioPlaybackService.PlaybackStateChanged += AudioPlaybackStateChanged;
        var settings = _settingsService.Load();
        _settings = settings;
        _ocrRepository = new OcrResultRepository();
        _bookmarkRepository = new BookmarkRepository();
        EnablePagePreviews = settings.EnablePagePreviews;
        EnableOcrCaptureCache = settings.EnableOcrCaptureCache;
        AutoGenerateOcrAudio = settings.AutoGenerateOcrAudio;
        EnableTtsAudioNormalization = settings.EnableTtsAudioNormalization;
        FfmpegPath = settings.FfmpegPath;
        EnableLocalApi = settings.EnableLocalApi;
        LocalApiPort = settings.LocalApiPort;
        LocalApiToken = settings.LocalApiToken;
        OcrCaptureDirectory = settings.OcrCaptureDirectory;
        AudioDirectory = settings.AudioDirectory;
        TtsBaseUrl = settings.TtsBaseUrl;
        TtsApiKey = settings.TtsApiKey;
        TtsModelType = settings.TtsModelType;
        TtsVoiceModel = settings.TtsVoiceModel;
        _ttsVoiceModels = CloneVoiceModels(settings.TtsVoiceModels);
        _automationService = new LocalAutomationService(CreateReaderSettings);
        _automationService.ImportCompleted += AutomationImportCompleted;
        _ocrQueueWorker = Task.Run(ProcessOcrQueueAsync);
        if (settings.EnableLocalApi)
        {
            _automationService.Start(settings.LocalApiPort);
        }
    }

    public async Task InitializeAsync()
    {
        try
        {
            AvailableDocuments.Clear();
            ArchivedDocuments.Clear();
            Documents.Clear();
            var documents = await _documentRepository.GetAllAsync();
            foreach (var document in documents)
            {
                document.RefreshPathStatus();
                (document.IsArchived ? ArchivedDocuments : AvailableDocuments).Add(document);
            }

            StatusMessage = documents.Count == 0
                ? "暂无已保存的 PDF 文件记录"
                : $"已加载 {documents.Count} 个 PDF 文件记录，请从“打开 PDF”导入工作区";
            ScheduleStartupCleanup();
        }
        catch (Exception exception)
        {
            StatusMessage = $"加载 PDF 文档列表失败: {exception.Message}";
        }
    }

    private void ScheduleStartupCleanup()
    {
        _ = Task.Run(() =>
        {
            try
            {
                DeleteResources(_ocrRepository.CleanupOrphanedData());
            }
            catch
            {
                // Startup cleanup is best-effort and must never affect document availability.
            }
        });
    }

    public void SetStatus(string message) => StatusMessage = message;

    public void ClearOcrHistorySelection()
    {
        SelectedOcrHistoryRecord = null;
    }

    public async Task<IReadOnlyList<OcrRecord>> GetCurrentDocumentOcrRecordsAsync()
    {
        if (!HasDocument)
        {
            return Array.Empty<OcrRecord>();
        }

        var records = await _ocrRepository.GetForDocumentAsync(_documentId);
        foreach (var record in records)
        {
            record.IsPersisted = true;
            record.RefreshAudioStatus();
        }

        return records;
    }

    public void RefreshResourceRelatedViews()
    {
        RefreshBookmarkDisplayTree();
        RefreshCurrentPageOcr();
        RefreshReadingPageOcr();
        OnPropertyChanged(nameof(CanGenerateSpeech));
    }

    public async Task<bool> DeleteOcrRecordAsync(OcrRecord? record)
    {
        if (record is null || !CanModifyDocument || IsBusy || IsOcrBusy || IsTtsBusy)
        {
            return false;
        }

        try
        {
            DeleteResources(await _ocrRepository.DeleteAsync(record.Id));
            var currentRecord = OcrHistory.FirstOrDefault(item => item.Id == record.Id) ?? record;
            OcrHistory.Remove(currentRecord);
            OcrProcessingQueue.Remove(currentRecord);
            if (SelectedOcrRecord?.Id == record.Id)
            {
                SelectedOcrRecord = null;
            }

            if (SelectedOcrHistoryRecord?.Id == record.Id)
            {
                SelectedOcrHistoryRecord = null;
            }

            RefreshBookmarkDisplayTree();
            RefreshCurrentPageOcr();
            RefreshReadingPageOcr();
            StatusMessage = "OCR 记录及其音频资源已删除";
            return true;
        }
        catch (Exception exception)
        {
            StatusMessage = $"删除 OCR 记录失败: {exception.Message}";
            return false;
        }
    }

    public async Task<bool> DeleteOcrAudiosAsync(OcrRecord? record)
    {
        if (record is null || !CanModifyDocument || IsBusy || IsOcrBusy || IsTtsBusy)
        {
            return false;
        }

        try
        {
            foreach (var path in await _ocrRepository.DeleteAudiosAsync(record.Id))
            {
                DeleteResource(path);
            }

            record.TtsAudios.Clear();
            record.RefreshAudioStatus();
            var currentRecord = OcrHistory.FirstOrDefault(item => item.Id == record.Id);
            if (currentRecord is not null && !ReferenceEquals(currentRecord, record))
            {
                currentRecord.TtsAudios.Clear();
                currentRecord.RefreshAudioStatus();
            }
            RefreshResourceRelatedViews();
            StatusMessage = "OCR 音频资源已删除";
            return true;
        }
        catch (Exception exception)
        {
            StatusMessage = $"删除 OCR 音频失败: {exception.Message}";
            return false;
        }
    }

    public Bitmap? PageImage
    {
        get => _pageImage;
        private set => SetProperty(ref _pageImage, value);
    }

    public Bitmap? PreviousReadingPageImage
    {
        get => _previousReadingPageImage;
        private set
        {
            if (SetProperty(ref _previousReadingPageImage, value))
            {
                OnPropertyChanged(nameof(HasPreviousReadingPage));
            }
        }
    }

    public Bitmap? NextReadingPageImage
    {
        get => _nextReadingPageImage;
        private set
        {
            if (SetProperty(ref _nextReadingPageImage, value))
            {
                OnPropertyChanged(nameof(HasNextReadingPage));
            }
        }
    }

    public bool CanGoPrevious => HasDocument && _currentPage > 0 && !IsBusy;
    public bool CanGoNext => HasDocument && _currentPage < _pdfService.PageCount - 1 && !IsBusy;
    public bool HasPreviousReadingPage => !IsAnnotationMode && !CanCapture && PreviousReadingPageImage is not null;
    public bool HasNextReadingPage => !IsAnnotationMode && !CanCapture && NextReadingPageImage is not null;
    public bool IsContinuousReadingMode => HasDocument && !IsAnnotationMode && !CanCapture;
    public bool CanCancelOcr => IsOcrBusy || HasPendingOcr;
    public bool CanSelectCaptureMode => HasDocument && IsOcrEnabled && !IsBusy && !IsDocumentReadOnly;
    public bool CanCapture => CanSelectCaptureMode && (_captureOnce || IsContinuousCapture);
    public bool CanCancelCapture => _captureOnce || IsContinuousCapture;
    public bool HasSelectedBookmark => SelectedBookmark is not null;
    public bool CanAttachOcr => SelectedOcrRecord is not null
        && SelectedOcrRecord.IsPersisted
        && SelectedOcrRecord.BookmarkId is null
        && OcrProcessingQueue.Contains(SelectedOcrRecord)
        && !IsBusy && !IsDocumentReadOnly
        && !IsOcrBusy;
    public bool CanGenerateSpeech => HasDocument && SelectedOcrRecord is not null && !IsTtsBusy && !IsDocumentReadOnly;
    public bool CanClearOcr => HasDocument && SelectedOcrRecord is not null
        && OcrProcessingQueue.Contains(SelectedOcrRecord)
        && SelectedOcrRecord.BookmarkId is null
        && !IsBusy && !IsOcrBusy && !IsTtsBusy && !IsDocumentReadOnly;
    public bool CanConfirmOcr => HasPendingOcr
        && SelectedOcrRecord is not null
        && !SelectedOcrRecord.IsProcessing
        && !SelectedOcrRecord.IsPersisted
        && !string.IsNullOrWhiteSpace(SelectedOcrRecord.Text)
        && CanModifyDocument;
    public bool CanAnnotate => HasDocument && IsAnnotationMode
        && !IsBusy && !IsOcrBusy && !IsTtsBusy && !IsReadingCurrentPage && !IsDocumentReadOnly;
    public bool CanReadCurrentPage => HasDocument
        && (IsReadingCurrentPage
            || IsAudioPlaying
            || IsAudioPaused
            || (!IsBusy && !IsTtsBusy && !IsAnnotationMode
                && CurrentPageOcrRecords.Any(record => record.HasAudio)));
    public bool CanReadPageOnly => HasDocument
        && !IsReadingCurrentPage
        && !IsAudioPlaying
        && !IsAudioPaused
        && !IsBusy
        && !IsTtsBusy
        && !IsAnnotationMode
        && CurrentPageOcrRecords.Any(record => record.HasAudio);
    public bool CanUndoBookmarkDelete => _deletedBookmarkHistory.Count > 0 && !IsBusy;
    public bool CanUndoAnnotationDelete => _deletedAnnotationHistory.Count > 0 && !IsBusy;
    public bool HasSelectedPdfAnnotation => SelectedPdfAnnotation is not null;
    public bool HasSelectedFreeTextAnnotation => SelectedPdfAnnotation?.Type == PdfAnnotationType.Text;
    public string SelectedAnnotationColor => SelectedPdfAnnotation?.StrokeColor ?? "-";
    public string SelectedAnnotationStrokeWidth => SelectedPdfAnnotation is null
        ? "-"
        : $"{SelectedPdfAnnotation.StrokeWidth:0.##} pt";
    public bool HasPendingAnnotationChanges => _pendingAnnotationChanges.Count > 0;
    public bool IsDocumentReadOnly => HasDocument && SelectedDocument?.IsArchived == true;
    public bool CanModifyDocument => HasDocument && !IsDocumentReadOnly;
    public bool CanSaveAnnotations => HasDocument && HasPendingAnnotationChanges && !IsBusy && !IsDocumentReadOnly;
    public int CurrentPageNumber => _currentPage + 1;
    public double CurrentZoom => _zoom;
    public int DocumentPageCount => Math.Max(1, _pdfService.PageCount);
    public string CurrentPageOcrButtonText => IsCurrentPageOcrVisible
        ? "隐藏 OCR"
        : "显示 OCR";
    public string AnnotationButtonText => IsAnnotationMode ? "取消标注" : "标注";
    public string AnnotationColorHex => $"#{AnnotationColor.R:X2}{AnnotationColor.G:X2}{AnnotationColor.B:X2}";
    public SolidColorBrush AnnotationColorBrush => new(AnnotationColor);
    public string AnnotationStrokeWidthText => $"{AnnotationStrokeWidth:0} pt";
    public string AnnotationToolText => AnnotationTool switch
    {
        AnnotationTool.Select => "选择标注",
        AnnotationTool.Line => "画线",
        AnnotationTool.Freehand => "自由绘制",
        AnnotationTool.Rectangle => "方框",
        AnnotationTool.Highlight => "高亮",
        AnnotationTool.Eraser => "橡皮擦",
        _ => "文本标注",
    };
    public bool IsAudioPaused { get; private set; }
    public bool IsAudioActive => IsAudioPlaying || IsAudioPaused;
    public bool IsAudioControlsVisible => IsAudioActive;
    public string AudioPauseButtonText => IsAudioPaused ? "继续播放" : "暂停播放";
    public string ReadCurrentPageButtonText => IsReadingCurrentPage || IsAudioActive
        ? "停止播放"
        : "朗读";
    public string ReadPageOnlyButtonText => "朗读本页";
    public bool IsBookmarkPaneVisible => !IsPagePreviewPaneVisible;

    [ObservableProperty]
    private bool _isPagePreviewPaneVisible;

    [RelayCommand]
    private void ShowPagePreviewPane()
    {
        if (!EnablePagePreviews)
        {
            return;
        }

        IsPagePreviewPaneVisible = true;
    }

    [RelayCommand]
    private void ShowBookmarkPane()
    {
        IsPagePreviewPaneVisible = false;
    }

    [RelayCommand]
    private void ToggleCurrentPageOcr()
    {
        if (!HasDocument)
        {
            return;
        }

        if (!IsCurrentPageOcrVisible)
        {
            IsCurrentPageOcrVisible = true;
        }
        else
        {
            IsCurrentPageOcrVisible = false;
        }

        StatusMessage = IsCurrentPageOcrVisible
            ? $"已显示第 {_currentPage + 1} 页的 {CurrentPageOcrRecords.Count} 条 OCR"
            : "已隐藏当前页 OCR 框";
    }

    [RelayCommand]
    private async Task ToggleAnnotation()
    {
        if (!CanModifyDocument || IsBusy)
        {
            return;
        }

        if (IsAnnotationMode)
        {
            IsAnnotationMode = false;
            InitializeReadingPages();
            ContinuousReadingPageRequested?.Invoke(_currentPage + 1);
            StatusMessage = "标注模式已取消";
        }
        else
        {
            CancelCaptureMode();
            StatusMessage = "正在加载当前页标注...";
            try
            {
                await ShowPageAsync(_currentPage);
                IsAnnotationMode = true;
                AnnotationTool = AnnotationTool.Select;
                await EnsureCurrentPageAnnotationsAsync();
                StatusMessage = "标注模式已开启，请框选要添加标注的区域";
            }
            catch (Exception exception)
            {
                IsAnnotationMode = false;
                StatusMessage = $"加载 PDF 标注失败: {exception.Message}";
            }
        }
    }

    public void SelectAnnotationTool(AnnotationTool tool)
    {
        if (!CanModifyDocument || IsBusy)
        {
            return;
        }

        CancelCaptureMode();
        IsAnnotationMode = true;
        AnnotationTool = tool;
        StatusMessage = tool switch
        {
            AnnotationTool.Select => "选择标注已开启，点击标注对象后可按 Delete 删除",
            AnnotationTool.Eraser => "橡皮擦已开启，请沿标注拖动擦除",
            AnnotationTool.Freehand => "自由绘制已开启，可连续绘制",
            _ => $"已选择{AnnotationToolText}，请在页面上框选区域",
        };
    }

    public void SetAnnotationColor(Color color)
    {
        AnnotationColor = Color.FromRgb(color.R, color.G, color.B);
        ApplySelectedAnnotationStyle();
    }

    public void CancelAnnotationMode()
    {
        if (IsAnnotationMode)
        {
            IsAnnotationMode = false;
            StatusMessage = "标注已取消";
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleReadCurrentPageAsync()
    {
        if (IsAudioActive || IsReadingCurrentPage)
        {
            _readingCancellation?.Cancel();
            _audioPlaybackService.Stop();
            SetAudioPlaying(false);
            return;
        }

        await ReadDocumentAsync();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ReadPageOnlyAsync()
    {
        if (!HasDocument || IsBusy || IsAnnotationMode)
        {
            return;
        }

        try
        {
            await ReadPageAudioAsync(_currentPage, "朗读本页");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "朗读已停止";
        }
    }

    private async Task ReadDocumentAsync()
    {
        if (!HasDocument || IsBusy || IsAnnotationMode)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _readingCancellation = cancellation;
        IsReadingCurrentPage = true;
        SetAudioPlaying(true);
        StatusMessage = "正在连续朗读 OCR 音频";
        try
        {
            for (var pageIndex = _currentPage; pageIndex < _pdfService.PageCount; pageIndex++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                await ReadPageAudioAsync(pageIndex, "连续朗读", cancellation.Token, false);
            }

            StatusMessage = "连续朗读完成";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "朗读已停止";
        }
        catch (Exception exception)
        {
            StatusMessage = $"朗读失败: {exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(_readingCancellation, cancellation))
            {
                _readingCancellation = null;
            }

            cancellation.Dispose();
            IsReadingCurrentPage = false;
            SetAudioPlaying(false);
            SetAudioPaused(false);
            OnPropertyChanged(nameof(CanReadPageOnly));
        }
    }

    public Task ReadCurrentPageAsync() => ReadPageAudioAsync(_currentPage, "朗读本页");

    private async Task ReadPageAudioAsync(
        int pageIndex,
        string statusPrefix,
        CancellationToken? externalCancellation = null,
        bool manageReadingState = true)
    {
        if (pageIndex < 0 || pageIndex >= _pdfService.PageCount)
        {
            return;
        }

        var records = GetAudioRecordsForPage(pageIndex);
        if (records.Count == 0)
        {
            if (manageReadingState)
            {
                StatusMessage = "当前页没有可播放的 OCR 音频";
                OnPropertyChanged(nameof(CanReadCurrentPage));
                OnPropertyChanged(nameof(CanReadPageOnly));
            }

            return;
        }

        var cancellation = externalCancellation is null
            ? new CancellationTokenSource()
            : null;
        var token = externalCancellation ?? cancellation!.Token;
        if (manageReadingState)
        {
            _readingCancellation = cancellation;
            IsReadingCurrentPage = true;
            SetAudioPlaying(true);
        }

        try
        {
            if (_currentPage != pageIndex)
            {
                if (IsContinuousReadingMode)
                {
                    NavigateContinuousReadingPage(pageIndex + 1);
                }
                else
                {
                    await ShowPageAsync(pageIndex);
                }
            }

            StatusMessage = $"{statusPrefix}第 {pageIndex + 1} 页的 {records.Count} 条 OCR";
            foreach (var record in records)
            {
                token.ThrowIfCancellationRequested();
                GeneratedAudioPath = record.LatestAudioPath!;
                SetAudioPlaying(true);
                await _audioPlaybackService.PlayAndWaitAsync(record.LatestAudioPath!, token);
            }
        }
        finally
        {
            if (manageReadingState)
            {
                if (ReferenceEquals(_readingCancellation, cancellation))
                {
                    _readingCancellation = null;
                }

                cancellation!.Dispose();
                IsReadingCurrentPage = false;
                SetAudioPlaying(false);
                SetAudioPaused(false);
            }
        }
    }

    private List<OcrRecord> GetAudioRecordsForPage(int pageIndex)
    {
        return OcrHistory
            .Where(record => record.PageNumber == pageIndex + 1)
            .Select(record =>
            {
                record.RefreshAudioStatus();
                return record;
            })
            .Where(record => record.HasAudio && !string.IsNullOrWhiteSpace(record.LatestAudioPath))
            .ToList();
    }

    public Task AddAnnotationAsync(
        double x,
        double y,
        double width,
        double height,
        string title,
        string contents)
    {
        if (!HasDocument || IsBusy || string.IsNullOrWhiteSpace(contents))
        {
            return Task.CompletedTask;
        }

        QueueAnnotation(new PdfAnnotationInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            PageNumber = _currentPage + 1,
            Type = PdfAnnotationType.Text,
            Title = string.IsNullOrWhiteSpace(title) ? "PDF Reader" : title.Trim(),
            Contents = contents.Trim(),
            X = x / _zoom,
            Y = y / _zoom,
            Width = width / _zoom,
            Height = height / _zoom,
            StrokeColor = AnnotationColorHex,
            StrokeWidth = (double)AnnotationStrokeWidth,
        });
        return Task.CompletedTask;
    }

    public Task AddLineAnnotationAsync(double startX, double startY, double endX, double endY)
    {
        if (!HasDocument || IsBusy)
        {
            return Task.CompletedTask;
        }

        QueueAnnotation(new PdfAnnotationInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            PageNumber = _currentPage + 1,
            Type = PdfAnnotationType.Line,
            X = Math.Min(startX, endX) / _zoom,
            Y = Math.Min(startY, endY) / _zoom,
            Width = Math.Max(1, Math.Abs(endX - startX) / _zoom),
            Height = Math.Max(1, Math.Abs(endY - startY) / _zoom),
            StartX = startX / _zoom,
            StartY = startY / _zoom,
            EndX = endX / _zoom,
            EndY = endY / _zoom,
            StrokeColor = AnnotationColorHex,
            StrokeWidth = (double)AnnotationStrokeWidth,
        });
        return Task.CompletedTask;
    }

    public Task AddHighlightAnnotationAsync(double x, double y, double width, double height)
    {
        if (!HasDocument || IsBusy)
        {
            return Task.CompletedTask;
        }

        QueueAnnotation(new PdfAnnotationInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            PageNumber = _currentPage + 1,
            Type = PdfAnnotationType.Highlight,
            X = x / _zoom,
            Y = y / _zoom,
            Width = width / _zoom,
            Height = height / _zoom,
            StrokeColor = AnnotationColorHex,
            StrokeWidth = (double)AnnotationStrokeWidth,
        });
        return Task.CompletedTask;
    }

    public Task AddRectangleAnnotationAsync(double x, double y, double width, double height)
    {
        if (!HasDocument || IsBusy)
        {
            return Task.CompletedTask;
        }

        QueueAnnotation(new PdfAnnotationInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            PageNumber = _currentPage + 1,
            Type = PdfAnnotationType.Rectangle,
            X = x / _zoom,
            Y = y / _zoom,
            Width = width / _zoom,
            Height = height / _zoom,
            StrokeColor = AnnotationColorHex,
            StrokeWidth = (double)AnnotationStrokeWidth,
        });
        return Task.CompletedTask;
    }

    public Task AddFreehandAnnotationAsync(IReadOnlyList<PdfAnnotationPoint> points)
    {
        if (!HasDocument || IsBusy || points.Count < 2)
        {
            return Task.CompletedTask;
        }

        var pdfPoints = points
            .Select(point => new PdfAnnotationPoint(point.X / _zoom, point.Y / _zoom))
            .ToArray();
        var left = pdfPoints.Min(point => point.X);
        var right = pdfPoints.Max(point => point.X);
        var top = pdfPoints.Min(point => point.Y);
        var bottom = pdfPoints.Max(point => point.Y);
        QueueAnnotation(new PdfAnnotationInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            PageNumber = _currentPage + 1,
            Type = PdfAnnotationType.Freehand,
            X = left,
            Y = top,
            Width = Math.Max(1, right - left),
            Height = Math.Max(1, bottom - top),
            StartX = pdfPoints[0].X,
            StartY = pdfPoints[0].Y,
            EndX = pdfPoints[^1].X,
            EndY = pdfPoints[^1].Y,
            Points = pdfPoints,
            StrokeColor = AnnotationColorHex,
            StrokeWidth = (double)AnnotationStrokeWidth,
        });
        return Task.CompletedTask;
    }

    public Task DeleteAnnotationAsync(PdfAnnotationInfo? annotation)
    {
        if (!HasDocument || IsBusy || annotation is null)
        {
            return Task.CompletedTask;
        }

        QueueAnnotationDeletion(annotation);
        if (ReferenceEquals(SelectedPdfAnnotation, annotation))
        {
            SelectedPdfAnnotation = null;
        }

        RefreshCurrentPageAnnotations();
        StatusMessage = "标注删除已暂存，请保存标注写入 PDF";
        NotifyAnnotationSaveChanged();
        return Task.CompletedTask;
    }

    public Task UpdateTextAnnotationAsync(PdfAnnotationInfo annotation, string title, string contents)
    {
        if (!HasDocument || IsBusy || annotation.Type != PdfAnnotationType.Text)
        {
            return Task.CompletedTask;
        }

        var updated = new PdfAnnotationInfo
        {
            Id = annotation.Id, Subtype = annotation.Subtype, PageNumber = annotation.PageNumber,
            Type = annotation.Type, Title = title, Contents = contents, X = annotation.X, Y = annotation.Y,
            Width = annotation.Width, Height = annotation.Height, StartX = annotation.StartX, StartY = annotation.StartY,
            EndX = annotation.EndX, EndY = annotation.EndY, Points = annotation.Points,
            StrokeColor = annotation.StrokeColor, StrokeWidth = annotation.StrokeWidth, FontSize = annotation.FontSize,
        };
        QueueAnnotationUpdate(updated);
        SelectedPdfAnnotation = updated;
        RefreshCurrentPageAnnotations();
        StatusMessage = "文本框修改已暂存，请保存标注写入 PDF";
        NotifyAnnotationSaveChanged();
        return Task.CompletedTask;
    }

    public void ResizeTextAnnotation(PdfAnnotationInfo annotation, string handle, double deltaX, double deltaY)
    {
        var left = annotation.X;
        var top = annotation.Y;
        var right = annotation.X + annotation.Width;
        var bottom = annotation.Y + annotation.Height;
        if (handle.Contains('L')) left += deltaX;
        if (handle.Contains('R')) right += deltaX;
        if (handle.Contains('T')) top += deltaY;
        if (handle.Contains('B')) bottom += deltaY;
        if (right - left < 24) { if (handle.Contains('L')) left = right - 24; else right = left + 24; }
        if (bottom - top < 20) { if (handle.Contains('T')) top = bottom - 20; else bottom = top + 20; }
        var updated = new PdfAnnotationInfo
        {
            Id = annotation.Id, Subtype = annotation.Subtype, PageNumber = annotation.PageNumber, Type = annotation.Type,
            Title = annotation.Title, Contents = annotation.Contents, X = left, Y = top, Width = right - left, Height = bottom - top,
            StartX = annotation.StartX, StartY = annotation.StartY, EndX = annotation.EndX, EndY = annotation.EndY,
            Points = annotation.Points, StrokeColor = annotation.StrokeColor, StrokeWidth = annotation.StrokeWidth, FontSize = annotation.FontSize,
        };
        QueueAnnotationUpdate(updated);
        SelectedPdfAnnotation = updated;
        RefreshCurrentPageAnnotations();
        StatusMessage = "文本框范围修改已暂存，请保存标注写入 PDF";
        NotifyAnnotationSaveChanged();
    }

    public void ResizeLineAnnotation(PdfAnnotationInfo annotation, string handle, double deltaX, double deltaY)
    {
        var startX = annotation.StartX + (handle == "P1" ? deltaX : 0);
        var startY = annotation.StartY + (handle == "P1" ? deltaY : 0);
        var endX = annotation.EndX + (handle == "P2" ? deltaX : 0);
        var endY = annotation.EndY + (handle == "P2" ? deltaY : 0);
        var updated = new PdfAnnotationInfo { Id = annotation.Id, Subtype = annotation.Subtype, PageNumber = annotation.PageNumber, Type = annotation.Type, Title = annotation.Title, Contents = annotation.Contents, X = Math.Min(startX, endX), Y = Math.Min(startY, endY), Width = Math.Abs(endX - startX), Height = Math.Abs(endY - startY), StartX = startX, StartY = startY, EndX = endX, EndY = endY, Points = annotation.Points, StrokeColor = annotation.StrokeColor, StrokeWidth = annotation.StrokeWidth, FontSize = annotation.FontSize };
        QueueAnnotationUpdate(updated);
        SelectedPdfAnnotation = updated;
        RefreshCurrentPageAnnotations();
        StatusMessage = "直线位置修改已暂存，请保存标注写入 PDF";
        NotifyAnnotationSaveChanged();
    }

    private void ApplySelectedAnnotationStyle()
    {
        var annotation = SelectedPdfAnnotation;
        if (annotation is null || !HasDocument || IsBusy)
        {
            return;
        }

        var updated = new PdfAnnotationInfo
        {
            Id = annotation.Id, Subtype = annotation.Subtype, PageNumber = annotation.PageNumber,
            Type = annotation.Type, Title = annotation.Title, Contents = annotation.Contents,
            X = annotation.X, Y = annotation.Y, Width = annotation.Width, Height = annotation.Height,
            StartX = annotation.StartX, StartY = annotation.StartY, EndX = annotation.EndX, EndY = annotation.EndY,
            Points = annotation.Points, StrokeColor = AnnotationColorHex, StrokeWidth = (double)AnnotationStrokeWidth,
            FontSize = annotation.Type == PdfAnnotationType.Text ? (double)AnnotationTextFontSize : annotation.FontSize,
        };
        QueueAnnotationUpdate(updated);
        SelectedPdfAnnotation = updated;
        RefreshCurrentPageAnnotations();
        StatusMessage = "标注样式修改已暂存，请保存标注写入 PDF";
        NotifyAnnotationSaveChanged();
    }

    private void QueueAnnotationUpdate(PdfAnnotationInfo updated)
    {
        var pendingAdd = _pendingAnnotationChanges.FirstOrDefault(change => change.Kind == PdfAnnotationChangeKind.Add
            && change.Annotation.Id == updated.Id);
        if (pendingAdd is not null)
        {
            _pendingAnnotationChanges.Remove(pendingAdd);
            _pendingAnnotationChanges.Add(new PdfAnnotationChange(PdfAnnotationChangeKind.Add, updated));
            return;
        }

        _pendingAnnotationChanges.RemoveAll(change => change.Kind == PdfAnnotationChangeKind.Update
            && change.Annotation.Id == updated.Id);
        _pendingAnnotationChanges.Add(new PdfAnnotationChange(PdfAnnotationChangeKind.Update, updated));
    }

    public void SelectPdfAnnotation(PdfAnnotationInfo annotation)
    {
        SelectedPdfAnnotation = annotation;
        if (annotation.Type == PdfAnnotationType.Text)
        {
            _isSynchronizingTextFontSize = true;
            AnnotationTextFontSize = (decimal)annotation.FontSize;
            _isSynchronizingTextFontSize = false;
        }
        StatusMessage = string.IsNullOrWhiteSpace(annotation.Subtype)
            ? "已选中 PDF 标注，按 Delete 删除"
            : $"已选中 PDF 标注: {annotation.Subtype}，按 Delete 删除";
    }

    public void ClearPdfAnnotationSelection()
    {
        SelectedPdfAnnotation = null;
    }

    public Task EraseAnnotationsAsync(IReadOnlyList<PdfAnnotationPoint> eraserPath)
    {
        if (!HasDocument || IsBusy || eraserPath.Count == 0)
        {
            return Task.CompletedTask;
        }

        var path = eraserPath
            .Select(point => new PdfAnnotationPoint(point.X / _zoom, point.Y / _zoom))
            .ToArray();
        var erased = 0;
        foreach (var annotation in CurrentPageAnnotations.ToList())
        {
            if (AnnotationIntersectsEraser(annotation, path))
            {
                erased += QueueAnnotationDeletion(annotation) ? 1 : 0;
            }
        }

        if (erased > 0)
        {
            RefreshCurrentPageAnnotations();
            StatusMessage = $"已暂存擦除 {erased} 个标注，请保存标注写入 PDF";
            NotifyAnnotationSaveChanged();
        }

        return Task.CompletedTask;
    }

    private bool QueueAnnotationDeletion(PdfAnnotationInfo annotation)
    {
        var pendingAdd = _pendingAnnotationChanges.FirstOrDefault(change =>
            change.Kind == PdfAnnotationChangeKind.Add
            && change.Annotation.Id == annotation.Id);
        if (pendingAdd is not null)
        {
            _pendingAnnotationChanges.Remove(pendingAdd);
            _deletedAnnotationHistory.Push(new DeletedAnnotationOperation(annotation, true));
            OnPropertyChanged(nameof(CanUndoAnnotationDelete));
            return true;
        }

        if (_pendingAnnotationChanges.Any(change =>
                change.Kind == PdfAnnotationChangeKind.Delete
                && change.Annotation.Id == annotation.Id))
        {
            return false;
        }

        _pendingAnnotationChanges.Add(new PdfAnnotationChange(
            PdfAnnotationChangeKind.Delete,
            annotation));
        _deletedAnnotationHistory.Push(new DeletedAnnotationOperation(annotation, false));
        OnPropertyChanged(nameof(CanUndoAnnotationDelete));
        return true;
    }

    [RelayCommand]
    private void UndoAnnotationDelete()
    {
        if (!CanUndoAnnotationDelete)
        {
            return;
        }

        var operation = _deletedAnnotationHistory.Pop();
        if (operation.WasPendingAdd)
        {
            _pendingAnnotationChanges.Add(new PdfAnnotationChange(PdfAnnotationChangeKind.Add, operation.Annotation));
        }
        else
        {
            _pendingAnnotationChanges.RemoveAll(change => change.Kind == PdfAnnotationChangeKind.Delete
                && change.Annotation.Id == operation.Annotation.Id);
        }

        SelectedPdfAnnotation = operation.Annotation;
        RefreshCurrentPageAnnotations();
        StatusMessage = "已撤回删除标注";
        NotifyAnnotationSaveChanged();
        OnPropertyChanged(nameof(CanUndoAnnotationDelete));
    }

    private static bool AnnotationIntersectsEraser(
        PdfAnnotationInfo annotation,
        IReadOnlyList<PdfAnnotationPoint> path)
    {
        const double tolerance = 7;
        if (annotation.Type is PdfAnnotationType.Line or PdfAnnotationType.Freehand)
        {
            var points = annotation.Type == PdfAnnotationType.Freehand
                ? annotation.Points
                : new[]
                {
                    new PdfAnnotationPoint(annotation.StartX, annotation.StartY),
                    new PdfAnnotationPoint(annotation.EndX, annotation.EndY),
                };
            if (path.Count == 1)
            {
                return Enumerable.Range(1, Math.Max(0, points.Count - 1))
                    .Any(segmentIndex => DistanceToSegment(
                        path[0], points[segmentIndex - 1], points[segmentIndex]) <= tolerance);
            }

            for (var segmentIndex = 1; segmentIndex < points.Count; segmentIndex++)
            {
                for (var pathIndex = 1; pathIndex < path.Count; pathIndex++)
                {
                    if (SegmentsNear(points[segmentIndex - 1], points[segmentIndex],
                            path[pathIndex - 1], path[pathIndex], tolerance))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        return path.Any(point => point.X >= annotation.X - tolerance
            && point.X <= annotation.X + annotation.Width + tolerance
            && point.Y >= annotation.Y - tolerance
            && point.Y <= annotation.Y + annotation.Height + tolerance);
    }

    private static bool SegmentsNear(
        PdfAnnotationPoint firstStart,
        PdfAnnotationPoint firstEnd,
        PdfAnnotationPoint secondStart,
        PdfAnnotationPoint secondEnd,
        double tolerance)
    {
        return SegmentsIntersect(firstStart, firstEnd, secondStart, secondEnd)
            || DistanceToSegment(firstStart, secondStart, secondEnd) <= tolerance
            || DistanceToSegment(firstEnd, secondStart, secondEnd) <= tolerance
            || DistanceToSegment(secondStart, firstStart, firstEnd) <= tolerance
            || DistanceToSegment(secondEnd, firstStart, firstEnd) <= tolerance;
    }

    private static bool SegmentsIntersect(
        PdfAnnotationPoint firstStart,
        PdfAnnotationPoint firstEnd,
        PdfAnnotationPoint secondStart,
        PdfAnnotationPoint secondEnd)
    {
        static double Cross(PdfAnnotationPoint a, PdfAnnotationPoint b, PdfAnnotationPoint c)
        {
            return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        }

        var first = Cross(firstStart, firstEnd, secondStart);
        var second = Cross(firstStart, firstEnd, secondEnd);
        var third = Cross(secondStart, secondEnd, firstStart);
        var fourth = Cross(secondStart, secondEnd, firstEnd);
        return ((first > 0 && second < 0) || (first < 0 && second > 0))
            && ((third > 0 && fourth < 0) || (third < 0 && fourth > 0));
    }

    private static double DistanceToSegment(
        PdfAnnotationPoint point,
        PdfAnnotationPoint segmentStart,
        PdfAnnotationPoint segmentEnd)
    {
        var dx = segmentEnd.X - segmentStart.X;
        var dy = segmentEnd.Y - segmentStart.Y;
        if (dx == 0 && dy == 0)
        {
            return Math.Sqrt(Math.Pow(point.X - segmentStart.X, 2)
                + Math.Pow(point.Y - segmentStart.Y, 2));
        }

        var projection = ((point.X - segmentStart.X) * dx + (point.Y - segmentStart.Y) * dy)
            / (dx * dx + dy * dy);
        projection = Math.Clamp(projection, 0, 1);
        var nearestX = segmentStart.X + projection * dx;
        var nearestY = segmentStart.Y + projection * dy;
        return Math.Sqrt(Math.Pow(point.X - nearestX, 2) + Math.Pow(point.Y - nearestY, 2));
    }

    private void QueueAnnotation(PdfAnnotationInfo annotation)
    {
        _pendingAnnotationChanges.Add(new PdfAnnotationChange(
            PdfAnnotationChangeKind.Add,
            annotation));
        RefreshCurrentPageAnnotations();
        StatusMessage = "标注已暂存，当前预览已更新；请保存标注写入 PDF";
        NotifyAnnotationSaveChanged();
    }

    [RelayCommand]
    public async Task SaveAnnotationsAsync()
    {
        if (!CanSaveAnnotations)
        {
            return;
        }

        var documentPath = DocumentPath;
        var pageIndex = _currentPage;
        var changes = _pendingAnnotationChanges.ToList();
        try
        {
            IsBusy = true;
            StatusMessage = "正在保存标注...";
            CloseRenderedDocument();
            await _annotationService.SaveIncrementalAsync(documentPath, changes);
            _pendingAnnotationChanges.Clear();
            _deletedAnnotationHistory.Clear();
            OnPropertyChanged(nameof(CanUndoAnnotationDelete));
            _annotationCache.Remove(_documentId);
            await ReopenRenderedDocumentAsync(documentPath, pageIndex);
            InitializeReadingPages();
            RefreshPagePreviewCacheMetadata();
            StatusMessage = "标注已保存到 PDF";
        }
        catch (Exception exception)
        {
            try
            {
                await ReopenRenderedDocumentAsync(documentPath, pageIndex);
            }
            catch
            {
            }

            StatusMessage = $"保存标注失败: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
            NotifyNavigationChanged();
            NotifyAnnotationSaveChanged();
        }
    }

    public void DiscardPendingAnnotations()
    {
        if (_pendingAnnotationChanges.Count == 0)
        {
            return;
        }

        _pendingAnnotationChanges.Clear();
        _deletedAnnotationHistory.Clear();
        OnPropertyChanged(nameof(CanUndoAnnotationDelete));
        RefreshCurrentPageAnnotations();
        StatusMessage = "已放弃未保存的标注变更";
        NotifyAnnotationSaveChanged();
    }

    private void NotifyAnnotationSaveChanged()
    {
        OnPropertyChanged(nameof(HasPendingAnnotationChanges));
        OnPropertyChanged(nameof(CanSaveAnnotations));
    }

    public async Task SavePdfAsync()
    {
        if (!CanModifyDocument || IsBusy)
        {
            return;
        }

        if (HasPendingAnnotationChanges)
        {
            StatusMessage = "请先保存或放弃缓存中的标注变更";
            return;
        }

        var documentPath = DocumentPath;
        var pageIndex = _currentPage;
        try
        {
            IsBusy = true;
            StatusMessage = "正在保存 PDF...";
            CloseRenderedDocument();
            await Task.Run(() => _pdfEditingService.SaveCopy(documentPath, documentPath));
            await ReopenRenderedDocumentAsync(documentPath, pageIndex);
            RefreshPagePreviewCacheMetadata();
            StatusMessage = "PDF 已保存";
        }
        catch (Exception exception)
        {
            try
            {
                await ReopenRenderedDocumentAsync(documentPath, pageIndex);
            }
            catch
            {
            }

            StatusMessage = $"保存 PDF 失败: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
            NotifyNavigationChanged();
        }
    }

    public async Task SavePdfAsAsync(string outputPath)
    {
        if (!CanModifyDocument || IsBusy || string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        if (HasPendingAnnotationChanges)
        {
            StatusMessage = "请先保存或放弃缓存中的标注变更，再另存为 PDF";
            return;
        }

        var sourcePath = DocumentPath;
        var destinationPath = Path.GetFullPath(outputPath);
        var pageIndex = _currentPage;
        try
        {
            IsBusy = true;
            StatusMessage = "正在另存为 PDF...";
            CloseRenderedDocument();
            await Task.Run(() => _pdfEditingService.SaveCopy(sourcePath, destinationPath));
            await _documentRepository.RebindAsync(_documentId, destinationPath);

            DocumentPath = destinationPath;
            DocumentTitle = Path.GetFileName(destinationPath);
            if (SelectedDocument is not null)
            {
                SelectedDocument.FilePath = destinationPath;
                SelectedDocument.Title = DocumentTitle;
                SelectedDocument.RefreshPathStatus();
            }

            await ReopenRenderedDocumentAsync(destinationPath, pageIndex);
            RefreshPagePreviewCacheMetadata();
            StatusMessage = "PDF 已另存为并切换到新文件";
        }
        catch (Exception exception)
        {
            try
            {
                await ReopenRenderedDocumentAsync(sourcePath, pageIndex);
            }
            catch
            {
            }

            StatusMessage = $"另存为 PDF 失败: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
            NotifyNavigationChanged();
        }
    }

    public async Task ExportPortablePdfAsync(string outputPath)
    {
        if (!HasDocument || IsBusy || HasPendingAnnotationChanges || string.IsNullOrWhiteSpace(outputPath))
        {
            StatusMessage = HasPendingAnnotationChanges ? "请先保存或放弃缓存中的标注变更" : StatusMessage;
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "正在导出包含书签和音频的 PDF...";
            await _annotationService.ExportWithMetadataAsync(DocumentPath, outputPath, Bookmarks.ToList(), OcrHistory.ToList());
            StatusMessage = "已导出可携带书签、音频附件和 OCR 元数据的 PDF";
        }
        catch (Exception exception)
        {
            StatusMessage = $"导出 PDF 失败: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ExportAcrobatRichMediaPdfAsync(string outputPath)
    {
        if (!HasDocument || IsBusy || HasPendingAnnotationChanges || string.IsNullOrWhiteSpace(outputPath))
        {
            StatusMessage = HasPendingAnnotationChanges ? "请先保存或放弃缓存中的标注变更" : StatusMessage;
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "正在导出 Adobe Acrobat 富媒体 PDF...";
            await _annotationService.ExportAcrobatRichMediaAsync(DocumentPath, outputPath, Bookmarks.ToList(), OcrHistory.ToList());
            StatusMessage = "已导出 Adobe Acrobat 富媒体 PDF";
        }
        catch (Exception exception)
        {
            StatusMessage = $"Adobe Acrobat 富媒体导出失败: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void CloseRenderedDocument()
    {
        _readingCancellation?.Cancel();
        _audioPlaybackService.Stop();
        ClearPrefetchedPageImages();
        ClearReadingPages();
        _pdfService.Close();
        PageImage?.Dispose();
        PageImage = null;
    }

    private async Task ReopenRenderedDocumentAsync(string path, int pageIndex)
    {
        await _pdfService.OpenAsync(path);
        await ShowPageAsync(Math.Clamp(pageIndex, 0, Math.Max(0, _pdfService.PageCount - 1)));
    }

    [RelayCommand]
    private async Task GoPreviousAsync()
    {
        if (CanGoPrevious)
        {
            if (IsContinuousReadingMode)
            {
                NavigateContinuousReadingPage(_currentPage);
                return;
            }

            await ShowPageAsync(_currentPage - 1);
        }
    }

    [RelayCommand]
    private async Task GoNextAsync()
    {
        if (CanGoNext)
        {
            if (IsContinuousReadingMode)
            {
                NavigateContinuousReadingPage(_currentPage + 2);
                return;
            }

            await ShowPageAsync(_currentPage + 1);
        }
    }

    [RelayCommand]
    private async Task JumpToPageAsync()
    {
        if (!HasDocument || IsBusy)
        {
            return;
        }

        if (!int.TryParse(PageNumberInput, out var pageNumber))
        {
            StatusMessage = "请输入有效的页码";
            PageNumberInput = CurrentPageNumber.ToString();
            return;
        }

        if (pageNumber < 1 || pageNumber > _pdfService.PageCount)
        {
            StatusMessage = $"页码必须在 1 到 {_pdfService.PageCount} 之间";
            PageNumberInput = Math.Clamp(pageNumber, 1, Math.Max(1, _pdfService.PageCount)).ToString();
            return;
        }

        if (IsContinuousReadingMode)
        {
            NavigateContinuousReadingPage(pageNumber);
            return;
        }

        await ShowPageAsync(pageNumber - 1);
    }

    public async Task GoToPageAsync(int pageNumber)
    {
        if (!HasDocument || IsBusy)
        {
            return;
        }

        if (pageNumber < 1 || pageNumber > _pdfService.PageCount)
        {
            StatusMessage = $"页码必须在 1 到 {_pdfService.PageCount} 之间";
            return;
        }

        if (IsContinuousReadingMode)
        {
            NavigateContinuousReadingPage(pageNumber);
            return;
        }

        await ShowPageAsync(pageNumber - 1);
    }

    public async Task GoToOcrRecordAsync(OcrRecord record)
    {
        if (!HasDocument || record.PdfDocumentId != _documentId)
        {
            return;
        }

        SelectedOcrRecord = record;
        SelectedOcrHistoryRecord = record;
        IsCurrentPageOcrVisible = true;
        if (IsContinuousReadingMode)
        {
            NavigateContinuousReadingPage(record.PageNumber);
        }
        else
        {
            await ShowPageAsync(record.PageNumber - 1);
        }
    }

    public void SetCurrentReadingPage(int pageNumber)
    {
        if (!IsContinuousReadingMode || pageNumber < 1 || pageNumber > _pdfService.PageCount
            || pageNumber == _currentPage + 1)
        {
            return;
        }

        _currentPage = pageNumber - 1;
        PageIndicator = $"{pageNumber} / {_pdfService.PageCount}";
        PageNumberInput = pageNumber.ToString();
        SelectedPagePreview = PagePreviews.FirstOrDefault(preview => preview.PageNumber == pageNumber);
        RefreshCurrentPageOcr();
        NotifyNavigationChanged();
        OnPropertyChanged(nameof(CurrentPageNumber));
    }

    private void NavigateContinuousReadingPage(int pageNumber)
    {
        if (!IsContinuousReadingMode || pageNumber < 1 || pageNumber > _pdfService.PageCount)
        {
            return;
        }

        SetCurrentReadingPage(pageNumber);
        ContinuousReadingPageRequested?.Invoke(pageNumber);
    }

    public void ResumeContinuousReadingAtPage(int pageNumber)
    {
        if (!HasDocument || IsAnnotationMode || CanCapture
            || pageNumber < 1 || pageNumber > _pdfService.PageCount)
        {
            return;
        }

        IsCurrentPageOcrVisible = false;
        _currentPage = pageNumber - 1;
        PageIndicator = $"{pageNumber} / {_pdfService.PageCount}";
        PageNumberInput = pageNumber.ToString();
        SelectedPagePreview = PagePreviews.FirstOrDefault(preview => preview.PageNumber == pageNumber);
        RefreshCurrentPageOcr();
        NotifyNavigationChanged();
        OnPropertyChanged(nameof(CurrentPageNumber));
        ContinuousReadingPageRequested?.Invoke(pageNumber);
    }

    [RelayCommand]
    private async Task ZoomInAsync()
    {
        await ChangeZoomAsync(0.25);
    }

    [RelayCommand]
    private async Task ZoomOutAsync()
    {
        await ChangeZoomAsync(-0.25);
    }

    public async Task ChangeZoomAsync(double delta)
    {
        if (!HasDocument)
        {
            return;
        }

        await _zoomChangeGate.WaitAsync();
        try
        {
            await SetZoomCoreAsync(_zoom + delta);
        }
        finally
        {
            _zoomChangeGate.Release();
        }
    }

    public async Task ApplyZoomInputAsync()
    {
        if (!HasDocument)
        {
            return;
        }

        var input = ZoomIndicator.Trim().TrimEnd('%').Trim();
        if (!double.TryParse(input, out var percent))
        {
            ZoomIndicator = $"{_zoom:P0}";
            StatusMessage = "请输入有效的缩放百分比";
            return;
        }

        await SetZoomAsync(percent / 100);
    }

    private async Task SetZoomAsync(double zoom)
    {
        if (!HasDocument)
        {
            return;
        }

        await _zoomChangeGate.WaitAsync();
        try
        {
            if (!HasDocument)
            {
                return;
            }

            await SetZoomCoreAsync(zoom);
        }
        finally
        {
            _zoomChangeGate.Release();
        }
    }

    private async Task SetZoomCoreAsync(double zoom)
    {
        var normalizedZoom = Math.Clamp(Math.Round(zoom, 2), 0.5, 3.0);
        if (Math.Abs(_zoom - normalizedZoom) < 0.001)
        {
            ZoomIndicator = $"{_zoom:P0}";
            return;
        }

        _zoom = normalizedZoom;
        ZoomIndicator = $"{_zoom:P0}";
        OnPropertyChanged(nameof(CurrentZoom));
        ClearPrefetchedPageImages();
        await ShowPageAsync(_currentPage);
        InitializeReadingPages();
    }

    [RelayCommand]
    private void StartOcr()
    {
        if (IsOcrEnabled || IsDocumentReadOnly)
        {
            return;
        }

        IsOcrEnabled = true;
        StatusMessage = "OCR 已启动，请选择截取模式";
        NotifyCaptureChanged();
    }

    [RelayCommand]
    private async Task CaptureOnce()
    {
        if (!CanSelectCaptureMode)
        {
            return;
        }

        await ShowPageAsync(_currentPage);
        _captureOnce = true;
        IsContinuousCapture = false;
        StatusMessage = "请在页面上拖动选择一次 OCR 区域";
        NotifyCaptureChanged();
    }

    [RelayCommand]
    private async Task ToggleContinuousCapture()
    {
        if (!CanSelectCaptureMode && !IsContinuousCapture)
        {
            return;
        }

        var enable = !IsContinuousCapture;
        if (enable)
        {
            await ShowPageAsync(_currentPage);
        }

        _captureOnce = false;
        IsContinuousCapture = enable;
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
            var document = await _documentRepository.GetOrCreateAsync(
                filePath,
                Path.GetFileName(filePath));
            document = AddOrUpdateDocument(document);
            await OpenDocumentCoreAsync(document);
        }
        catch (Exception exception)
        {
            HasDocument = false;
            StatusMessage = $"打开失败: {exception.Message}";
        }
    }

    public async Task<IReadOnlyList<PdfDocument>> AddPdfFilesAsync(
        IEnumerable<string> filePaths)
    {
        var imported = new List<PdfDocument>();
        var duplicateCount = 0;
        foreach (var filePath in filePaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (!File.Exists(filePath))
            {
                continue;
            }

            var normalizedPath = Path.GetFullPath(filePath);
            if (AvailableDocuments.Concat(ArchivedDocuments).Any(document =>
                    string.Equals(document.FilePath, normalizedPath, StringComparison.OrdinalIgnoreCase)))
            {
                duplicateCount++;
                continue;
            }

            var document = await _documentRepository.GetOrCreateAsync(
                normalizedPath,
                Path.GetFileName(normalizedPath));
            document = AddOrUpdateAvailableDocument(document);
            imported.Add(document);
        }

        if (imported.Count > 0)
        {
            StatusMessage = duplicateCount > 0
                ? $"已添加 {imported.Count} 个 PDF 文件记录，跳过 {duplicateCount} 个重复路径"
                : $"已添加 {imported.Count} 个 PDF 文件记录，请选择后导入工作区";
        }
        else if (duplicateCount > 0)
        {
            StatusMessage = "所选 PDF 路径已存在，请直接选择已有文件记录";
        }

        return imported;
    }

    public async Task ImportDocumentsToWorkspaceAsync(
        IEnumerable<PdfDocument> documents)
    {
        var imported = new List<PdfDocument>();
        foreach (var document in documents)
        {
            var available = AvailableDocuments.Concat(ArchivedDocuments).SingleOrDefault(item => item.Id == document.Id);
            if (available is null)
            {
                continue;
            }

            if (Documents.All(item => item.Id != available.Id))
            {
                Documents.Add(available);
            }

            imported.Add(available);
        }

        if (imported.Count == 0)
        {
            return;
        }

        SelectedDocument = imported[0];
        await OpenStoredDocumentAsync(imported[0]);
        StatusMessage = $"已导入 {imported.Count} 个 PDF 到工作区";
    }

    public async Task OpenStoredDocumentAsync(PdfDocument document)
    {
        document.RefreshPathStatus();
        if (document.IsMissing)
        {
            StatusMessage = "PDF 文件不存在，请选择重新绑定、暂时搁置或删除";
            return;
        }

        await OpenDocumentCoreAsync(document);
    }

    public async Task RebindDocumentAsync(PdfDocument document, string newFilePath)
    {
        if (!File.Exists(newFilePath))
        {
            StatusMessage = "重新绑定失败：选择的文件不存在";
            return;
        }

        try
        {
            DeletePagePreviewCache(document.Id);
            _annotationCache.Remove(document.Id);
            await _documentRepository.RebindAsync(document.Id, newFilePath);
            document.FilePath = Path.GetFullPath(newFilePath);
            document.Title = Path.GetFileName(document.FilePath);
            document.RefreshPathStatus();
            await OpenDocumentCoreAsync(document);
        }
        catch (Exception exception)
        {
            StatusMessage = $"重新绑定失败: {exception.Message}";
        }
    }

    public async Task DeleteDocumentAsync(PdfDocument document)
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            DeletePagePreviewCache(document.Id);
            _annotationCache.Remove(document.Id);
            var isCurrentDocument = document.Id == _documentId;
            if (isCurrentDocument)
            {
                _audioPlaybackService.Stop();
                _pdfService.Close();
                PageImage?.Dispose();
                PageImage = null;
                HasDocument = false;
                _documentId = Guid.Empty;
                DocumentTitle = "未打开文档";
                DocumentPath = "从文件菜单打开一个 PDF 文档";
                PageIndicator = "0 / 0";
                PageNumberInput = "1";
                OnPropertyChanged(nameof(DocumentPageCount));
                ClearPagePreviews();
                OcrHistory.Clear();
                Bookmarks.Clear();
                _deletedBookmarkHistory.Clear();
                OnPropertyChanged(nameof(CanUndoBookmarkDelete));
            }

            DeleteResources(await _documentRepository.DeleteAsync(document.Id));
            Documents.Remove(document);
            AvailableDocuments.Remove(document);
            ArchivedDocuments.Remove(document);
            if (ReferenceEquals(SelectedDocument, document))
            {
                SelectedDocument = null;
            }

            StatusMessage = "PDF 对象及其关联资源已删除";
        }
        catch (Exception exception)
        {
            StatusMessage = $"删除 PDF 对象失败: {exception.Message}";
        }
    }

    public async Task SetDocumentArchivedAsync(PdfDocument document, bool archived)
    {
        if (IsBusy || document.IsArchived == archived) return;
        try
        {
            await _documentRepository.SetArchivedAsync(document.Id, archived);
            document.IsArchived = archived;
            AvailableDocuments.Remove(document);
            ArchivedDocuments.Remove(document);
            (archived ? ArchivedDocuments : AvailableDocuments).Insert(0, document);
            if (document.Id == _documentId)
            {
                if (archived)
                {
                    CancelCaptureMode();
                    IsAnnotationMode = false;
                }
                NotifyDocumentReadOnlyChanged();
            }
            StatusMessage = archived ? "PDF 已归档，当前工作区为只读模式" : "PDF 已恢复到正在使用，可继续编辑";
        }
        catch (Exception exception)
        {
            StatusMessage = $"更新归档状态失败: {exception.Message}";
        }
    }

    private async Task OpenDocumentCoreAsync(PdfDocument document)
    {
        await _documentOpenGate.WaitAsync();
        try
        {
            if (HasDocument && _documentId == document.Id && _pdfService.IsOpen)
            {
                SelectedDocument = document;
                StatusMessage = "文档已打开";
                return;
            }

            IsBusy = true;
            StatusMessage = "正在打开文档...";
            ReleaseCurrentDocumentResources();
            await _pdfService.OpenAsync(document.FilePath);
            await _documentRepository.MarkOpenedAsync(document.Id);

            _documentId = document.Id;
            DocumentTitle = document.Title;
            DocumentPath = document.FilePath;
            HasDocument = true;
            _currentPage = 0;
            _zoom = 1.25;
            ClearPagePreviews();
            await ShowPageAsync(_currentPage);
            InitializeReadingPages();
            await LoadDocumentDataAsync();
            await LoadPagePreviewsAsync();
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
            _documentOpenGate.Release();
        }
    }

    private void ReleaseCurrentDocumentResources()
    {
        _readingCancellation?.Cancel();
        _audioPlaybackService.Stop();
        IsAnnotationMode = false;
        ClearPrefetchedPageImages();
        ClearReadingPages();
        _pdfService.Close();
        PageImage?.Dispose();
        PageImage = null;
        ClearPagePreviews();
    }

    private PdfDocument AddOrUpdateDocument(PdfDocument document)
    {
        var existing = AddOrUpdateAvailableDocument(document);
        if (Documents.All(item => item.Id != existing.Id))
        {
            Documents.Insert(0, existing);
        }

        return existing;
    }

    private PdfDocument AddOrUpdateAvailableDocument(PdfDocument document)
    {
        var existing = AvailableDocuments.Concat(ArchivedDocuments).SingleOrDefault(item => item.Id == document.Id);
        if (existing is null)
        {
            document.RefreshPathStatus();
            (document.IsArchived ? ArchivedDocuments : AvailableDocuments).Insert(0, document);
            return document;
        }

        existing.FilePath = document.FilePath;
        existing.Title = document.Title;
        existing.RefreshPathStatus();
        return existing;
    }

    private async Task LoadDocumentDataAsync()
    {
        LoadBookmarkExpansionCache();
        _pendingAnnotationChanges.Clear();
        _deletedAnnotationHistory.Clear();
        OnPropertyChanged(nameof(CanUndoAnnotationDelete));
        NotifyAnnotationSaveChanged();
        OcrHistory.Clear();
        OcrProcessingQueue.Clear();
        Bookmarks.Clear();
        _deletedBookmarkHistory.Clear();
        OnPropertyChanged(nameof(CanUndoBookmarkDelete));
        CurrentPageOcrRecords.Clear();
        CurrentPageAnnotations.Clear();
        SelectedTreeItem = null;
        SelectedBookmark = null;
        SelectedOcrHistoryRecord = null;
        SelectedOcrRecord = null;

        var ocrRecords = await _ocrRepository.GetForDocumentAsync(_documentId);
        foreach (var record in ocrRecords)
        {
            record.IsPersisted = true;
            record.RefreshAudioStatus();
            OcrHistory.Add(record);
        }

        var bookmarks = await _bookmarkRepository.GetForDocumentAsync(_documentId);
        if (bookmarks.Count == 0)
        {
            var restored = await RestorePdfReaderMetadataAsync();
            if (!restored)
            {
                await ImportPdfOutlineAsync();
            }
            bookmarks = await _bookmarkRepository.GetForDocumentAsync(_documentId);
            if (restored)
            {
                ocrRecords = await _ocrRepository.GetForDocumentAsync(_documentId);
                OcrHistory.Clear();
                foreach (var record in ocrRecords)
                {
                    record.IsPersisted = true;
                    record.RefreshAudioStatus();
                    OcrHistory.Add(record);
                }
            }
        }
        var bookmarkMap = bookmarks.ToDictionary(bookmark => bookmark.Id);
        foreach (var bookmark in bookmarks)
        {
            bookmark.IsPersisted = true;
            bookmark.Children.Clear();
        }

        foreach (var bookmark in bookmarks)
        {
            if (bookmark.ParentId is Guid parentId && bookmarkMap.TryGetValue(parentId, out var parent))
            {
                bookmark.Parent = parent;
                parent.Children.Add(bookmark);
            }
            else
            {
                Bookmarks.Add(bookmark);
            }
        }

        RefreshBookmarkDisplayTree();
        RestoreBookmarkExpansionState();
        RefreshOcrProcessingQueue();
        RefreshCurrentPageOcr();
        RefreshReadingPageOcr();
    }

    private async Task<bool> RestorePdfReaderMetadataAsync()
    {
        try
        {
            var metadata = await _annotationService.RestoreMetadataAsync(DocumentPath, AudioDirectory);
            if (metadata is null || (metadata.Bookmarks.Count == 0 && metadata.OcrRecords.Count == 0)) return false;

            var now = DateTime.UtcNow;
            var bookmarkIds = metadata.Bookmarks
                .Where(bookmark => bookmark.Id != Guid.Empty)
                .ToDictionary(bookmark => bookmark.Id, _ => Guid.NewGuid());
            foreach (var item in metadata.Bookmarks)
            {
                var newId = item.Id != Guid.Empty && bookmarkIds.TryGetValue(item.Id, out var mappedId)
                    ? mappedId
                    : Guid.NewGuid();
                Guid? parentId = item.ParentId is Guid parentIdValue && bookmarkIds.TryGetValue(parentIdValue, out var mappedParentId)
                    ? mappedParentId : null;
                var bookmark = new Bookmark
                {
                    Id = newId,
                    PdfDocumentId = _documentId,
                    ParentId = parentId,
                    PageNumber = Math.Max(1, item.Page), Title = item.Title, SortOrder = item.SortOrder,
                    CreatedAtUtc = now, UpdatedAtUtc = now,
                };
                await _bookmarkRepository.SaveAsync(bookmark);
            }

            Bookmark? recoveryBookmark = null;
            if (metadata.OcrRecords.Any(record => record.BookmarkId is Guid bookmarkId && !bookmarkIds.ContainsKey(bookmarkId)))
            {
                recoveryBookmark = new Bookmark
                {
                    Id = Guid.NewGuid(), PdfDocumentId = _documentId, ParentId = null,
                    PageNumber = Math.Max(1, metadata.OcrRecords.First().PageNumber), Title = "恢复的 OCR",
                    SortOrder = metadata.Bookmarks.Where(bookmark => bookmark.ParentId is null).Select(bookmark => bookmark.SortOrder).DefaultIfEmpty(-1).Max() + 1,
                    CreatedAtUtc = now, UpdatedAtUtc = now,
                };
                await _bookmarkRepository.SaveAsync(recoveryBookmark);
            }
            foreach (var item in metadata.OcrRecords)
            {
                var record = new OcrRecord
                {
                    Id = Guid.NewGuid(), PdfDocumentId = _documentId,
                    BookmarkId = item.BookmarkId is Guid bookmarkId && bookmarkIds.TryGetValue(bookmarkId, out var mappedBookmarkId)
                        ? mappedBookmarkId : recoveryBookmark?.Id,
                    PageNumber = Math.Max(1, item.PageNumber), X = item.X, Y = item.Y, Width = item.Width, Height = item.Height,
                    CaptureZoom = item.CaptureZoom > 0 ? item.CaptureZoom : 1, Title = item.Title, Text = item.Text,
                    CreatedAtUtc = item.CreatedAtUtc == default ? now : item.CreatedAtUtc,
                };
                await _ocrRepository.AddAsync(record);
                foreach (var audio in item.AudioFiles.Where(audio => File.Exists(audio.FilePath)))
                {
                    await _ocrRepository.AddAudioAsync(new TtsAudioRecord
                    {
                        Id = Guid.NewGuid(), OcrRecordId = record.Id,
                        FilePath = audio.FilePath, CreatedAtUtc = audio.CreatedAtUtc == default ? now : audio.CreatedAtUtc,
                    });
                }
            }
            StatusMessage = "已恢复 PDFReader 书签、OCR 记录和音频附件";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task ImportPdfOutlineAsync()
    {
        try
        {
            var outline = await _annotationService.GetOutlineAsync(DocumentPath);
            if (outline.Count == 0) return;

            var parents = new Dictionary<int, Bookmark>();
            var sortOrders = new Dictionary<string, int>();
            foreach (var entry in outline)
            {
                var level = Math.Max(1, entry.Level);
                while (level > 1 && !parents.ContainsKey(level - 1)) level--;
                foreach (var key in parents.Keys.Where(key => key >= level).ToList()) parents.Remove(key);
                var parent = level > 1 && parents.TryGetValue(level - 1, out var candidate) ? candidate : null;
                var parentId = parent?.Id;
                var sortKey = parentId?.ToString("N") ?? "root";
                sortOrders.TryGetValue(sortKey, out var sortOrder);
                var now = DateTime.UtcNow;
                var bookmark = new Bookmark
                {
                    PdfDocumentId = _documentId,
                    ParentId = parentId,
                    Parent = parent,
                    PageNumber = Math.Max(1, entry.PageNumber),
                    Title = string.IsNullOrWhiteSpace(entry.Title) ? "未命名书签" : entry.Title.Trim(),
                    SortOrder = sortOrder,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                };
                await _bookmarkRepository.SaveAsync(bookmark);
                sortOrders[sortKey] = sortOrder + 1;
                parents[level] = bookmark;
            }
        }
        catch
        {
            // An unreadable or unsupported Outline must not prevent the PDF itself from opening.
        }
    }

    private void RefreshBookmarkDisplayTree()
    {
        CacheBookmarkExpansionState();
        foreach (var bookmark in Bookmarks.SelectMany(EnumerateBookmarkTree))
        {
            bookmark.DisplayChildren.Clear();
            foreach (var record in OcrHistory
                         .Where(record => record.BookmarkId == bookmark.Id)
                         .OrderBy(record => record.CreatedAtUtc))
            {
                bookmark.DisplayChildren.Add(record);
            }

            foreach (var child in bookmark.Children)
            {
                bookmark.DisplayChildren.Add(child);
            }
        }

        RestoreBookmarkExpansionState();
    }

    private void CacheBookmarkExpansionState()
    {
        foreach (var bookmark in Bookmarks.SelectMany(EnumerateBookmarkTree))
        {
            if (bookmark.IsExpanded)
            {
                _expandedBookmarkIds.Add(bookmark.Id);
            }
            else
            {
                _expandedBookmarkIds.Remove(bookmark.Id);
            }
        }
    }

    private void RestoreBookmarkExpansionState()
    {
        foreach (var bookmark in Bookmarks.SelectMany(EnumerateBookmarkTree))
        {
            bookmark.IsExpanded = _expandedBookmarkIds.Contains(bookmark.Id);
        }
    }

    public void SaveBookmarkExpansionCache(IEnumerable<Guid> expandedBookmarkIds)
    {
        if (_documentId == Guid.Empty)
        {
            return;
        }

        _expandedBookmarkIds.Clear();
        _expandedBookmarkIds.UnionWith(expandedBookmarkIds);
        try
        {
            var path = GetBookmarkExpansionCachePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_expandedBookmarkIds));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public bool IsBookmarkExpansionCached(Guid bookmarkId) => _expandedBookmarkIds.Contains(bookmarkId);

    private void LoadBookmarkExpansionCache()
    {
        _expandedBookmarkIds.Clear();
        if (_documentId == Guid.Empty)
        {
            return;
        }

        try
        {
            var path = GetBookmarkExpansionCachePath();
            if (File.Exists(path))
            {
                _expandedBookmarkIds.UnionWith(JsonSerializer.Deserialize<Guid[]>(File.ReadAllText(path)) ?? Array.Empty<Guid>());
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
    }

    private string GetBookmarkExpansionCachePath() =>
        Path.Combine(ReaderSettings.GetPagePreviewCacheDirectory(_documentId), "bookmark-tree.json");

    private void RefreshCurrentPageOcr()
    {
        CurrentPageOcrRecords.Clear();
        foreach (var record in OcrHistory.Where(record => record.PageNumber == _currentPage + 1))
        {
            record.UpdateDisplayBounds(_zoom);
            CurrentPageOcrRecords.Add(record);
        }

        OnPropertyChanged(nameof(CanReadCurrentPage));
        UpdateReadingPageOcrVisibility();
    }

    private void RefreshOcrProcessingQueue()
    {
        OcrProcessingQueue.Clear();
        foreach (var record in OcrHistory.Where(record => record.BookmarkId is null && !record.IsExternalImport))
        {
            OcrProcessingQueue.Add(record);
        }

        OnPropertyChanged(nameof(CanClearOcr));
    }

    private void RefreshReadingPageOcr()
    {
        foreach (var page in ReadingPages)
        {
            page.OcrRecords.Clear();
        }

        foreach (var record in OcrHistory)
        {
            if (record.PageNumber < 1 || record.PageNumber > ReadingPages.Count)
            {
                continue;
            }

            record.UpdateDisplayBounds(_zoom);
            ReadingPages[record.PageNumber - 1].OcrRecords.Add(record);
        }

        UpdateReadingPageOcrVisibility();
    }

    private void UpdateReadingPageOcrVisibility()
    {
        foreach (var page in ReadingPages)
        {
            page.IsOcrVisible = IsCurrentPageOcrVisible && page.PageNumber == _currentPage + 1;
        }
    }

    private void RefreshCurrentPageAnnotations()
    {
        CurrentPageAnnotations.Clear();
        if (!HasDocument || !File.Exists(DocumentPath))
        {
            return;
        }

        try
        {
            var deletedIds = _pendingAnnotationChanges
                .Where(change => change.Kind == PdfAnnotationChangeKind.Delete)
                .Select(change => change.Annotation.Id)
                .ToHashSet(StringComparer.Ordinal);
            var updates = _pendingAnnotationChanges
                .Where(change => change.Kind == PdfAnnotationChangeKind.Update)
                .Select(change => change.Annotation)
                .ToDictionary(annotation => annotation.Id, StringComparer.Ordinal);
            var annotations = GetCachedAnnotations(_documentId, _currentPage)
                .Where(annotation => !deletedIds.Contains(annotation.Id))
                .Select(annotation => updates.TryGetValue(annotation.Id, out var updated) ? updated : annotation)
                .ToList();
            annotations.AddRange(_pendingAnnotationChanges
                .Where(change => change.Kind == PdfAnnotationChangeKind.Add
                    && change.Annotation.PageNumber == _currentPage + 1)
                .Select(change => change.Annotation));
            foreach (var annotation in annotations)
            {
                CurrentPageAnnotations.Add(annotation);
            }
        }
        catch (Exception exception)
        {
            StatusMessage = $"读取 PDF 标注失败: {exception.Message}";
        }
    }

    private IReadOnlyList<PdfAnnotationInfo> GetCachedAnnotations(Guid documentId, int pageIndex)
    {
        if (!_annotationCache.TryGetValue(documentId, out var pages))
        {
            pages = new Dictionary<int, IReadOnlyList<PdfAnnotationInfo>>();
            _annotationCache.Add(documentId, pages);
        }

        return pages.TryGetValue(pageIndex, out var annotations)
            ? annotations
            : Array.Empty<PdfAnnotationInfo>();
    }

    private async Task EnsureCurrentPageAnnotationsAsync()
    {
        if (!HasDocument || !File.Exists(DocumentPath))
        {
            return;
        }

        if (!_annotationCache.TryGetValue(_documentId, out var pages))
        {
            pages = new Dictionary<int, IReadOnlyList<PdfAnnotationInfo>>();
            _annotationCache.Add(_documentId, pages);
        }

        if (!pages.ContainsKey(_currentPage))
        {
            pages[_currentPage] = await _annotationService.GetAnnotationsAsync(DocumentPath, _currentPage);
        }

        RefreshCurrentPageAnnotations();
    }

    public async Task CreateBookmarkAsync(string? title, int pageNumber)
    {
        if (!CanModifyDocument || IsBusy)
        {
            return;
        }

        if (pageNumber < 1 || pageNumber > _pdfService.PageCount)
        {
            StatusMessage = "书签页码超出当前 PDF 范围";
            return;
        }

        var parent = SelectedBookmark;
        title = title?.Trim();

        var bookmark = new Bookmark
        {
            PdfDocumentId = _documentId,
            ParentId = parent?.Id,
            PageNumber = pageNumber,
            Title = string.IsNullOrWhiteSpace(title)
                ? $"第 {pageNumber} 页"
                : title,
            SortOrder = parent is null
                ? Bookmarks.Count
                : parent.Children.Count,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };
        bookmark.Parent = parent;
        if (parent is null)
        {
            Bookmarks.Add(bookmark);
        }
        else
        {
            parent.Children.Add(bookmark);
        }

        try
        {
            await SaveBookmarkAndAncestorsAsync(bookmark);
            SelectedBookmark = bookmark;
            var recordsToAttach = await AttachUnattachedOcrRecordsAsync(bookmark);
            RefreshBookmarkDisplayTree();
            StatusMessage = recordsToAttach == 0
                ? "书签已创建并自动保存"
                : $"书签已创建并自动保存，已挂载 {recordsToAttach} 条当前页 OCR";
            NotifyBookmarkChanged();
        }
        catch (Exception exception)
        {
            if (parent is not null)
            {
                parent.Children.Remove(bookmark);
            }
            else
            {
                Bookmarks.Remove(bookmark);
            }

            bookmark.Parent = null;
            bookmark.ParentId = null;
            StatusMessage = $"创建书签失败: {exception.Message}";
        }
    }

    public async Task ReattachCurrentPageOcrAsync(Bookmark? bookmark)
    {
        if (bookmark is null || !CanModifyDocument || IsBusy || bookmark.PdfDocumentId != _documentId)
        {
            return;
        }

        try
        {
            await SaveBookmarkAndAncestorsAsync(bookmark);
            SelectedBookmark = bookmark;
            var count = await AttachUnattachedOcrRecordsAsync(bookmark);
            RefreshBookmarkDisplayTree();
            StatusMessage = count == 0
                ? $"书签“{bookmark.Title}”所在页没有未挂载的 OCR 记录"
                : $"已将 {count} 条 OCR 记录挂载到书签“{bookmark.Title}”";
            NotifyBookmarkChanged();
        }
        catch (Exception exception)
        {
            StatusMessage = $"重新读取 OCR 记录失败: {exception.Message}";
        }
    }

    private async Task<int> AttachUnattachedOcrRecordsAsync(Bookmark bookmark)
    {
        var records = OcrHistory
            .Where(record => record.PageNumber == bookmark.PageNumber && record.BookmarkId is null)
            .ToList();
        foreach (var record in records)
        {
            await _ocrRepository.AttachToBookmarkAsync(record.Id, bookmark.Id);
            record.BookmarkId = bookmark.Id;
            OcrProcessingQueue.Remove(record);
            OnPropertyChanged(nameof(CanAttachOcr));
        }

        return records.Count;
    }

    public void FindCurrentPageBookmark()
    {
        if (!HasDocument)
        {
            StatusMessage = "请先打开一个 PDF 文档";
            return;
        }

        var bookmark = Bookmarks
            .SelectMany(EnumerateBookmarkTree)
            .FirstOrDefault(item => item.PageNumber == CurrentPageNumber);
        if (bookmark is null)
        {
            StatusMessage = $"第 {CurrentPageNumber} 页没有书签";
            return;
        }

        LocateBookmarkInTree(bookmark);
        StatusMessage = $"已定位到第 {CurrentPageNumber} 页书签：{bookmark.Title}";
    }

    public void FindSelectedBookmark()
    {
        if (SelectedBookmark is null)
        {
            StatusMessage = "请先选择一个书签";
            return;
        }

        LocateBookmarkInTree(SelectedBookmark);
        StatusMessage = $"已定位书签：{SelectedBookmark.Title}";
    }

    public void FindBookmarkByName(string title)
    {
        if (!HasDocument)
        {
            StatusMessage = "请先打开一个 PDF 文档";
            return;
        }

        var bookmark = Bookmarks
            .SelectMany(EnumerateBookmarkTree)
            .FirstOrDefault(item => item.Title.Contains(title.Trim(), StringComparison.OrdinalIgnoreCase));
        if (bookmark is null)
        {
            StatusMessage = $"未找到书签：{title}";
            return;
        }

        LocateBookmarkInTree(bookmark);
        StatusMessage = $"已定位书签“{bookmark.Title}”，第 {bookmark.PageNumber} 页";
    }

    private void LocateBookmarkInTree(Bookmark bookmark)
    {
        for (var parent = bookmark.Parent; parent is not null; parent = parent.Parent)
        {
            parent.IsExpanded = true;
            _expandedBookmarkIds.Add(parent.Id);
        }

        SelectedBookmark = bookmark;
        BookmarkLocationRequested?.Invoke(bookmark);
    }

    [RelayCommand]
    private async Task AttachOcrToBookmarkAsync()
    {
        if (!CanAttachOcr)
        {
            return;
        }

        var record = SelectedOcrRecord!;
        var bookmark = SelectedBookmark;
        if (bookmark is null)
        {
            OcrBookmarkCreationRequested?.Invoke(record);
            return;
        }

        if (bookmark.PageNumber != record.PageNumber)
        {
            OcrCrossPageAttachRequested?.Invoke(record, bookmark);
            return;
        }

        await AttachOcrToBookmarkAsync(record, bookmark);
    }

    public async Task AttachOcrToBookmarkAsync(OcrRecord record, Bookmark bookmark)
    {
        if (!CanAttachOcr || !ReferenceEquals(SelectedOcrRecord, record))
        {
            return;
        }

        try
        {
            await SaveBookmarkAndAncestorsAsync(bookmark);
            await _ocrRepository.AttachToBookmarkAsync(
                record.Id,
                bookmark.Id);
            record.BookmarkId = bookmark.Id;
            OcrProcessingQueue.Remove(record);
            OnPropertyChanged(nameof(CanAttachOcr));
            RefreshBookmarkDisplayTree();
            StatusMessage = "OCR 已挂载到书签，书签已自动保存";
            NotifyBookmarkChanged();
        }
        catch (Exception exception)
        {
            StatusMessage = $"挂载 OCR 失败: {exception.Message}";
        }
    }

    public async Task CreateBookmarkForOcrAsync(OcrRecord record, string? title, int pageNumber)
    {
        if (!CanAttachOcr || !ReferenceEquals(SelectedOcrRecord, record))
        {
            return;
        }

        var previousBookmark = SelectedBookmark;
        SelectedBookmark = null;
        await CreateBookmarkAsync(title, pageNumber);
        if (record.BookmarkId is null)
        {
            SelectedBookmark = previousBookmark;
        }
    }

    public async Task MoveOcrToBookmarkAsync(OcrRecord? record, Bookmark? target)
    {
        if (record is null || target is null || !HasDocument || IsBusy
            || record.PdfDocumentId != _documentId)
        {
            return;
        }

        if (record.BookmarkId == target.Id)
        {
            StatusMessage = "OCR 已挂载在该书签下";
            return;
        }

        try
        {
            await SaveBookmarkAndAncestorsAsync(target);
            await _ocrRepository.AttachToBookmarkAsync(record.Id, target.Id);
            record.BookmarkId = target.Id;
            OcrProcessingQueue.Remove(record);
            OnPropertyChanged(nameof(CanAttachOcr));
            SelectedBookmark = target;
            SelectedOcrRecord = record;
            RefreshBookmarkDisplayTree();
            StatusMessage = "OCR 已重新挂载到目标书签";
            NotifyBookmarkChanged();
        }
        catch (Exception exception)
        {
            StatusMessage = $"重新挂载 OCR 失败: {exception.Message}";
        }
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

    public async Task RenameBookmarkAsync(Bookmark? bookmark, string? title)
    {
        if (bookmark is null || !CanModifyDocument || IsBusy)
        {
            return;
        }

        title = title?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            StatusMessage = "书签名称不能为空";
            return;
        }

        try
        {
            bookmark.Title = title;
            bookmark.UpdatedAtUtc = DateTime.UtcNow;
            if (bookmark.IsPersisted)
            {
                await _bookmarkRepository.SaveAsync(bookmark);
            }

            if (ReferenceEquals(SelectedBookmark, bookmark))
            {
                SelectedBookmark = bookmark;
            }

            StatusMessage = "书签名称已更新";
        }
        catch (Exception exception)
        {
            StatusMessage = $"修改书签名称失败: {exception.Message}";
        }
    }

    public IReadOnlyList<Bookmark> GetParentBookmarkCandidates(Bookmark bookmark)
    {
        return Bookmarks
            .SelectMany(EnumerateBookmarkTree)
            .Where(candidate => !ReferenceEquals(candidate, bookmark)
                && !IsBookmarkInSubtree(bookmark, candidate))
            .OrderBy(candidate => candidate.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task MoveBookmarkAsync(Bookmark? dragged, Bookmark? target, bool asChild)
    {
        if (dragged is null || target is null || ReferenceEquals(dragged, target)
            || !HasDocument || IsBusy || IsBookmarkInSubtree(dragged, target))
        {
            return;
        }

        try
        {
            // A persisted item cannot point at a new, unsaved parent. Save the target
            // first so the database can accept the new relationship.
            if (!target.IsPersisted)
            {
                await SaveBookmarkAndAncestorsAsync(target);
            }

            var oldParent = dragged.Parent;
            var oldSiblings = oldParent?.Children ?? Bookmarks;
            var newParent = asChild ? target : target.Parent;
            var newSiblings = newParent?.Children ?? Bookmarks;

            oldSiblings.Remove(dragged);
            if (asChild)
            {
                newParent!.Children.Add(dragged);
            }
            else
            {
                var targetIndex = newSiblings.IndexOf(target);
                newSiblings.Insert(Math.Max(0, targetIndex + 1), dragged);
            }

            dragged.Parent = newParent;
            dragged.ParentId = newParent?.Id;
            await RenumberAndPersistAsync(oldSiblings);
            if (!ReferenceEquals(oldSiblings, newSiblings))
            {
                await RenumberAndPersistAsync(newSiblings);
            }

            RefreshBookmarkDisplayTree();
            SelectedBookmark = dragged;
            StatusMessage = asChild ? "书签已设为子书签" : "书签已调整为同级书签";
        }
        catch (Exception exception)
        {
            StatusMessage = $"调整书签层级失败: {exception.Message}";
        }
    }

    public async Task DetachBookmarkAsync(Bookmark? bookmark)
    {
        if (bookmark is null || bookmark.Parent is null || !HasDocument || IsBusy)
        {
            return;
        }

        try
        {
            var oldParent = bookmark.Parent;
            oldParent.Children.Remove(bookmark);
            bookmark.Parent = null;
            bookmark.ParentId = null;
            Bookmarks.Add(bookmark);
            await RenumberAndPersistAsync(oldParent.Children);
            await RenumberAndPersistAsync(Bookmarks);
            RefreshBookmarkDisplayTree();
            SelectedBookmark = bookmark;
            StatusMessage = "书签已脱离父书签";
        }
        catch (Exception exception)
        {
            StatusMessage = $"脱离父书签失败: {exception.Message}";
        }
    }

    public async Task DeleteBookmarkAsync(Bookmark? bookmark)
    {
        if (bookmark is null || !CanModifyDocument || IsBusy)
        {
            return;
        }

        try
        {
            var parent = bookmark.Parent;
            var siblings = parent?.Children ?? Bookmarks;
            var deletedTree = EnumerateBookmarkTree(bookmark).ToList();
            var deletedIds = deletedTree.Select(item => item.Id).ToHashSet();
            var ocrAttachments = OcrHistory
                .Where(record => record.BookmarkId is Guid bookmarkId && deletedIds.Contains(bookmarkId))
                .Select(record => new OcrAttachment(record.Id, record.BookmarkId!.Value))
                .ToList();
            var originalIndex = siblings.IndexOf(bookmark);
            var selectedIsInSubtree = SelectedBookmark is not null
                && (ReferenceEquals(SelectedBookmark, bookmark)
                    || IsBookmarkInSubtree(bookmark, SelectedBookmark));

            if (bookmark.IsPersisted)
            {
                await _bookmarkRepository.DeleteSubtreeAsync(bookmark.Id);
                foreach (var record in OcrHistory.Where(record => record.BookmarkId is Guid bookmarkId
                    && deletedIds.Contains(bookmarkId)))
                {
                    record.BookmarkId = null;
                }
            }

            siblings.Remove(bookmark);
            bookmark.Parent = null;
            bookmark.ParentId = null;
            await RenumberAndPersistAsync(siblings);
            RefreshBookmarkDisplayTree();
            _deletedBookmarkHistory.Push(new DeletedBookmarkOperation(
                bookmark,
                parent,
                originalIndex,
                bookmark.IsPersisted,
                ocrAttachments));
            OnPropertyChanged(nameof(CanUndoBookmarkDelete));

            if (selectedIsInSubtree)
            {
                SelectedBookmark = parent;
            }

            StatusMessage = "书签及其子书签已删除";
        }
        catch (Exception exception)
        {
            StatusMessage = $"删除书签失败: {exception.Message}";
        }
    }

    [RelayCommand]
    private async Task UndoBookmarkDeleteAsync()
    {
        if (!CanUndoBookmarkDelete)
        {
            return;
        }

        var operation = _deletedBookmarkHistory.Peek();
        var siblings = operation.Parent?.Children ?? Bookmarks;
        if (operation.Parent is not null && !IsBookmarkInCurrentTree(operation.Parent))
        {
            StatusMessage = "无法撤回：原父书签当前不在书签树中";
            return;
        }

        try
        {
            var index = Math.Clamp(operation.OriginalIndex, 0, siblings.Count);
            operation.Root.Parent = operation.Parent;
            operation.Root.ParentId = operation.Parent?.Id;
            siblings.Insert(index, operation.Root);
            if (operation.WasPersisted)
            {
                await SaveBookmarkTreeAsync(operation.Root);
            }
            await RenumberAndPersistAsync(siblings);
            RefreshBookmarkDisplayTree();

            foreach (var attachment in operation.OcrAttachments)
            {
                await _ocrRepository.AttachToBookmarkAsync(attachment.OcrRecordId, attachment.BookmarkId);
                var record = OcrHistory.FirstOrDefault(item => item.Id == attachment.OcrRecordId);
                if (record is not null)
                {
                    record.BookmarkId = attachment.BookmarkId;
                }
            }

            _deletedBookmarkHistory.Pop();
            SelectedBookmark = operation.Root;
            OnPropertyChanged(nameof(CanUndoBookmarkDelete));
            StatusMessage = "已撤回上一次书签删除";
        }
        catch (Exception exception)
        {
            siblings.Remove(operation.Root);
            operation.Root.Parent = null;
            operation.Root.ParentId = null;
            StatusMessage = $"撤回书签删除失败: {exception.Message}";
        }
    }

    private static IEnumerable<Bookmark> EnumerateBookmarkTree(Bookmark root)
    {
        yield return root;
        foreach (var child in root.Children)
        {
            foreach (var descendant in EnumerateBookmarkTree(child))
            {
                yield return descendant;
            }
        }
    }

    private Bookmark? FindBookmarkForPage(int pageNumber)
    {
        var candidates = Bookmarks
            .SelectMany(EnumerateBookmarkTree)
            .Where(bookmark => bookmark.PageNumber == pageNumber)
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        if (SelectedBookmark is not null && candidates.Contains(SelectedBookmark))
        {
            return SelectedBookmark;
        }

        return candidates
            .OrderByDescending(GetBookmarkDepth)
            .ThenBy(bookmark => bookmark.SortOrder)
            .First();
    }

    private Bookmark? FindBookmarkForCurrentPage() => FindBookmarkForPage(_currentPage + 1);

    private static int GetBookmarkDepth(Bookmark bookmark)
    {
        var depth = 0;
        for (var current = bookmark.Parent; current is not null; current = current.Parent)
        {
            depth++;
        }

        return depth;
    }

    private bool IsBookmarkInCurrentTree(Bookmark bookmark)
    {
        return Bookmarks.Any(root => IsBookmarkInSubtree(root, bookmark));
    }

    private async Task SaveBookmarkTreeAsync(Bookmark root)
    {
        root.UpdatedAtUtc = DateTime.UtcNow;
        await _bookmarkRepository.SaveAsync(root);
        foreach (var child in root.Children)
        {
            await SaveBookmarkTreeAsync(child);
        }
    }

    private sealed record OcrAttachment(Guid OcrRecordId, Guid BookmarkId);

    private sealed record DeletedBookmarkOperation(
        Bookmark Root,
        Bookmark? Parent,
        int OriginalIndex,
        bool WasPersisted,
        IReadOnlyList<OcrAttachment> OcrAttachments);

    private sealed record DeletedAnnotationOperation(PdfAnnotationInfo Annotation, bool WasPendingAdd);

    private static bool IsBookmarkInSubtree(Bookmark root, Bookmark? candidate)
    {
        while (candidate is not null)
        {
            if (ReferenceEquals(root, candidate))
            {
                return true;
            }

            candidate = candidate.Parent;
        }

        return false;
    }

    private async Task RenumberAndPersistAsync(IList<Bookmark> siblings)
    {
        for (var index = 0; index < siblings.Count; index++)
        {
            var bookmark = siblings[index];
            bookmark.SortOrder = index;
            bookmark.UpdatedAtUtc = DateTime.UtcNow;
            if (bookmark.IsPersisted)
            {
                await _bookmarkRepository.SaveAsync(bookmark);
            }
        }
    }

    public async Task RunOcrSelectionAsync(double x, double y, double width, double height)
    {
        if (!CanCapture || width < 8 || height < 8)
        {
            return;
        }

        var captureZoom = _zoom;
        string? capturePath = null;
        try
        {
            await using var imageStream = await _pdfService.RenderPageRegionAsync(
                _currentPage, x, y, width, height, captureZoom, CancellationToken.None);
            await using var imageBuffer = new MemoryStream();
            await imageStream.CopyToAsync(imageBuffer);
            var imageBytes = imageBuffer.ToArray();
            if (EnableOcrCaptureCache)
            {
                await using var captureStream = new MemoryStream(imageBytes, writable: false);
                LastCapturePath = await SaveDebugCaptureAsync(
                    captureStream,
                    _currentPage + 1,
                    OcrCaptureDirectory,
                    CancellationToken.None);
                capturePath = LastCapturePath;
            }
            else
            {
                LastCapturePath = "截图缓存未开启";
            }
            var pendingRecord = new OcrRecord
            {
                PdfDocumentId = _documentId,
                PageNumber = _currentPage + 1,
                X = x,
                Y = y,
                Width = width,
                Height = height,
                CaptureZoom = captureZoom,
                Title = "OCR 识别中...",
                Text = string.Empty,
                CapturePath = capturePath,
                CreatedAtUtc = DateTime.UtcNow,
                IsPersisted = false,
                IsProcessing = true,
            };
            OcrHistory.Insert(0, pendingRecord);
            OcrProcessingQueue.Insert(0, pendingRecord);
            SelectedOcrRecord = pendingRecord;
            SelectedOcrHistoryRecord = pendingRecord;
            RefreshCurrentPageOcr();
            RefreshReadingPageOcr();
            _ocrQueue.Enqueue(new OcrQueueJob(pendingRecord, imageBytes));
            _ocrQueueSignal.Release();
            HasPendingOcr = true;
            StatusMessage = "选区已加入 OCR 队列";
        }
        catch (Exception exception)
        {
            DeleteResource(capturePath);
            StatusMessage = $"选区 OCR 失败: {exception.Message}";
        }
        if (_captureOnce)
        {
            _captureOnce = false;
            NotifyCaptureChanged();
        }
    }

    private async Task ProcessOcrQueueAsync()
    {
        try
        {
            while (await _ocrQueueSignal.WaitAsync(Timeout.Infinite, _ocrQueueCancellation.Token))
            {
                if (!_ocrQueue.TryDequeue(out var job))
                {
                    continue;
                }

                var cancellation = BeginOcrCancellation();
                try
                {
                    IsOcrBusy = true;
                    StatusMessage = "正在识别 OCR 队列...";
                    await using var imageStream = new MemoryStream(job.ImageBytes, writable: false);
                    var result = await _ocrService.RecognizeAsync(imageStream, cancellation.Token);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        job.Record.Text = string.IsNullOrWhiteSpace(result.Text) ? "选区内未识别到文本。" : result.Text;
                        job.Record.Title = CreateDefaultOcrTitle(job.Record.Text);
                        job.Record.IsProcessing = false;
                        OnPropertyChanged(nameof(CanConfirmOcr));
                        if (ReferenceEquals(SelectedOcrRecord, job.Record))
                        {
                            OcrText = job.Record.Text;
                            OcrTitle = job.Record.Title;
                        }
                        RefreshCurrentPageOcr();
                        RefreshReadingPageOcr();
                        StatusMessage = $"OCR 完成，识别到 {result.Lines.Count} 行，请确认后保存";
                    });
                }
                catch (OperationCanceledException)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        DeleteResource(job.Record.CapturePath);
                        OcrHistory.Remove(job.Record);
                        OcrProcessingQueue.Remove(job.Record);
                        HasPendingOcr = OcrHistory.Any(item => !item.IsPersisted);
                        OnPropertyChanged(nameof(CanConfirmOcr));
                    });
                }
                finally
                {
                    EndOcrCancellation(cancellation);
                    IsOcrBusy = false;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    private void CancelOcr()
    {
        if (!CanCancelOcr)
        {
            return;
        }

        lock (_ocrCancellationLock)
        {
            foreach (var cancellation in _ocrCancellations.ToList())
            {
                cancellation.Cancel();
            }
        }
        DiscardPendingOcr();
        OcrText = "识别结果已取消，未保存任何结果。";
        StatusMessage = IsOcrBusy ? "正在停止 OCR..." : "OCR 结果已取消";
    }

    private CancellationTokenSource BeginOcrCancellation()
    {
        var cancellation = new CancellationTokenSource();
        lock (_ocrCancellationLock)
        {
            _ocrCancellations.Add(cancellation);
        }

        return cancellation;
    }

    private void EndOcrCancellation(CancellationTokenSource cancellation)
    {
        lock (_ocrCancellationLock)
        {
            _ocrCancellations.Remove(cancellation);
        }

        cancellation.Dispose();
    }

    private void NotifyCaptureChanged()
    {
        OnPropertyChanged(nameof(CanSelectCaptureMode));
        OnPropertyChanged(nameof(CanCapture));
        OnPropertyChanged(nameof(CanCancelCapture));
        OnPropertyChanged(nameof(HasPreviousReadingPage));
        OnPropertyChanged(nameof(HasNextReadingPage));
        OnPropertyChanged(nameof(IsContinuousReadingMode));
    }

    private async Task SaveBookmarkAndAncestorsAsync(Bookmark bookmark)
    {
        if (bookmark.Parent is not null && !bookmark.Parent.IsPersisted)
        {
            await SaveBookmarkAndAncestorsAsync(bookmark.Parent);
        }

        bookmark.UpdatedAtUtc = DateTime.UtcNow;
        await _bookmarkRepository.SaveAsync(bookmark);
    }

    [RelayCommand]
    private async Task SaveSettings()
    {
        if (string.IsNullOrWhiteSpace(OcrCaptureDirectory))
        {
            OcrCaptureDirectory = ReaderSettings.GetDefaultCaptureDirectory();
        }

        await ApplySettingsAsync(CreateReaderSettings());
    }

    public ReaderSettings GetSettings() => CreateReaderSettings();

    public async Task ApplySettingsAsync(ReaderSettings settings)
    {
        _settings = settings;
        _settingsService.Save(_settings);

        EnableOcrCaptureCache = settings.EnableOcrCaptureCache;
        AutoGenerateOcrAudio = settings.AutoGenerateOcrAudio;
        EnableTtsAudioNormalization = settings.EnableTtsAudioNormalization;
        FfmpegPath = settings.FfmpegPath;
        EnableLocalApi = settings.EnableLocalApi;
        LocalApiPort = settings.LocalApiPort;
        LocalApiToken = settings.LocalApiToken;
        var pagePreviewsWereEnabled = EnablePagePreviews;
        EnablePagePreviews = settings.EnablePagePreviews;
        OcrCaptureDirectory = settings.OcrCaptureDirectory;
        AudioDirectory = settings.AudioDirectory;
        TtsBaseUrl = settings.TtsBaseUrl;
        TtsApiKey = settings.TtsApiKey;
        TtsModelType = settings.TtsModelType;
        TtsVoiceModel = settings.TtsVoiceModel;
        _ttsVoiceModels = CloneVoiceModels(settings.TtsVoiceModels);
        if (settings.EnableLocalApi)
        {
            _automationService.Start(settings.LocalApiPort);
        }
        else
        {
            _automationService.Stop();
        }

        if (HasDocument)
        {
            if (!EnablePagePreviews)
            {
                IsPagePreviewPaneVisible = false;
                ClearPagePreviews();
            }
            else if (!pagePreviewsWereEnabled)
            {
                await LoadPagePreviewsAsync();
            }
        }

        StatusMessage = "设置已保存";
    }

    [RelayCommand]
    private Task GenerateSpeechAsync() => GenerateSpeechForRecordAsync(SelectedOcrRecord);

    public Task GenerateSpeechForRecordAsync(OcrRecord? record) => GenerateSpeechForRecordAsync(record, null, false);

    public Task GenerateSpeechForRecordAsync(OcrRecord? record, string? voiceModelName) =>
        GenerateSpeechForRecordAsync(record, voiceModelName, false);

    public Task RegenerateSpeechForRecordAsync(OcrRecord? record, string voiceModelName) =>
        GenerateSpeechForRecordAsync(record, voiceModelName, true);

    private async Task GenerateSpeechForRecordAsync(OcrRecord? record, string? voiceModelName, bool replaceExistingAudio)
    {
        if (record is null || !CanModifyDocument || IsTtsBusy)
        {
            StatusMessage = "请先选择已保存的 OCR 记录。";
            return;
        }

        try
        {
            SelectedOcrRecord = record;
            IsTtsBusy = true;
            StatusMessage = "正在生成语音...";
            var settings = CreateReaderSettings();
            if (!string.IsNullOrWhiteSpace(voiceModelName))
            {
                if (!settings.TtsVoiceModels.Any(model => string.Equals(model.Name, voiceModelName, StringComparison.Ordinal)))
                {
                    StatusMessage = "指定的 Voice Model 不存在或已被移除";
                    return;
                }

                settings.TtsVoiceModel = voiceModelName;
            }
            var audioPath = await _ttsService.GenerateAsync(
                record.Text,
                settings,
                record.PageNumber);
            if (replaceExistingAudio)
            {
                foreach (var path in await _ocrRepository.DeleteAudiosAsync(record.Id))
                {
                    if (!string.Equals(path, audioPath, StringComparison.OrdinalIgnoreCase))
                    {
                        DeleteResource(path);
                    }
                }

                record.TtsAudios.Clear();
            }
            var audio = new TtsAudioRecord
            {
                OcrRecordId = record.Id,
                FilePath = audioPath,
                CreatedAtUtc = DateTime.UtcNow,
            };
            await _ocrRepository.AddAudioAsync(audio);
            record.TtsAudios.Add(audio);
            record.RefreshAudioStatus();
            GeneratedAudioPath = record.LatestAudioPath ?? audioPath;
            StatusMessage = "语音生成完成";
        }
        catch (Exception exception)
        {
            StatusMessage = $"语音生成失败: {exception.Message}";
        }
        finally
        {
            IsTtsBusy = false;
            OnPropertyChanged(nameof(CanReadCurrentPage));
        }
    }

    public async Task PlayOrGenerateOcrAudioAsync(OcrRecord? record)
    {
        if (record is null || !HasDocument)
        {
            return;
        }

        record.RefreshAudioStatus();
        if (!record.HasAudio)
        {
            if (!AutoGenerateOcrAudio)
            {
                StatusMessage = "该 OCR 记录尚未生成音频，请通过右键菜单选择模型生成";
                return;
            }

            await GenerateSpeechForRecordAsync(record);
            record.RefreshAudioStatus();
        }

        if (record.HasAudio)
        {
            PlayOcrAudio(record);
        }
    }

    public void PlayOcrAudio(OcrRecord? record)
    {
        if (record is null || !HasDocument)
        {
            return;
        }

        record.RefreshAudioStatus();
        if (!record.HasAudio || string.IsNullOrWhiteSpace(record.LatestAudioPath))
        {
            StatusMessage = "该 OCR 记录尚未生成可用音频";
            return;
        }

        SelectedOcrRecord = record;
        GeneratedAudioPath = record.LatestAudioPath;
        try
        {
            _audioPlaybackService.Play(record.LatestAudioPath);
            SetAudioPlaying(true);
            SetAudioPaused(false);
            StatusMessage = "正在播放 OCR 音频";
        }
        catch (Exception exception)
        {
            StatusMessage = $"播放失败: {exception.Message}";
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
            SetAudioPlaying(true);
            SetAudioPaused(false);
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
        var cancellation = _readingCancellation;
        if (cancellation is not null)
        {
            cancellation.Cancel();
        }
        else
        {
            _audioPlaybackService.Stop();
        }

        SetAudioPlaying(false);
        SetAudioPaused(false);
        StatusMessage = "音频已停止";
    }

    public async Task ExportHtml5Async(string outputDirectory)
    {
        if (!HasDocument || IsBusy || HasPendingAnnotationChanges || string.IsNullOrWhiteSpace(outputDirectory))
        {
            StatusMessage = HasPendingAnnotationChanges ? "请先保存或放弃缓存中的标注变更" : StatusMessage;
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "正在导出 HTML5 阅读包...";
            await _html5ExportService.ExportAsync(
                DocumentPath,
                outputDirectory,
                DocumentTitle,
                _pdfService.PageCount,
                Bookmarks.ToList(),
                OcrHistory.ToList());
            StatusMessage = "已导出 HTML5 阅读包";
        }
        catch (Exception exception)
        {
            StatusMessage = $"HTML5 导出失败: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void TogglePauseAudio()
    {
        if (!IsAudioActive)
        {
            return;
        }

        if (IsAudioPaused)
        {
            _audioPlaybackService.Resume();
            SetAudioPaused(false);
            StatusMessage = "继续播放音频";
        }
        else
        {
            _audioPlaybackService.Pause();
            SetAudioPaused(true);
            StatusMessage = "音频已暂停";
        }
    }

    private void AudioPlaybackStateChanged(object? sender, EventArgs e)
    {
        // LibVLC may report a transient stopped/paused state while toggling pause.
        // Keep the session visible until the user stops it or the reading loop ends.
        if (IsAudioPaused)
        {
            return;
        }

        SetAudioPlaying(_audioPlaybackService.IsPlaying);
        SetAudioPaused(_audioPlaybackService.IsPaused);
    }

    private void SetAudioPlaying(bool value)
    {
        IsAudioPlaying = value;
        OnPropertyChanged(nameof(IsAudioActive));
        OnPropertyChanged(nameof(IsAudioControlsVisible));
        OnPropertyChanged(nameof(ReadCurrentPageButtonText));
        OnPropertyChanged(nameof(AudioPauseButtonText));
    }

    private void SetAudioPaused(bool value)
    {
        if (IsAudioPaused == value)
        {
            return;
        }

        IsAudioPaused = value;
        OnPropertyChanged(nameof(IsAudioActive));
        OnPropertyChanged(nameof(IsAudioControlsVisible));
        OnPropertyChanged(nameof(AudioPauseButtonText));
        OnPropertyChanged(nameof(ReadCurrentPageButtonText));
        OnPropertyChanged(nameof(CanReadCurrentPage));
        OnPropertyChanged(nameof(CanReadPageOnly));
    }

    private ReaderSettings CreateReaderSettings()
    {
        return new ReaderSettings
        {
            EnablePagePreviews = EnablePagePreviews,
            EnableOcrCaptureCache = EnableOcrCaptureCache,
            AutoGenerateOcrAudio = AutoGenerateOcrAudio,
            EnableTtsAudioNormalization = EnableTtsAudioNormalization,
            FfmpegPath = FfmpegPath,
            EnableLocalApi = EnableLocalApi,
            LocalApiPort = LocalApiPort,
            LocalApiToken = LocalApiToken,
            OcrCaptureDirectory = OcrCaptureDirectory,
            AudioDirectory = AudioDirectory,
            TtsBaseUrl = TtsBaseUrl,
            TtsApiKey = TtsApiKey,
            TtsModelType = TtsModelType,
            TtsVoiceModel = TtsVoiceModel,
            TtsVoiceModels = CloneVoiceModels(_ttsVoiceModels),
        };
    }

    public IReadOnlyList<TtsVoiceModelOption> GetConfiguredVoiceModels() => _ttsVoiceModels;

    private static List<TtsVoiceModelOption> CloneVoiceModels(IEnumerable<TtsVoiceModelOption>? voiceModels)
    {
        return (voiceModels ?? Enumerable.Empty<TtsVoiceModelOption>())
            .Select(voiceModel => new TtsVoiceModelOption
            {
                Name = voiceModel.Name,
                VoiceId = voiceModel.VoiceId,
            })
            .ToList();
    }

    [RelayCommand]
    private async Task SaveOcrAsync()
    {
        var record = SelectedOcrHistoryRecord ?? SelectedOcrRecord;
        if (!HasPendingOcr || record is null || record.IsProcessing || record.IsPersisted
            || !OcrProcessingQueue.Contains(record)
            || string.IsNullOrWhiteSpace(OcrText) || !CanModifyDocument)
        {
            return;
        }

        try
        {
            IsOcrBusy = true;
            var matchingBookmark = FindBookmarkForPage(record.PageNumber);
            record.BookmarkId = matchingBookmark?.Id;
            record.Title = string.IsNullOrWhiteSpace(OcrTitle) ? CreateDefaultOcrTitle(OcrText) : OcrTitle.Trim();
            record.Text = OcrText.Trim();
            await _ocrRepository.AddAsync(record);
            record.IsPersisted = true;
            OcrProcessingQueue.Remove(record);
            SelectedOcrRecord = record;
            OcrTitle = record.Title;
            RefreshCurrentPageOcr();
            RefreshReadingPageOcr();
            RefreshBookmarkDisplayTree();
            GeneratedAudioPath = "尚未生成音频";
            HasPendingOcr = OcrHistory.Any(item => !item.IsPersisted);
            RefreshOcrProcessingQueue();
            SelectNextOcrQueueItem(record);
            NotifyBookmarkChanged();
            StatusMessage = matchingBookmark is null
                ? "OCR 结果已保存，请选择书签进行挂载"
                : $"OCR 结果已保存并挂载到书签：{matchingBookmark.Title}";
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

    private void SelectNextOcrQueueItem(OcrRecord completedRecord)
    {
        var next = OcrProcessingQueue.FirstOrDefault(item => !item.IsProcessing && !item.IsPersisted);
        if (next is null)
        {
            if (ReferenceEquals(SelectedOcrHistoryRecord, completedRecord))
            {
                SelectedOcrHistoryRecord = null;
            }

            return;
        }

        SelectedOcrHistoryRecord = next;
        SelectedOcrRecord = next;
    }

    private void DiscardPendingOcr()
    {
        var record = SelectedOcrRecord;
        if (record is not null && !record.IsPersisted)
        {
            DeleteResource(record.CapturePath);
            OcrHistory.Remove(record);
            OcrProcessingQueue.Remove(record);
            if (ReferenceEquals(SelectedOcrHistoryRecord, record))
            {
                SelectedOcrHistoryRecord = null;
            }
        }

        HasPendingOcr = OcrHistory.Any(item => !item.IsPersisted);
        if (ReferenceEquals(SelectedOcrRecord, record))
        {
            SelectedOcrRecord = OcrHistory.FirstOrDefault(item => !item.IsPersisted);
        }
        RefreshCurrentPageOcr();
        RefreshReadingPageOcr();
    }

    private static string CreateDefaultOcrTitle(string text)
    {
        var title = text.Trim().Replace('\r', ' ').Replace('\n', ' ');
        while (title.Contains("  ", StringComparison.Ordinal))
        {
            title = title.Replace("  ", " ", StringComparison.Ordinal);
        }

        return title.Length <= 24 ? title : $"{title[..24]}...";
    }

    private static void DeleteResources(IEnumerable<string> resources)
    {
        foreach (var resource in resources)
        {
            DeleteResource(resource);
        }
    }

    private static void DeleteResource(string? resource)
    {
        if (string.IsNullOrWhiteSpace(resource))
        {
            return;
        }

        try
        {
            if (File.Exists(resource))
            {
                File.Delete(resource);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
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
            Bitmap image;
            await _pageRenderGate.WaitAsync();
            try
            {
                if (pageIndex == _currentPage - 1 && PreviousReadingPageImage is not null)
                {
                    image = PreviousReadingPageImage;
                    PreviousReadingPageImage = null;
                }
                else if (pageIndex == _currentPage + 1 && NextReadingPageImage is not null)
                {
                    image = NextReadingPageImage;
                    NextReadingPageImage = null;
                }
                else if (!_prefetchedPageImages.Remove(pageIndex, out image!))
                {
                    await using var stream = await _pdfService.RenderPageAsync(pageIndex, _zoom);
                    image = new Bitmap(stream);
                }
            }
            finally
            {
                _pageRenderGate.Release();
            }

            var oldImage = PageImage;
            PageImage = image;
            if (oldImage is not null && Math.Abs(pageIndex - _currentPage) == 1)
            {
                SetReadingNeighbor(pageIndex > _currentPage, oldImage);
            }
            else
            {
                oldImage?.Dispose();
            }
            _currentPage = pageIndex;
            var now = DateTime.UtcNow;
            _preloadRadius = now - _lastPageNavigationUtc < TimeSpan.FromMilliseconds(750) ? 10 : 5;
            _lastPageNavigationUtc = now;
            PageIndicator = $"{_currentPage + 1} / {_pdfService.PageCount}";
            PageNumberInput = (_currentPage + 1).ToString();
            OnPropertyChanged(nameof(DocumentPageCount));
            SelectedPagePreview = PagePreviews.FirstOrDefault(preview => preview.PageNumber == _currentPage + 1);
            ZoomIndicator = $"{_zoom:P0}";
            RefreshCurrentPageOcr();
            await EnsureCurrentPageAnnotationsAsync();
            ContinuousReadingPageRequested?.Invoke(_currentPage + 1);
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
            PreloadAdjacentPages();
        }
    }

    public void PreloadAdjacentPages()
    {
        if (!HasDocument || IsBusy || IsAnnotationMode || CanCapture || IsContinuousReadingMode)
        {
            return;
        }

        var generation = _pageRenderGeneration;
        PromotePrefetchedReadingNeighbors();
        var preloadRadius = _preloadRadius;
        foreach (var pageIndex in Enumerable.Range(_currentPage - preloadRadius, preloadRadius * 2 + 1)
                     .Where(index => index != _currentPage))
        {
            if (pageIndex < 0 || pageIndex >= _pdfService.PageCount
                || _prefetchedPageImages.ContainsKey(pageIndex)
                || !_prefetchingPageIndexes.Add(pageIndex))
            {
                continue;
            }

            _ = PrefetchPageAsync(pageIndex, _documentId, _zoom, generation);
        }
    }

    private async Task PrefetchPageAsync(int pageIndex, Guid documentId, double zoom, int generation)
    {
        try
        {
            await Task.Yield();
            await _pageRenderGate.WaitAsync();
            try
            {
                if (!HasDocument || _documentId != documentId || _pageRenderGeneration != generation)
                {
                    return;
                }

                await using var stream = await _pdfService.RenderPageAsync(pageIndex, zoom);
                var image = new Bitmap(stream);
                if (!HasDocument || _documentId != documentId || _pageRenderGeneration != generation)
                {
                    image.Dispose();
                    return;
                }

                if (pageIndex == _currentPage - 1)
                {
                    SetReadingNeighbor(previous: true, image);
                }
                else if (pageIndex == _currentPage + 1)
                {
                    SetReadingNeighbor(previous: false, image);
                }
                else
                {
                    _prefetchedPageImages[pageIndex] = image;
                }
                foreach (var stalePage in _prefetchedPageImages.Keys
                             .Where(index => Math.Abs(index - _currentPage) > _preloadRadius)
                             .ToArray())
                {
                    _prefetchedPageImages[stalePage].Dispose();
                    _prefetchedPageImages.Remove(stalePage);
                }
            }
            finally
            {
                _pageRenderGate.Release();
            }
        }
        catch (Exception)
        {
            // Preloading is opportunistic and must never interrupt reading.
        }
        finally
        {
            if (_pageRenderGeneration == generation)
            {
                _prefetchingPageIndexes.Remove(pageIndex);
            }
        }
    }

    private void ClearPrefetchedPageImages()
    {
        _pageRenderGeneration++;
        foreach (var image in _prefetchedPageImages.Values)
        {
            image.Dispose();
        }

        _prefetchedPageImages.Clear();
        _prefetchingPageIndexes.Clear();
        PreviousReadingPageImage?.Dispose();
        PreviousReadingPageImage = null;
        NextReadingPageImage?.Dispose();
        NextReadingPageImage = null;
    }

    private void SetReadingNeighbor(bool previous, Bitmap image)
    {
        var current = previous ? PreviousReadingPageImage : NextReadingPageImage;
        if (ReferenceEquals(current, image))
        {
            return;
        }

        current?.Dispose();
        if (previous)
        {
            PreviousReadingPageImage = image;
        }
        else
        {
            NextReadingPageImage = image;
        }
    }

    private void PromotePrefetchedReadingNeighbors()
    {
        if (PreviousReadingPageImage is null
            && _prefetchedPageImages.Remove(_currentPage - 1, out var previous))
        {
            PreviousReadingPageImage = previous;
        }

        if (NextReadingPageImage is null
            && _prefetchedPageImages.Remove(_currentPage + 1, out var next))
        {
            NextReadingPageImage = next;
        }
    }

    private async Task LoadPagePreviewsAsync()
    {
        ClearPagePreviews();
        if (!EnablePagePreviews || !HasDocument || _pdfService.PageCount == 0)
        {
            return;
        }

        const double previewZoom = 0.30;
        var cacheDirectory = ReaderSettings.GetPagePreviewCacheDirectory(_documentId);
        var metadataPath = Path.Combine(cacheDirectory, "metadata.json");
        var expectedMetadata = CreatePagePreviewCacheMetadata(previewZoom);
        var cacheIsValid = IsPagePreviewCacheValid(metadataPath, expectedMetadata);

        if (!cacheIsValid)
        {
            DeletePagePreviewCache(_documentId);
            Directory.CreateDirectory(cacheDirectory);
        }

        for (var pageIndex = 0; pageIndex < _pdfService.PageCount; pageIndex++)
        {
            var cachePath = Path.Combine(cacheDirectory, $"page-{pageIndex + 1:0000}.png");
            if (!cacheIsValid || !File.Exists(cachePath))
            {
                StatusMessage = $"正在生成页面预览 {pageIndex + 1}/{_pdfService.PageCount}...";
                await using var stream = await _pdfService.RenderPageAsync(pageIndex, previewZoom);
                await using (var cacheStream = File.Create(cachePath))
                {
                    await stream.CopyToAsync(cacheStream);
                }

            }

            PagePreviews.Add(new PagePreview(pageIndex + 1, cachePath));
        }

        await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(expectedMetadata));

        SelectedPagePreview = PagePreviews.FirstOrDefault(preview => preview.PageNumber == CurrentPageNumber);
    }

    private PagePreviewCacheMetadata CreatePagePreviewCacheMetadata(double previewZoom)
    {
        var fileInfo = new FileInfo(DocumentPath);
        return new PagePreviewCacheMetadata(
            fileInfo.Length,
            fileInfo.LastWriteTimeUtc.Ticks,
            _pdfService.PageCount,
            previewZoom);
    }

    // Annotation overlays are rendered separately from the cached page bitmaps.
    // A known PDF write should therefore retain the thumbnails and only update their source stamp.
    private void RefreshPagePreviewCacheMetadata()
    {
        if (!HasDocument || _documentId == Guid.Empty)
        {
            return;
        }

        var cacheDirectory = ReaderSettings.GetPagePreviewCacheDirectory(_documentId);
        if (!Directory.Exists(cacheDirectory))
        {
            return;
        }

        try
        {
            const double previewZoom = 0.30;
            var metadataPath = Path.Combine(cacheDirectory, "metadata.json");
            File.WriteAllText(metadataPath, JsonSerializer.Serialize(CreatePagePreviewCacheMetadata(previewZoom)));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool IsPagePreviewCacheValid(string metadataPath, PagePreviewCacheMetadata expected)
    {
        if (!File.Exists(metadataPath))
        {
            return false;
        }

        try
        {
            var metadata = JsonSerializer.Deserialize<PagePreviewCacheMetadata>(File.ReadAllText(metadataPath));
            return metadata is not null
                && metadata.SourceLength == expected.SourceLength
                && metadata.SourceLastWriteUtcTicks == expected.SourceLastWriteUtcTicks
                && metadata.PageCount == expected.PageCount
                && Math.Abs(metadata.Zoom - expected.Zoom) < 0.0001;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void DeletePagePreviewCache(Guid documentId)
    {
        var directory = ReaderSettings.GetPagePreviewCacheDirectory(documentId);
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record PagePreviewCacheMetadata(
        long SourceLength,
        long SourceLastWriteUtcTicks,
        int PageCount,
        double Zoom);

    private void ClearPagePreviews()
    {
        SelectedPagePreview = null;
        foreach (var preview in PagePreviews)
        {
            preview.Dispose();
        }

        PagePreviews.Clear();
    }

    private void InitializeReadingPages()
    {
        ClearReadingPages();
        if (!HasDocument || _pdfService.PageCount == 0)
        {
            return;
        }

        for (var pageIndex = 0; pageIndex < _pdfService.PageCount; pageIndex++)
        {
            var size = _pdfService.GetPageSize(pageIndex, _zoom);
            var previewPath = Path.Combine(
                ReaderSettings.GetPagePreviewCacheDirectory(_documentId),
                $"page-{pageIndex + 1:0000}.png");
            ReadingPages.Add(new ReadingPage(pageIndex + 1, size.Width, size.Height, previewPath));
        }

        RefreshReadingPageOcr();
    }

    public void ActivateReadingPage(ReadingPage? page)
    {
        if (page is null || !ReadingPages.Contains(page))
        {
            return;
        }

        page.IsActive = true;
        if (page.Image is null && page.PreviewImage is null && !page.IsPreviewQueued
            && File.Exists(page.PreviewCachePath))
        {
            page.IsPreviewQueued = true;
            _ = LoadReadingPagePreviewAsync(page, _documentId);
        }
    }

    private async Task LoadReadingPagePreviewAsync(ReadingPage page, Guid documentId)
    {
        Bitmap? preview = null;
        try
        {
            preview = await Task.Run(() =>
            {
                using var stream = File.OpenRead(page.PreviewCachePath);
                return new Bitmap(stream);
            });

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (HasDocument && _documentId == documentId && page.IsActive
                    && page.Image is null && ReadingPages.Contains(page))
                {
                    page.SetPreview(preview);
                    preview = null;
                }
            });
        }
        catch (Exception)
        {
        }
        finally
        {
            preview?.Dispose();
            await Dispatcher.UIThread.InvokeAsync(() => page.IsPreviewQueued = false);
        }
    }

    public void QueueReadingPageRender(ReadingPage? page)
    {
        if (page is null || !page.IsActive || page.Image is not null || page.IsRenderQueued
            || !ReadingPages.Contains(page) || !HasDocument)
        {
            return;
        }

        page.IsRenderQueued = true;
        _ = RenderReadingPageAsync(page, _documentId, _zoom);
    }

    private async Task RenderReadingPageAsync(ReadingPage page, Guid documentId, double zoom)
    {
        Bitmap? image = null;
        try
        {
            await _readingRenderGate.WaitAsync();
            await _pageRenderGate.WaitAsync();
            try
            {
                image = await Task.Run(() =>
                {
                    using var stream = _pdfService.RenderPageAsync(page.PageNumber - 1, zoom).GetAwaiter().GetResult();
                    return new Bitmap(stream);
                });
            }
            finally
            {
                _pageRenderGate.Release();
                _readingRenderGate.Release();
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (HasDocument && _documentId == documentId && page.IsActive && ReadingPages.Contains(page))
                {
                    page.Image = image;
                    page.UnloadPreview();
                    image = null;
                }
            });
        }
        catch (Exception)
        {
        }
        finally
        {
            image?.Dispose();
            await Dispatcher.UIThread.InvokeAsync(() => page.IsRenderQueued = false);
        }
    }

    public void UnloadReadingPageImage(ReadingPage? page)
    {
        if (page is not null && ReadingPages.Contains(page))
        {
            page.IsActive = false;
            page.IsRenderQueued = false;
            page.IsPreviewQueued = false;
            page.Unload();
            page.Annotations.Clear();
        }
    }

    public async Task LoadReadingPageAnnotationsAsync(ReadingPage? page)
    {
        if (page is null || !page.IsActive || !HasDocument || !ReadingPages.Contains(page))
        {
            return;
        }

        try
        {
            var annotations = await _annotationService.GetAnnotationsAsync(DocumentPath, page.PageNumber - 1);
            if (!page.IsActive || !HasDocument || !ReadingPages.Contains(page))
            {
                return;
            }

            page.Annotations.Clear();
            foreach (var annotation in annotations)
            {
                page.Annotations.Add(ScaleAnnotation(annotation, _zoom));
            }
        }
        catch (Exception)
        {
            // Continuous reading annotations are a visual enhancement only.
        }
    }

    private static PdfAnnotationInfo ScaleAnnotation(PdfAnnotationInfo annotation, double scale) => new()
    {
        Id = annotation.Id, Subtype = annotation.Subtype, PageNumber = annotation.PageNumber,
        Type = annotation.Type, Title = annotation.Title, Contents = annotation.Contents,
        X = annotation.X * scale, Y = annotation.Y * scale,
        Width = annotation.Width * scale, Height = annotation.Height * scale,
        StartX = annotation.StartX * scale, StartY = annotation.StartY * scale,
        EndX = annotation.EndX * scale, EndY = annotation.EndY * scale,
        Points = annotation.Points.Select(point => new PdfAnnotationPoint(point.X * scale, point.Y * scale)).ToArray(),
        StrokeColor = annotation.StrokeColor, StrokeWidth = annotation.StrokeWidth * scale,
        FontSize = annotation.FontSize * scale,
    };

    private void ClearReadingPages()
    {
        foreach (var page in ReadingPages)
        {
            page.Dispose();
        }

        ReadingPages.Clear();
    }

    public void LoadPagePreviewImage(PagePreview? preview)
    {
        if (preview is null || !PagePreviews.Contains(preview))
        {
            return;
        }

        try
        {
            preview.LoadImage();
        }
        catch (Exception)
        {
            preview.UnloadImage();
        }
    }

    public void UnloadPagePreviewImage(PagePreview? preview)
    {
        if (preview is null || ReferenceEquals(preview, SelectedPagePreview))
        {
            return;
        }

        preview.UnloadImage();
    }

    partial void OnHasDocumentChanged(bool value)
    {
        NotifyNavigationChanged();
        NotifyCaptureChanged();
        OnPropertyChanged(nameof(IsDocumentReadOnly));
        OnPropertyChanged(nameof(CanModifyDocument));
        OnPropertyChanged(nameof(CanGenerateSpeech));
        OnPropertyChanged(nameof(CanClearOcr));
        OnPropertyChanged(nameof(CanAnnotate));
        OnPropertyChanged(nameof(CanReadCurrentPage));
        OnPropertyChanged(nameof(CanReadPageOnly));
        OnPropertyChanged(nameof(CurrentPageOcrButtonText));
        OnPropertyChanged(nameof(IsContinuousReadingMode));
        if (!value)
        {
            _pendingAnnotationChanges.Clear();
            _deletedAnnotationHistory.Clear();
            OnPropertyChanged(nameof(CanUndoAnnotationDelete));
            NotifyAnnotationSaveChanged();
            IsCurrentPageOcrVisible = false;
            CurrentPageOcrRecords.Clear();
            CurrentPageAnnotations.Clear();
            ClearPagePreviews();
            ClearReadingPages();
        }
        NotifyBookmarkChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        NotifyNavigationChanged();
        NotifyCaptureChanged();
        NotifyBookmarkChanged();
        OnPropertyChanged(nameof(CanUndoBookmarkDelete));
        OnPropertyChanged(nameof(CanClearOcr));
        OnPropertyChanged(nameof(CanAnnotate));
        OnPropertyChanged(nameof(CanReadCurrentPage));
        OnPropertyChanged(nameof(CanReadPageOnly));
        OnPropertyChanged(nameof(CanSaveAnnotations));
    }

    partial void OnIsOcrBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCancelOcr));
        NotifyCaptureChanged();
        NotifyBookmarkChanged();
        OnPropertyChanged(nameof(CanClearOcr));
        OnPropertyChanged(nameof(CanConfirmOcr));
    }

    partial void OnIsTtsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanGenerateSpeech));
        OnPropertyChanged(nameof(CanClearOcr));
        OnPropertyChanged(nameof(CanAnnotate));
        OnPropertyChanged(nameof(CanReadCurrentPage));
        OnPropertyChanged(nameof(CanReadPageOnly));
    }

    partial void OnIsAnnotationModeChanged(bool value)
    {
        OnPropertyChanged(nameof(AnnotationButtonText));
        OnPropertyChanged(nameof(CanAnnotate));
        OnPropertyChanged(nameof(CanReadCurrentPage));
        OnPropertyChanged(nameof(CanReadPageOnly));
        OnPropertyChanged(nameof(HasPreviousReadingPage));
        OnPropertyChanged(nameof(HasNextReadingPage));
        OnPropertyChanged(nameof(IsContinuousReadingMode));
    }

    partial void OnAnnotationToolChanged(AnnotationTool value)
    {
        OnPropertyChanged(nameof(AnnotationToolText));
    }

    partial void OnAnnotationColorChanged(Color value)
    {
        OnPropertyChanged(nameof(AnnotationColorHex));
        OnPropertyChanged(nameof(AnnotationColorBrush));
    }

    partial void OnAnnotationStrokeWidthChanged(decimal value)
    {
        OnPropertyChanged(nameof(AnnotationStrokeWidthText));
        ApplySelectedAnnotationStyle();
    }

    partial void OnAnnotationTextFontSizeChanged(decimal value)
    {
        if (!_isSynchronizingTextFontSize && HasSelectedFreeTextAnnotation)
        {
            ApplySelectedAnnotationStyle();
        }
    }

    partial void OnIsReadingCurrentPageChanged(bool value)
    {
        OnPropertyChanged(nameof(ReadCurrentPageButtonText));
        OnPropertyChanged(nameof(CanReadCurrentPage));
        OnPropertyChanged(nameof(CanReadPageOnly));
    }

    partial void OnIsAudioPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(ReadCurrentPageButtonText));
        OnPropertyChanged(nameof(CanReadCurrentPage));
    }

    partial void OnIsCurrentPageOcrVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(CurrentPageOcrButtonText));
        OnPropertyChanged(nameof(IsContinuousReadingMode));
        if (value)
        {
            RefreshReadingPageOcr();
        }
        else
        {
            UpdateReadingPageOcrVisibility();
        }
    }

    partial void OnSelectedBookmarkChanged(Bookmark? value)
    {
        if (!ReferenceEquals(SelectedTreeItem, value))
        {
            SelectedTreeItem = value;
        }
        NotifyBookmarkChanged();
    }

    partial void OnSelectedPdfAnnotationChanged(PdfAnnotationInfo? value)
    {
        OnPropertyChanged(nameof(HasSelectedPdfAnnotation));
        OnPropertyChanged(nameof(HasSelectedFreeTextAnnotation));
        OnPropertyChanged(nameof(SelectedAnnotationColor));
        OnPropertyChanged(nameof(SelectedAnnotationStrokeWidth));
    }

    partial void OnSelectedTreeItemChanged(object? value)
    {
        if (value is Bookmark bookmark && !ReferenceEquals(SelectedBookmark, bookmark))
        {
            SelectedBookmark = bookmark;
        }
        else if (value is OcrRecord record && !ReferenceEquals(SelectedOcrRecord, record))
        {
            SelectedOcrRecord = record;
        }
    }

    partial void OnSelectedOcrRecordChanged(OcrRecord? value)
    {
        if (value is null)
        {
            GeneratedAudioPath = "尚未生成音频";
            if (SelectedOcrHistoryRecord is not null)
            {
                SelectedOcrHistoryRecord = null;
            }
            if (SelectedTreeItem is OcrRecord)
            {
                SelectedTreeItem = null;
            }
        }
        else
        {
            OcrTitle = value.Title;
            OcrText = value.Text;
            value.RefreshAudioStatus();
            GeneratedAudioPath = value.LatestAudioPath ?? "尚未生成音频";
            if (!ReferenceEquals(SelectedTreeItem, value))
            {
                SelectedTreeItem = value;
            }
        }

        OnPropertyChanged(nameof(CanGenerateSpeech));
        OnPropertyChanged(nameof(CanClearOcr));
        NotifyBookmarkChanged();
    }

    partial void OnSelectedOcrHistoryRecordChanged(OcrRecord? value)
    {
        if (value is not null && !ReferenceEquals(SelectedOcrRecord, value))
        {
            SelectedOcrRecord = value;
        }

        OnPropertyChanged(nameof(CanConfirmOcr));
        OnPropertyChanged(nameof(CanClearOcr));
    }

    private void NotifyBookmarkChanged()
    {
        OnPropertyChanged(nameof(HasSelectedBookmark));
        OnPropertyChanged(nameof(CanAttachOcr));
    }

    private void NotifyDocumentReadOnlyChanged()
    {
        OnPropertyChanged(nameof(IsDocumentReadOnly));
        OnPropertyChanged(nameof(CanModifyDocument));
        OnPropertyChanged(nameof(CanGenerateSpeech));
        OnPropertyChanged(nameof(CanClearOcr));
        OnPropertyChanged(nameof(CanAnnotate));
        OnPropertyChanged(nameof(CanSaveAnnotations));
        NotifyCaptureChanged();
        NotifyBookmarkChanged();
    }

    partial void OnSelectedDocumentChanged(PdfDocument? value) => NotifyDocumentReadOnlyChanged();

    partial void OnHasPendingOcrChanged(bool value) => OnPropertyChanged(nameof(CanCancelOcr));
    partial void OnIsOcrEnabledChanged(bool value) => NotifyCaptureChanged();
    partial void OnIsContinuousCaptureChanged(bool value) => NotifyCaptureChanged();
    partial void OnIsPagePreviewPaneVisibleChanged(bool value) =>
        OnPropertyChanged(nameof(IsBookmarkPaneVisible));

    private void NotifyNavigationChanged()
    {
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
    }

    private async void AutomationImportCompleted(object? sender, Guid documentId)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (documentId != _documentId)
            {
                return;
            }

            await LoadDocumentDataAsync();
            RefreshReadingPageOcr();
            StatusMessage = "已导入外部 OCR 与音频结果";
        });
    }

    public void Dispose()
    {
        _ocrQueueCancellation.Cancel();
        _ocrQueueSignal.Release();
        lock (_ocrCancellationLock)
        {
            foreach (var cancellation in _ocrCancellations.ToList())
            {
                cancellation.Cancel();
            }
        }
        _readingCancellation?.Cancel();
        _audioPlaybackService.PlaybackStateChanged -= AudioPlaybackStateChanged;
        _automationService.ImportCompleted -= AutomationImportCompleted;
        _automationService.Dispose();
        PageImage?.Dispose();
        ClearReadingPages();
        ClearPagePreviews();
        _pdfService.Dispose();
        _audioPlaybackService.Dispose();
        _documentOpenGate.Dispose();
        try
        {
            DeleteResources(_ocrRepository.RemoveUnattachedRecords());
        }
        catch (Exception)
        {
        }
    }
}
