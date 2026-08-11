using System;
using System.IO;

namespace PDFReader.Models;

public sealed class ReaderSettings
{
    public bool EnableOcrCaptureCache { get; set; }
    public string OcrCaptureDirectory { get; set; } = GetDefaultCaptureDirectory();
    public string AudioDirectory { get; set; } = GetDefaultAudioDirectory();
    public string TtsBaseUrl { get; set; } = string.Empty;
    public string TtsApiKey { get; set; } = string.Empty;
    public string TtsModelType { get; set; } = string.Empty;
    public string TtsVoiceModel { get; set; } = string.Empty;

    public static string GetDefaultCaptureDirectory()
    {
        return Path.Combine(GetUserDataDirectory(), "resource", "image");
    }

    public static string GetDefaultDatabasePath()
    {
        return Path.Combine(GetUserDataDirectory(), "reader.db");
    }

    public static string GetDefaultSettingsPath()
    {
        return Path.Combine(GetUserDataDirectory(), "settings.json");
    }

    public static string GetUserDataDirectory()
    {
        return Path.Combine(AppContext.BaseDirectory, "user_data");
    }

    public static string GetDefaultAudioDirectory()
    {
        return Path.Combine(GetUserDataDirectory(), "resource", "voice");
    }

    public static string GetDefaultPageCacheDirectory()
    {
        return Path.Combine(GetUserDataDirectory(), "cache");
    }

    public static string GetPagePreviewCacheDirectory(Guid documentId)
    {
        return Path.Combine(GetDefaultPageCacheDirectory(), documentId.ToString("N"));
    }
}
