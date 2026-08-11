using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PDFReader;

public enum UnsavedAnnotationAction
{
    Cancel,
    Discard,
    Save,
}

public partial class AnnotationUnsavedChangesWindow : Window
{
    public AnnotationUnsavedChangesWindow()
    {
        InitializeComponent();
    }

    private void CancelClick(object? sender, RoutedEventArgs e)
    {
        Close(UnsavedAnnotationAction.Cancel);
    }

    private void DiscardClick(object? sender, RoutedEventArgs e)
    {
        Close(UnsavedAnnotationAction.Discard);
    }

    private void SaveClick(object? sender, RoutedEventArgs e)
    {
        Close(UnsavedAnnotationAction.Save);
    }
}
