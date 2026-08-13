using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PDFReader;

public enum OcrCrossPageAttachAction
{
    Cancel,
    AttachCrossPage,
    CreateBookmark,
}

public partial class OcrCrossPageAttachWindow : Window
{
    public OcrCrossPageAttachWindow()
        : this(1, "未命名书签", 1)
    {
    }

    public OcrCrossPageAttachWindow(int ocrPageNumber, string bookmarkTitle, int bookmarkPageNumber)
    {
        InitializeComponent();
        MessageText.Text = $"当前 OCR 位于第 {ocrPageNumber} 页，选中书签“{bookmarkTitle}”位于第 {bookmarkPageNumber} 页。";
    }

    private void AttachCrossPageClick(object? sender, RoutedEventArgs e) => Close(OcrCrossPageAttachAction.AttachCrossPage);

    private void CreateBookmarkClick(object? sender, RoutedEventArgs e) => Close(OcrCrossPageAttachAction.CreateBookmark);

    private void CancelClick(object? sender, RoutedEventArgs e) => Close(OcrCrossPageAttachAction.Cancel);
}
