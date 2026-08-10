using System;
using System.IO;
using LibVLCSharp.Shared;

namespace PDFReader.Services;

public sealed class AudioPlaybackService : IDisposable
{
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Media? _media;

    public void Play(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("找不到音频文件。", filePath);
        }

        EnsureInitialized();
        _media?.Dispose();
        _media = new Media(_libVlc!, new Uri(filePath));
        _mediaPlayer!.Play(_media);
    }

    public void Stop()
    {
        _mediaPlayer?.Stop();
    }

    private void EnsureInitialized()
    {
        if (_mediaPlayer is not null)
        {
            return;
        }

        Core.Initialize();
        _libVlc = new LibVLC();
        _mediaPlayer = new MediaPlayer(_libVlc);
    }

    public void Dispose()
    {
        _mediaPlayer?.Stop();
        _media?.Dispose();
        _mediaPlayer?.Dispose();
        _libVlc?.Dispose();
        _media = null;
        _mediaPlayer = null;
        _libVlc = null;
    }
}
