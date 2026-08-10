using CommunityToolkit.Mvvm.ComponentModel;
using PDFReader.Models;

namespace PDFReader.ViewModels;

public partial class SettingsWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _audioDirectory;

    [ObservableProperty]
    private bool _enableOcrCaptureCache;

    [ObservableProperty]
    private string _ocrCaptureDirectory;

    [ObservableProperty]
    private string _ttsBaseUrl;

    [ObservableProperty]
    private string _ttsApiKey;

    [ObservableProperty]
    private string _ttsModelType;

    [ObservableProperty]
    private string _ttsVoiceModel;

    public SettingsWindowViewModel(ReaderSettings settings)
    {
        _audioDirectory = settings.AudioDirectory;
        _enableOcrCaptureCache = settings.EnableOcrCaptureCache;
        _ocrCaptureDirectory = settings.OcrCaptureDirectory;
        _ttsBaseUrl = settings.TtsBaseUrl;
        _ttsApiKey = settings.TtsApiKey;
        _ttsModelType = settings.TtsModelType;
        _ttsVoiceModel = settings.TtsVoiceModel;
    }

    public ReaderSettings ToSettings()
    {
        return new ReaderSettings
        {
            AudioDirectory = AudioDirectory,
            EnableOcrCaptureCache = EnableOcrCaptureCache,
            OcrCaptureDirectory = OcrCaptureDirectory,
            TtsBaseUrl = TtsBaseUrl,
            TtsApiKey = TtsApiKey,
            TtsModelType = TtsModelType,
            TtsVoiceModel = TtsVoiceModel,
        };
    }
}
