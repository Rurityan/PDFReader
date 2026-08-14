using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PDFReader.Models;

namespace PDFReader;

public partial class ResourceManagerWindow : Window
{
    private readonly List<OcrRecord> _records;
    private readonly Func<OcrRecord, Task<bool>> _deleteOcr;
    private readonly Func<OcrRecord, Task<bool>> _deleteAudios;
    private readonly IReadOnlyList<TtsVoiceModelOption> _voiceModels;
    private readonly Func<OcrRecord, string, Task> _generateAudio;
    private readonly IReadOnlyList<Bookmark> _bookmarks;
    private readonly Func<OcrRecord, Bookmark, Task<bool>> _attachToBookmark;
    private readonly ObservableCollection<OcrRecord> _filteredRecords = new();
    private bool _isInitialized;

    public bool CanModifyResources { get; }
    public bool CanAttachToBookmark => CanModifyResources && _bookmarks.Count > 0;

    public ResourceManagerWindow()
        : this(
            Array.Empty<OcrRecord>(),
            _ => Task.FromResult(false),
            _ => Task.FromResult(false),
            Array.Empty<TtsVoiceModelOption>(),
            (_, _) => Task.CompletedTask,
            Array.Empty<Bookmark>(),
            (_, _) => Task.FromResult(false),
            false)
    {
    }

    public ResourceManagerWindow(
        IReadOnlyList<OcrRecord> records,
        Func<OcrRecord, Task<bool>> deleteOcr,
        Func<OcrRecord, Task<bool>> deleteAudios,
        IReadOnlyList<TtsVoiceModelOption> voiceModels,
        Func<OcrRecord, string, Task> generateAudio,
        IReadOnlyList<Bookmark> bookmarks,
        Func<OcrRecord, Bookmark, Task<bool>> attachToBookmark,
        bool canModifyResources = true)
    {
        _records = records?.ToList() ?? new List<OcrRecord>();
        _deleteOcr = deleteOcr ?? (_ => Task.FromResult(false));
        _deleteAudios = deleteAudios ?? (_ => Task.FromResult(false));
        _voiceModels = voiceModels ?? Array.Empty<TtsVoiceModelOption>();
        _generateAudio = generateAudio ?? ((_, _) => Task.CompletedTask);
        _bookmarks = bookmarks ?? Array.Empty<Bookmark>();
        _attachToBookmark = attachToBookmark ?? ((_, _) => Task.FromResult(false));
        CanModifyResources = canModifyResources;
        InitializeComponent();
        DataContext = this;
        RecordList.ItemsSource = _filteredRecords;
        ReadOnlyText.Text = "当前 PDF 为只读，资源仅可查看";
        ReadOnlyText.IsVisible = !CanModifyResources;
        _isInitialized = true;
        ApplyFilter();
    }

    private void SearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isInitialized)
        {
            ApplyFilter();
        }
    }

    private void FilterChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isInitialized)
        {
            ApplyFilter();
        }
    }

    private void ApplyFilter()
    {
        foreach (var record in _records)
        {
            record.RefreshAudioStatus();
        }

        var search = SearchBox?.Text?.Trim() ?? string.Empty;
        var filter = FilterBox?.SelectedIndex ?? 0;
        var records = _records.Where(record =>
                string.IsNullOrWhiteSpace(search)
                || record.Title.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Where(record => filter == 0
                || (filter == 1 && record.BookmarkId is not null)
                || (filter == 2 && record.BookmarkId is null))
            .ToList();

        _filteredRecords.Clear();
        foreach (var record in records)
        {
            _filteredRecords.Add(record);
        }

        ResourceSummaryText.Text = $"OCR {_records.Count} 条 · VOC {_records.Count(record => record.HasAudio)} 条";
        EmptyText.IsVisible = _filteredRecords.Count == 0;
    }

    private OcrRecord? GetRecord(object? sender) =>
        sender is Control control && control.DataContext is OcrRecord record ? record : null;

    private async void DeleteAudioClick(object? sender, RoutedEventArgs e)
    {
        var record = GetRecord(sender);
        if (record is null || record.TtsAudios.Count == 0)
        {
            return;
        }

        var dialog = new OcrDeleteConfirmWindow(record.Title, "的全部音频将被删除，OCR 正文会保留，是否继续？");
        if (await dialog.ShowDialog<bool>(this))
        {
            if (await _deleteAudios(record))
            {
                ApplyFilter();
            }
        }

        e.Handled = true;
    }

    private void GenerateAudioClick(object? sender, RoutedEventArgs e)
    {
        var record = GetRecord(sender);
        if (record is null || record.HasAudio || !CanModifyResources)
        {
            return;
        }

        var menu = new ContextMenu();
        if (_voiceModels.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "未配置 Voice Model", IsEnabled = false });
        }
        else
        {
            foreach (var voiceModel in _voiceModels)
            {
                var item = new MenuItem { Header = voiceModel.Name, Tag = (record, voiceModel.Name) };
                item.Click += GenerateAudioWithVoiceClick;
                menu.Items.Add(item);
            }
        }

        menu.Open((Control)sender!);
        e.Handled = true;
    }

    private async void GenerateAudioWithVoiceClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is ValueTuple<OcrRecord, string> request)
        {
            await _generateAudio(request.Item1, request.Item2);
            request.Item1.RefreshAudioStatus();
            ApplyFilter();
        }

        e.Handled = true;
    }

    private async void AttachToBookmarkClick(object? sender, RoutedEventArgs e)
    {
        var record = GetRecord(sender);
        if (record is null || record.BookmarkId is not null || !CanModifyResources)
        {
            return;
        }

        if (_bookmarks.Count == 0)
        {
            return;
        }

        var dialog = new BookmarkParentPickerWindow(
            _bookmarks,
            "挂载 OCR 到书签",
            "搜索并选择目标书签");
        var bookmark = await dialog.ShowDialog<Bookmark?>(this);
        if (bookmark is not null && await _attachToBookmark(record, bookmark))
        {
            ApplyFilter();
        }

        e.Handled = true;
    }

    private async void DeleteOcrClick(object? sender, RoutedEventArgs e)
    {
        var record = GetRecord(sender);
        if (record is null)
        {
            return;
        }

        var dialog = new OcrDeleteConfirmWindow(
            record.Title,
            "及其关联的音频、截图资源将被删除，是否继续？");
        if (await dialog.ShowDialog<bool>(this))
        {
            if (await _deleteOcr(record))
            {
                _records.Remove(record);
                ApplyFilter();
            }
        }

        e.Handled = true;
    }

    private void CloseClick(object? sender, RoutedEventArgs e) => Close();
}
