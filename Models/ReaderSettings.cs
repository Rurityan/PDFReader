using System;
using System.IO;

namespace PDFReader.Models;

public sealed class ReaderSettings
{
    public bool EnableOcrCaptureCache { get; set; }
    public string OcrCaptureDirectory { get; set; } = GetDefaultCaptureDirectory();
    public string TtsBaseUrl { get; set; } = string.Empty;
    public string TtsApiKey { get; set; } = string.Empty;
    public string TtsModelType { get; set; } = string.Empty;
    public string TtsVoiceModel { get; set; } = string.Empty;

    public static string GetDefaultCaptureDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PDFReader",
            "ocr-captures");
    }
}
