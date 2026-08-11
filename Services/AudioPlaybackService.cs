using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;

namespace PDFReader.Services;

public sealed class AudioPlaybackService : IDisposable
{
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Media? _media;

    public event EventHandler? PlaybackStateChanged;

    public bool IsPlaying => _mediaPlayer?.IsPlaying == true;

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

    public async Task PlayAndWaitAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("找不到音频文件。", filePath);
        }

        EnsureInitialized();
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<EventArgs>? endReached = null;
        endReached = (_, _) => completion.TrySetResult(true);
        _mediaPlayer!.EndReached += endReached;

        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            _mediaPlayer.Stop();
            completion.TrySetCanceled(cancellationToken);
        });

        try
        {
            _media?.Dispose();
            _media = new Media(_libVlc!, new Uri(filePath));
            _mediaPlayer.Play(_media);
            await completion.Task;
        }
        finally
        {
            _mediaPlayer.EndReached -= endReached;
        }
    }

    public void Stop()
    {
        _mediaPlayer?.Stop();
        NotifyPlaybackStateChanged();
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
        _mediaPlayer.EndReached += MediaPlayerStateChanged;
        _mediaPlayer.Stopped += MediaPlayerStateChanged;
        _mediaPlayer.EncounteredError += MediaPlayerStateChanged;
    }

    private void MediaPlayerStateChanged(object? sender, EventArgs e)
    {
        NotifyPlaybackStateChanged();
    }

    private void NotifyPlaybackStateChanged()
    {
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _mediaPlayer?.Stop();
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.EndReached -= MediaPlayerStateChanged;
            _mediaPlayer.Stopped -= MediaPlayerStateChanged;
            _mediaPlayer.EncounteredError -= MediaPlayerStateChanged;
        }
        _media?.Dispose();
        _mediaPlayer?.Dispose();
        _libVlc?.Dispose();
        _media = null;
        _mediaPlayer = null;
        _libVlc = null;
    }
}
