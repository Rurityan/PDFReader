using System;
using Avalonia.Input;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PDFReader;

public sealed record BookmarkCreationRequest(string Title, int PageNumber);

public partial class BookmarkCreateWindow : Window
{
    public BookmarkCreateWindow()
        : this(1, 1)
    {
    }

    public BookmarkCreateWindow(int currentPage, int pageCount)
    {
        InitializeComponent();
        PageInput.Maximum = Math.Max(1, pageCount);
        PageInput.Value = Math.Clamp(currentPage, 1, Math.Max(1, pageCount));
        TitleInput.Text = $"第 {currentPage} 页";
    }

    private void ConfirmClick(object? sender, RoutedEventArgs e)
    {
        var title = TitleInput.Text?.Trim();
        var page = (int)Math.Round(PageInput.Value ?? 1);
        if (!string.IsNullOrWhiteSpace(title))
        {
            Close(new BookmarkCreationRequest(title, page));
        }
    }

    private void WindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        ConfirmClick(this, new RoutedEventArgs());
        e.Handled = true;
    }

    private void CancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}
