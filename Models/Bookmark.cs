using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;

namespace PDFReader.Models;

public sealed class Bookmark : INotifyPropertyChanged
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PdfDocumentId { get; set; }
    public Guid? ParentId { get; set; }
    public int PageNumber { get; set; }

    private string _title = string.Empty;

    public string Title
    {
        get => _title;
        set
        {
            if (string.Equals(_title, value, StringComparison.Ordinal))
            {
                return;
            }

            _title = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
        }
    }

    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public PdfDocument? PdfDocument { get; set; }
    public Bookmark? Parent { get; set; }
    public ObservableCollection<Bookmark> Children { get; } = new();
    public ICollection<OcrRecord> OcrRecords { get; } = new Collection<OcrRecord>();

    [NotMapped]
    public ObservableCollection<object> DisplayChildren { get; } = new();

    [NotMapped]
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    private bool _isExpanded;

    [NotMapped]
    public bool IsPersisted { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
}
