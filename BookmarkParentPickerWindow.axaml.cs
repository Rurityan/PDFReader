using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PDFReader.Models;

namespace PDFReader;

public partial class BookmarkParentPickerWindow : Window
{
    private readonly IReadOnlyList<Bookmark> _candidates;
    private readonly ObservableCollection<Bookmark> _filtered = new();

    public BookmarkParentPickerWindow()
        : this(Array.Empty<Bookmark>())
    {
    }

    public BookmarkParentPickerWindow(IEnumerable<Bookmark> candidates)
    {
        InitializeComponent();
        _candidates = candidates.ToList();
        DataContext = _filtered;
        ApplyFilter();
    }

    private void SearchInputTextChanged(object? sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var keyword = SearchInput.Text?.Trim() ?? string.Empty;
        _filtered.Clear();
        foreach (var bookmark in _candidates.Where(item => item.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        {
            _filtered.Add(bookmark);
        }
    }

    private void ConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (BookmarkList.SelectedItem is Bookmark bookmark)
        {
            Close(bookmark);
        }
    }

    private void CancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
