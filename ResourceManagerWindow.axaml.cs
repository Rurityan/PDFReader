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
    private readonly IReadOnlyList<OcrRecord> _records;
    private readonly Func<OcrRecord, Task<bool>> _deleteOcr;
    private readonly Func<OcrRecord, Task<bool>> _deleteAudios;
    private readonly ObservableCollection<OcrRecord> _filteredRecords = new();
    private bool _isInitialized;

    public bool CanModifyResources { get; }

    public ResourceManagerWindow()
        : this(
            Array.Empty<OcrRecord>(),
            _ => Task.FromResult(false),
            _ => Task.FromResult(false),
            false)
    {
    }

    public ResourceManagerWindow(
        IReadOnlyList<OcrRecord> records,
        Func<OcrRecord, Task<bool>> deleteOcr,
        Func<OcrRecord, Task<bool>> deleteAudios,
        bool canModifyResources = true)
    {
        _records = records ?? Array.Empty<OcrRecord>();
        _deleteOcr = deleteOcr ?? (_ => Task.FromResult(false));
        _deleteAudios = deleteAudios ?? (_ => Task.FromResult(false));
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
            record.RefreshAudioStatus();
            _filteredRecords.Add(record);
        }

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
                _filteredRecords.Remove(record);
                EmptyText.IsVisible = _filteredRecords.Count == 0;
            }
        }

        e.Handled = true;
    }

    private void CloseClick(object? sender, RoutedEventArgs e) => Close();
}
