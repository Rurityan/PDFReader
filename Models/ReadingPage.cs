using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;

namespace PDFReader.Models;

public sealed class ReadingPage : IDisposable, INotifyPropertyChanged
{
    private Bitmap? _image;
    private Bitmap? _previewImage;
    private bool _isOcrVisible;

    public ReadingPage(int pageNumber, double width, double height, string previewCachePath)
    {
        PageNumber = pageNumber;
        Width = width;
        Height = height;
        PreviewCachePath = previewCachePath;
    }

    public int PageNumber { get; }
    public double Width { get; }
    public double Height { get; }
    public string PreviewCachePath { get; }
    public bool IsActive { get; set; }
    public bool IsRenderQueued { get; set; }
    public bool IsPreviewQueued { get; set; }
    public bool IsOcrVisible
    {
        get => _isOcrVisible;
        set
        {
            if (_isOcrVisible == value) return;
            _isOcrVisible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOcrVisible)));
        }
    }
    public ObservableCollection<OcrRecord> OcrRecords { get; } = new();
    public ObservableCollection<PdfAnnotationInfo> Annotations { get; } = new();

    public Bitmap? Image
    {
        get => _image;
        set
        {
            if (ReferenceEquals(_image, value)) return;
            _image = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
        }
    }

    public Bitmap? PreviewImage
    {
        get => _previewImage;
        private set
        {
            if (ReferenceEquals(_previewImage, value)) return;
            _previewImage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PreviewImage)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetPreview(Bitmap? preview) => PreviewImage = preview;

    public void UnloadPreview()
    {
        var preview = PreviewImage;
        PreviewImage = null;
        preview?.Dispose();
    }

    public void Unload()
    {
        var image = Image;
        Image = null;
        image?.Dispose();
        UnloadPreview();
    }

    public void Dispose() => Unload();
}
