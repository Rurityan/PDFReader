using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;

namespace PDFReader.Models;

public sealed class PagePreview : IDisposable, INotifyPropertyChanged
{
    private Bitmap? _image;

    public PagePreview(int pageNumber, string cachePath)
    {
        PageNumber = pageNumber;
        CachePath = cachePath;
    }

    public int PageNumber { get; }
    public string CachePath { get; }
    public Bitmap? Image
    {
        get => _image;
        private set
        {
            if (ReferenceEquals(_image, value))
            {
                return;
            }

            _image = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void LoadImage()
    {
        if (Image is not null || !File.Exists(CachePath))
        {
            return;
        }

        using var stream = File.OpenRead(CachePath);
        Image = new Bitmap(stream);
    }

    public void UnloadImage()
    {
        var image = Image;
        Image = null;
        image?.Dispose();
    }

    public void Dispose() => UnloadImage();
}
