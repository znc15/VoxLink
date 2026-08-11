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
    private bool _commitImmediately = true;
    private readonly Dictionary<HeaderEntry, string> _secretValues = [];

    public HeaderEditor()
    {
        InitializeComponent();
    }

    public ObservableCollection<HeaderEntry> Entries { get; } = [];

    public void Configure(
        AppController controller,
        HeaderEditorTarget target,
        bool commitImmediately = true)
    {
        _controller = controller;
        _target = target;
        _commitImmediately = commitImmediately;
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
            CommitIfImmediate();
        }
    }

    private void HeaderName_TextChanged(object sender, TextChangedEventArgs args)
    {
        if (_loading || sender is not TextBox { Tag: HeaderEntry entry } textBox)
        {
            return;
        }

        entry.Name = textBox.Text;
        CommitIfImmediate();
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
        CommitIfImmediate();
    }

    private void CommitIfImmediate()
    {
        if (_commitImmediately)
        {
            Commit();
        }
    }

    public bool Validate()
    {
        HeaderErrorBar.IsOpen = false;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries)
        {
            var name = entry.Name.Trim();
            if (name.Length == 0)
            {
                return ShowValidationError("请求头名称不能为空，请填写名称或删除该行。");
            }
            if (!IsValidHeaderName(name))
            {
                return ShowValidationError(
                    $"请求头 {name} 名称无效，只能使用 HTTP token 字符。");
            }
            if (IsRestrictedHeader(name))
            {
                return ShowValidationError($"请求头 {name} 由 VoxLink 自动管理，不能自定义。");
            }
            var value = _secretValues.GetValueOrDefault(entry, string.Empty);
            if (value.Contains('\r') || value.Contains('\n'))
            {
                return ShowValidationError($"请求头 {name} 的值不能包含换行符。");
            }
            if (!names.Add(name))
            {
                return ShowValidationError($"请求头 {name} 重复，请只保留一项。");
            }
        }

        return true;
    }

    public void Commit()
    {
        if (_controller is null || !Validate())
        {
            return;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries)
        {
            var name = entry.Name.Trim();
            values.Add(name, _secretValues.GetValueOrDefault(entry, string.Empty));
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

    private bool ShowValidationError(string message)
    {
        HeaderErrorBar.Message = message;
        HeaderErrorBar.IsOpen = true;
        return false;
    }

    private static bool IsValidHeaderName(string name) =>
        name.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || "!#$%&'*+-.^_`|~".Contains(character, StringComparison.Ordinal));

    private static bool IsRestrictedHeader(string name)
    {
        return name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Host", StringComparison.OrdinalIgnoreCase);
    }
}
