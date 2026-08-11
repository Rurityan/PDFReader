using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PDFReader;

public partial class OcrTextWindow : Window
{
    public OcrTextWindow()
        : this("OCR 正文", string.Empty)
    {
    }

    public OcrTextWindow(string title, string text)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = text;
    }

    private void CloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
