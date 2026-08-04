using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VoxLink.Services;
using VoxLink.ViewModels;

namespace VoxLink;

public partial class MainWindow : Window
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);
    private readonly MainViewModel _viewModel;
    private readonly OverlayWindow _overlayWindow = new();
    private GlobalHotkeyService? _hotkeys;
    private bool _shutdownStarted;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
        _viewModel.IncomingSubtitle += OnIncomingSubtitle;
        _viewModel.SettingsChanged += OnSettingsChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            await _viewModel.InitializeAsync();
            OpenAiPasswordBox.Password = _viewModel.OpenAiApiKey;
            _hotkeys = new GlobalHotkeyService(this);
            _hotkeys.ToggleRequested += OnToggleRequested;
            _hotkeys.TranslateRequested += OnTranslateRequested;
            RegisterHotkeys();
            _overlayWindow.SetEnabled(_viewModel.ShowOverlay);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "VoxLink 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (_shutdownStarted)
        {
            return;
        }

        eventArgs.Cancel = true;
        _shutdownStarted = true;
        IsEnabled = false;
        try
        {
            _hotkeys?.Dispose();
            _hotkeys = null;
            _overlayWindow.Close();
            await _viewModel.DisposeAsync().AsTask().WaitAsync(ShutdownTimeout);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(exception);
        }
        finally
        {
            Closing -= OnClosing;
            Close();
        }
    }

    private void Navigation_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out var index))
        {
            WorkspaceTabs.SelectedIndex = index;
        }
    }

    private void OpenAiPasswordBox_OnPasswordChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is PasswordBox passwordBox)
        {
            _viewModel.OpenAiApiKey = passwordBox.Password;
        }
    }

    private void InputTextBox_OnPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ExecuteIfAvailable(_viewModel.TranslateTextCommand);
            eventArgs.Handled = true;
        }
    }

    private void OnIncomingSubtitle(object? sender, Models.ConversationMessage message)
    {
        if (_viewModel.ShowOverlay)
        {
            _overlayWindow.ShowSubtitle(message);
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs eventArgs)
    {
        _overlayWindow.SetEnabled(_viewModel.ShowOverlay);
        RegisterHotkeys();
    }

    private void OnToggleRequested(object? sender, EventArgs eventArgs) =>
        ExecuteIfAvailable(_viewModel.StartStopCommand);

    private void OnTranslateRequested(object? sender, EventArgs eventArgs) =>
        ExecuteIfAvailable(_viewModel.TranslateTextCommand);

    private void RegisterHotkeys()
    {
        if (_hotkeys is null)
        {
            return;
        }

        try
        {
            _hotkeys.Register(_viewModel.ToggleHotkey, _viewModel.TranslateHotkey);
        }
        catch (Win32Exception exception)
        {
            MessageBox.Show(this, exception.Message, "快捷键冲突", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(this, exception.Message, "快捷键无效", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void ExecuteIfAvailable(ICommand command)
    {
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }
}
