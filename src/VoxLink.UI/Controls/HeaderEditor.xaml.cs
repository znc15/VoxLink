using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VoxLink.UI.Core.ViewModels;

namespace VoxLink.UI.Controls;

public enum HeaderEditorTarget
{
    Translation,
    Asr,
    Speech
}
public sealed partial class HeaderEditor : UserControl
{
    private AppController? _controller;
    private HeaderEditorTarget _target;
    private bool _loading;
    private readonly Dictionary<HeaderEntry, string> _secretValues = [];

    public HeaderEditor()
    {
        InitializeComponent();
    }

    public ObservableCollection<HeaderEntry> Entries { get; } = [];

    public void Configure(AppController controller, HeaderEditorTarget target)
    {
        _controller = controller;
        _target = target;
        _loading = true;
        Entries.Clear();
        _secretValues.Clear();
        var values = target switch
        {
            HeaderEditorTarget.Translation => controller.Settings.TranslationHeaders,
            HeaderEditorTarget.Asr => controller.Settings.AsrHeaders,
            _ => controller.Settings.SpeechHeaders
        };
        foreach (var pair in values)
        {
            var entry = new HeaderEntry(pair.Key);
            Entries.Add(entry);
            _secretValues[entry] = pair.Value;
        }

        _loading = false;
    }

    private void AddHeader_Click(object sender, RoutedEventArgs args)
    {
        var entry = new HeaderEntry(string.Empty);
        Entries.Add(entry);
        _secretValues[entry] = string.Empty;
    }

    private void RemoveHeader_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: HeaderEntry entry })
        {
            Entries.Remove(entry);
            _secretValues.Remove(entry);
            Commit();
        }
    }

    private void HeaderName_TextChanged(object sender, TextChangedEventArgs args)
    {
        if (_loading || sender is not TextBox { Tag: HeaderEntry entry } textBox)
        {
            return;
        }

        entry.Name = textBox.Text;
        Commit();
    }

    private void HeaderValue_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is PasswordBox { Tag: HeaderEntry entry } passwordBox)
        {
            _loading = true;
            passwordBox.Password = _secretValues.GetValueOrDefault(entry, string.Empty);
            _loading = false;
        }
    }

    private void HeaderValue_PasswordChanged(object sender, RoutedEventArgs args)
    {
        if (_loading || sender is not PasswordBox { Tag: HeaderEntry entry } passwordBox)
        {
            return;
        }

        _secretValues[entry] = passwordBox.Password;
        Commit();
    }

    private void Commit()
    {
        if (_controller is null)
        {
            return;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries)
        {
            var name = entry.Name.Trim();
            if (name.Length > 0)
            {
                values[name] = _secretValues.GetValueOrDefault(entry, string.Empty);
            }
        }

        switch (_target)
        {
            case HeaderEditorTarget.Translation:
                _controller.Settings.TranslationHeaders = values;
                break;
            case HeaderEditorTarget.Asr:
                _controller.Settings.AsrHeaders = values;
                break;
            default:
                _controller.Settings.SpeechHeaders = values;
                break;
        }
    }
}
