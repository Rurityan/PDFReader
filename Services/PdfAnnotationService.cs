using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

    private static async Task RunAsync(params string[] arguments)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var python = ResolvePythonPath(baseDirectory);
        var worker = Path.Combine(baseDirectory, "Scripts", "annotation_worker.py");
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
