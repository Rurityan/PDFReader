using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Controls.Primitives;

namespace PDFReader;

public partial class AnnotationColorPickerWindow : Window
{
    private bool _updating;

    public AnnotationColorPickerWindow()
        : this(Color.Parse("#2B6CB0"))
    {
    }

    public AnnotationColorPickerWindow(Color initialColor)
    {
        InitializeComponent();
        SetColor(initialColor);
    }

    private void ChannelChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        UpdateColorFromChannels();
    }

    private void UpdateColorFromChannels()
    {
        var color = Color.FromRgb(
            (byte)Math.Clamp(Math.Round(RedSlider.Value), 0, 255),
            (byte)Math.Clamp(Math.Round(GreenSlider.Value), 0, 255),
            (byte)Math.Clamp(Math.Round(BlueSlider.Value), 0, 255));
        ColorPreview.Background = new SolidColorBrush(color);
        RedValueText.Text = color.R.ToString();
        GreenValueText.Text = color.G.ToString();
        BlueValueText.Text = color.B.ToString();
        HexTextBox.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        ValidationText.Text = string.Empty;
    }

    private void SetColor(Color color)
    {
        _updating = true;
        RedSlider.Value = color.R;
        GreenSlider.Value = color.G;
        BlueSlider.Value = color.B;
        _updating = false;
        UpdateColorFromChannels();
    }

    private void ApplyClick(object? sender, RoutedEventArgs e)
    {
        if (!Color.TryParse(HexTextBox.Text?.Trim() ?? string.Empty, out var color))
        {
            ValidationText.Text = "请输入有效的 HEX 颜色，例如 #2B6CB0";
            return;
        }

        Close(Color.FromRgb(color.R, color.G, color.B));
    }

    private void CancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}
