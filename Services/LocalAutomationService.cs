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
    private const string Prefix = "http://127.0.0.1:38421/";
    private readonly Func<ReaderSettings> _settingsProvider;
    private readonly PdfDocumentRepository _documents = new();
    private readonly BookmarkRepository _bookmarks = new();
    private readonly OcrResultRepository _ocr = new();
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cancellation = new();

    public LocalAutomationService(Func<ReaderSettings> settingsProvider) => _settingsProvider = settingsProvider;

    public event EventHandler<Guid>? ImportCompleted;

    public void Start()
    {
        if (_listener.IsListening) return;
        _listener.Prefixes.Add(Prefix);
        _listener.Start();
        _ = ListenAsync();
    }

    private async Task ListenAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = HandleAsync(context);
            }
            catch (HttpListenerException) when (_cancellation.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException)
            {
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

            var request = await JsonSerializer.DeserializeAsync<AutomationImportRequest>(context.Request.InputStream);
            if (request is null || string.IsNullOrWhiteSpace(request.PdfPath) || !File.Exists(request.PdfPath))
            {
                await RespondAsync(context.Response, 400, new { error = "pdf_path must reference an existing PDF" });
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
            var target = bookmarks.Where(bookmark => bookmark.PageNumber == item.Page)
                .OrderByDescending(GetDepth).ThenBy(bookmark => bookmark.SortOrder).FirstOrDefault();
            var record = new OcrRecord
            {
                PdfDocumentId = document.Id, BookmarkId = target?.Id, PageNumber = item.Page,
                X = item.Region.X, Y = item.Region.Y, Width = item.Region.Width, Height = item.Region.Height,
                CaptureZoom = item.CaptureZoom > 0 ? item.CaptureZoom : 1,
                Title = string.IsNullOrWhiteSpace(item.Title) ? item.Text.Trim()[..Math.Min(32, item.Text.Trim().Length)] : item.Title.Trim(),
                Text = item.Text.Trim(), CreatedAtUtc = DateTime.UtcNow,
            };
            await _ocr.AddAsync(record);
            if (!string.IsNullOrWhiteSpace(item.AudioFile) && File.Exists(item.AudioFile))
            {
                var extension = Path.GetExtension(item.AudioFile);
                var destination = Path.Combine(settings.AudioDirectory, $"ocr-{record.Id:N}{extension}");
                File.Copy(item.AudioFile, destination, overwrite: false);
                await _ocr.AddAudioAsync(new TtsAudioRecord { OcrRecordId = record.Id, FilePath = destination, CreatedAtUtc = DateTime.UtcNow });
            }
            count++;
        }
        ImportCompleted?.Invoke(this, document.Id);
        return count;
    }

    private static int GetDepth(Bookmark bookmark)
    {
        var depth = 0;
        for (var current = bookmark.Parent; current is not null; current = current.Parent) depth++;
        return depth;
    }

    private static async Task RespondAsync(HttpListenerResponse response, int status, object body)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body));
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        if (_listener.IsListening) _listener.Stop();
        _listener.Close();
        _cancellation.Dispose();
    }
}
