using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace PDFReader.Models;

public sealed class TtsVoiceModelOption : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _voiceId = string.Empty;

    [JsonPropertyName("name")]
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    [JsonPropertyName("voice_id")]
    public string VoiceId
    {
        get => _voiceId;
        set => SetProperty(ref _voiceId, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
