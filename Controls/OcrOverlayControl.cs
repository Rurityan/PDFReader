using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using PDFReader.Models;

namespace PDFReader.Controls;

public sealed class OcrOverlayControl : Canvas
{
    public static readonly RoutedEvent<OcrAudioRequestedEventArgs> AudioRequestedEvent =
        RoutedEvent.Register<OcrOverlayControl, OcrAudioRequestedEventArgs>("AudioRequested", RoutingStrategies.Bubble);

    public static readonly StyledProperty<IEnumerable<OcrRecord>?> RecordsProperty =
        AvaloniaProperty.Register<OcrOverlayControl, IEnumerable<OcrRecord>?>(nameof(Records));

    private INotifyCollectionChanged? _recordsNotifier;

    public IEnumerable<OcrRecord>? Records
    {
        get => GetValue(RecordsProperty);
        set => SetValue(RecordsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RecordsProperty)
        {
            SetRecords(change.GetNewValue<IEnumerable<OcrRecord>?>());
        }
    }

    private void SetRecords(IEnumerable<OcrRecord>? records)
    {
        if (_recordsNotifier is not null)
        {
            _recordsNotifier.CollectionChanged -= RecordsChanged;
        }

        _recordsNotifier = records as INotifyCollectionChanged;
        if (_recordsNotifier is not null)
        {
            _recordsNotifier.CollectionChanged += RecordsChanged;
        }

        Rebuild();
    }

    private void RecordsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        Children.Clear();
        if (Records is null)
        {
            return;
        }

        foreach (var record in Records)
        {
            var box = new Border
            {
                Width = record.DisplayWidth,
                Height = record.DisplayHeight,
                BorderBrush = new SolidColorBrush(Color.Parse(record.IsPersisted ? "#2B6CB0" : "#D97706")),
                BorderThickness = new Thickness(2),
                Background = new SolidColorBrush(Color.Parse(record.IsPersisted ? "#224A90E2" : "#22F59E0B")),
                IsHitTestVisible = false,
            };
            SetLeft(box, record.DisplayX);
            SetTop(box, record.DisplayY);
            Children.Add(box);

            var audioButton = new Button
            {
                Width = 30,
                Height = 28,
                Content = "🔊",
                Background = new SolidColorBrush(Color.Parse("#AA2B6CB0")),
                Foreground = Brushes.White,
                Opacity = 0.78,
                Padding = new Thickness(4, 2),
            };
            ToolTip.SetTip(audioButton, "生成或播放 OCR 音频");
            audioButton.Click += (_, _) => RaiseEvent(new OcrAudioRequestedEventArgs(AudioRequestedEvent, record));
            SetLeft(audioButton, record.DisplayX + Math.Max(0, record.DisplayWidth - audioButton.Width));
            SetTop(audioButton, record.DisplayY);
            Children.Add(audioButton);
        }
    }
}

public sealed class OcrAudioRequestedEventArgs : RoutedEventArgs
{
    public OcrAudioRequestedEventArgs(RoutedEvent route, OcrRecord record)
        : base(route)
    {
        Record = record;
    }

    public OcrRecord Record { get; }
}
