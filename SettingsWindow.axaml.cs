using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PDFReader.Models;
using PDFReader.ViewModels;

namespace PDFReader;

public partial class SettingsWindow : Window
{
    private readonly Func<ReaderSettings, Task> _applySettings;

    public SettingsWindow()
        : this(new ReaderSettings(), _ => Task.CompletedTask)
    {
    }

    public SettingsWindow(ReaderSettings settings, Func<ReaderSettings, Task> applySettings)
    {
        InitializeComponent();
        DataContext = new SettingsWindowViewModel(settings);
        _applySettings = applySettings;
    }

    private async void SaveClick(object? sender, RoutedEventArgs e)
    {
        await _applySettings(((SettingsWindowViewModel)DataContext!).ToSettings());
        Close();
    }

    private void CancelClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
