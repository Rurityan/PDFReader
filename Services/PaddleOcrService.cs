using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PDFReader.Services;

public sealed class PaddleOcrService
{
    public async Task<OcrResult> RecognizeAsync(Stream imageStream, CancellationToken cancellationToken = default)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "PDFReader", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var imagePath = Path.Combine(tempRoot, "page.png");
        var outputPath = Path.Combine(tempRoot, "result.json");

        try
        {
            await using (var output = File.Create(imagePath))
            {
                await imageStream.CopyToAsync(output, cancellationToken);
            }

            var pythonPath = ResolvePythonPath();
            var workerPath = Path.Combine(Directory.GetCurrentDirectory(), "Scripts", "ocr_worker.py");
            if (!File.Exists(pythonPath))
            {
                throw new FileNotFoundException("找不到 Python 运行时，请检查 .venv。", pythonPath);
            }

            if (!File.Exists(workerPath))
            {
                throw new FileNotFoundException("找不到 PaddleOCR worker。", workerPath);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
                WorkingDirectory = Directory.GetCurrentDirectory(),
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(workerPath);
            startInfo.ArgumentList.Add(imagePath);
            startInfo.ArgumentList.Add(outputPath);

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var error = await errorTask;
            _ = await outputTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                    ? "PaddleOCR 执行失败。"
                    : error.Trim());
            }

            await using var resultStream = File.OpenRead(outputPath);
            var result = await JsonSerializer.DeserializeAsync<OcrResult>(resultStream, cancellationToken: cancellationToken);
            return result ?? new OcrResult();
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch (IOException)
            {
                // Temporary files can be held briefly by the Python process after cancellation.
            }
        }
    }

    private static string ResolvePythonPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("PDFREADER_PYTHON");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        return Path.Combine(Directory.GetCurrentDirectory(), ".venv", "Scripts", "python.exe");
    }
}

public sealed class OcrResult
{
    public string Text { get; set; } = string.Empty;
    public List<OcrLine> Lines { get; set; } = new();
}

public sealed class OcrLine
{
    public string Text { get; set; } = string.Empty;
    public double? Score { get; set; }
}
