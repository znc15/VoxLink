using System;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using VoxLink.UI.Core.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace VoxLink.UI.Pages;

public sealed partial class LogsPage : Page
{
    private const int MaxDisplayedEntries = 800;

    private readonly ObservableCollection<LogEntry> _entries = new();
    private LogLevel _minLevel = LogLevel.Info;
    private bool _autoScroll = true;
    private ScrollViewer? _scrollViewer;

    public LogsPage()
    {
        InitializeComponent();
        LogList.ItemsSource = _entries;
        Loaded += LogsPage_Loaded;
        Unloaded += LogsPage_Unloaded;
    }

    private void LogsPage_Loaded(object sender, RoutedEventArgs e)
    {
        _scrollViewer = FindScrollViewer(LogList);
        if (_scrollViewer is not null)
        {
            _scrollViewer.ViewChanged += ScrollViewer_ViewChanged;
        }

        LogService.Instance.EntryAdded += LogService_EntryAdded;
        if (LevelFilterBox.SelectedIndex < 0)
        {
            LevelFilterBox.SelectedIndex = 1;
        }

        RebuildFromSnapshot();
    }

    private void LogsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        LogService.Instance.EntryAdded -= LogService_EntryAdded;
        if (_scrollViewer is not null)
        {
            _scrollViewer.ViewChanged -= ScrollViewer_ViewChanged;
            _scrollViewer = null;
        }
    }

    private void LogService_EntryAdded(object? sender, LogEntry entry) =>
        DispatcherQueue.TryEnqueue(() => AddEntry(entry));

    private void RebuildFromSnapshot()
    {
        _entries.Clear();
        foreach (var entry in LogService.Instance.Snapshot())
        {
            if (entry.Level >= _minLevel)
            {
                _entries.Add(entry);
            }
        }

        TrimDisplayed();
        UpdateEmptyState();
        AutoScrollToEnd();
    }

    private void AddEntry(LogEntry entry)
    {
        if (entry.Level < _minLevel)
        {
            return;
        }

        _entries.Add(entry);
        TrimDisplayed();
        UpdateEmptyState();
        AutoScrollToEnd();
    }

    private void TrimDisplayed()
    {
        while (_entries.Count > MaxDisplayedEntries)
        {
            _entries.RemoveAt(0);
        }
    }

    private void UpdateEmptyState()
    {
        var empty = _entries.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        LogList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AutoScrollToEnd()
    {
        if (!_autoScroll || _entries.Count == 0 || _scrollViewer is null)
        {
            return;
        }

        LogList.ScrollIntoView(_entries[^1]);
    }

    private void ScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_scrollViewer is null)
        {
            return;
        }

        var scrollable = _scrollViewer.ScrollableHeight;
        _autoScroll = scrollable == 0
            || _scrollViewer.VerticalOffset >= scrollable - 28;
    }

    private void LevelFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LevelFilterBox.SelectedIndex < 0)
        {
            return;
        }

        _minLevel = LevelFilterBox.SelectedIndex switch
        {
            0 => LogLevel.Debug,
            1 => LogLevel.Info,
            2 => LogLevel.Warning,
            3 => LogLevel.Error,
            _ => LogLevel.Info
        };
        RebuildFromSnapshot();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var lines = LogService.Instance.Snapshot()
            .Where(entry => entry.Level >= _minLevel)
            .Select(FormatLine);
        var text = string.Join(Environment.NewLine, lines);
        if (text.Length == 0)
        {
            return;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch
        {
            // 剪贴板不可用时静默忽略。
        }
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var directory = LogService.Instance.LogDirectory;
        if (string.IsNullOrEmpty(directory) || !System.IO.Directory.Exists(directory))
        {
            return;
        }

        try
        {
            await Launcher.LaunchFolderPathAsync(directory);
        }
        catch
        {
            // 无法打开文件夹时静默忽略。
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => LogService.Instance.ClearMemory();

    private static string FormatLine(LogEntry entry) =>
        $"{entry.Timestamp:HH:mm:ss.fff} [{LogService.LevelTag(entry.Level)}] [{entry.Source}] {entry.Message}";

    private static ScrollViewer? FindScrollViewer(DependencyObject start)
    {
        var queue = new System.Collections.Generic.Queue<DependencyObject>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current is ScrollViewer viewer)
            {
                return viewer;
            }

            var count = VisualTreeHelper.GetChildrenCount(current);
            for (var index = 0; index < count; index++)
            {
                queue.Enqueue(VisualTreeHelper.GetChild(current, index));
            }
        }

        return null;
    }
}

public sealed class LogTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is DateTime time ? time.ToString("HH:mm:ss.fff") : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class LogLevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var level = value is LogLevel l ? l : LogLevel.Info;
        var key = level switch
        {
            LogLevel.Error => "VoxLinkCriticalBrush",
            LogLevel.Warning => "VoxLinkWarningBrush",
            LogLevel.Debug => "TextFillColorTertiaryBrush",
            _ => "VoxLinkAccentBrush"
        };
        return Application.Current.Resources[key] as Brush ?? Application.Current.Resources["TextFillColorPrimaryBrush"];
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
