using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PDFReader;

public partial class DocumentRemovalConfirmWindow : Window
{
    public DocumentRemovalConfirmWindow()
        : this(1)
    {
    }

    public DocumentRemovalConfirmWindow(int count)
    {
        InitializeComponent();
        MessageText.Text = $"将移除 {count} 个 PDF 记录及其书签、OCR 记录、音频和截图资源。若尚未进行“全量导出 PDF”，这些元数据将无法恢复。";
    }

    private void ConfirmClick(object? sender, RoutedEventArgs e) => Close(true);
    private void CancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
