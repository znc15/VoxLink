using System.Net.Http;
using System.Windows;
using VoxLink.Audio;
using VoxLink.Services;
using VoxLink.ViewModels;

namespace VoxLink;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\VoxLink.Desktop.Instance";
    private HttpClient? _httpClient;
    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out _ownsInstanceMutex);
        if (!_ownsInstanceMutex)
        {
            MessageBox.Show(
                "VoxLink 已经在运行。",
                "VoxLink",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("VoxLink/0.1 (Windows desktop translator)");

        var settingsStore = new SettingsStore();
        var audioDevices = new AudioDeviceService();
        var recognizer = new WhisperSpeechRecognizer();
        var translationFactory = new TranslationServiceFactory(_httpClient);
        var textToSpeech = new HybridTextToSpeechService(_httpClient);
        var session = new TranslationSession(recognizer, translationFactory, textToSpeech);
        var viewModel = new MainViewModel(settingsStore, audioDevices, session, recognizer);

        var mainWindow = new MainWindow(viewModel);
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _httpClient?.Dispose();
        _httpClient = null;
        if (_instanceMutex is not null)
        {
            if (_ownsInstanceMutex)
            {
                _instanceMutex.ReleaseMutex();
                _ownsInstanceMutex = false;
            }

            _instanceMutex.Dispose();
            _instanceMutex = null;
        }

        base.OnExit(e);
    }
}
