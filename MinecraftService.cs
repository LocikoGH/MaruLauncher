using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;

namespace MaruLauncher;

public sealed record MinecraftVersionItem(string Id, string Type);
public sealed record LaunchResult(DateTimeOffset StartedAt, int? ExitCode, string MinecraftVersion, string Loader, string JavaPath);

public sealed class MinecraftService
{
    private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private static readonly string[] SupportedReleaseVersions =
        ["26.3", "26.1.2", "1.21.11", "1.21.4"];
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    public event Action<string>? DiagnosticWriteFailed;
    public event Action<string>? LaunchFailed;

    public async Task<IReadOnlyList<MinecraftVersionItem>> GetReleaseVersionsAsync(CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(ManifestUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var officialVersions = json.RootElement.GetProperty("versions").EnumerateArray()
            .Where(item => item.GetProperty("type").GetString() == "release")
            .Select(item => item.GetProperty("id").GetString())
            .Where(id => id is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var versions = SupportedReleaseVersions
            .Where(officialVersions.Contains)
            .Select(version => new MinecraftVersionItem(version, "release"))
            .ToArray();
        if (versions.Length != SupportedReleaseVersions.Length)
            throw new InvalidDataException("Официальный манифест не содержит все поддерживаемые версии Minecraft.");
        return versions;
    }

    public Task InstallVanillaAsync(
        GameInstance instance, DownloadOperation operation, CancellationToken cancellationToken) =>
        FileOperationCoordinator.RunInstallationAsync(
            GetGameDirectory(instance), cancellationToken,
            () => InstallVanillaCoreAsync(instance, operation, cancellationToken));

    private async Task InstallVanillaCoreAsync(
        GameInstance instance, DownloadOperation operation, CancellationToken cancellationToken)
    {
        var gameDirectory = GetGameDirectory(instance);
        var versionDirectory = Path.Combine(gameDirectory, "versions", instance.MinecraftVersion);
        Directory.CreateDirectory(versionDirectory);
        var versionFile = Path.Combine(versionDirectory, $"{instance.MinecraftVersion}.json");
        var bundledClientPath = GetBundledClientPath(instance.MinecraftVersion);
        JsonObject metadata;
        if (bundledClientPath is not null)
        {
            SetStage(operation, "Проверка официальных метаданных версии");
            metadata = await DownloadVersionMetadataAsync(instance.MinecraftVersion, cancellationToken);
            await File.WriteAllTextAsync(versionFile, metadata.ToJsonString(), cancellationToken);
        }
        else if (File.Exists(versionFile))
        {
            metadata = JsonNode.Parse(await File.ReadAllTextAsync(versionFile, cancellationToken))?.AsObject()
                ?? throw new InvalidDataException("Метаданные установленной версии повреждены.");
        }

        else
        {
            SetStage(operation, "Получение официальных метаданных версии");
            metadata = await DownloadVersionMetadataAsync(instance.MinecraftVersion, cancellationToken);
            await File.WriteAllTextAsync(versionFile, metadata.ToJsonString(), cancellationToken);
        }

        await InstallMetadataFilesAsync(gameDirectory, metadata, operation, cancellationToken, bundledClientPath);
        instance.Loader = "Vanilla";
        instance.Settings.FabricProfileId = string.Empty;
        instance.Settings.FabricVersion = string.Empty;
    }

    public Task InstallFabricAsync(
        GameInstance instance, string fabricVersion, DownloadOperation operation, CancellationToken cancellationToken) =>
        FileOperationCoordinator.RunInstallationAsync(
            GetGameDirectory(instance), cancellationToken,
            () => InstallFabricCoreAsync(instance, fabricVersion, operation, cancellationToken));

    private async Task InstallFabricCoreAsync(
        GameInstance instance, string fabricVersion, DownloadOperation operation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fabricVersion))
            throw new ArgumentException("Укажите версию загрузчика Fabric.", nameof(fabricVersion));
        if (string.Equals(fabricVersion, "latest", StringComparison.OrdinalIgnoreCase))
            fabricVersion = await GetLatestFabricVersionAsync(instance.MinecraftVersion, cancellationToken);

        await InstallVanillaCoreAsync(instance, operation, cancellationToken);
        var gameDirectory = GetGameDirectory(instance);
        SetStage(operation, "Получение официального профиля Fabric");
        var profileUri = new Uri(
            $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(instance.MinecraftVersion)}/{Uri.EscapeDataString(fabricVersion)}/profile/json");
        using var response = await Http.GetAsync(profileUri, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException(
                $"Fabric Loader {fabricVersion} недоступен для Minecraft {instance.MinecraftVersion}. Выберите совместимую версию.");
        response.EnsureSuccessStatusCode();
        var profile = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))?.AsObject()
            ?? throw new InvalidDataException("Fabric вернул пустой или некорректный профиль.");
        if (profile["id"]?.GetValue<string>() is not { Length: > 0 } profileId ||
            profile["inheritsFrom"]?.GetValue<string>() is not { } parentId ||
            !string.Equals(parentId, instance.MinecraftVersion, StringComparison.OrdinalIgnoreCase) ||
            profileId is "." or ".." || profileId.Contains('/') || profileId.Contains('\\') ||
            profileId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            profile["mainClass"]?.GetValue<string>() is not { Length: > 0 } ||
            profile["libraries"] is not JsonArray)
            throw new InvalidDataException("Профиль Fabric не содержит необходимых данных или относится к другой версии Minecraft.");

        var profileDirectory = SafeCombine(Path.Combine(gameDirectory, "versions"), profileId);
        Directory.CreateDirectory(profileDirectory);
        var profilePath = Path.Combine(profileDirectory, $"{profileId}.json");
        var temporaryProfilePath = profilePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryProfilePath, profile.ToJsonString(), cancellationToken);
            File.Move(temporaryProfilePath, profilePath, true);
        }
        finally
        {
            if (File.Exists(temporaryProfilePath))
                File.Delete(temporaryProfilePath);
        }

        instance.Loader = "Fabric";
        instance.Settings.FabricVersion = fabricVersion;
        instance.Settings.FabricProfileId = profileId;
        var effective = await ReadEffectiveMetadataAsync(instance, cancellationToken);
        await InstallMetadataFilesAsync(gameDirectory, effective, operation, cancellationToken);
    }

    public async Task InstallFabricApiAsync(
        GameInstance instance, DownloadOperation operation, CancellationToken cancellationToken)
    {
        if (!string.Equals(instance.Loader, "Fabric", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Сначала установите Fabric для этого экземпляра.");
        var query = Uri.EscapeDataString($"[\"{instance.MinecraftVersion}\"]");
        var url = $"https://api.modrinth.com/v2/project/fabric-api/version?game_versions={query}&loaders=%5B%22fabric%22%5D";
        using var response = await Http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var versions = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var file = versions.RootElement.EnumerateArray()
            .SelectMany(version => version.GetProperty("files").EnumerateArray())
            .FirstOrDefault(candidate => candidate.TryGetProperty("primary", out var primary) && primary.GetBoolean());
        if (file.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException(
                $"Для Minecraft {instance.MinecraftVersion} не найдена совместимая версия Fabric API.");
        var downloadUrl = file.GetProperty("url").GetString();
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            (uri.Host != "cdn.modrinth.com" && !uri.Host.EndsWith(".modrinth.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Modrinth вернул ссылку вне доверенного домена загрузок.");
        var filename = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrWhiteSpace(filename) || !filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Ссылка Fabric API не указывает на JAR-файл.");
        var modsDirectory = Path.Combine(GetGameDirectory(instance), "mods");
        Directory.CreateDirectory(modsDirectory);
        var destination = Path.Combine(modsDirectory, filename);
        await DownloadFileAsync(uri, destination,
            file.TryGetProperty("hashes", out var hashes) && hashes.TryGetProperty("sha1", out var sha1) ? sha1.GetString() : null,
            operation, "Загрузка Fabric API с Modrinth", cancellationToken,
            expectedSize: file.TryGetProperty("size", out var size) && size.TryGetInt64(out var length) ? length : null);
    }

    public Task<int> LaunchAsync(
        GameInstance instance, LauncherProfile profile, DownloadOperation operation,
        CancellationToken cancellationToken) =>
        FileOperationCoordinator.RunInstallationAsync(
            GetGameDirectory(instance), cancellationToken,
            () => LaunchCoreAsync(instance, profile, operation, cancellationToken));

    private async Task<int> LaunchCoreAsync(
        GameInstance instance, LauncherProfile profile, DownloadOperation operation,
        CancellationToken cancellationToken)
    {
        ValidateLaunchSettings(instance);
        if (!string.Equals(instance.Loader, "Fabric", StringComparison.OrdinalIgnoreCase))
            await InstallVanillaCoreAsync(instance, operation, cancellationToken);
        else
        {
            var metadata = await ReadEffectiveMetadataAsync(instance, cancellationToken);
            await InstallMetadataFilesAsync(GetGameDirectory(instance), metadata, operation, cancellationToken);
        }

        var gameDirectory = GetGameDirectory(instance);
        var metadataRoot = await ReadEffectiveMetadataAsync(instance, cancellationToken);
        var mainClass = metadataRoot["mainClass"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(mainClass))
            throw new InvalidDataException("В метаданных версии отсутствует точка входа Minecraft.");

        var requiredJavaMajor = metadataRoot["javaVersion"]?["majorVersion"]?.GetValue<int>();
        var javaPath = await ResolveCompatibleJavaPathAsync(
            instance.Settings.JavaPath, requiredJavaMajor ?? 8, cancellationToken);
        var librariesRoot = Path.Combine(gameDirectory, "libraries");
        var nativeDirectory = Path.Combine(gameDirectory, "natives", instance.Id);
        Directory.CreateDirectory(nativeDirectory);
        foreach (var nativeGroup in new[] { "java", "jna", "lwjgl", "netty" })
            Directory.CreateDirectory(Path.Combine(nativeDirectory, nativeGroup));
        SetStage(operation, "Подготовка библиотек и нативных файлов");
        var classpath = await BuildClasspathAsync(metadataRoot, gameDirectory, nativeDirectory, operation, cancellationToken);
        var assetIndexId = metadataRoot["assetIndex"]?["id"]?.GetValue<string>()
            ?? metadataRoot["assets"]?.GetValue<string>()
            ?? "legacy";
        var assetsRoot = Path.Combine(gameDirectory, "assets");
        var gameAssets = metadataRoot["assetIndex"]?["virtual"]?.GetValue<bool>() == true
            ? Path.Combine(assetsRoot, "virtual", assetIndexId)
            : assetsRoot;
        var loggingConfigPath = GetLoggingConfigPath(metadataRoot, gameDirectory);
        var substitutions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["${auth_player_name}"] = profile.Name,
            ["${version_name}"] = instance.MinecraftVersion,
            ["${game_directory}"] = GetGameDirectory(instance),
            ["${assets_root}"] = assetsRoot,
            ["${assets_index_name}"] = assetIndexId,
            ["${auth_uuid}"] = profile.Uuid,
            ["${auth_access_token}"] = string.Empty,
            ["${auth_session}"] = "-",
            ["${clientid}"] = string.Empty,
            ["${auth_xuid}"] = string.Empty,
            ["${user_type}"] = "legacy",
            ["${version_type}"] = metadataRoot["type"]?.GetValue<string>() ?? "release",
            ["${natives_directory}"] = nativeDirectory,
            ["${library_directory}"] = librariesRoot,
            ["${classpath}"] = classpath,
            ["${classpath_separator}"] = Path.PathSeparator.ToString(),
            ["${launcher_name}"] = "MaruLauncher",
            ["${launcher_version}"] = "1.0",
            ["${path}"] = loggingConfigPath ?? string.Empty,
            ["${quickPlayPath}"] = string.Empty,
            ["${quickPlaySingleplayer}"] = string.Empty,
            ["${quickPlayMultiplayer}"] = string.Empty,
            ["${quickPlayRealms}"] = string.Empty,
            ["${resolution_width}"] = instance.Settings.WindowWidth.ToString(),
            ["${resolution_height}"] = instance.Settings.WindowHeight.ToString(),
            ["${user_properties}"] = "{}",
            ["${game_assets}"] = gameAssets
        };
        var features = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["is_demo_user"] = false,
            ["has_custom_resolution"] = !instance.Settings.Fullscreen,
            ["has_quick_plays_support"] = false,
            ["is_quick_play_singleplayer"] = false,
            ["is_quick_play_multiplayer"] = false,
            ["is_quick_play_realms"] = false
        };
        var arguments = new List<string>();
        arguments.AddRange(GetArguments(metadataRoot, "jvm", features).Select(value => Substitute(value, substitutions)));
        if (metadataRoot["logging"]?["client"]?["argument"]?.GetValue<string>() is { Length: > 0 } loggingArgument)
            arguments.Add(Substitute(loggingArgument, substitutions));
        arguments.AddRange(SplitArguments(instance.Settings.JvmArguments));
        arguments.Add($"-Xmx{instance.Settings.MaximumMemoryMb}M");
        if (!arguments.Any(argument => argument.StartsWith("-Djava.library.path=", StringComparison.Ordinal)))
            arguments.Add("-Djava.library.path=" + nativeDirectory);
        if (!arguments.Contains("-cp", StringComparer.Ordinal) &&
            !arguments.Contains("-classpath", StringComparer.Ordinal))
        {
            arguments.Add("-cp");
            arguments.Add(classpath);
        }
        arguments.Add(mainClass);

        var gameArguments = GetGameArguments(metadataRoot, features)
            .Select(value => Substitute(value, substitutions)).ToList();
        if (instance.Settings.Fullscreen && !gameArguments.Contains("--fullscreen", StringComparer.Ordinal))
        {
            gameArguments.Add("--fullscreen");
        }
        else if (!instance.Settings.Fullscreen && !gameArguments.Contains("--width", StringComparer.Ordinal))
        {
            gameArguments.Add("--width");
            gameArguments.Add(instance.Settings.WindowWidth.ToString());
            gameArguments.Add("--height");
            gameArguments.Add(instance.Settings.WindowHeight.ToString());
        }
        arguments.AddRange(gameArguments);

        var executableDirectory = Path.GetDirectoryName(javaPath)!;
        var consoleJavaPath = Path.Combine(executableDirectory, "java.exe");
        var startInfo = new ProcessStartInfo(
            File.Exists(consoleJavaPath) ? consoleJavaPath : javaPath)
        {
            UseShellExecute = false,
            WorkingDirectory = gameDirectory,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        SetStage(operation, "Запуск Minecraft");
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException("Windows не смогла запустить Java.");
        var launchTime = DateTimeOffset.Now;
        SaveLaunchResult(instance, launchTime, null, javaPath);
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        var exitTask = process.WaitForExitAsync();
        var startupTimeout = Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        if (await Task.WhenAny(exitTask, startupTimeout) == exitTask)
        {
            try
            {
                await exitTask;
                var output = await standardOutput;
                var error = await standardError;
                var logPath = WriteLaunchProcessOutput(instance, javaPath, process.ExitCode, output, error);
                SaveLaunchResult(instance, launchTime, process.ExitCode, javaPath);
                throw new InvalidOperationException(
                    $"Minecraft завершился через {Math.Max(0, (DateTimeOffset.Now - launchTime).TotalSeconds):0.#} с " +
                    $"(код {process.ExitCode}). Вывод Java сохранён: {logPath}" +
                    (string.IsNullOrWhiteSpace(error) && string.IsNullOrWhiteSpace(output)
                        ? string.Empty
                        : $"{Environment.NewLine}{Truncate((error + Environment.NewLine + output).Trim(), 1600)}"));
            }
            finally
            {
                process.Dispose();
            }
        }
        var processId = process.Id;
        _ = ObserveLaunchProcessAsync(process, exitTask, standardOutput, standardError,
            instance, launchTime, javaPath);
        return processId;
    }

    private async Task ObserveLaunchProcessAsync(
        Process process, Task exitTask, Task<string> standardOutput, Task<string> standardError,
        GameInstance instance, DateTimeOffset launchTime, string javaPath)
    {
        try
        {
            await exitTask;
            var output = await standardOutput;
            var error = await standardError;
            var logPath = WriteLaunchProcessOutput(instance, javaPath, process.ExitCode, output, error);
            SaveLaunchResult(instance, launchTime, process.ExitCode, javaPath);
            if (process.ExitCode != 0)
            {
                LauncherLog.Write("minecraft-exit",
                    new InvalidOperationException($"Minecraft exited with code {process.ExitCode}."),
                    $"Instance: {instance.Name}; version: {instance.MinecraftVersion}; Java: {javaPath}; log: {logPath}");
                LaunchFailed?.Invoke(
                    $"Minecraft завершился с кодом {process.ExitCode}. Подробный вывод: {logPath}");
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
        {
            LauncherLog.Write("minecraft-process-monitor", exception,
                $"Instance: {instance.Name}; version: {instance.MinecraftVersion}; Java: {javaPath}");
        }
        finally
        {
            process.Dispose();
        }
    }

    private static string WriteLaunchProcessOutput(
        GameInstance instance, string javaPath, int exitCode, string standardOutput, string standardError)
    {
        var diagnostics = Path.Combine(instance.RootPath, "diagnostics");
        Directory.CreateDirectory(diagnostics);
        var logPath = Path.Combine(diagnostics, "java-process.log");
        var contents = new StringBuilder()
            .AppendLine($"Time: {DateTimeOffset.Now:O}")
            .AppendLine($"Minecraft: {instance.MinecraftVersion}")
            .AppendLine($"Java: {javaPath}")
            .AppendLine($"Exit code: {exitCode}")
            .AppendLine()
            .AppendLine("--- stderr ---")
            .AppendLine(standardError)
            .AppendLine("--- stdout ---")
            .AppendLine(standardOutput)
            .ToString();
        File.WriteAllText(logPath, contents, new UTF8Encoding(false));
        if (!string.IsNullOrWhiteSpace(standardError) || !string.IsNullOrWhiteSpace(standardOutput))
            LauncherLog.Write("minecraft-output",
                new InvalidOperationException(Truncate((standardError + Environment.NewLine + standardOutput).Trim(), 6000)),
                $"Instance: {instance.Name}; version: {instance.MinecraftVersion}; exit code: {exitCode}; log: {logPath}");
        return logPath;
    }

    public static string GetGameDirectory(GameInstance instance)
    {
        var path = string.IsNullOrWhiteSpace(instance.Settings.GameDirectory)
            ? Path.Combine(instance.RootPath, "game")
            : Environment.ExpandEnvironmentVariables(instance.Settings.GameDirectory.Trim());
        return Path.GetFullPath(path);
    }

    public static void ValidateLaunchSettings(GameInstance instance)
    {
        if (string.IsNullOrWhiteSpace(instance.Name))
            throw new InvalidOperationException("Укажите название экземпляра.");
        if (instance.Settings.MaximumMemoryMb < 1024 || instance.Settings.MaximumMemoryMb > 65536)
            throw new InvalidOperationException("ОЗУ должно быть от 1024 до 65536 МБ.");
        if (!instance.Settings.Fullscreen &&
            (instance.Settings.WindowWidth is < 320 or > 7680 || instance.Settings.WindowHeight is < 240 or > 4320))
            throw new InvalidOperationException("Разрешение должно быть в диапазоне от 320×240 до 7680×4320.");
        var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (available > 0 && instance.Settings.MaximumMemoryMb * 1024L * 1024L > available * 0.9)
            throw new InvalidOperationException("Выделенный объём ОЗУ почти равен доступной памяти Windows; уменьшите значение.");
        var gameDirectory = GetGameDirectory(instance);
        if (string.IsNullOrWhiteSpace(gameDirectory))
            throw new InvalidOperationException("Не удалось определить каталог игры.");
        _ = SplitArguments(instance.Settings.JvmArguments);
    }

    private static async Task<string> ResolveCompatibleJavaPathAsync(
        string configuredPath, int requiredMajor, CancellationToken cancellationToken)
    {
        string? bestCompatiblePath = null;
        var bestCompatibleMajor = int.MaxValue;
        var highestFoundMajor = 0;
        foreach (var candidate in GetJavaCandidates(configuredPath).Take(64))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(candidate))
                continue;
            int major;
            try
            {
                var javaExecutable = Path.Combine(Path.GetDirectoryName(candidate)!, "java.exe");
                major = await GetJavaMajorVersionAsync(
                    File.Exists(javaExecutable) ? javaExecutable : candidate, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or
                InvalidDataException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                continue;
            }
            highestFoundMajor = Math.Max(highestFoundMajor, major);
            if (major < requiredMajor)
                continue;
            if (major == requiredMajor)
                return candidate;
            if (major < bestCompatibleMajor)
            {
                bestCompatiblePath = candidate;
                bestCompatibleMajor = major;
            }
        }
        if (bestCompatiblePath is not null)
            return bestCompatiblePath;
        if (highestFoundMajor > 0)
            throw new InvalidOperationException(
                $"Для Minecraft требуется Java {requiredMajor} или новее; найдена только Java {highestFoundMajor}. " +
                "Установите совместимую Java либо добавьте её в JAVA_HOME или PATH.");
        throw new FileNotFoundException(
            $"Не найдена совместимая Java {requiredMajor}. Установите Java или добавьте её в JAVA_HOME или PATH.");
    }

    private static IEnumerable<string> GetJavaCandidates(string configuredPath)
    {
        var candidates = new List<string>();
        AddJavaCandidate(candidates, configuredPath);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var expandedPath = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"'));
            if (!Path.IsPathRooted(expandedPath))
                AddJavaCandidate(candidates, Path.Combine(FindApplicationRoot() ?? AppContext.BaseDirectory, expandedPath));
        }
        if (FindBundledJavaPath() is { } bundledJava)
            AddJavaCandidate(candidates, bundledJava);
        if (FindApplicationRoot() is { } applicationRoot)
        {
            foreach (var directory in new[] { "java-21", "java-25" })
                AddJavaCandidate(candidates, Path.Combine(applicationRoot, directory, "bin", "javaw.exe"));
        }
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
            AddJavaCandidate(candidates, Path.Combine(javaHome, "bin", "javaw.exe"));
        foreach (var directory in Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [])
            AddJavaCandidate(candidates, Path.Combine(directory.Trim().Trim('"'), "javaw.exe"));

        var programFilesRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
        var vendors = new[] { "Java", "Eclipse Adoptium", "Microsoft", "Amazon Corretto", "Zulu", "BellSoft" };
        foreach (var programFiles in programFilesRoots)
        {
            foreach (var vendor in vendors)
            {
                var vendorDirectory = Path.Combine(programFiles, vendor);
                if (!Directory.Exists(vendorDirectory))
                    continue;
                foreach (var installation in Directory.EnumerateDirectories(vendorDirectory, "*", SearchOption.TopDirectoryOnly))
                    AddJavaCandidate(candidates, Path.Combine(installation, "bin", "javaw.exe"));
            }
        }
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static void AddJavaCandidate(List<string> candidates, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return;
        var path = Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'));
        if (Directory.Exists(path))
            path = Path.Combine(path, "bin", "javaw.exe");
        else if (string.Equals(Path.GetFileName(path), "java.exe", StringComparison.OrdinalIgnoreCase))
        {
            var javaw = Path.Combine(Path.GetDirectoryName(path)!, "javaw.exe");
            if (File.Exists(javaw))
                path = javaw;
        }
        candidates.Add(Path.GetFullPath(path));
    }

    public static string? FindBundledJavaPath()
    {
        var root = FindApplicationRoot();
        if (root is null)
            return null;
        var javaw = Path.Combine(root, "java", "bin", "javaw.exe");
        if (File.Exists(javaw))
            return Path.GetFullPath(javaw);
        var java = Path.Combine(root, "java", "bin", "java.exe");
        return File.Exists(java) ? Path.GetFullPath(java) : null;
    }

    public static string? FindApplicationRoot()
    {
        var searchRoots = new List<string>();
        AddAncestorRoots(searchRoots, AppContext.BaseDirectory);
        AddAncestorRoots(searchRoots, Environment.CurrentDirectory);
        foreach (var root in searchRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(Path.Combine(root, "java", "bin", "javaw.exe")) ||
                File.Exists(Path.Combine(root, "java", "bin", "java.exe")))
                return root;
        }
        return null;
    }

    private static void AddAncestorRoots(List<string> roots, string path)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        while (directory is not null)
        {
            roots.Add(directory.FullName);
            directory = directory.Parent;
        }
    }

    private static async Task<JsonObject> DownloadVersionMetadataAsync(string version, CancellationToken cancellationToken)
    {
        using var manifestResponse = await Http.GetAsync(ManifestUrl, cancellationToken);
        manifestResponse.EnsureSuccessStatusCode();
        using var manifest = JsonDocument.Parse(await manifestResponse.Content.ReadAsStringAsync(cancellationToken));
        var entry = manifest.RootElement.GetProperty("versions").EnumerateArray()
            .FirstOrDefault(item => item.GetProperty("id").GetString() == version);
        if (entry.ValueKind == JsonValueKind.Undefined ||
            !Uri.TryCreate(entry.GetProperty("url").GetString(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"Версия Minecraft {version} отсутствует в официальном манифесте.");
        using var response = await Http.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (entry.TryGetProperty("sha1", out var hashElement) &&
            !string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(bytes)),
                hashElement.GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Официальные метаданные Minecraft {version} не прошли проверку SHA-1.");
        return JsonNode.Parse(bytes)?.AsObject()
            ?? throw new InvalidDataException("Официальный манифест версии повреждён.");
    }

    private static string? GetBundledClientPath(string version)
    {
        var root = FindApplicationRoot() ?? AppContext.BaseDirectory;
        var path = Path.Combine(root, "versions", version, $"{version}.jar");
        return File.Exists(path) ? path : null;
    }

    private static async Task<int> GetJavaMajorVersionAsync(string javaPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(javaPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-version");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Не удалось проверить выбранную Java.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Проверка версии Java не завершилась за 15 секунд.");
        }
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Выбранный java.exe завершился с ошибкой при проверке версии.");
        var text = output + Environment.NewLine + error;
        var match = Regex.Match(text, @"(?:version\s+""|openjdk\s+|java\s+)(?<version>\d+(?:\.\d+)*)",
            RegexOptions.IgnoreCase);
        if (!match.Success)
            throw new InvalidDataException("Не удалось определить версию выбранного java.exe.");
        var parts = match.Groups["version"].Value.Split('.');
        if (!int.TryParse(parts[0], out var major))
            throw new InvalidDataException("Java вернула некорректный номер версии.");
        if (major == 1 && parts.Length > 1 && int.TryParse(parts[1], out var legacyMajor))
            return legacyMajor;
        return major;
    }

    private static async Task<string> GetLatestFabricVersionAsync(
        string minecraftVersion, CancellationToken cancellationToken)
    {
        var uri = new Uri($"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(minecraftVersion)}");
        using var response = await Http.GetAsync(uri, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException($"Fabric не поддерживает Minecraft {minecraftVersion}.");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var loader = document.RootElement.EnumerateArray()
            .FirstOrDefault(item => item.GetProperty("loader").TryGetProperty("stable", out var stable) && stable.GetBoolean());
        if (loader.ValueKind == JsonValueKind.Undefined)
            loader = document.RootElement.EnumerateArray().FirstOrDefault();
        if (loader.ValueKind == JsonValueKind.Undefined ||
            !loader.GetProperty("loader").TryGetProperty("version", out var version) ||
            string.IsNullOrWhiteSpace(version.GetString()))
            throw new InvalidDataException("Сервис Fabric не вернул совместимую версию загрузчика.");
        return version.GetString()!;
    }

    private async Task InstallMetadataFilesAsync(
        string gameDirectory, JsonObject metadata, DownloadOperation operation, CancellationToken cancellationToken,
        string? bundledClientPath = null)
    {
        var versionId = metadata["id"]?.GetValue<string>()
            ?? metadata["inheritsFrom"]?.GetValue<string>()
            ?? "unknown";
        var versionDirectory = Path.Combine(gameDirectory, "versions", versionId);
        Directory.CreateDirectory(versionDirectory);
        var versionFile = Path.Combine(versionDirectory, $"{versionId}.json");
        if (!File.Exists(versionFile))
            await File.WriteAllTextAsync(versionFile, metadata.ToJsonString(), cancellationToken);

        if (metadata["downloads"]?["client"] is JsonObject client &&
            client["url"]?.GetValue<string>() is { } clientUrl)
        {
            var clientVersion = metadata["inheritsFrom"]?.GetValue<string>() ?? versionId;
            var clientDirectory = Path.Combine(gameDirectory, "versions", clientVersion);
            Directory.CreateDirectory(clientDirectory);
            var clientPath = Path.Combine(clientDirectory, $"{clientVersion}.jar");
            if (bundledClientPath is not null)
            {
                var expectedSha1 = client["sha1"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(expectedSha1) ||
                    !await HasSha1Async(bundledClientPath, expectedSha1, cancellationToken))
                    throw new InvalidDataException(
                        $"Локальный клиент Minecraft {clientVersion} не совпадает с официальной контрольной суммой.");
                File.Copy(bundledClientPath, clientPath, true);
                SetStage(operation, $"Используется локальный клиент Minecraft {clientVersion}");
            }
            await DownloadFileAsync(new Uri(clientUrl), clientPath, client["sha1"]?.GetValue<string>(),
                operation, "Загрузка клиента Minecraft", cancellationToken,
                expectedSize: client["size"]?.GetValue<long>());
        }

        var libraries = metadata["libraries"]?.AsArray().ToArray() ?? [];
        await Parallel.ForEachAsync(libraries, new ParallelOptions
        {
            MaxDegreeOfParallelism = 8,
            CancellationToken = cancellationToken
        }, async (library, token) =>
        {
            if (library is not JsonObject libraryObject || !IsAllowed(libraryObject["rules"]))
                return;
            if (IsWindowsNativeClassifier(libraryObject) && GetWindowsNativeGroup(libraryObject) is null)
                return;
            if (GetArtifactReference(libraryObject) is { } artifact)
            {
                var target = SafeCombine(Path.Combine(gameDirectory, "libraries"), artifact.Path);
                await DownloadFileAsync(new Uri(artifact.Url), target, artifact.Sha1,
                    operation, "Загрузка библиотек Minecraft", token,
                    expectedSize: artifact.Size);
            }
            if (libraryObject["downloads"]?["classifiers"] is JsonObject &&
                libraryObject["natives"]?["windows"]?.GetValue<string>() is { } nativePattern)
            {
                var nativeKey = nativePattern.Replace("${arch}", Environment.Is64BitProcess ? "64" : "32",
                    StringComparison.Ordinal);
                if (GetArtifactReference(libraryObject, nativeKey) is { } native)
                {
                    var target = SafeCombine(Path.Combine(gameDirectory, "libraries"), native.Path);
                    await DownloadFileAsync(new Uri(native.Url), target, native.Sha1,
                        operation, "Загрузка нативных библиотек", token,
                        expectedSize: native.Size);
                }
            }
        });

        if (metadata["logging"]?["client"]?["file"] is JsonObject logConfig &&
            logConfig["id"]?.GetValue<string>() is { Length: > 0 } logConfigId &&
            logConfig["url"]?.GetValue<string>() is { Length: > 0 } logConfigUrl)
        {
            if (!Uri.TryCreate(logConfigUrl, UriKind.Absolute, out var logConfigUri))
                throw new InvalidDataException("В метаданных указана некорректная ссылка на конфигурацию логирования.");
            var logConfigPath = SafeCombine(
                Path.Combine(gameDirectory, "assets", "log_configs"), logConfigId);
            await DownloadFileAsync(logConfigUri, logConfigPath, logConfig["sha1"]?.GetValue<string>(),
                operation, "Загрузка конфигурации логирования", cancellationToken,
                expectedSize: logConfig["size"]?.GetValue<long>());
        }

        if (metadata["assetIndex"] is JsonObject assetIndex &&
            assetIndex["id"]?.GetValue<string>() is { } assetIndexId &&
            assetIndex["url"]?.GetValue<string>() is { } assetIndexUrl)
        {
            var indexPath = Path.Combine(gameDirectory, "assets", "indexes", $"{assetIndexId}.json");
            await DownloadFileAsync(new Uri(assetIndexUrl), indexPath, assetIndex["sha1"]?.GetValue<string>(),
                operation, "Загрузка индекса ресурсов", cancellationToken,
                expectedSize: assetIndex["size"]?.GetValue<long>());
            var index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath, cancellationToken))?["objects"]?.AsObject()
                ?? throw new InvalidDataException("Индекс игровых ресурсов повреждён.");
            var objects = index.Select(item => item.Value?["hash"]?.GetValue<string>())
                .Where(hash => hash is { Length: 40 }).Distinct(StringComparer.Ordinal).ToArray();
            var objectSizes = index
                .Where(item => item.Value?["hash"]?.GetValue<string>() is { Length: 40 } &&
                    item.Value["size"]?.GetValue<long>() is >= 0)
                .GroupBy(item => item.Value!["hash"]!.GetValue<string>(), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Value!["size"]!.GetValue<long>(),
                    StringComparer.Ordinal);
            SetStage(operation, $"Загрузка ресурсов Minecraft ({objects.Length} файлов)");
            long totalReceived = 0;
            var completed = 0;
            await Parallel.ForEachAsync(objects, new ParallelOptions
            {
                MaxDegreeOfParallelism = 8,
                CancellationToken = cancellationToken
            }, async (hash, token) =>
            {
                var resourcePath = Path.Combine(gameDirectory, "assets", "objects", hash![..2], hash);
                var uri = new Uri($"https://resources.download.minecraft.net/{hash[..2]}/{hash}");
                var transferred = await DownloadFileAsync(uri, resourcePath, hash, operation,
                    "Загрузка ресурсов", token, reportProgress: false,
                    expectedSize: objectSizes.TryGetValue(hash, out var expectedSize) ? expectedSize : null);
                var received = Interlocked.Add(ref totalReceived, transferred);
                var finished = Interlocked.Increment(ref completed);
                await SetTransferAsync(operation, received, null, null);
                SetStage(operation, $"Загрузка ресурсов: обработано {finished} из {objects.Length} файлов");
            });

            var objectMap = index.ToDictionary(
                item => item.Key,
                item => item.Value?["hash"]?.GetValue<string>(),
                StringComparer.Ordinal);
            if (assetIndex["virtual"]?.GetValue<bool>() == true)
            {
                foreach (var (assetName, hash) in objectMap)
                {
                    if (hash is not { Length: 40 })
                        continue;
                    var source = Path.Combine(gameDirectory, "assets", "objects", hash[..2], hash);
                    var target = SafeCombine(Path.Combine(gameDirectory, "assets", "virtual", assetIndexId), assetName);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (!File.Exists(target))
                        File.Copy(source, target);
                }
            }
            if (assetIndex["map_to_resources"]?.GetValue<bool>() == true)
            {
                foreach (var (assetName, hash) in objectMap)
                {
                    if (hash is not { Length: 40 })
                        continue;
                    var source = Path.Combine(gameDirectory, "assets", "objects", hash[..2], hash);
                    var target = SafeCombine(Path.Combine(gameDirectory, "resources"), assetName);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (!File.Exists(target))
                        File.Copy(source, target);
                }
            }
        }
    }

    private async Task<JsonObject> ReadEffectiveMetadataAsync(GameInstance instance, CancellationToken cancellationToken)
    {
        if (string.Equals(instance.Loader, "Fabric", StringComparison.OrdinalIgnoreCase))
        {
            var fabricPath = Path.Combine(GetGameDirectory(instance), "versions",
                instance.Settings.FabricProfileId, $"{instance.Settings.FabricProfileId}.json");
            if (!File.Exists(fabricPath))
                throw new FileNotFoundException("Профиль Fabric отсутствует. Переустановите Fabric в этом экземпляре.");
            var fabric = JsonNode.Parse(await File.ReadAllTextAsync(fabricPath, cancellationToken))?.AsObject()
                ?? throw new InvalidDataException("Профиль Fabric повреждён.");
            var parentId = fabric["inheritsFrom"]?.GetValue<string>()
                ?? throw new InvalidDataException("Профиль Fabric не ссылается на версию Minecraft.");
            var parentPath = Path.Combine(GetGameDirectory(instance), "versions", parentId, $"{parentId}.json");
            JsonObject parent;
            if (File.Exists(parentPath))
                parent = JsonNode.Parse(await File.ReadAllTextAsync(parentPath, cancellationToken))?.AsObject()
                    ?? throw new InvalidDataException("Родительские метаданные Minecraft повреждены.");
            else
            {
                parent = await DownloadVersionMetadataAsync(parentId, cancellationToken);
                Directory.CreateDirectory(Path.GetDirectoryName(parentPath)!);
                await File.WriteAllTextAsync(parentPath, parent.ToJsonString(), cancellationToken);
            }
            return MergeMetadata(parent, fabric);
        }

        var path = Path.Combine(GetGameDirectory(instance), "versions", instance.MinecraftVersion,
            $"{instance.MinecraftVersion}.json");
        if (File.Exists(path))
            return JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken))?.AsObject()
                ?? throw new InvalidDataException("Метаданные версии Minecraft повреждены.");
        return await DownloadVersionMetadataAsync(instance.MinecraftVersion, cancellationToken);
    }

    private static JsonObject MergeMetadata(JsonObject parent, JsonObject child)
    {
        var merged = (JsonObject)parent.DeepClone();
        foreach (var property in new[] { "id", "mainClass", "type", "assetIndex", "assets", "javaVersion", "downloads" })
        {
            if (child[property] is { } value)
                merged[property] = value.DeepClone();
        }
        if (child["libraries"] is JsonArray childLibraries)
        {
            var libraries = merged["libraries"] as JsonArray ?? [];
            foreach (var item in childLibraries)
                libraries.Add(item?.DeepClone());
            merged["libraries"] = libraries;
        }
        if (child["inheritsFrom"] is { } parentVersion)
            merged["inheritsFrom"] = parentVersion.DeepClone();
        if (child["arguments"] is JsonObject childArguments)
        {
            var arguments = merged["arguments"] as JsonObject ?? [];
            foreach (var argumentType in new[] { "jvm", "game" })
            {
                if (childArguments[argumentType] is not JsonArray childArgs)
                    continue;
                var combined = arguments[argumentType] as JsonArray ?? [];
                foreach (var argument in childArgs)
                    combined.Add(argument?.DeepClone());
                arguments[argumentType] = combined;
            }
            merged["arguments"] = arguments;
        }
        if (child["minecraftArguments"] is { } legacyArguments)
            merged["minecraftArguments"] = legacyArguments.DeepClone();
        return merged;
    }

    internal static async Task<string> BuildClasspathAsync(
        JsonObject metadata, string gameDirectory, string nativesDirectory,
        DownloadOperation operation, CancellationToken cancellationToken)
    {
        var classpath = new List<string>();
        foreach (var library in metadata["libraries"]?.AsArray() ?? [])
        {
            if (library is not JsonObject item || !IsAllowed(item["rules"]))
                continue;
            if (IsWindowsNativeClassifier(item) && GetWindowsNativeGroup(item) is null)
                continue;
            if (GetArtifactReference(item) is { } artifact)
            {
                var artifactPath = SafeCombine(Path.Combine(gameDirectory, "libraries"), artifact.Path);
                if (!File.Exists(artifactPath))
                    throw new FileNotFoundException(
                        $"Не найдена обязательная библиотека Minecraft: {artifact.Path}", artifactPath);
                if (GetWindowsNativeGroup(item) is { } nativeGroup)
                {
                    var nativeTarget = Path.Combine(nativesDirectory, nativeGroup);
                    await ExtractNativesAsync(
                        artifactPath, nativeTarget, item["extract"]?["exclude"]?.AsArray(), cancellationToken);
                }
                classpath.Add(artifactPath);
            }
            if (item["natives"]?["windows"]?.GetValue<string>() is not { } nativePattern)
                continue;
            var nativeKey = nativePattern.Replace("${arch}", Environment.Is64BitProcess ? "64" : "32",
                StringComparison.Ordinal);
            if (GetArtifactReference(item, nativeKey) is not { } native)
                continue;
            var nativeJar = SafeCombine(Path.Combine(gameDirectory, "libraries"), native.Path);
            if (!File.Exists(nativeJar))
                throw new FileNotFoundException(
                    $"Не найдена обязательная нативная библиотека Minecraft: {native.Path}", nativeJar);
            await ExtractNativesAsync(nativeJar, nativesDirectory, item["extract"]?["exclude"]?.AsArray(),
                cancellationToken);
        }
        var clientVersion = metadata["inheritsFrom"]?.GetValue<string>()
            ?? metadata["id"]?.GetValue<string>()
            ?? "unknown";
        var clientJar = Path.Combine(gameDirectory, "versions", clientVersion, $"{clientVersion}.jar");
        if (!File.Exists(clientJar))
            throw new FileNotFoundException(
                $"Не найден клиентский JAR Minecraft {clientVersion}. Переустановите эту версию.", clientJar);
        classpath.Add(clientJar);
        if (classpath.Count == 0)
            throw new InvalidDataException("Не найдены библиотеки для запуска Minecraft.");
        return string.Join(Path.PathSeparator, classpath.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    internal static string? GetWindowsNativeGroup(JsonObject library)
    {
        var coordinate = library["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(coordinate))
            return null;
        var parts = coordinate.Split(':');
        if (parts.Length < 4)
            return null;
        var classifier = parts[^1];
        var architecture = RuntimeInformation.ProcessArchitecture;
        if (classifier.Equals("natives-windows", StringComparison.OrdinalIgnoreCase))
        {
            if (architecture is not Architecture.X64 and not Architecture.X86)
                return null;
        }
        else if (classifier.Equals("natives-windows-arm64", StringComparison.OrdinalIgnoreCase))
        {
            if (architecture != Architecture.Arm64)
                return null;
        }
        else if (classifier.Equals("natives-windows-x86", StringComparison.OrdinalIgnoreCase))
        {
            if (architecture != Architecture.X86)
                return null;
        }
        else
        {
            return null;
        }
        return parts[0] switch
        {
            "org.lwjgl" => "lwjgl",
            "net.java.dev.jna" => "jna",
            "io.netty" => "netty",
            _ => "java"
        };
    }

    private static bool IsWindowsNativeClassifier(JsonObject library) =>
        library["name"]?.GetValue<string>()?.Split(':') is { Length: >= 4 } parts &&
        parts[^1].StartsWith("natives-windows", StringComparison.OrdinalIgnoreCase);

    private static string? GetLoggingConfigPath(JsonObject metadata, string gameDirectory)
    {
        var id = metadata["logging"]?["client"]?["file"]?["id"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(id)
            ? null
            : SafeCombine(Path.Combine(gameDirectory, "assets", "log_configs"), id);
    }

    private static ArtifactReference? GetArtifactReference(JsonObject library, string? classifier = null)
    {
        if (classifier is null && library["downloads"]?["artifact"] is JsonObject artifact &&
            artifact["path"]?.GetValue<string>() is { } artifactPath &&
            artifact["url"]?.GetValue<string>() is { } artifactUrl)
            return new ArtifactReference(
                artifactPath, artifactUrl, artifact["sha1"]?.GetValue<string>(),
                artifact["size"]?.GetValue<long>());

        if (classifier is not null && library["downloads"]?["classifiers"] is JsonObject classifiers &&
            classifiers[classifier] is JsonObject classified &&
            classified["path"]?.GetValue<string>() is { } classifiedPath &&
            classified["url"]?.GetValue<string>() is { } classifiedUrl)
            return new ArtifactReference(
                classifiedPath, classifiedUrl, classified["sha1"]?.GetValue<string>(),
                classified["size"]?.GetValue<long>());

        var coordinate = library["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(coordinate))
            return null;
        var parts = coordinate.Split(':');
        if (parts.Length < 3)
            throw new InvalidDataException($"Некорректная Maven-координата библиотеки «{coordinate}».");
        var extensionParts = parts[2].Split('@', 2);
        var extension = extensionParts.Length == 2 ? extensionParts[1] : "jar";
        var requestedClassifier = classifier ?? (parts.Length > 3 ? parts[3] : null);
        var filename = $"{parts[1]}-{extensionParts[0]}" +
            (string.IsNullOrWhiteSpace(requestedClassifier) ? string.Empty : $"-{requestedClassifier}") +
            $".{extension}";
        var relative = $"{parts[0].Replace('.', '/')}/{parts[1]}/{extensionParts[0]}/{filename}";
        var baseUrl = library["url"]?.GetValue<string>() ?? "https://libraries.minecraft.net/";
        var url = $"{baseUrl.TrimEnd('/')}/{relative}";
        return new ArtifactReference(relative, url, null, null);
    }

    internal static async Task ExtractNativesAsync(
        string archivePath, string destination, JsonArray? excludedPaths, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        await using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        var excluded = excludedPaths?.Select(node => node?.GetValue<string>())
            .OfType<string>().ToArray() ?? [];
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name) ||
                entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) ||
                excluded.Any(prefix => entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                continue;
            var target = SafeCombine(destination, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var source = entry.Open();
            await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(output, cancellationToken);
        }
    }

    private Task<long> DownloadFileAsync(
        Uri uri, string target, string? expectedSha1, DownloadOperation operation,
        string stage, CancellationToken cancellationToken, bool reportProgress = true, long? expectedSize = null)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Загрузка отменена: источник не использует HTTPS.");
        return FileOperationCoordinator.DownloadAsync(
            target, expectedSha1, expectedSize, cancellationToken,
            async (temporary, token) =>
        {
            if (reportProgress)
                SetStage(operation, $"{stage}: {Path.GetFileName(target)}");
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new HttpRequestException($"Не удалось скачать файл (HTTP {(int)response.StatusCode}) из доверенного манифеста.");
            var total = response.Content.Headers.ContentLength;
            if (reportProgress)
                await SetTransferAsync(operation, 0, total, null);
            await using var source = await response.Content.ReadAsStreamAsync(token);
            await using var destination = await OpenDownloadDestinationAsync(temporary, token);
            var buffer = new byte[81920];
            long downloaded = 0;
            long lastReported = 0;
            var lastUpdate = Stopwatch.StartNew();
            while (true)
            {
                var count = await source.ReadAsync(buffer, token);
                if (count == 0)
                    break;
                await destination.WriteAsync(buffer.AsMemory(0, count), token);
                downloaded += count;
                if (reportProgress && (lastUpdate.ElapsedMilliseconds >= 150 ||
                    (total.HasValue && downloaded == total.Value)))
                {
                    var speed = (downloaded - lastReported) / Math.Max(lastUpdate.Elapsed.TotalSeconds, 0.001);
                    await SetTransferAsync(operation, downloaded, total, speed);
                    lastReported = downloaded;
                    lastUpdate.Restart();
                }
            }
            if (reportProgress && downloaded != lastReported)
            {
                var speed = (downloaded - lastReported) / Math.Max(lastUpdate.Elapsed.TotalSeconds, 0.001);
                await SetTransferAsync(operation, downloaded, total, speed);
            }
            await destination.FlushAsync(token);
            destination.Flush(flushToDisk: true);
            return downloaded;
        });
    }

    private static async Task<FileStream> OpenDownloadDestinationAsync(
        string path, CancellationToken cancellationToken)
    {
        const int maximumAttempts = 25;
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            }
            catch (IOException exception) when (IsFileSharingViolation(exception) && attempt < maximumAttempts - 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            }
        }
    }

    private static bool IsFileSharingViolation(IOException exception) =>
        (exception.HResult & 0xFFFF) is 32 or 33;

    private static async Task<bool> HasSha1Async(string path, string expected, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        var actual = Convert.ToHexString(await System.Security.Cryptography.SHA1.HashDataAsync(stream, cancellationToken));
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllowed(JsonNode? rules, IReadOnlyDictionary<string, bool>? features = null)
    {
        if (rules is not JsonArray ruleArray || ruleArray.Count == 0)
            return true;
        var allowed = false;
        foreach (var rule in ruleArray.OfType<JsonObject>())
        {
            var os = rule["os"] as JsonObject;
            var osName = os?["name"]?.GetValue<string>();
            if (osName is not null && osName != "windows")
                continue;
            var arch = os?["arch"]?.GetValue<string>();
            if (arch is not null && !string.Equals(
                RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.X64 => "x86_64",
                    Architecture.X86 => "x86",
                    Architecture.Arm64 => "arm64",
                    Architecture.Arm => "arm32",
                    _ => RuntimeInformation.ProcessArchitecture.ToString()
                }, arch, StringComparison.OrdinalIgnoreCase))
                continue;
            var osVersion = os?["version"]?.GetValue<string>();
            if (osVersion is not null && !Regex.IsMatch(Environment.OSVersion.Version.ToString(), osVersion))
                continue;
            if (rule["features"] is JsonObject requiredFeatures &&
                requiredFeatures.Any(feature =>
                    features is null ||
                    !features.TryGetValue(feature.Key, out var actual) ||
                    actual != feature.Value?.GetValue<bool>()))
                continue;
            allowed = rule["action"]?.GetValue<string>() == "allow";
        }
        return allowed;
    }

    private static IEnumerable<string> GetArguments(
        JsonObject metadata, string type, IReadOnlyDictionary<string, bool>? features = null)
    {
        if (metadata["arguments"]?[type] is not JsonArray values)
            return [];
        var result = new List<string>();
        foreach (var value in values)
        {
            if (value is JsonValue scalar && scalar.TryGetValue<string>(out var argument))
                result.Add(argument);
            else if (value is JsonObject conditional && IsAllowed(conditional["rules"], features))
            {
                if (conditional["value"] is JsonValue conditionalValue &&
                    conditionalValue.TryGetValue<string>(out var single))
                    result.Add(single);
                else if (conditional["value"] is JsonArray argumentArray)
                    result.AddRange(argumentArray.Select(node => node?.GetValue<string>()).OfType<string>());
            }
        }
        return result;
    }

    private static IEnumerable<string> GetGameArguments(
        JsonObject metadata, IReadOnlyDictionary<string, bool> features)
    {
        var arguments = GetArguments(metadata, "game", features).ToList();
        if (arguments.Count > 0)
            return arguments;
        var legacy = metadata["minecraftArguments"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(legacy) ? [] : SplitArguments(legacy);
    }

    private static string Substitute(string argument, IReadOnlyDictionary<string, string> values)
    {
        foreach (var (key, value) in values)
            argument = argument.Replace(key, value, StringComparison.Ordinal);
        if (Regex.IsMatch(argument, @"\$\{[^}]+\}"))
            throw new InvalidDataException($"Не удалось подставить переменную аргумента Minecraft: {argument}");
        return argument;
    }

    private static IReadOnlyList<string> SplitArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return [];
        var matches = Regex.Matches(arguments, "(?:[^\\s\"']+|\"[^\"]*\"|'[^']*')");
        return matches.Select(match => match.Value.Trim('"', '\'')).ToArray();
    }

    private static string SafeCombine(string root, string relative)
    {
        var normalizedRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Путь файла выходит за пределы каталога экземпляра.");
        return target;
    }

    private void SaveLaunchResult(
        GameInstance instance, DateTimeOffset startedAt, int? exitCode, string javaPath)
    {
        try
        {
            var diagnostics = Path.Combine(instance.RootPath, "diagnostics");
            Directory.CreateDirectory(diagnostics);
            var result = new LaunchResult(startedAt, exitCode, instance.MinecraftVersion, instance.Loader, javaPath);
            File.WriteAllText(Path.Combine(diagnostics, "latest-launch.json"), JsonSerializer.Serialize(result,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            DiagnosticWriteFailed?.Invoke(
                $"Не удалось сохранить результат запуска для экземпляра «{instance.Name}»: {exception.Message}");
        }
    }

    private static async Task SetTransferAsync(
        DownloadOperation operation, long received, long? total, double? bytesPerSecond)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            operation.BytesReceived = received;
            operation.TotalBytes = total;
            operation.BytesPerSecond = bytesPerSecond;
            return;
        }
        await dispatcher.InvokeAsync(() =>
        {
            operation.BytesReceived = received;
            operation.TotalBytes = total;
            operation.BytesPerSecond = bytesPerSecond;
        });
    }

    private static void SetStage(DownloadOperation operation, string stage)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            operation.Stage = stage;
        else
            dispatcher.Invoke(() => operation.Stage = stage);
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[^length..];

    private sealed record ArtifactReference(string Path, string Url, string? Sha1, long? Size);
}
