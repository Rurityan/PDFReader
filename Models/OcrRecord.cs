using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace PDFReader.Models;

public sealed class OcrRecord : INotifyPropertyChanged
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PdfDocumentId { get; set; }
    public Guid? BookmarkId { get; set; }
    public int PageNumber { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double CaptureZoom { get; set; } = 1.0;
    public string Title { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string? CapturePath { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public PdfDocument? PdfDocument { get; set; }
    public Bookmark? Bookmark { get; set; }
    public ICollection<TtsAudioRecord> TtsAudios { get; } = new List<TtsAudioRecord>();

    [NotMapped]
    public bool HasAudio { get; private set; }

    [NotMapped]
    public string? LatestAudioPath { get; private set; }

    [NotMapped]
    public string AudioStatusText => HasAudio ? "音频：已生成" : "音频：未生成";

    [NotMapped]
    public double DisplayX { get; private set; }

    [NotMapped]
    public double DisplayY { get; private set; }

    [NotMapped]
    public double DisplayWidth { get; private set; }

    [NotMapped]
    public double DisplayHeight { get; private set; }

    [NotMapped]
    public double OverlayWidth => DisplayWidth + 34;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RefreshAudioStatus()
    {
        var path = TtsAudios
            .OrderByDescending(audio => audio.CreatedAtUtc)
            .Select(audio => audio.FilePath)
            .FirstOrDefault(File.Exists);
        var hasAudio = !string.IsNullOrWhiteSpace(path);

        if (HasAudio != hasAudio)
        {
            HasAudio = hasAudio;
            OnPropertyChanged(nameof(HasAudio));
            OnPropertyChanged(nameof(AudioStatusText));
        }

        if (!string.Equals(LatestAudioPath, path, StringComparison.OrdinalIgnoreCase))
        {
            LatestAudioPath = path;
            OnPropertyChanged(nameof(LatestAudioPath));
        }
    }

    public void UpdateDisplayBounds(double currentZoom)
    {
        var sourceZoom = CaptureZoom > 0 ? CaptureZoom : 1.0;
        var scale = currentZoom / sourceZoom;
        DisplayX = X * scale;
        DisplayY = Y * scale;
        DisplayWidth = Math.Max(1, Width * scale);
        DisplayHeight = Math.Max(1, Height * scale);
        OnPropertyChanged(nameof(DisplayX));
        OnPropertyChanged(nameof(DisplayY));
        OnPropertyChanged(nameof(DisplayWidth));
        OnPropertyChanged(nameof(DisplayHeight));
        OnPropertyChanged(nameof(OverlayWidth));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
