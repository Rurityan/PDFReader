using System;
using System.IO;
using System.Text.Json;
using PDFReader.Models;

namespace PDFReader.Services;

public sealed class SettingsService
{
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PDFReader",
        "settings.json");

    public ReaderSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                var defaults = new ReaderSettings();
                Save(defaults);
                return defaults;
            }

            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<ReaderSettings>(json);
            return settings ?? new ReaderSettings();
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
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        File.WriteAllText(_settingsPath, json);
    }
}
