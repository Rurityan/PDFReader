using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PDFReader.Models;

namespace PDFReader.Services;

public sealed class LocalAutomationService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Func<ReaderSettings> _settingsProvider;
    private readonly PdfDocumentRepository _documents = new();
    private readonly BookmarkRepository _bookmarks = new();
    private readonly OcrResultRepository _ocr = new();
    private readonly object _listenerLock = new();
    private HttpListener? _listener;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _importGate = new(1, 1);

    public LocalAutomationService(Func<ReaderSettings> settingsProvider) => _settingsProvider = settingsProvider;

    public event EventHandler<Guid>? ImportCompleted;

    public void Start(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "本地 API 端口必须在 1 到 65535 之间。");
        }

        Stop();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        lock (_listenerLock)
        {
            _listener = listener;
        }
        _ = ListenAsync(listener);
    }

    public void Stop()
    {
        HttpListener? listener;
        lock (_listenerLock)
        {
            listener = _listener;
            _listener = null;
        }

        if (listener is null)
        {
            return;
        }

        listener.Stop();
        listener.Close();
    }

    private async Task ListenAsync(HttpListener listener)
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                var context = await listener.GetContextAsync();
                _ = HandleAsync(context);
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            if (context.Request.HttpMethod != "POST" || context.Request.Url?.AbsolutePath != "/api/v1/import/ocr-tts")
            {
                await RespondAsync(context.Response, 404, new { error = "not_found" });
                return;
            }

            var token = context.Request.Headers["X-PDFReader-Token"];
            if (!string.Equals(token, _settingsProvider().LocalApiToken, StringComparison.Ordinal))
            {
                await RespondAsync(context.Response, 401, new { error = "unauthorized" });
                return;
            }

            var request = await JsonSerializer.DeserializeAsync<AutomationImportRequest>(context.Request.InputStream, JsonOptions);
            if (request is null || string.IsNullOrWhiteSpace(request.PdfPath))
            {
                await RespondAsync(context.Response, 400, new { error = "pdfPath is required" });
                return;
            }

            if (!File.Exists(request.PdfPath))
            {
                await RespondAsync(context.Response, 400, new { error = $"pdfPath does not exist: {request.PdfPath}" });
                return;
            }

            var imported = await ImportAsync(request);
            await RespondAsync(context.Response, 200, new { imported, pdf_path = Path.GetFullPath(request.PdfPath) });
        }
        catch (JsonException)
        {
            await RespondAsync(context.Response, 400, new { error = "invalid_json" });
        }
        catch (Exception exception)
        {
            await RespondAsync(context.Response, 500, new { error = exception.Message });
        }
    }

    private async Task<int> ImportAsync(AutomationImportRequest request)
    {
        await _importGate.WaitAsync(_cancellation.Token);
        try
        {
            return await ImportCoreAsync(request);
        }
        finally
        {
            _importGate.Release();
        }
    }

    private async Task<int> ImportCoreAsync(AutomationImportRequest request)
    {
        var path = Path.GetFullPath(request.PdfPath);
        var document = await _documents.GetOrCreateAsync(path, Path.GetFileName(path));
        var bookmarks = await _bookmarks.GetForDocumentAsync(document.Id);
        var bookmarkMap = bookmarks.ToDictionary(item => item.Id);
        foreach (var bookmark in bookmarks)
        {
            bookmark.Parent = bookmark.ParentId is Guid parentId && bookmarkMap.TryGetValue(parentId, out var parent) ? parent : null;
        }

        var settings = _settingsProvider();
        Directory.CreateDirectory(settings.AudioDirectory);
        var count = 0;
        foreach (var item in request.Records.Where(item => item.Page > 0 && !string.IsNullOrWhiteSpace(item.Text)))
        {
            var text = item.Text.Trim();
            var target = bookmarks.Where(bookmark => bookmark.PageNumber == item.Page)
                .OrderByDescending(GetDepth).ThenBy(bookmark => bookmark.SortOrder).FirstOrDefault();

            var duplicate = await _ocr.FindDuplicateAsync(
                document.Id, item.Page, text, item.Region.X, item.Region.Y,
                item.Region.Width, item.Region.Height);
            if (duplicate is not null)
            {
                if (duplicate.BookmarkId is null && target is not null)
                {
                    await _ocr.AttachToBookmarkAsync(duplicate.Id, target.Id);
                }

                await ImportAudioIfMissingAsync(duplicate, item.AudioFile, settings);
                continue;
            }

            var record = new OcrRecord
            {
                PdfDocumentId = document.Id, BookmarkId = target?.Id, PageNumber = item.Page,
                X = item.Region.X, Y = item.Region.Y, Width = item.Region.Width, Height = item.Region.Height,
                CaptureZoom = item.CaptureZoom > 0 ? item.CaptureZoom : 1,
                Title = string.IsNullOrWhiteSpace(item.Title) ? item.Text.Trim()[..Math.Min(32, item.Text.Trim().Length)] : item.Title.Trim(),
                Text = text, CreatedAtUtc = DateTime.UtcNow,
                IsExternalImport = target is null,
            };
            await _ocr.AddAsync(record);
            await ImportAudioIfMissingAsync(record, item.AudioFile, settings);
            count++;
        }
        ImportCompleted?.Invoke(this, document.Id);
        return count;
    }

    private async Task ImportAudioIfMissingAsync(
        OcrRecord record,
        string? audioFile,
        ReaderSettings settings)
    {
        if (record.TtsAudios.Any(audio => File.Exists(audio.FilePath))
            || string.IsNullOrWhiteSpace(audioFile)
            || !File.Exists(audioFile))
        {
            return;
        }

        var extension = Path.GetExtension(audioFile);
        var destination = Path.Combine(settings.AudioDirectory, $"ocr-{record.Id:N}{extension}");
        File.Copy(audioFile, destination, overwrite: false);
        await _ocr.AddAudioAsync(new TtsAudioRecord
        {
            OcrRecordId = record.Id,
            FilePath = destination,
            CreatedAtUtc = DateTime.UtcNow,
        });
    }

    private static int GetDepth(Bookmark bookmark)
    {
        var depth = 0;
        for (var current = bookmark.Parent; current is not null; current = current.Parent) depth++;
        return depth;
    }

    private static async Task RespondAsync(HttpListenerResponse response, int status, object body)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, JsonOptions));
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        Stop();
        _importGate.Dispose();
        _cancellation.Dispose();
    }
}
