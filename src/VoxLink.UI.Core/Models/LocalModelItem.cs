using System.Globalization;
using System.Text.Json;
using VoxLink.UI.Core.Infrastructure;

namespace VoxLink.UI.Core.Models;

public sealed class LocalModelItem : ObservableObject
{
    private string _installState = "notinstalled";
    private string _operationStatus = string.Empty;
    private double _progress;
    private bool _isBusy;
    private bool _isActive;
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    public required string SupportLevel { get; init; }
    public required string Runtime { get; init; }
    public required string Parameters { get; init; }
    public required double NumericParameterBillions { get; init; }
    public required string License { get; init; }
    public required string Languages { get; init; }
    public required string Requirements { get; init; }
    public required string SourceUrl { get; init; }
    public required string Description { get; init; }
    public string UnavailableReason { get; init; } = string.Empty;
    public long DownloadBytes { get; init; }
    public bool IsInstallable { get; init; }

    public string InstallState
    {
        get => _installState;
        private set
        {
            if (SetProperty(ref _installState, NormalizeState(value)))
            {
                RaiseActionProperties();
            }
        }
    }

    public string OperationStatus
    {
        get => _operationStatus;
        private set => SetProperty(ref _operationStatus, value);
    }

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, Math.Clamp(value, 0, 1));
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseActionProperties();
            }
        }
    }

    public bool IsActive
    {
        get => _isActive;
        internal set
        {
            if (SetProperty(ref _isActive, value))
            {
                RaiseActionProperties();
            }
        }
    }

    public bool Installed => InstallState == "installed";
    public bool IsPartial => InstallState == "partial";
    public bool CanInstall => IsInstallable && !Installed && !IsBusy;
    public bool CanRemove => IsInstallable && Installed && !IsBusy;
    public bool CanActivate => IsInstallable && Installed && !IsActive && !IsBusy;
    public bool CanTest => IsInstallable && Installed && !IsBusy;
    public bool CanRunPrimaryAction => !IsActive && !IsBusy && IsInstallable;
    public string InstallActionLabel => IsPartial ? "重试" : "安装";
    public string PrimaryActionLabel => IsActive
        ? "已启用"
        : Installed
            ? "启用"
            : IsPartial
                ? "重试并启用"
                : "安装并启用";
    public string CategoryLabel => Category switch
    {
        "asr" => "语音识别",
        "translation" => "翻译",
        "tts" => "语音合成",
        _ => Category
    };
    public string SupportLabel => SupportLevel switch
    {
        "stable" => "稳定支持",
        "experimental" => "实验支持",
        _ => "目录展示"
    };
    public string RuntimeLabel => Runtime switch
    {
        "whispercpp" => "Whisper.cpp",
        "llamacppgguf" => "llama.cpp GGUF",
        "sherpaonnxkokoro" => "sherpa-onnx Kokoro",
        _ => "仅目录信息"
    };
    public string InstallStateLabel => InstallState switch
    {
        "installed" => "已安装",
        "partial" => "需要重试",
        _ when !IsInstallable => "不可部署",
        _ => "未安装"
    };
    public string DownloadSizeLabel => DownloadBytes <= 0
        ? ""
        : DownloadBytes >= 1024L * 1024 * 1024
            ? $"{DownloadBytes / (1024d * 1024 * 1024):F1} GB"
            : $"{DownloadBytes / (1024d * 1024):F0} MB";
    public string ParameterLabel => NumericParameterBillions > 0
        ? $"{Parameters} · {NumericParameterBillions.ToString("0.###", CultureInfo.InvariantCulture)}B"
        : Parameters;

    public static LocalModelItem FromJson(JsonElement json)
    {
        var item = new LocalModelItem
        {
            Id = JsonValue.String(json, "id"),
            Name = JsonValue.String(json, "name", "未命名模型"),
            Category = JsonValue.String(json, "category"),
            SupportLevel = JsonValue.String(json, "supportLevel"),
            Runtime = JsonValue.String(json, "runtime"),
            Parameters = JsonValue.String(json, "parameters"),
            NumericParameterBillions = JsonValue.Double(json, "numericParameterBillions"),
            License = JsonValue.String(json, "license"),
            Languages = JsonValue.String(json, "languages"),
            Requirements = JsonValue.String(json, "requirements"),
            SourceUrl = JsonValue.String(json, "sourceUrl"),
            Description = JsonValue.String(json, "description"),
            UnavailableReason = JsonValue.String(json, "unavailableReason"),
            DownloadBytes = JsonValue.Int64(json, "downloadBytes"),
            IsInstallable = JsonValue.Boolean(json, "isInstallable")
        };
        item.SetInstallState(JsonValue.String(json, "installState", "notinstalled"));
        return item;
    }

    internal void BeginOperation(string status)
    {
        OperationStatus = status;
        Progress = 0;
        IsBusy = true;
    }

    internal void UpdateProgress(string status, double? progress)
    {
        OperationStatus = status;
        if (progress.HasValue)
        {
            Progress = progress.Value;
            if (progress.Value >= 1)
            {
                InstallState = "installed";
                IsBusy = false;
                return;
            }
        }

        IsBusy = true;
    }

    internal void CompleteOperation(string installState, string status)
    {
        InstallState = installState;
        OperationStatus = status;
        Progress = Installed ? 1 : 0;
        IsBusy = false;
    }

    internal void FailOperation(string status)
    {
        OperationStatus = status;
        IsBusy = false;
    }

    private void SetInstallState(string value) => InstallState = value;

    private void RaiseActionProperties()
    {
        OnPropertyChanged(nameof(Installed));
        OnPropertyChanged(nameof(IsPartial));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(CanActivate));
        OnPropertyChanged(nameof(CanTest));
        OnPropertyChanged(nameof(CanRunPrimaryAction));
        OnPropertyChanged(nameof(InstallActionLabel));
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(InstallStateLabel));
    }

    private static string NormalizeState(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "installed" => "installed",
        "partial" => "partial",
        _ => "notinstalled"
    };
}
