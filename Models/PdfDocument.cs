using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using System.IO;
using System.Runtime.CompilerServices;

namespace PDFReader.Models;

public sealed class PdfDocument : INotifyPropertyChanged
{
    private string _filePath = string.Empty;
    private string _title = string.Empty;
    private bool _isMissing;
    private string _pathStatus = string.Empty;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string FilePath
    {
        get => _filePath;
        set => SetProperty(ref _filePath, value);
    }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime LastOpenedAtUtc { get; set; }
    public bool IsArchived { get; set; }

    [NotMapped]
    public bool IsMissing
    {
        get => _isMissing;
        private set => SetProperty(ref _isMissing, value);
    }

    [NotMapped]
    public string PathStatus
    {
        get => _pathStatus;
        private set => SetProperty(ref _pathStatus, value);
    }

    public ICollection<Bookmark> Bookmarks { get; } = new List<Bookmark>();
    public ICollection<OcrRecord> OcrRecords { get; } = new List<OcrRecord>();

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RefreshPathStatus()
    {
        IsMissing = !File.Exists(FilePath);
        PathStatus = IsMissing ? "文件不存在，需要重新绑定" : string.Empty;
    }

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
