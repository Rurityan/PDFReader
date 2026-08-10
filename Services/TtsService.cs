using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PDFReader.Models;

namespace PDFReader.Services;

public sealed class TtsService
{
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(5),
    };

    public async Task<string> GenerateAsync(
        string text,
        ReaderSettings settings,
        int pageNumber,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("没有可生成语音的文本。");
        }

        if (string.IsNullOrWhiteSpace(settings.TtsBaseUrl)
            || string.IsNullOrWhiteSpace(settings.TtsApiKey)
            || string.IsNullOrWhiteSpace(settings.TtsModelType)
            || string.IsNullOrWhiteSpace(settings.TtsVoiceModel))
        {
            throw new InvalidOperationException("请先完整填写 TTS 配置。");
        }

        var endpoint = settings.TtsBaseUrl.TrimEnd('/');
        if (!endpoint.EndsWith("/audio/speech", StringComparison.OrdinalIgnoreCase))
        {
            endpoint += "/audio/speech";
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.TtsApiKey.Trim());
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = settings.TtsModelType.Trim(),
            input = text.Trim(),
            voice = settings.TtsVoiceModel.Trim(),
            response_format = "mp3",
        }), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"TTS 请求失败 ({(int)response.StatusCode}): {error}");
        }

        var directory = string.IsNullOrWhiteSpace(settings.AudioDirectory)
            ? ReaderSettings.GetDefaultAudioDirectory()
            : settings.AudioDirectory.Trim();
        Directory.CreateDirectory(directory);
        var fileName = $"page-{pageNumber}-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.mp3";
        var path = Path.Combine(directory, fileName);
        await using var output = File.Create(path);
        await response.Content.CopyToAsync(output, cancellationToken);
        return path;
    }
}
