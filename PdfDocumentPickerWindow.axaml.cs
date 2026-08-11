using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PDFReader.Models;

namespace PDFReader;

public partial class PdfDocumentPickerWindow : Window
{
    private readonly System.Func<IReadOnlyList<string>, Task<IReadOnlyList<PdfDocument>>> _addPdfFiles;
    private readonly Func<PdfDocument, bool, Task> _setArchived;
    private readonly Func<PdfDocument, Task> _removeDocument;

    public PdfDocumentPickerWindow()
        : this(
            new ObservableCollection<PdfDocument>(),
            new ObservableCollection<PdfDocument>(),
            _ => Task.FromResult<IReadOnlyList<PdfDocument>>(Array.Empty<PdfDocument>()),
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask)
    {
    }

    public PdfDocumentPickerWindow(
        ObservableCollection<PdfDocument> documents,
        ObservableCollection<PdfDocument> archivedDocuments,
        System.Func<IReadOnlyList<string>, Task<IReadOnlyList<PdfDocument>>> addPdfFiles,
        Func<PdfDocument, bool, Task> setArchived,
        Func<PdfDocument, Task> removeDocument)
    {
        InitializeComponent();
        ActiveDocuments = documents;
        ArchivedDocuments = archivedDocuments;
        DataContext = this;
        _addPdfFiles = addPdfFiles;
        _setArchived = setArchived;
        _removeDocument = removeDocument;
        ActiveDocuments.CollectionChanged += DocumentsCollectionChanged;
        ArchivedDocuments.CollectionChanged += DocumentsCollectionChanged;
        UpdateDocumentPanelLayout();
    }

    public ObservableCollection<PdfDocument> ActiveDocuments { get; }
    public ObservableCollection<PdfDocument> ArchivedDocuments { get; }

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
            .Concat((ArchivedDocumentList.SelectedItems ?? Array.Empty<object>()).OfType<PdfDocument>())
            .ToList();
        Close(selected);
    }

    private void CancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private async void ArchiveClick(object? sender, RoutedEventArgs e)
    {
        foreach (var document in (DocumentList.SelectedItems ?? Array.Empty<object>()).OfType<PdfDocument>().ToList())
            await _setArchived(document, true);
        UpdateSelectionActions();
    }

    private async void RestoreClick(object? sender, RoutedEventArgs e)
    {
        foreach (var document in (ArchivedDocumentList.SelectedItems ?? Array.Empty<object>()).OfType<PdfDocument>().ToList())
            await _setArchived(document, false);
        UpdateSelectionActions();
    }

    private async void RemoveClick(object? sender, RoutedEventArgs e)
    {
        var selected = (DocumentList.SelectedItems ?? Array.Empty<object>()).OfType<PdfDocument>()
            .Concat((ArchivedDocumentList.SelectedItems ?? Array.Empty<object>()).OfType<PdfDocument>()).Distinct().ToList();
        if (selected.Count == 0) return;
        var dialog = new DocumentRemovalConfirmWindow(selected.Count);
        if (!await dialog.ShowDialog<bool>(this)) return;
        foreach (var document in selected) await _removeDocument(document);
        UpdateSelectionActions();
    }

    private void ActiveSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if ((DocumentList.SelectedItems?.Count ?? 0) > 0)
        {
            ArchivedDocumentList.SelectedItems?.Clear();
        }
        UpdateSelectionActions();
    }

    private void ArchivedSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if ((ArchivedDocumentList.SelectedItems?.Count ?? 0) > 0)
        {
            DocumentList.SelectedItems?.Clear();
        }
        UpdateSelectionActions();
    }

    private void PaneExpandedChanged(object? sender, RoutedEventArgs e) => UpdateDocumentPanelLayout();

    private void DocumentsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            if (ReferenceEquals(sender, ActiveDocuments)) ActiveExpander.IsChecked = true;
            if (ReferenceEquals(sender, ArchivedDocuments)) ArchivedExpander.IsChecked = true;
        }
        UpdateDocumentPanelLayout();
    }

    private void UpdateSelectionActions()
    {
        var activeSelected = DocumentList.SelectedItems?.Count ?? 0;
        var archivedSelected = ArchivedDocumentList.SelectedItems?.Count ?? 0;
        ArchiveButton.IsEnabled = activeSelected > 0;
        RestoreButton.IsEnabled = archivedSelected > 0;
        RemoveButton.IsEnabled = activeSelected + archivedSelected > 0;
        ImportButton.IsEnabled = activeSelected + archivedSelected > 0;
    }

    private void UpdateDocumentPanelLayout()
    {
        var activeExpanded = ActiveExpander.IsChecked == true;
        var archivedExpanded = ArchivedExpander.IsChecked == true;
        if (ActiveDocuments.Count == 0 && activeExpanded)
        {
            ActiveExpander.IsChecked = false;
            return;
        }
        if (ArchivedDocuments.Count == 0 && archivedExpanded)
        {
            ArchivedExpander.IsChecked = false;
            return;
        }
        DocumentList.IsVisible = activeExpanded;
        ArchivedDocumentList.IsVisible = archivedExpanded;
        if (!activeExpanded)
        {
            DocumentPanels.RowDefinitions[0].Height = GridLength.Auto;
            DocumentPanels.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);
            return;
        }
        if (!archivedExpanded)
        {
            DocumentPanels.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            DocumentPanels.RowDefinitions[2].Height = GridLength.Auto;
            return;
        }

        // Both open panes use a predictable 60 / 40 split; each list scrolls independently.
        DocumentPanels.RowDefinitions[0].Height = new GridLength(3, GridUnitType.Star);
        DocumentPanels.RowDefinitions[2].Height = new GridLength(2, GridUnitType.Star);
    }
}
