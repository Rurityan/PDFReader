using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PDFReader;

public partial class BookmarkDeleteConfirmWindow : Window
{
    public BookmarkDeleteConfirmWindow()
        : this("未命名书签", 0)
    {
    }

    public BookmarkDeleteConfirmWindow(string title, int childCount)
    {
        InitializeComponent();
        MessageText.Text = $"书签“{title}”下有 {childCount} 个直接子书签。删除后整个书签子树都会被移除，但可以使用“撤回删除”恢复。";
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
