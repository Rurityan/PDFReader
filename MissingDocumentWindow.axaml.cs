using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PDFReader;

public enum MissingDocumentAction
{
    None,
    Rebind,
    Postpone,
    Delete,
}

public partial class MissingDocumentWindow : Window
{
    public MissingDocumentWindow()
        : this("未命名 PDF", string.Empty)
    {
    }

    public MissingDocumentWindow(string title, string path)
    {
        InitializeComponent();
        DocumentTitleText.Text = $"文档：{title}";
        DocumentPathText.Text = $"原路径：{path}";
    }

    private void RebindClick(object? sender, RoutedEventArgs e)
    {
        Close(MissingDocumentAction.Rebind);
    }

    private void PostponeClick(object? sender, RoutedEventArgs e)
    {
        Close(MissingDocumentAction.Postpone);
    }

    private void DeleteClick(object? sender, RoutedEventArgs e)
    {
        Close(MissingDocumentAction.Delete);
    }
}
