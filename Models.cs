using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace MaruLauncher;

public enum AccountType
{
    Offline,
    // Kept to load and remove profiles saved by versions that supported Microsoft sign-in.
    Microsoft
}

public enum ThemeChoice
{
    Dark,
    Light,
    System
}

public enum IssueSeverity
{
    Error,
    Warning,
    Information
}

public sealed class LauncherProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public AccountType Type { get; set; }
    public string Uuid { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class GameInstance : INotifyPropertyChanged
{
    private bool _isFavorite;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string MinecraftVersion { get; set; } = string.Empty;
    public string Loader { get; set; } = "Vanilla";
    public string RootPath { get; set; } = string.Empty;
    public InstanceSettings Settings { get; set; } = new();
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value)
                return;
            _isFavorite = value;
            OnPropertyChanged();
        }
    }

    public string VersionSummary => $"{MinecraftVersion} · {Loader}";
    public string Status
    {
        get
        {
            var gameDirectory = string.IsNullOrWhiteSpace(Settings.GameDirectory)
                ? Path.Combine(RootPath, "game")
                : Environment.ExpandEnvironmentVariables(Settings.GameDirectory);
            var versionId = string.Equals(Loader, "Fabric", StringComparison.OrdinalIgnoreCase)
                ? Settings.FabricProfileId
                : MinecraftVersion;
            if (string.IsNullOrWhiteSpace(versionId))
                return "Загрузчик не установлен";
            var metadata = Path.Combine(gameDirectory, "versions", versionId, $"{versionId}.json");
            var clientVersion = string.Equals(Loader, "Fabric", StringComparison.OrdinalIgnoreCase)
                ? MinecraftVersion
                : versionId;
            var client = Path.Combine(gameDirectory, "versions", clientVersion, $"{clientVersion}.jar");
            return File.Exists(metadata) && File.Exists(client) ? "Установлена" : "Не установлена";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class InstanceSettings
{
    public int MaximumMemoryMb { get; set; } = 4096;
    public string JavaPath { get; set; } = string.Empty;
    public int WindowWidth { get; set; } = 854;
    public int WindowHeight { get; set; } = 480;
    public bool Fullscreen { get; set; }
    public string JvmArguments { get; set; } = string.Empty;
    public string GameDirectory { get; set; } = string.Empty;
    public bool CloseLauncherAfterStart { get; set; }
    public string FabricVersion { get; set; } = string.Empty;
    public string FabricProfileId { get; set; } = string.Empty;
}

public sealed class LauncherSettings
{
    public ThemeChoice Theme { get; set; } = ThemeChoice.Dark;
}

public sealed class AppData
{
    public LauncherSettings Settings { get; set; } = new();
    public List<LauncherProfile> Profiles { get; set; } = [];
    public List<GameInstance> Instances { get; set; } = [];
    public string? ActiveProfileId { get; set; }
    public string? LastInstanceId { get; set; }
    public string? ActiveInstanceId { get; set; }
}

public sealed class DownloadOperation : INotifyPropertyChanged
{
    private string _stage = "В очереди";
    private string? _error;
    private long? _bytesReceived;
    private long? _totalBytes;
    private double? _bytesPerSecond;
    private bool _isComplete;

    public string Name { get; init; } = string.Empty;
    public string InstanceName { get; init; } = string.Empty;
    public string? InstanceId { get; init; }
    public Func<CancellationToken, Task>? RetryAction { get; set; }
    public CancellationTokenSource Cancellation { get; } = new();
    public string Stage { get => _stage; set => Set(ref _stage, value); }
    public string? Error { get => _error; set => Set(ref _error, value); }
    public long? BytesReceived { get => _bytesReceived; set => Set(ref _bytesReceived, value); }
    public long? TotalBytes { get => _totalBytes; set => Set(ref _totalBytes, value); }
    public double? BytesPerSecond { get => _bytesPerSecond; set => Set(ref _bytesPerSecond, value); }
    public bool IsComplete { get => _isComplete; set => Set(ref _isComplete, value); }

    public string ProgressText
    {
        get
        {
            var received = BytesReceived is long bytes ? FormatBytes(bytes) : "Размер неизвестен";
            var total = TotalBytes is long size ? $" / {FormatBytes(size)}" : string.Empty;
            var speed = BytesPerSecond is double rate ? $" · {FormatBytes((long)rate)}/с" : string.Empty;
            return $"{Stage} · получено {received}{total}{speed}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName is nameof(BytesReceived) or nameof(TotalBytes) or nameof(BytesPerSecond) or nameof(Stage))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProgressText)));
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }
}

public sealed record BuildIssue(IssueSeverity Severity, string Title, string Explanation, string SuggestedFix)
{
    public string Category => Severity switch
    {
        IssueSeverity.Error => "Ошибка",
        IssueSeverity.Warning => "Предупреждение",
        _ => "Информация"
    };
}
