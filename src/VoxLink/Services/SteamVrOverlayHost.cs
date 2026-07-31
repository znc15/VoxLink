using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VoxLink.Models;
using Valve.VR;

namespace VoxLink.Services;

internal sealed class SteamVrOverlayHost : IDisposable
{
    private const string OverlayKey = "com.voxlink.translation.subtitles";
    private readonly DispatcherTimer _hideTimer;
    private readonly Func<bool> _isSteamVrRunning;
    private readonly string _textureDirectory = Path.Combine(
        Path.GetTempPath(),
        "VoxLink",
        $"steamvr-overlay-{Environment.ProcessId}");
    private bool _enabled;
    private bool _initialized;
    private bool _openVrInitialized;
    private bool _disposed;
    private int _textureIndex;
    private ulong _overlayHandle = OpenVR.k_ulOverlayHandleInvalid;
    private DateTimeOffset _nextInitializationAttempt;
    private double _widthMeters = 1.6;
    private double _distanceMeters = 1.8;
    private double _verticalOffsetMeters = -0.35;

    public string Status { get; private set; } = "SteamVR 字幕未启用";

    public SteamVrOverlayHost()
        : this(IsSteamVrRunning)
    {
    }

    internal SteamVrOverlayHost(Func<bool> isSteamVrRunning)
    {
        ArgumentNullException.ThrowIfNull(isSteamVrRunning);
        _isSteamVrRunning = isSteamVrRunning;
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            Hide();
        };
    }
    public void Configure(
        bool enabled,
        double widthMeters,
        double distanceMeters,
        double verticalOffsetMeters)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var wasEnabled = _enabled;
        _enabled = enabled;
        if (enabled && !wasEnabled)
        {
            _nextInitializationAttempt = DateTimeOffset.MinValue;
        }
        _widthMeters = Math.Clamp(widthMeters, 0.6, 3.0);
        _distanceMeters = Math.Clamp(distanceMeters, 0.6, 4.0);
        _verticalOffsetMeters = Math.Clamp(verticalOffsetMeters, -1.0, 0.5);

        if (!_enabled)
        {
            Hide();
            Status = "SteamVR 字幕未启用";
            return;
        }

        if (_initialized)
        {
            try
            {
                ApplyLayout();
                Status = "SteamVR 字幕已连接";
            }
            catch (Exception exception)
            {
                Status = $"SteamVR 字幕不可用：{exception.GetBaseException().Message}";
                ResetOpenVr();
            }
        }
        else
        {
            Status = "等待 SteamVR";
        }
    }

    public string ShowSubtitle(ConversationMessage message) =>
        ShowSubtitleCore(message, allowWhenDisabled: false);

    public string ShowTest()
    {
        _nextInitializationAttempt = DateTimeOffset.MinValue;
        var message = new ConversationMessage(
            TranslationDirection.Inbound,
            "VRChat subtitle test",
            "VoxLink SteamVR 字幕测试",
            DateTimeOffset.Now);
        return ShowSubtitleCore(message, allowWhenDisabled: true);
    }

    private string ShowSubtitleCore(ConversationMessage message, bool allowWhenDisabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_enabled && !allowWhenDisabled)
        {
            return Status;
        }

        if (!EnsureInitialized())
        {
            return Status;
        }

        try
        {
            Directory.CreateDirectory(_textureDirectory);
            var texturePath = Path.Combine(_textureDirectory, $"subtitle-{_textureIndex++ % 2}.png");
            RenderSubtitleTexture(message, texturePath);
            ThrowOnOverlayError(OpenVR.Overlay.SetOverlayFromFile(_overlayHandle, texturePath));
            ThrowOnOverlayError(OpenVR.Overlay.ShowOverlay(_overlayHandle));
            _hideTimer.Stop();
            _hideTimer.Start();
            Status = "SteamVR 字幕已显示";
        }
        catch (Exception exception)
        {
            Status = $"SteamVR 字幕失败：{exception.GetBaseException().Message}";
            ResetOpenVr();
        }

        return Status;
    }

    public void Hide()
    {
        if (!_initialized || _overlayHandle == OpenVR.k_ulOverlayHandleInvalid)
        {
            return;
        }

        try
        {
            OpenVR.Overlay.HideOverlay(_overlayHandle);
        }
        catch (Exception exception)
        {
            Status = $"SteamVR 字幕不可用：{exception.GetBaseException().Message}";
            ResetOpenVr();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hideTimer.Stop();
        ResetOpenVr();

        try
        {
            Directory.Delete(_textureDirectory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private bool EnsureInitialized()
    {
        if (_initialized)
        {
            return true;
        }

        if (DateTimeOffset.UtcNow < _nextInitializationAttempt)
        {
            return false;
        }

        _nextInitializationAttempt = DateTimeOffset.UtcNow.AddSeconds(15);
        try
        {
            if (!_isSteamVrRunning())
            {
                Status = "SteamVR 未运行";
                return false;
            }

            if (!OpenVR.IsRuntimeInstalled())
            {
                Status = "未安装 SteamVR/OpenVR";
                return false;
            }

            if (!OpenVR.IsHmdPresent())
            {
                Status = "SteamVR 未检测到头显";
                return false;
            }

            var initError = EVRInitError.None;
            _ = OpenVR.Init(ref initError, EVRApplicationType.VRApplication_Overlay);
            if (initError != EVRInitError.None)
            {
                Status = $"SteamVR 初始化失败：{initError}";
                return false;
            }

            _openVrInitialized = true;
            var overlayError = OpenVR.Overlay.FindOverlay(OverlayKey, ref _overlayHandle);
            if (overlayError == EVROverlayError.UnknownOverlay)
            {
                overlayError = OpenVR.Overlay.CreateOverlay(
                    OverlayKey,
                    "VoxLink Translation Subtitles",
                    ref _overlayHandle);
            }

            ThrowOnOverlayError(overlayError);
            _initialized = true;
            ApplyLayout();
            Status = "SteamVR 字幕已连接";
            return true;
        }
        catch (Exception exception)
        {
            Status = $"SteamVR 字幕不可用：{exception.GetBaseException().Message}";
            ResetOpenVr();
            return false;
        }
    }

    private void ApplyLayout()
    {
        var transform = new HmdMatrix34_t
        {
            m0 = 1,
            m5 = 1,
            m10 = 1,
            m3 = 0,
            m7 = (float)_verticalOffsetMeters,
            m11 = (float)-_distanceMeters
        };
        ThrowOnOverlayError(OpenVR.Overlay.SetOverlayWidthInMeters(
            _overlayHandle,
            (float)_widthMeters));
        ThrowOnOverlayError(OpenVR.Overlay.SetOverlayAlpha(_overlayHandle, 0.96f));
        ThrowOnOverlayError(OpenVR.Overlay.SetOverlayTransformTrackedDeviceRelative(
            _overlayHandle,
            OpenVR.k_unTrackedDeviceIndex_Hmd,
            ref transform));
    }

    private static void RenderSubtitleTexture(ConversationMessage message, string path)
    {
        const int width = 1400;
        const int height = 440;
        var primaryText = message.PrimaryDisplayText;
        var secondaryText = message.SecondaryDisplayText;
        var sourceText = message.SourceDisplayText;
        var header = new TextBlock
        {
            Text = TrimForDisplay(message.HeaderLabel, 80),
            Foreground = new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)),
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(48, 22, 48, 0)
        };
        var source = new TextBlock
        {
            Text = TrimForDisplay(sourceText, 220),
            Foreground = new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)),
            FontSize = sourceText.Length > 120 ? 20 : 24,
            MaxHeight = 80,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(48, 8, 48, 20),
            Visibility = string.IsNullOrWhiteSpace(sourceText) ? Visibility.Collapsed : Visibility.Visible
        };
        var translated = new TextBlock
        {
            Text = TrimForDisplay(primaryText, 180),
            Foreground = Brushes.White,
            FontSize = primaryText.Length switch
            {
                > 120 => 34,
                > 70 => 42,
                _ => 52
            },
            FontWeight = FontWeights.SemiBold,
            MaxHeight = 210,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(48, 14, 48, 0)
        };
        var secondary = new TextBlock
        {
            Text = TrimForDisplay(secondaryText, 180),
            Foreground = new SolidColorBrush(Color.FromArgb(230, 185, 255, 241)),
            FontSize = secondaryText.Length > 100 ? 24 : 30,
            MaxHeight = 80,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(48, 8, 48, 0),
            Visibility = string.IsNullOrWhiteSpace(secondaryText) ? Visibility.Collapsed : Visibility.Visible
        };
        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(translated);
        panel.Children.Add(secondary);
        panel.Children.Add(source);
        var visual = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(28),
            Background = new SolidColorBrush(Color.FromArgb(230, 18, 27, 25)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
            BorderThickness = new Thickness(2),
            Child = panel
        };
        visual.Measure(new Size(width, height));
        visual.Arrange(new Rect(0, 0, width, height));
        visual.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        encoder.Save(stream);
    }

    private static string TrimForDisplay(string text, int maximumLength) =>
        text.Length <= maximumLength ? text : string.Concat(text.AsSpan(0, maximumLength), "…");

    private static bool IsSteamVrRunning()
    {
        var processes = Process.GetProcessesByName("vrserver");
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private void ResetOpenVr()
    {
        if (_overlayHandle != OpenVR.k_ulOverlayHandleInvalid)
        {
            try
            {
                OpenVR.Overlay.HideOverlay(_overlayHandle);
                OpenVR.Overlay.DestroyOverlay(_overlayHandle);
            }
            catch
            {
            }
            finally
            {
                _overlayHandle = OpenVR.k_ulOverlayHandleInvalid;
            }
        }

        if (_openVrInitialized)
        {
            try
            {
                OpenVR.Shutdown();
            }
            catch
            {
            }
        }

        _openVrInitialized = false;
        _initialized = false;
    }
    private static void ThrowOnOverlayError(EVROverlayError error)
    {
        if (error != EVROverlayError.None)
        {
            throw new InvalidOperationException($"OpenVR Overlay 返回 {error}。");
        }
    }
}
