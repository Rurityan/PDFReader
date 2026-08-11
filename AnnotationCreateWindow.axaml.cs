using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PDFReader;

public sealed record AnnotationCreationRequest(string Title, string Contents);

public partial class AnnotationCreateWindow : Window
{
    public AnnotationCreateWindow()
        : this(null, null)
    {
    }

    public AnnotationCreateWindow(string? title, string? contents)
    {
        InitializeComponent();
        TitleInput.Text = string.IsNullOrWhiteSpace(title) ? "PDF Reader" : title;
        ContentsInput.Text = contents ?? string.Empty;
    }

    private void ConfirmClick(object? sender, RoutedEventArgs e)
    {
        var title = TitleInput.Text?.Trim();
        var contents = ContentsInput.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(contents))
        {
            Close(new AnnotationCreationRequest(
                string.IsNullOrWhiteSpace(title) ? "PDF Reader" : title,
                contents));
        }
    }

    private void CancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}
