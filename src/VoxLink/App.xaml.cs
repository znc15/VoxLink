using System.Net.Http;
using System.Windows;
using VoxLink.Audio;
using VoxLink.Services;
using VoxLink.ViewModels;

namespace VoxLink;

public partial class App : Application
{
    // This legacy debug entry shares the production frontend mutex and therefore cannot
    // start a second engine/model owner alongside the WinUI application.
    private const string InstanceMutexName = @"Local\VoxLink.Frontend.Singleton";
    private static readonly TimeSpan LegacyShutdownTimeout = TimeSpan.FromSeconds(10);
    private HttpClient? _httpClient;
    private TranslationSession? _session;
    private TranslationServiceFactory? _translationFactory;
    private LocalModelManager? _localModelManager;
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
        // Keep the legacy process-internal pipeline functional for debugging, but use one
        // manager instance for translation and TTS just like EngineHost.
        _localModelManager = new LocalModelManager();
        _translationFactory = new TranslationServiceFactory(_httpClient, _localModelManager);
        var textToSpeech = new HybridTextToSpeechService(
            _httpClient,
            enableEdgeTts: true,
            _localModelManager);
        _session = new TranslationSession(recognizer, _translationFactory, textToSpeech);
        var viewModel = new MainViewModel(settingsStore, audioDevices, _session, recognizer);

        var mainWindow = new MainWindow(viewModel);
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        var pipelineDrained = true;
        var session = _session;
        if (session is not null)
        {
            try
            {
                Task.Run(() => session.DisposeAsync().AsTask())
                    .WaitAsync(LegacyShutdownTimeout)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception exception)
            {
                pipelineDrained = false;
                System.Diagnostics.Debug.WriteLine(exception);
            }

            _session = null;
        }

        var translationFactory = _translationFactory;
        if (pipelineDrained && translationFactory is not null)
        {
            try
            {
                Task.Run(translationFactory.Dispose)
                    .WaitAsync(LegacyShutdownTimeout)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception exception)
            {
                pipelineDrained = false;
                System.Diagnostics.Debug.WriteLine(exception);
            }

            _translationFactory = null;
        }

        var localModelManager = _localModelManager;
        if (pipelineDrained && localModelManager is not null)
        {
            try
            {
                Task.Run(() => localModelManager.DisposeAsync().AsTask())
                    .WaitAsync(LegacyShutdownTimeout)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception exception)
            {
                pipelineDrained = false;
                System.Diagnostics.Debug.WriteLine(exception);
            }

            _localModelManager = null;
        }

        if (pipelineDrained)
        {
            _httpClient?.Dispose();
            _httpClient = null;
        }
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
