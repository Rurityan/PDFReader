using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using PDFReader.Models;

namespace PDFReader.Services;

public sealed class PdfAnnotationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<IReadOnlyList<PdfAnnotationInfo>> GetAnnotationsAsync(string path, int pageIndex)
    {
        var resultPath = Path.Combine(Path.GetTempPath(), $"pdfreader-annotations-{Guid.NewGuid():N}.json");
        try
        {
            await RunAsync("read", path, (pageIndex + 1).ToString(), resultPath);
            await using var stream = File.OpenRead(resultPath);
            return await JsonSerializer.DeserializeAsync<List<PdfAnnotationInfo>>(stream, JsonOptions)
                ?? new List<PdfAnnotationInfo>();
        }
        finally
        {
            TryDelete(resultPath);
        }
    }

    public async Task<IReadOnlyList<PdfOutlineEntry>> GetOutlineAsync(string path)
    {
        var resultPath = Path.Combine(Path.GetTempPath(), $"pdfreader-outline-{Guid.NewGuid():N}.json");
        try
        {
            await RunAsync("outline", path, resultPath);
            await using var stream = File.OpenRead(resultPath);
            return await JsonSerializer.DeserializeAsync<List<PdfOutlineEntry>>(stream, JsonOptions)
                ?? new List<PdfOutlineEntry>();
        }
        finally
        {
            TryDelete(resultPath);
        }
    }

    public async Task SaveIncrementalAsync(string path, IReadOnlyList<PdfAnnotationChange> changes)
    {
        var requestPath = Path.Combine(Path.GetTempPath(), $"pdfreader-annotation-changes-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(changes, JsonOptions));
            await RunAsync("save", path, requestPath);
        }
        finally
        {
            TryDelete(requestPath);
        }
    }

    public async Task ExportWithMetadataAsync(string sourcePath, string outputPath, IReadOnlyList<Bookmark> bookmarks, IReadOnlyList<OcrRecord> ocrRecords)
    {
        var manifestPath = Path.Combine(Path.GetTempPath(), $"pdfreader-export-{Guid.NewGuid():N}.json");
        try
        {
            var outline = new List<Dictionary<string, object?>>();
            AddOutlineEntries(bookmarks.Where(bookmark => bookmark.ParentId is null), 1, outline);
            var manifest = new
            {
                format = "PDFReader portable metadata v1",
                bookmarks = outline,
                ocrRecords = ocrRecords.Select(record => new
                {
                    id = record.Id, bookmarkId = record.BookmarkId, record.AllowStandalone, record.PageNumber, record.X, record.Y,
                    record.Width, record.Height, record.CaptureZoom, record.Title, record.Text, record.CreatedAtUtc,
                    audioFiles = record.TtsAudios.Where(audio => File.Exists(audio.FilePath)).Select(audio => new { audio.Id, audio.FilePath, audio.CreatedAtUtc }),
                }),
            };
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
            await RunAsync("export", sourcePath, outputPath, manifestPath);
        }
        finally
        {
            TryDelete(manifestPath);
        }
    }

    public async Task ExportAcrobatRichMediaAsync(string sourcePath, string outputPath, IReadOnlyList<Bookmark> bookmarks, IReadOnlyList<OcrRecord> ocrRecords)
    {
        var manifestPath = Path.Combine(Path.GetTempPath(), $"pdfreader-rich-media-{Guid.NewGuid():N}.json");
        try
        {
            var outline = new List<Dictionary<string, object?>>();
            AddOutlineEntries(bookmarks.Where(bookmark => bookmark.ParentId is null), 1, outline);
            var manifest = new
            {
                format = "PDFReader Acrobat rich media v1",
                bookmarks = outline,
                ocrRecords = ocrRecords.Select(record => new
                {
                    record.PageNumber, record.X, record.Y, record.Width, record.Height,
                    audioFiles = record.TtsAudios.Where(audio => File.Exists(audio.FilePath)).Select(audio => new
                    {
                        audio.FilePath, audio.CreatedAtUtc,
                    }),
                }),
            };
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
            await RunAsync("export-acrobat", sourcePath, outputPath, manifestPath);
        }
        finally
        {
            TryDelete(manifestPath);
        }
    }

    public async Task<PdfReaderMetadata?> RestoreMetadataAsync(string path, string audioDirectory)
    {
        var resultPath = Path.Combine(Path.GetTempPath(), $"pdfreader-restore-{Guid.NewGuid():N}.json");
        try
        {
            await RunAsync("restore", path, audioDirectory, resultPath);
            if (!File.Exists(resultPath)) return null;
            await using var stream = File.OpenRead(resultPath);
            return await JsonSerializer.DeserializeAsync<PdfReaderMetadata>(stream, JsonOptions);
        }
        finally
        {
            TryDelete(resultPath);
        }
    }

    private static void AddOutlineEntries(IEnumerable<Bookmark> bookmarks, int level, List<Dictionary<string, object?>> output)
    {
        foreach (var bookmark in bookmarks.OrderBy(bookmark => bookmark.SortOrder).ThenBy(bookmark => bookmark.CreatedAtUtc))
        {
            output.Add(new Dictionary<string, object?>
            {
                ["level"] = level, ["id"] = bookmark.Id, ["parentId"] = bookmark.ParentId,
                ["title"] = bookmark.Title, ["page"] = bookmark.PageNumber, ["sortOrder"] = bookmark.SortOrder,
            });
            AddOutlineEntries(bookmark.Children, level + 1, output);
        }
    }

    private static async Task RunAsync(params string[] arguments)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var python = ResolvePythonPath(baseDirectory);
        var worker = arguments.FirstOrDefault() == "export-acrobat"
            ? Path.Combine(baseDirectory, "Scripts", "rich_media_worker.py")
            : Path.Combine(baseDirectory, "Scripts", "annotation_worker.py");
        if (!File.Exists(python) || !File.Exists(worker))
        {
            throw new FileNotFoundException("找不到 PyMuPDF 标注运行环境。");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            WorkingDirectory = baseDirectory,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(worker);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动标注服务。");
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "PDF 标注操作失败。" : error.Trim());
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } }

    private static string ResolvePythonPath(string baseDirectory)
    {
        var configured = Environment.GetEnvironmentVariable("PDFREADER_PYTHON");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        for (var directory = new DirectoryInfo(baseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, ".venv", "Scripts", "python.exe");
            if (File.Exists(candidate)) return candidate;
        }

        return Path.Combine(baseDirectory, ".venv", "Scripts", "python.exe");
    }
}
