using System;

namespace PDFReader.Models;

public sealed class TtsAudioRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OcrRecordId { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }

    public OcrRecord? OcrRecord { get; set; }
}
