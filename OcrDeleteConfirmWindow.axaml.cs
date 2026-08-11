using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PDFReader;

public partial class OcrDeleteConfirmWindow : Window
{
    public OcrDeleteConfirmWindow()
        : this("未命名 OCR", string.Empty)
    {
    }

    public OcrDeleteConfirmWindow(string title, string message)
    {
        InitializeComponent();
        MessageText.Text = $"OCR“{title}”{message}";
    }

    private void ConfirmClick(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }

    private void CancelClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
