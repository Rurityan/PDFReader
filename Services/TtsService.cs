using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
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
            || string.IsNullOrWhiteSpace(settings.TtsVoiceModel)
            || settings.TtsVoiceModels is null)
        {
            throw new InvalidOperationException("请先完整填写 TTS 配置。");
        }

        var selectedVoiceModel = settings.TtsVoiceModels.FirstOrDefault(
            voiceModel => string.Equals(voiceModel.Name, settings.TtsVoiceModel, StringComparison.Ordinal));
        if (selectedVoiceModel is null || string.IsNullOrWhiteSpace(selectedVoiceModel.VoiceId))
        {
            throw new InvalidOperationException("请选择有效的 TTS Voice Model。");
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
            voice = selectedVoiceModel.VoiceId.Trim(),
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
        var downloadPath = path + ".download";
        try
        {
            await using (var output = File.Create(downloadPath))
            {
                await response.Content.CopyToAsync(output, cancellationToken);
            }

            if (settings.EnableTtsAudioNormalization)
            {
                await NormalizeAudioAsync(downloadPath, path, settings.FfmpegPath, cancellationToken);
            }
            else
            {
                File.Move(downloadPath, path, true);
            }

            return path;
        }
        catch
        {
            TryDelete(downloadPath);
            TryDelete(path);
            throw;
        }
    }

    private static async Task NormalizeAudioAsync(
        string sourcePath,
        string destinationPath,
        string? ffmpegPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            throw new FileNotFoundException("已启用音频归一化，请在设置中选择有效的 ffmpeg.exe 路径。", ffmpegPath);
        }

        var normalizedPath = destinationPath + ".normalized.mp3";
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-loglevel");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-y");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(sourcePath);
            startInfo.ArgumentList.Add("-af");
            startInfo.ArgumentList.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
            startInfo.ArgumentList.Add("-c:a");
            startInfo.ArgumentList.Add("libmp3lame");
            startInfo.ArgumentList.Add("-b:a");
            startInfo.ArgumentList.Add("192k");
            startInfo.ArgumentList.Add(normalizedPath);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动 ffmpeg 音频归一化进程。");
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0 || !File.Exists(normalizedPath) || new FileInfo(normalizedPath).Length == 0)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error) ? "ffmpeg 音频归一化失败。" : error.Trim());
            }

            File.Move(normalizedPath, destinationPath, true);
        }
        finally
        {
            TryDelete(sourcePath);
            TryDelete(normalizedPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
