using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PDFReader.Models;

namespace PDFReader;

public partial class PdfDocumentPickerWindow : Window
{
    private readonly System.Func<IReadOnlyList<string>, Task<IReadOnlyList<PdfDocument>>> _addPdfFiles;

    public PdfDocumentPickerWindow()
        : this(
            new ObservableCollection<PdfDocument>(),
            _ => Task.FromResult<IReadOnlyList<PdfDocument>>(Array.Empty<PdfDocument>()))
    {
    }

    public PdfDocumentPickerWindow(
        ObservableCollection<PdfDocument> documents,
        System.Func<IReadOnlyList<string>, Task<IReadOnlyList<PdfDocument>>> addPdfFiles)
    {
        InitializeComponent();
        DataContext = documents;
        _addPdfFiles = addPdfFiles;
    }

    private async void AddNewPdfClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "添加新的 PDF 文件",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("PDF 文档") { Patterns = new[] { "*.pdf" } },
            },
        });

        var paths = files.Select(file => file.Path.LocalPath).ToList();
        if (paths.Count == 0)
        {
            return;
        }

        var addedDocuments = await _addPdfFiles(paths);
        var selectedItems = DocumentList.SelectedItems;
        if (selectedItems is null)
        {
            return;
        }

        foreach (var document in addedDocuments)
        {
            if (!selectedItems.Contains(document))
            {
                selectedItems.Add(document);
            }
        }
    }

    private void ImportClick(object? sender, RoutedEventArgs e)
    {
        var selected = (DocumentList.SelectedItems ?? Array.Empty<object>())
            .OfType<PdfDocument>()
            .ToList();
        Close(selected);
    }

    private void CancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}
