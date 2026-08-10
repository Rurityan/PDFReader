using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PDFReader;

public partial class BookmarkNameWindow : Window
{
    public BookmarkNameWindow()
        : this(string.Empty)
    {
    }

    public BookmarkNameWindow(string currentName)
    {
        InitializeComponent();
        NameInput.Text = currentName;
    }

    private void ConfirmClick(object? sender, RoutedEventArgs e)
    {
        var title = NameInput.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(title))
        {
            Close(title);
        }
    }

    private void CancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}
