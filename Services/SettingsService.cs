using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Linq;
using PDFReader.Models;

namespace PDFReader.Services;

#pragma warning disable CA1416 // This desktop application uses Windows DPAPI for API key protection.
public sealed class SettingsService
{
    private readonly string _settingsPath = ReaderSettings.GetDefaultSettingsPath();
    private readonly string _legacySettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PDFReader",
        "settings.json");

    public ReaderSettings Load()
    {
        try
        {
            var settingsPath = File.Exists(_settingsPath)
                ? _settingsPath
                : _legacySettingsPath;

            if (!File.Exists(settingsPath))
            {
                var defaults = new ReaderSettings();
                Save(defaults);
                return defaults;
            }

            var json = File.ReadAllText(settingsPath);
            var settings = JsonSerializer.Deserialize<ReaderSettings>(json);
            var originalCaptureDirectory = settings?.OcrCaptureDirectory;
            var originalAudioDirectory = settings?.AudioDirectory;
            var normalized = Normalize(settings ?? new ReaderSettings());
            var shouldPersist = !File.Exists(_settingsPath)
                || !string.Equals(settingsPath, _settingsPath, StringComparison.OrdinalIgnoreCase)
                || !AreSamePath(originalCaptureDirectory, normalized.OcrCaptureDirectory)
                || !AreSamePath(originalAudioDirectory, normalized.AudioDirectory);
            if (shouldPersist)
            {
                Save(normalized);
            }

            return normalized;
        }
        catch (JsonException)
        {
            return new ReaderSettings();
        }
        catch (IOException)
        {
            return new ReaderSettings();
        }
    }

    public void Save(ReaderSettings settings)
    {
        settings = Normalize(settings);
        var persistedSettings = new ReaderSettings
        {
            EnablePagePreviews = settings.EnablePagePreviews,
            EnableOcrCaptureCache = settings.EnableOcrCaptureCache,
            OcrCaptureDirectory = settings.OcrCaptureDirectory,
            AudioDirectory = settings.AudioDirectory,
            TtsBaseUrl = settings.TtsBaseUrl,
            TtsApiKey = ProtectApiKey(settings.TtsApiKey),
            TtsModelType = settings.TtsModelType,
            TtsVoiceModel = settings.TtsVoiceModel,
            TtsVoiceModels = (settings.TtsVoiceModels ?? new())
                .Select(voiceModel => new TtsVoiceModelOption
                {
                    Name = voiceModel.Name,
                    VoiceId = voiceModel.VoiceId,
                })
                .ToList(),
        };
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(settings.OcrCaptureDirectory);
        Directory.CreateDirectory(settings.AudioDirectory);
        var json = JsonSerializer.Serialize(persistedSettings, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        File.WriteAllText(_settingsPath, json);
    }

    private static string ProtectApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return string.Empty;
        }

        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(apiKey),
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);
        return $"enc:{Convert.ToBase64String(protectedBytes)}";
    }

    private static ReaderSettings Normalize(ReaderSettings settings)
    {
        var legacyCaptureDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PDFReader",
            "ocr-captures");
        if (string.IsNullOrWhiteSpace(settings.OcrCaptureDirectory)
            || AreSamePath(settings.OcrCaptureDirectory, legacyCaptureDirectory))
        {
            settings.OcrCaptureDirectory = ReaderSettings.GetDefaultCaptureDirectory();
        }

        var legacyAudioDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PDFReader",
            "audio");
        if (string.IsNullOrWhiteSpace(settings.AudioDirectory)
            || AreSamePath(settings.AudioDirectory, legacyAudioDirectory))
        {
            settings.AudioDirectory = ReaderSettings.GetDefaultAudioDirectory();
        }

        if (settings.TtsApiKey.StartsWith("enc:", StringComparison.Ordinal))
        {
            try
            {
                var protectedBytes = Convert.FromBase64String(settings.TtsApiKey[4..]);
                var bytes = System.Security.Cryptography.ProtectedData.Unprotect(
                    protectedBytes,
                    optionalEntropy: null,
                    System.Security.Cryptography.DataProtectionScope.CurrentUser);
                settings.TtsApiKey = System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch (CryptographicException)
            {
                settings.TtsApiKey = string.Empty;
            }
            catch (FormatException)
            {
                settings.TtsApiKey = string.Empty;
            }
        }

        return settings;
    }

    private static bool AreSamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
#pragma warning restore CA1416
