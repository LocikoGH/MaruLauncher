using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.IO;
using System.Diagnostics;

namespace MaruLauncher;

public static class AppPaths
{
    private static bool IsPortable => File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt"));
    public static string Root => IsPortable
        ? Path.Combine(AppContext.BaseDirectory, "data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MaruLauncher");
    public static string Instances => Path.Combine(Root, "instances");
    public static string Accounts => Path.Combine(Root, "accounts");
    public static string DataFile => Path.Combine(Root, "launcher.json");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Instances);
        Directory.CreateDirectory(Accounts);
    }
}

public static class LauncherLog
{
    private static readonly object Sync = new();

    public static void Write(string category, Exception exception, string details)
    {
        try
        {
            var directory = Path.Combine(AppPaths.Root, "logs");
            Directory.CreateDirectory(directory);
            var entry = $"{DateTimeOffset.Now:O} [{category}] {details}{Environment.NewLine}{exception}{Environment.NewLine}";
            lock (Sync)
                File.AppendAllText(Path.Combine(directory, "launcher.log"), entry, new UTF8Encoding(false));
        }
        catch (Exception loggingException) when (
            loggingException is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Unable to write launcher log: {loggingException}");
        }
    }
}

public sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    public AppData Data { get; }

    public StateStore()
    {
        AppPaths.EnsureCreated();
        Data = File.Exists(AppPaths.DataFile)
            ? JsonSerializer.Deserialize<AppData>(File.ReadAllText(AppPaths.DataFile), JsonOptions)
                ?? throw new InvalidDataException("Файл настроек пуст или содержит некорректные данные.")
            : new AppData();
    }

    public void Save()
    {
        var temporaryFile = AppPaths.DataFile + ".tmp";
        File.WriteAllText(temporaryFile, JsonSerializer.Serialize(Data, JsonOptions), new UTF8Encoding(false));
        File.Move(temporaryFile, AppPaths.DataFile, true);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public static class OfflineIdentity
{
    public static bool IsValidName(string name) =>
        name.Length is >= 3 and <= 16 &&
        name.All(static character => char.IsAsciiLetterOrDigit(character) || character == '_');

    public static string CreateUuid(string name)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes($"OfflinePlayer:{name}"));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public static class LegacyTokenCleanup
{
    public static void DeleteTokenFiles()
    {
        foreach (var tokenFile in Directory.EnumerateFiles(AppPaths.Accounts, "*.bin", SearchOption.TopDirectoryOnly))
        {
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(tokenFile), "N", out _))
                File.Delete(tokenFile);
        }
    }
}
