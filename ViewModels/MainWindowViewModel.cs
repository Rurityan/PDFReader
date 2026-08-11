using System;
using System.Collections.Generic;
using System.IO;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
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
    private readonly SettingsService _settingsService = new();
    private readonly TtsService _ttsService = new();
    private readonly AudioPlaybackService _audioPlaybackService = new();
    private readonly PdfEditingService _pdfEditingService = new();
    private readonly PdfDocumentRepository _documentRepository = new();
    private OcrResultRepository _ocrRepository;
    private BookmarkRepository _bookmarkRepository;
    private ReaderSettings _settings;
    private Guid _documentId;
    private CancellationTokenSource? _ocrCancellation;
    private CancellationTokenSource? _readingCancellation;
    private Bitmap? _pageImage;
    private int _currentPage;
    private double _zoom = 1.25;
    private readonly List<PdfAnnotationChange> _pendingAnnotationChanges = new();

    [ObservableProperty]
    private string _documentTitle = "未打开文档";

    [ObservableProperty]
    private string _documentPath = "从文件菜单打开一个 PDF 文档";

    [ObservableProperty]
    private PdfDocument? _selectedDocument;

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
    private AnnotationTool _annotationTool = AnnotationTool.Freehand;

    [ObservableProperty]
    private Color _annotationColor = Color.Parse("#2B6CB0");

    [ObservableProperty]
    private decimal _annotationStrokeWidth = 2;

    private bool _captureOnce;

    [ObservableProperty]
    private bool _hasPendingOcr;

    private double _pendingOcrX;
    private double _pendingOcrY;
    private double _pendingOcrWidth;
    private double _pendingOcrHeight;
    private double _pendingOcrZoom;
    private string? _pendingCapturePath;

    [ObservableProperty]
    private bool _enableOcrCaptureCache;

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

    public ObservableCollection<OcrRecord> OcrHistory { get; } = new();
    public ObservableCollection<OcrRecord> CurrentPageOcrRecords { get; } = new();
    public ObservableCollection<PdfAnnotationInfo> CurrentPageAnnotations { get; } = new();
    public ObservableCollection<Bookmark> Bookmarks { get; } = new();
    public ObservableCollection<PdfDocument> Documents { get; } = new();

    public MainWindowViewModel()
    {
        var settings = _settingsService.Load();
        _settings = settings;
        _ocrRepository = new OcrResultRepository();
        _bookmarkRepository = new BookmarkRepository();
        DeleteResources(_ocrRepository.RemoveUnattachedRecords());
        EnableOcrCaptureCache = settings.EnableOcrCaptureCache;
        OcrCaptureDirectory = settings.OcrCaptureDirectory;
        AudioDirectory = settings.AudioDirectory;
        TtsBaseUrl = settings.TtsBaseUrl;
        TtsApiKey = settings.TtsApiKey;
        TtsModelType = settings.TtsModelType;
        TtsVoiceModel = settings.TtsVoiceModel;
    }

    public async Task InitializeAsync()
    {
        try
        {
            Documents.Clear();
            var documents = await _documentRepository.GetAllAsync();
            foreach (var document in documents)
            {
                document.RefreshPathStatus();
                Documents.Add(document);
            }

            StatusMessage = documents.Count == 0
                ? "暂无已保存的 PDF 文档"
                : $"已加载 {documents.Count} 个 PDF 文档";
        }
        catch (Exception exception)
        {
            StatusMessage = $"加载 PDF 文档列表失败: {exception.Message}";
        }
    }

    public void SetStatus(string message) => StatusMessage = message;

    public void ClearOcrHistorySelection()
    {
        SelectedOcrHistoryRecord = null;
    }

    public async Task DeleteOcrRecordAsync(OcrRecord? record)
    {
        if (record is null || !HasDocument || IsBusy || IsOcrBusy || IsTtsBusy)
        {
            return;
        }

        try
        {
            DeleteResources(await _ocrRepository.DeleteAsync(record.Id));
            OcrHistory.Remove(record);
            if (ReferenceEquals(SelectedOcrRecord, record))
            {
                SelectedOcrRecord = null;
            }

            if (ReferenceEquals(SelectedOcrHistoryRecord, record))
            {
                SelectedOcrHistoryRecord = null;
            }

            RefreshBookmarkDisplayTree();
            RefreshCurrentPageOcr();
            StatusMessage = "OCR 记录及其音频资源已删除";
        }
        catch (Exception exception)
        {
            StatusMessage = $"删除 OCR 记录失败: {exception.Message}";
        }
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
    public bool HasSelectedBookmark => SelectedBookmark is not null;
    public bool CanAttachOcr => SelectedBookmark is not null
        && SelectedOcrRecord is not null
        && !IsBusy
        && !IsOcrBusy;
    public bool CanGenerateSpeech => HasDocument && SelectedOcrRecord is not null && !IsTtsBusy;
    public bool CanClearOcr => HasDocument && SelectedOcrRecord is not null
        && !IsBusy && !IsOcrBusy && !IsTtsBusy;
    public bool CanAnnotate => HasDocument && IsAnnotationMode
        && !IsBusy && !IsOcrBusy && !IsTtsBusy && !IsReadingCurrentPage;
    public bool CanReadCurrentPage => HasDocument
        && (IsReadingCurrentPage
            || (!IsBusy && !IsTtsBusy && !IsAnnotationMode
                && CurrentPageOcrRecords.Any(record => record.HasAudio)));
    public bool CanUndoBookmarkDelete => _deletedBookmarkHistory.Count > 0 && !IsBusy;
    public bool HasPendingAnnotationChanges => _pendingAnnotationChanges.Count > 0;
    public bool CanSaveAnnotations => HasDocument && HasPendingAnnotationChanges && !IsBusy;
    public int CurrentPageNumber => _currentPage + 1;
    public double CurrentZoom => _zoom;
    public int DocumentPageCount => Math.Max(1, _pdfService.PageCount);
    public string CurrentPageOcrButtonText => IsCurrentPageOcrVisible
        ? "隐藏当前页 OCR"
        : "显示当前页 OCR";
    public string AnnotationButtonText => IsAnnotationMode ? "取消标注" : "标注";
    public string AnnotationColorHex => $"#{AnnotationColor.R:X2}{AnnotationColor.G:X2}{AnnotationColor.B:X2}";
    public SolidColorBrush AnnotationColorBrush => new(AnnotationColor);
    public string AnnotationStrokeWidthText => $"{AnnotationStrokeWidth:0} pt";
    public string AnnotationToolText => AnnotationTool switch
    {
        AnnotationTool.Line => "画线",
        AnnotationTool.Freehand => "自由绘制",
        AnnotationTool.Rectangle => "方框",
        AnnotationTool.Highlight => "高亮",
        AnnotationTool.Eraser => "橡皮擦",
        _ => "文本标注",
    };
    public string ReadCurrentPageButtonText => IsReadingCurrentPage ? "停止朗读" : "朗读";

    [RelayCommand]
    private void ToggleCurrentPageOcr()
    {
        if (!HasDocument)
        {
            return;
        }

        IsCurrentPageOcrVisible = !IsCurrentPageOcrVisible;
        StatusMessage = IsCurrentPageOcrVisible
            ? $"已显示第 {_currentPage + 1} 页的 {CurrentPageOcrRecords.Count} 条 OCR"
            : "已隐藏当前页 OCR 框";
    }

    [RelayCommand]
    private void ToggleAnnotation()
    {
        if (!HasDocument || IsBusy)
        {
            return;
        }

        if (IsAnnotationMode)
        {
            IsAnnotationMode = false;
            StatusMessage = "标注模式已取消";
        }
        else
        {
            CancelCaptureMode();
            IsAnnotationMode = true;
            AnnotationTool = AnnotationTool.Freehand;
            StatusMessage = "标注模式已开启，请框选要添加标注的区域";
        }
    }

    public void SelectAnnotationTool(AnnotationTool tool)
    {
        if (!HasDocument || IsBusy)
        {
            return;
        }

        CancelCaptureMode();
        IsAnnotationMode = true;
        AnnotationTool = tool;
        StatusMessage = tool switch
        {
            AnnotationTool.Eraser => "橡皮擦已开启，请沿标注拖动擦除",
            AnnotationTool.Freehand => "自由绘制已开启，可连续绘制",
            _ => $"已选择{AnnotationToolText}，请在页面上框选区域",
        };
    }

    public void SetAnnotationColor(Color color)
    {
        AnnotationColor = Color.FromRgb(color.R, color.G, color.B);
    }

    public void CancelAnnotationMode()
    {
        if (IsAnnotationMode)
        {
            IsAnnotationMode = false;
            StatusMessage = "标注已取消";
        }
    }

    [RelayCommand]
    private async Task ToggleReadCurrentPageAsync()
    {
        if (IsReadingCurrentPage)
        {
            _readingCancellation?.Cancel();
            return;
        }

        await ReadCurrentPageAsync();
    }

    public async Task ReadCurrentPageAsync()
    {
        if (!HasDocument || IsBusy || IsAnnotationMode)
        {
            return;
        }

        var records = CurrentPageOcrRecords
            .Select(record =>
            {
                record.RefreshAudioStatus();
                return record;
            })
            .Where(record => record.HasAudio && !string.IsNullOrWhiteSpace(record.LatestAudioPath))
            .ToList();
        if (records.Count == 0)
        {
            StatusMessage = "当前页没有可播放的 OCR 音频";
            OnPropertyChanged(nameof(CanReadCurrentPage));
            return;
        }

        var cancellation = new CancellationTokenSource();
        _readingCancellation = cancellation;
        IsReadingCurrentPage = true;
        StatusMessage = $"正在朗读当前页的 {records.Count} 条 OCR";
        try
        {
            foreach (var record in records)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                GeneratedAudioPath = record.LatestAudioPath!;
                await _audioPlaybackService.PlayAndWaitAsync(
                    record.LatestAudioPath!,
                    cancellation.Token);
            }

            StatusMessage = "当前页朗读完成";
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
        }
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

        RefreshCurrentPageAnnotations();
        StatusMessage = "标注删除已暂存，请保存标注写入 PDF";
        NotifyAnnotationSaveChanged();
        return Task.CompletedTask;
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
        return true;
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
        var temporaryPath = $"{documentPath}.{Guid.NewGuid():N}.annotations.tmp.pdf";
        try
        {
            IsBusy = true;
            StatusMessage = "正在保存标注...";
            CloseRenderedDocument();
            await Task.Run(() =>
            {
                _pdfEditingService.SaveCopy(documentPath, temporaryPath);
                foreach (var change in changes)
                {
                    var annotation = change.Annotation;
                    if (change.Kind == PdfAnnotationChangeKind.Delete)
                    {
                        _pdfEditingService.DeleteAnnotation(
                            temporaryPath,
                            temporaryPath,
                            annotation.PageNumber - 1,
                            annotation.Id);
                    }
                    else
                    {
                        switch (annotation.Type)
                        {
                            case PdfAnnotationType.Text:
                                _pdfEditingService.AddTextAnnotation(
                                    temporaryPath,
                                    temporaryPath,
                                    annotation.PageNumber - 1,
                                    annotation.X,
                                    annotation.Y,
                                    annotation.Width,
                                    annotation.Height,
                                    1,
                                    annotation.Title ?? "PDF Reader",
                                    annotation.Contents ?? string.Empty,
                                    annotation.Id);
                                break;
                            case PdfAnnotationType.Line:
                                _pdfEditingService.AddLineAnnotation(
                                    temporaryPath,
                                    temporaryPath,
                                    annotation.PageNumber - 1,
                                    annotation.StartX,
                                    annotation.StartY,
                                    annotation.EndX,
                                    annotation.EndY,
                                    1,
                                    annotation.Id,
                                    annotation.StrokeColor,
                                    annotation.StrokeWidth);
                                break;
                            case PdfAnnotationType.Highlight:
                                _pdfEditingService.AddHighlightAnnotation(
                                    temporaryPath,
                                    temporaryPath,
                                    annotation.PageNumber - 1,
                                    annotation.X,
                                    annotation.Y,
                                    annotation.Width,
                                    annotation.Height,
                                    1,
                                    annotation.Id);
                                break;
                            case PdfAnnotationType.Rectangle:
                                _pdfEditingService.AddRectangleAnnotation(
                                    temporaryPath,
                                    temporaryPath,
                                    annotation.PageNumber - 1,
                                    annotation.X,
                                    annotation.Y,
                                    annotation.Width,
                                    annotation.Height,
                                    1,
                                    annotation.Id,
                                    annotation.StrokeColor,
                                    annotation.StrokeWidth);
                                break;
                            case PdfAnnotationType.Freehand:
                                _pdfEditingService.AddFreehandAnnotation(
                                    temporaryPath,
                                    temporaryPath,
                                    annotation.PageNumber - 1,
                                    annotation.Points,
                                    1,
                                    annotation.Id,
                                    annotation.StrokeColor,
                                    annotation.StrokeWidth);
                                break;
                        }
                    }
                }

                File.Move(temporaryPath, documentPath, true);
            });
            _pendingAnnotationChanges.Clear();
            await ReopenRenderedDocumentAsync(documentPath, pageIndex);
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
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                }
            }

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
        if (!HasDocument || IsBusy)
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
        if (!HasDocument || IsBusy || string.IsNullOrWhiteSpace(outputPath))
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

    private void CloseRenderedDocument()
    {
        _readingCancellation?.Cancel();
        _audioPlaybackService.Stop();
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
                OcrHistory.Clear();
                Bookmarks.Clear();
                _deletedBookmarkHistory.Clear();
                OnPropertyChanged(nameof(CanUndoBookmarkDelete));
            }

            DeleteResources(await _documentRepository.DeleteAsync(document.Id));
            Documents.Remove(document);
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

    private async Task OpenDocumentCoreAsync(PdfDocument document)
    {
        try
        {
            IsBusy = true;
            StatusMessage = "正在打开文档...";
            await _pdfService.OpenAsync(document.FilePath);
            await _documentRepository.MarkOpenedAsync(document.Id);

            _documentId = document.Id;
            DocumentTitle = document.Title;
            DocumentPath = document.FilePath;
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

    private PdfDocument AddOrUpdateDocument(PdfDocument document)
    {
        var existing = Documents.SingleOrDefault(item => item.Id == document.Id);
        if (existing is null)
        {
            document.RefreshPathStatus();
            Documents.Insert(0, document);
            return document;
        }

        existing.FilePath = document.FilePath;
        existing.Title = document.Title;
        existing.RefreshPathStatus();
        return existing;
    }

    private async Task LoadDocumentDataAsync()
    {
        _pendingAnnotationChanges.Clear();
        NotifyAnnotationSaveChanged();
        OcrHistory.Clear();
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
            record.RefreshAudioStatus();
            OcrHistory.Add(record);
        }

        var bookmarks = await _bookmarkRepository.GetForDocumentAsync(_documentId);
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
        RefreshCurrentPageOcr();
    }

    private void RefreshBookmarkDisplayTree()
    {
        foreach (var bookmark in Bookmarks.SelectMany(EnumerateBookmarkTree))
        {
            bookmark.DisplayChildren.Clear();
            foreach (var child in bookmark.Children)
            {
                bookmark.DisplayChildren.Add(child);
            }

            foreach (var record in OcrHistory.Where(record => record.BookmarkId == bookmark.Id))
            {
                bookmark.DisplayChildren.Add(record);
            }
        }
    }

    private void RefreshCurrentPageOcr()
    {
        CurrentPageOcrRecords.Clear();
        foreach (var record in OcrHistory.Where(record => record.PageNumber == _currentPage + 1))
        {
            record.UpdateDisplayBounds(_zoom);
            CurrentPageOcrRecords.Add(record);
        }

        OnPropertyChanged(nameof(CanReadCurrentPage));
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
            var annotations = _pdfEditingService.GetAnnotations(DocumentPath, _currentPage)
                .Where(annotation => !deletedIds.Contains(annotation.Id))
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

    public async Task CreateBookmarkAsync(string? title, int pageNumber)
    {
        if (!HasDocument || IsBusy)
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
            RefreshBookmarkDisplayTree();
            StatusMessage = "书签已创建并自动保存";
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

        for (var parent = bookmark.Parent; parent is not null; parent = parent.Parent)
        {
            parent.IsExpanded = true;
        }

        SelectedBookmark = bookmark;
        StatusMessage = $"已定位到第 {CurrentPageNumber} 页书签：{bookmark.Title}";
    }

    [RelayCommand]
    private async Task AttachOcrToBookmarkAsync()
    {
        if (SelectedBookmark is null || SelectedOcrRecord is null || !HasDocument || IsBusy)
        {
            return;
        }

        try
        {
            await SaveBookmarkAndAncestorsAsync(SelectedBookmark);
            await _ocrRepository.AttachToBookmarkAsync(
                SelectedOcrRecord.Id,
                SelectedBookmark.Id);
            SelectedOcrRecord.BookmarkId = SelectedBookmark.Id;
            RefreshBookmarkDisplayTree();
            StatusMessage = "OCR 已挂载到书签，书签已自动保存";
            NotifyBookmarkChanged();
        }
        catch (Exception exception)
        {
            StatusMessage = $"挂载 OCR 失败: {exception.Message}";
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
        if (bookmark is null || !HasDocument || IsBusy)
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
        if (bookmark is null || !HasDocument || IsBusy)
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

        if (HasPendingOcr)
        {
            DiscardPendingOcr();
        }

        var cancellation = BeginOcrCancellation();
        var captureZoom = _zoom;
        try
        {
            IsOcrBusy = true;
            StatusMessage = "正在识别选区...";
            await using var imageStream = await _pdfService.RenderPageRegionAsync(
                _currentPage, x, y, width, height, captureZoom, cancellation.Token);
            if (EnableOcrCaptureCache)
            {
                LastCapturePath = await SaveDebugCaptureAsync(
                    imageStream,
                    _currentPage + 1,
                    OcrCaptureDirectory,
                    cancellation.Token);
                _pendingCapturePath = LastCapturePath;
            }
            else
            {
                LastCapturePath = "截图缓存未开启";
                _pendingCapturePath = null;
            }
            imageStream.Position = 0;
            StatusMessage = EnableOcrCaptureCache
                ? "选区截图已保存，正在识别..."
                : "正在识别选区...";
            var result = await _ocrService.RecognizeAsync(imageStream, cancellation.Token);
            OcrText = string.IsNullOrWhiteSpace(result.Text) ? "选区内未识别到文本。" : result.Text;
            OcrTitle = CreateDefaultOcrTitle(OcrText);
            SetPendingOcr(x, y, width, height, captureZoom);
            StatusMessage = $"选区 OCR 完成，识别到 {result.Lines.Count} 行，请确认后保存";
        }
        catch (OperationCanceledException)
        {
            DiscardPendingOcr();
            StatusMessage = "OCR 已停止";
        }
        catch (Exception exception)
        {
            DiscardPendingOcr();
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
        DiscardPendingOcr();
        OcrText = "识别结果已取消，未保存任何结果。";
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
        OcrCaptureDirectory = settings.OcrCaptureDirectory;
        AudioDirectory = settings.AudioDirectory;
        TtsBaseUrl = settings.TtsBaseUrl;
        TtsApiKey = settings.TtsApiKey;
        TtsModelType = settings.TtsModelType;
        TtsVoiceModel = settings.TtsVoiceModel;

        _ocrRepository = new OcrResultRepository();
        _bookmarkRepository = new BookmarkRepository();
        if (HasDocument)
        {
            await LoadDocumentDataAsync();
        }

        StatusMessage = "设置已保存";
    }

    [RelayCommand]
    private Task GenerateSpeechAsync() => GenerateSpeechForRecordAsync(SelectedOcrRecord);

    public async Task GenerateSpeechForRecordAsync(OcrRecord? record)
    {
        if (record is null || !HasDocument || IsTtsBusy)
        {
            StatusMessage = "请先选择已保存的 OCR 记录。";
            return;
        }

        try
        {
            SelectedOcrRecord = record;
            IsTtsBusy = true;
            StatusMessage = "正在生成语音...";
            var audioPath = await _ttsService.GenerateAsync(
                record.Text,
                CreateReaderSettings(),
                record.PageNumber);
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
            AudioDirectory = AudioDirectory,
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
                PdfDocumentId = _documentId,
                PageNumber = _currentPage + 1,
                X = _pendingOcrX,
                Y = _pendingOcrY,
                Width = _pendingOcrWidth,
                Height = _pendingOcrHeight,
                CaptureZoom = _pendingOcrZoom,
                Title = string.IsNullOrWhiteSpace(OcrTitle)
                    ? CreateDefaultOcrTitle(OcrText)
                    : OcrTitle.Trim(),
                Text = OcrText.Trim(),
                CapturePath = _pendingCapturePath,
                CreatedAtUtc = DateTime.UtcNow,
            };
            await _ocrRepository.AddAsync(record);
            OcrHistory.Insert(0, record);
            SelectedOcrRecord = record;
            OcrTitle = record.Title;
            RefreshCurrentPageOcr();
            GeneratedAudioPath = "尚未生成音频";
            _pendingCapturePath = null;
            HasPendingOcr = false;
            StatusMessage = "OCR 结果已保存，请选择书签进行挂载";
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

    private void SetPendingOcr(double x, double y, double width, double height, double captureZoom)
    {
        _pendingOcrX = x;
        _pendingOcrY = y;
        _pendingOcrWidth = width;
        _pendingOcrHeight = height;
        _pendingOcrZoom = captureZoom;
        HasPendingOcr = true;
    }

    private void DiscardPendingOcr()
    {
        if (!string.IsNullOrWhiteSpace(_pendingCapturePath))
        {
            DeleteResource(_pendingCapturePath);
            _pendingCapturePath = null;
        }

        HasPendingOcr = false;
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
            await using var stream = await _pdfService.RenderPageAsync(pageIndex, _zoom);
            var image = new Bitmap(stream);
            var oldImage = PageImage;
            PageImage = image;
            oldImage?.Dispose();
            _currentPage = pageIndex;
            PageIndicator = $"{_currentPage + 1} / {_pdfService.PageCount}";
            ZoomIndicator = $"{_zoom:P0}";
            RefreshCurrentPageOcr();
            RefreshCurrentPageAnnotations();
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
        OnPropertyChanged(nameof(CanGenerateSpeech));
        OnPropertyChanged(nameof(CanClearOcr));
        OnPropertyChanged(nameof(CanAnnotate));
        OnPropertyChanged(nameof(CanReadCurrentPage));
        OnPropertyChanged(nameof(CurrentPageOcrButtonText));
        if (!value)
        {
            _pendingAnnotationChanges.Clear();
            NotifyAnnotationSaveChanged();
            IsCurrentPageOcrVisible = false;
            CurrentPageOcrRecords.Clear();
            CurrentPageAnnotations.Clear();
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
        OnPropertyChanged(nameof(CanSaveAnnotations));
    }

    partial void OnIsOcrBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCancelOcr));
        NotifyCaptureChanged();
        NotifyBookmarkChanged();
        OnPropertyChanged(nameof(CanClearOcr));
    }

    partial void OnIsTtsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanGenerateSpeech));
        OnPropertyChanged(nameof(CanClearOcr));
        OnPropertyChanged(nameof(CanAnnotate));
        OnPropertyChanged(nameof(CanReadCurrentPage));
    }

    partial void OnIsAnnotationModeChanged(bool value)
    {
        OnPropertyChanged(nameof(AnnotationButtonText));
        OnPropertyChanged(nameof(CanAnnotate));
        OnPropertyChanged(nameof(CanReadCurrentPage));
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
    }

    partial void OnIsReadingCurrentPageChanged(bool value)
    {
        OnPropertyChanged(nameof(ReadCurrentPageButtonText));
        OnPropertyChanged(nameof(CanReadCurrentPage));
    }

    partial void OnIsCurrentPageOcrVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(CurrentPageOcrButtonText));
    }

    partial void OnSelectedBookmarkChanged(Bookmark? value)
    {
        if (!ReferenceEquals(SelectedTreeItem, value))
        {
            SelectedTreeItem = value;
        }
        NotifyBookmarkChanged();
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
    }

    private void NotifyBookmarkChanged()
    {
        OnPropertyChanged(nameof(HasSelectedBookmark));
        OnPropertyChanged(nameof(CanAttachOcr));
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
        _ocrCancellation?.Cancel();
        _readingCancellation?.Cancel();
        PageImage?.Dispose();
        _pdfService.Dispose();
        _audioPlaybackService.Dispose();
        try
        {
            DeleteResources(_ocrRepository.RemoveUnattachedRecords());
        }
        catch (Exception)
        {
        }
    }
}
