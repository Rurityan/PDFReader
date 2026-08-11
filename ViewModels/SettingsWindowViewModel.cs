using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Linq;
using PDFReader.Models;

namespace PDFReader.ViewModels;

public partial class SettingsWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _audioDirectory;

    [ObservableProperty]
    private bool _enablePagePreviews;

    [ObservableProperty]
    private bool _enableOcrCaptureCache;

    [ObservableProperty]
    private bool _autoGenerateOcrAudio;

    [ObservableProperty]
    private string _ocrCaptureDirectory;

    [ObservableProperty]
    private string _ttsBaseUrl;

    [ObservableProperty]
    private string _ttsApiKey;

    [ObservableProperty]
    private string _ttsModelType;

    [ObservableProperty]
    private TtsVoiceModelOption? _selectedVoiceModel;

    public ObservableCollection<TtsVoiceModelOption> VoiceModels { get; } = new();

    public SettingsWindowViewModel(ReaderSettings settings)
    {
        _audioDirectory = settings.AudioDirectory;
        _enablePagePreviews = settings.EnablePagePreviews;
        _enableOcrCaptureCache = settings.EnableOcrCaptureCache;
        _autoGenerateOcrAudio = settings.AutoGenerateOcrAudio;
        _ocrCaptureDirectory = settings.OcrCaptureDirectory;
        _ttsBaseUrl = settings.TtsBaseUrl;
        _ttsApiKey = settings.TtsApiKey;
        _ttsModelType = settings.TtsModelType;
        foreach (var voiceModel in settings.TtsVoiceModels ?? new())
        {
            VoiceModels.Add(new TtsVoiceModelOption
            {
                Name = voiceModel.Name,
                VoiceId = voiceModel.VoiceId,
            });
        }

        SelectedVoiceModel = VoiceModels.FirstOrDefault(
            voiceModel => string.Equals(voiceModel.Name, settings.TtsVoiceModel, StringComparison.Ordinal));
    }

    [RelayCommand]
    private void AddVoiceModel()
    {
        var voiceModel = new TtsVoiceModelOption
        {
            Name = "新语音",
            VoiceId = string.Empty,
        };
        VoiceModels.Add(voiceModel);
        SelectedVoiceModel = voiceModel;
    }

    [RelayCommand]
    private void RemoveVoiceModel(TtsVoiceModelOption? voiceModel)
    {
        if (voiceModel is null)
        {
            return;
        }

        var wasSelected = ReferenceEquals(SelectedVoiceModel, voiceModel);
        VoiceModels.Remove(voiceModel);
        if (wasSelected)
        {
            SelectedVoiceModel = VoiceModels.FirstOrDefault();
        }
    }

    public ReaderSettings ToSettings()
    {
        return new ReaderSettings
        {
            AudioDirectory = AudioDirectory,
            EnablePagePreviews = EnablePagePreviews,
            EnableOcrCaptureCache = EnableOcrCaptureCache,
            AutoGenerateOcrAudio = AutoGenerateOcrAudio,
            OcrCaptureDirectory = OcrCaptureDirectory,
            TtsBaseUrl = TtsBaseUrl,
            TtsApiKey = TtsApiKey,
            TtsModelType = TtsModelType,
            TtsVoiceModel = SelectedVoiceModel?.Name.Trim() ?? string.Empty,
            TtsVoiceModels = VoiceModels
                .Select(voiceModel => new TtsVoiceModelOption
                {
                    Name = voiceModel.Name.Trim(),
                    VoiceId = voiceModel.VoiceId.Trim(),
                })
                .ToList(),
        };
    }
}
