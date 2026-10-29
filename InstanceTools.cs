using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace MaruLauncher;

public sealed record BackupSelection(
    bool Mods, bool Config, bool ResourcePacks, bool Screenshots, bool Worlds, bool Settings);

public sealed record BackupContents(
    string SourceInstanceId, string InstanceName, string MinecraftVersion, string Loader,
    IReadOnlyList<string> Files, IReadOnlyList<string> ExistingFiles, InstanceSettings? Settings,
    IReadOnlyList<string> RedactedFiles, IReadOnlyList<string> OmittedTextFiles);

public static class InstanceTools
{
    private static readonly string[] BackupFolders =
        ["mods", "config", "resourcepacks", "screenshots", "saves"];
    private const string BackupManifest = "marulauncher-backup.json";

    public static IReadOnlyList<BuildIssue> CheckBuild(GameInstance instance)
    {
        var issues = new List<BuildIssue>();
        var gameDirectory = MinecraftService.GetGameDirectory(instance);
        CheckGameFiles(instance, gameDirectory, issues);
        var modsDirectory = Path.Combine(gameDirectory, "mods");
        if (!Directory.Exists(modsDirectory))
        {
            issues.Add(new BuildIssue(IssueSeverity.Information, "Папка модов ещё не создана",
                "В этом экземпляре пока нет каталога mods.", "Запустите Minecraft или добавьте мод JAR."));
            return issues;
        }

        var jarFiles = Directory.GetFiles(modsDirectory, "*.jar", SearchOption.TopDirectoryOnly);
        if (Directory.GetDirectories(modsDirectory).Length > 0)
            issues.Add(new BuildIssue(IssueSeverity.Warning, "В mods есть вложенные папки",
                "Загрузчики обычно ищут моды только в корне каталога mods.", "Переместите JAR-файлы модов прямо в папку mods."));

        var modIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var foundFabric = false;
        var foundForge = false;
        var loadedMods = new List<(string File, string Id, Dictionary<string, string> Dependencies)>();
        foreach (var jarPath in jarFiles)
        {
            try
            {
                using var archive = ZipFile.OpenRead(jarPath);
                var fabricEntry = archive.GetEntry("fabric.mod.json");
                var forgeEntry = archive.GetEntry("META-INF/mods.toml");
                foundFabric |= fabricEntry is not null;
                foundForge |= forgeEntry is not null;
                if (fabricEntry is not null)
                {
                    using var stream = fabricEntry.Open();
                    using var metadata = JsonDocument.Parse(stream);
                    var root = metadata.RootElement;
                    var id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        issues.Add(new BuildIssue(IssueSeverity.Error, $"Некорректный мод: {Path.GetFileName(jarPath)}",
                            "В fabric.mod.json отсутствует обязательный идентификатор id.",
                            "Проверьте источник JAR и используйте сборку, совместимую с выбранной версией Minecraft."));
                        continue;
                    }
                    var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (root.TryGetProperty("depends", out var depends) && depends.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var dependency in depends.EnumerateObject())
                        {
                            if (dependency.Name is "minecraft" or "java" or "fabricloader")
                                continue;
                            dependencies[dependency.Name] = dependency.Value.ToString();
                        }
                        if (depends.TryGetProperty("minecraft", out var mcVersion) &&
                            Version.TryParse(mcVersion.GetString(), out _) &&
                            !string.Equals(mcVersion.GetString(), instance.MinecraftVersion, StringComparison.OrdinalIgnoreCase))
                        {
                            issues.Add(new BuildIssue(IssueSeverity.Warning, $"Возможна несовместимость: {Path.GetFileName(jarPath)}",
                                $"Метаданные мода требуют Minecraft {mcVersion.GetString()}, экземпляр использует {instance.MinecraftVersion}.",
                                "Проверьте страницу мода и установите версию, совместимую с этим экземпляром."));
                        }
                    }
                    if (modIds.TryGetValue(id, out var duplicate))
                        issues.Add(new BuildIssue(IssueSeverity.Error, $"Дублирующийся мод: {id}",
                            $"Мод найден в файлах {Path.GetFileName(duplicate)} и {Path.GetFileName(jarPath)}.",
                            "Оставьте одну совместимую версию мода; файлы автоматически не удалялись."));
                    else
                        modIds[id] = jarPath;
                    loadedMods.Add((jarPath, id, dependencies));
                }
                else if (forgeEntry is not null)
                {
                    var forgeText = ReadEntryText(forgeEntry);
                    foreach (Match match in Regex.Matches(forgeText, @"(?m)^\s*modId\s*=\s*""([^""]+)"""))
                    {
                        var id = match.Groups[1].Value;
                        if (modIds.TryGetValue(id, out var duplicate))
                            issues.Add(new BuildIssue(IssueSeverity.Error, $"Дублирующийся мод: {id}",
                                $"Мод найден в файлах {Path.GetFileName(duplicate)} и {Path.GetFileName(jarPath)}.",
                                "Оставьте одну совместимую версию мода; файлы автоматически не удалялись."));
                        else
                            modIds[id] = jarPath;
                    }
                }
                else
                {
                    issues.Add(new BuildIssue(IssueSeverity.Information, $"Метаданные не распознаны: {Path.GetFileName(jarPath)}",
                        "Файл является ZIP/JAR, но ожидаемые метаданные Fabric или Forge не найдены.",
                        "Убедитесь, что это мод, а не библиотека или файл для другого загрузчика."));
                }
            }
            catch (InvalidDataException)
            {
                issues.Add(new BuildIssue(IssueSeverity.Error, $"Повреждённый архив: {Path.GetFileName(jarPath)}",
                    "JAR-файл не удалось прочитать как ZIP-архив.", "Загрузите файл заново с доверенного источника."));
            }
            catch (JsonException)
            {
                issues.Add(new BuildIssue(IssueSeverity.Error, $"Повреждены метаданные: {Path.GetFileName(jarPath)}",
                    "Файл fabric.mod.json содержит некорректный JSON.", "Проверьте целостность JAR и совместимость версии."));
            }
            catch (IOException exception)
            {
                issues.Add(new BuildIssue(IssueSeverity.Error, $"Не удалось проверить: {Path.GetFileName(jarPath)}",
                    exception.Message, "Проверьте доступ к файлу и закройте программы, которые его используют."));
            }
        }

        if (foundFabric && !string.Equals(instance.Loader, "Fabric", StringComparison.OrdinalIgnoreCase))
            issues.Add(new BuildIssue(IssueSeverity.Error, "Загрузчик экземпляра не совпадает с модами",
                "В каталоге найдены Fabric-моды, но для экземпляра Fabric не выбран.",
                "Установите Fabric в экземпляр либо используйте отдельную сборку с подходящим загрузчиком."));
        if (foundForge && !string.Equals(instance.Loader, "Forge", StringComparison.OrdinalIgnoreCase))
            issues.Add(new BuildIssue(IssueSeverity.Error, "Найдены моды для Forge",
                "В каталоге обнаружены метаданные Forge, но экземпляр использует другой загрузчик.",
                "Создайте экземпляр с совместимым Forge; автоматическая замена загрузчика не выполняется."));
        if (string.Equals(instance.Loader, "Fabric", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(instance.Settings.FabricProfileId))
            issues.Add(new BuildIssue(IssueSeverity.Error, "Профиль Fabric не установлен",
                "Для экземпляра не сохранён идентификатор установленного профиля Fabric.",
                "Установите Fabric заново в настройках экземпляра."));

        foreach (var mod in loadedMods)
        {
            foreach (var dependency in mod.Dependencies)
            {
                if (!modIds.ContainsKey(dependency.Key))
                    issues.Add(new BuildIssue(IssueSeverity.Warning, $"Не найдена зависимость {dependency.Key}",
                        $"Мод {Path.GetFileName(mod.File)} указывает зависимость «{dependency.Key}» ({dependency.Value}), которой нет в папке mods.",
                        "Установите указанную зависимость с официального сайта проекта или отключите зависимый мод."));
            }
        }

        if (jarFiles.Length == 0)
            issues.Add(new BuildIssue(IssueSeverity.Information, "Моды не установлены",
                "Каталог mods пуст.", "Добавьте моды, совместимые с версией игры и загрузчиком."));
        if (issues.Count == 0)
            issues.Add(new BuildIssue(IssueSeverity.Information, "Проверка завершена",
                $"Проверено JAR-файлов: {jarFiles.Length}. Очевидных проблем структуры и зависимостей не найдено.",
                "Результат не гарантирует, что моды совместимы между собой во время игры."));
        return issues;
    }

    public static void CreateBackup(
        GameInstance instance, string destination, BackupSelection selection, CancellationToken cancellationToken)
    {
        if (!selection.Mods && !selection.Config && !selection.ResourcePacks &&
            !selection.Screenshots && !selection.Worlds && !selection.Settings)
            throw new InvalidOperationException("Выберите хотя бы одну категорию данных для резервной копии.");
        var selectedFolders = new List<string>();
        if (selection.Mods) selectedFolders.Add("mods");
        if (selection.Config) selectedFolders.Add("config");
        if (selection.ResourcePacks) selectedFolders.Add("resourcepacks");
        if (selection.Screenshots) selectedFolders.Add("screenshots");
        if (selection.Worlds) selectedFolders.Add("saves");

        var temporary = destination + ".tmp";
        var redactedFiles = new List<string>();
        var omittedTextFiles = new List<string>();
        try
        {
            using var archive = ZipFile.Open(temporary, ZipArchiveMode.Create);
            foreach (var folder in selectedFolders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = Path.Combine(MinecraftService.GetGameDirectory(instance), folder);
                if (!Directory.Exists(source))
                    continue;
                foreach (var file in SafeFileTree.EnumerateRegularFiles(source))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(MinecraftService.GetGameDirectory(instance), file);
                    byte[]? sanitizedContent = null;
                    if (SecureArchiveContent.IsTextCandidate(file))
                    {
                        var sanitized = SecureArchiveContent.SanitizeTextFile(file);
                        if (!sanitized.IsSafeToInclude)
                        {
                            omittedTextFiles.Add(relative.Replace(Path.DirectorySeparatorChar, '/'));
                            continue;
                        }
                        sanitizedContent = sanitized.Content;
                        if (sanitized.Redacted)
                            redactedFiles.Add(relative.Replace(Path.DirectorySeparatorChar, '/'));
                    }
                    var entry = archive.CreateEntry(relative.Replace(Path.DirectorySeparatorChar, '/'),
                        CompressionLevel.Optimal);
                    using var output = entry.Open();
                    if (sanitizedContent is not null)
                        output.Write(sanitizedContent);
                    else
                    {
                        using var input = File.OpenRead(file);
                        input.CopyTo(output);
                    }
                }
            }
            var manifest = new BackupManifestData
            {
                InstanceId = instance.Id,
                InstanceName = instance.Name,
                MinecraftVersion = instance.MinecraftVersion,
                Loader = instance.Loader,
                Settings = selection.Settings ? CreateBackupSettings(instance.Settings) : null,
                RedactedFiles = redactedFiles,
                OmittedTextFiles = omittedTextFiles
            };
            var manifestEntry = archive.CreateEntry(BackupManifest);
            using var manifestStream = manifestEntry.Open();
            JsonSerializer.Serialize(manifestStream, manifest, new JsonSerializerOptions { WriteIndented = true });
            manifestStream.Flush();
            archive.Dispose();
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public static BackupContents InspectBackup(string archivePath, GameInstance target)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 100_000)
            throw new InvalidDataException("Архив содержит слишком много файлов.");
        long expandedSize = 0;
        var files = new List<string>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        BackupManifestData? manifest = null;
        foreach (var entry in archive.Entries)
        {
            ValidateArchivePath(entry.FullName);
            expandedSize = checked(expandedSize + entry.Length);
            if (expandedSize > 50L * 1024 * 1024 * 1024)
                throw new InvalidDataException("Размер распакованных данных превышает допустимый предел 50 ГБ.");
            if (entry.FullName == BackupManifest)
            {
                if (manifest is not null)
                    throw new InvalidDataException("Архив содержит несколько манифестов резервной копии.");
                using var stream = entry.Open();
                manifest = JsonSerializer.Deserialize<BackupManifestData>(stream)
                    ?? throw new InvalidDataException("Манифест резервной копии пуст или повреждён.");
                continue;
            }

            if (entry.FullName.EndsWith('/'))
                continue;
            if (!seenFiles.Add(entry.FullName))
                throw new InvalidDataException($"Архив содержит повторяющийся файл «{entry.FullName}».");
            var folder = entry.FullName.Split('/')[0];
            if (!BackupFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException($"Архив содержит неподдерживаемый путь «{entry.FullName}».");
            files.Add(entry.FullName);
        }
        if (files.Count == 0 && manifest is null)
            throw new InvalidDataException("Архив не содержит данных резервной копии MaruLauncher.");
        var gameDirectory = MinecraftService.GetGameDirectory(target);
        var overwrite = files
            .Where(relative => File.Exists(Path.Combine(gameDirectory, relative.Replace('/', Path.DirectorySeparatorChar))))
            .ToArray();
        return new BackupContents(
            manifest?.InstanceId ?? string.Empty,
            manifest?.InstanceName ?? "Неизвестный экземпляр",
            manifest?.MinecraftVersion ?? "не указана",
            manifest?.Loader ?? "не указан",
            files,
            overwrite,
            manifest?.Settings,
            manifest?.RedactedFiles ?? [],
            manifest?.OmittedTextFiles ?? []);
    }

    public static InstanceSettings? RestoreBackup(
        string archivePath, GameInstance target, bool overwriteExisting, CancellationToken cancellationToken)
    {
        var contents = InspectBackup(archivePath, target);
        if (contents.ExistingFiles.Count > 0 && !overwriteExisting)
            throw new InvalidOperationException("Восстановление отменено: в целевом экземпляре уже есть файлы с такими именами.");

        var staging = Path.Combine(AppPaths.Root, "restore-staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ValidateArchivePath(entry.FullName);
                    if (entry.FullName.EndsWith('/') || entry.FullName == BackupManifest)
                        continue;
                    var stagedPath = SafePath(staging, entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
                    using var source = entry.Open();
                    using var destination = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    source.CopyTo(destination);
                }
            }

            var gameDirectory = MinecraftService.GetGameDirectory(target);
            foreach (var relative in contents.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var staged = SafePath(staging, relative);
                var destination = SafePath(gameDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(staged, destination, overwriteExisting);
            }
            if (contents.Settings is not null)
                return contents.Settings;
            return null;
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, true);
        }
    }

    private static void ValidateArchivePath(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry) || Path.IsPathRooted(entry) ||
            entry.Contains(':') || entry.Contains('\\') ||
            entry.Split('/').Any(part => part is ".." or "."))
            throw new InvalidDataException("Архив содержит небезопасный путь.");
    }

    private static void CheckGameFiles(GameInstance instance, string gameDirectory, List<BuildIssue> issues)
    {
        var baseVersionDirectory = Path.Combine(gameDirectory, "versions", instance.MinecraftVersion);
        var baseMetadataPath = Path.Combine(baseVersionDirectory, $"{instance.MinecraftVersion}.json");
        if (!File.Exists(baseMetadataPath))
        {
            issues.Add(new BuildIssue(IssueSeverity.Error, "Файлы Minecraft не установлены",
                $"Не найден манифест версии {instance.MinecraftVersion}.",
                "Установите или запустите экземпляр, чтобы загрузить официальные файлы Minecraft."));
            return;
        }

        JsonDocument baseMetadata;
        try
        {
            baseMetadata = JsonDocument.Parse(File.ReadAllText(baseMetadataPath));
        }
        catch (JsonException)
        {
            issues.Add(new BuildIssue(IssueSeverity.Error, "Повреждены метаданные Minecraft",
                $"Файл {baseMetadataPath} содержит некорректный JSON.",
                "Повторно установите версию Minecraft из официального манифеста."));
            return;
        }
        using (baseMetadata)
        {
            CheckVersionArtifacts(baseMetadata.RootElement, instance.MinecraftVersion, gameDirectory, issues);
            CheckAssetFiles(baseMetadata.RootElement, gameDirectory, issues);
        }

        if (!string.Equals(instance.Loader, "Fabric", StringComparison.OrdinalIgnoreCase))
            return;
        var fabricId = instance.Settings.FabricProfileId;
        var fabricPath = Path.Combine(gameDirectory, "versions", fabricId, $"{fabricId}.json");
        if (string.IsNullOrWhiteSpace(fabricId) || !File.Exists(fabricPath))
        {
            issues.Add(new BuildIssue(IssueSeverity.Error, "Профиль Fabric отсутствует",
                "Не найден JSON установленного загрузчика Fabric.",
                "Установите Fabric в настройки этого экземпляра."));
            return;
        }
        try
        {
            using var fabric = JsonDocument.Parse(File.ReadAllText(fabricPath));
            CheckVersionArtifacts(fabric.RootElement, fabricId, gameDirectory, issues);
        }
        catch (JsonException)
        {
            issues.Add(new BuildIssue(IssueSeverity.Error, "Повреждён профиль Fabric",
                "JSON профиля Fabric не удалось прочитать.",
                "Переустановите Fabric из официального сервиса Fabric."));
        }
    }

    private static void CheckVersionArtifacts(
        JsonElement metadata, string versionId, string gameDirectory, List<BuildIssue> issues)
    {
        if (metadata.TryGetProperty("downloads", out var downloads) &&
            downloads.TryGetProperty("client", out var client) &&
            client.TryGetProperty("sha1", out var clientHash))
        {
            var clientVersion = metadata.TryGetProperty("inheritsFrom", out var parent)
                ? parent.GetString() ?? versionId
                : versionId;
            var clientPath = Path.Combine(gameDirectory, "versions", clientVersion, $"{clientVersion}.jar");
            if (!FileMatchesHash(clientPath, clientHash.GetString()))
                issues.Add(new BuildIssue(IssueSeverity.Error, "Клиент Minecraft отсутствует или повреждён",
                    $"Не прошла проверка официального JAR-файла версии {clientVersion}.",
                    "Запустите экземпляр, чтобы повторно загрузить и проверить официальный файл."));
        }

        if (!metadata.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Array)
            return;
        foreach (var library in libraries.EnumerateArray())
        {
            if (!LibraryAllowedOnWindows(library))
                continue;
            if (library.TryGetProperty("downloads", out var libraryDownloads) &&
                libraryDownloads.TryGetProperty("artifact", out var artifact) &&
                artifact.TryGetProperty("path", out var pathElement) &&
                artifact.TryGetProperty("sha1", out var hashElement))
            {
                var path = SafeMetadataPath(Path.Combine(gameDirectory, "libraries"), pathElement.GetString());
                if (!FileMatchesHash(path, hashElement.GetString()))
                    issues.Add(new BuildIssue(IssueSeverity.Error, "Библиотека Minecraft отсутствует или повреждена",
                        $"Не прошла проверка файла {pathElement.GetString()}.",
                        "Запустите экземпляр, чтобы повторно загрузить недостающую библиотеку."));
            }
            if (library.TryGetProperty("natives", out var natives) &&
                natives.TryGetProperty("windows", out var nativeName) &&
                library.TryGetProperty("downloads", out var downloadsNode) &&
                downloadsNode.TryGetProperty("classifiers", out var classifiers))
            {
                var classifierName = nativeName.GetString()?.Replace(
                    "${arch}", Environment.Is64BitProcess ? "64" : "32", StringComparison.Ordinal);
                if (classifierName is not null && classifiers.TryGetProperty(classifierName, out var native) &&
                    native.TryGetProperty("path", out var nativePath) && native.TryGetProperty("sha1", out var nativeHash))
                {
                    var path = SafeMetadataPath(Path.Combine(gameDirectory, "libraries"), nativePath.GetString());
                    if (!FileMatchesHash(path, nativeHash.GetString()))
                        issues.Add(new BuildIssue(IssueSeverity.Error, "Нативная библиотека отсутствует или повреждена",
                            $"Не прошла проверка {nativePath.GetString()}.",
                            "Запустите экземпляр для повторной загрузки нативных файлов."));
                }
            }
        }
    }

    private static void CheckAssetFiles(JsonElement metadata, string gameDirectory, List<BuildIssue> issues)
    {
        if (!metadata.TryGetProperty("assetIndex", out var assetIndex) ||
            !assetIndex.TryGetProperty("id", out var idElement))
            return;
        var indexPath = Path.Combine(gameDirectory, "assets", "indexes", $"{idElement.GetString()}.json");
        if (!File.Exists(indexPath))
        {
            issues.Add(new BuildIssue(IssueSeverity.Error, "Индекс игровых ресурсов отсутствует",
                $"Не найден индекс {idElement.GetString()}.",
                "Запустите экземпляр для повторной загрузки официальных ресурсов."));
            return;
        }
        try
        {
            using var index = JsonDocument.Parse(File.ReadAllText(indexPath));
            var objects = index.RootElement.GetProperty("objects");
            var damaged = new List<string>();
            var totalAssets = 0;
            foreach (var asset in objects.EnumerateObject())
            {
                totalAssets++;
                var hash = asset.Value.GetProperty("hash").GetString();
                if (hash is not { Length: 40 })
                {
                    damaged.Add(asset.Name);
                    continue;
                }
                var path = Path.Combine(gameDirectory, "assets", "objects", hash[..2], hash);
                var expectedSize = asset.Value.TryGetProperty("size", out var size) ? size.GetInt64() : (long?)null;
                if (!File.Exists(path) ||
                    (expectedSize.HasValue && new FileInfo(path).Length != expectedSize.Value) ||
                    !FileMatchesHash(path, hash))
                    damaged.Add(asset.Name);
            }
            if (damaged.Count > 0)
            {
                var examples = string.Join(", ", damaged.Take(4));
                issues.Add(new BuildIssue(IssueSeverity.Error, "Игровые ресурсы отсутствуют или повреждены",
                    $"Не прошли проверку {damaged.Count} из {totalAssets} ресурсов. Примеры: {examples}.",
                    "Запустите экземпляр, чтобы повторно загрузить недостающие ресурсы с официального сервера."));
            }
        }
        catch (JsonException)
        {
            issues.Add(new BuildIssue(IssueSeverity.Error, "Повреждён индекс игровых ресурсов",
                "JSON-файл asset index не удалось прочитать.",
                "Запустите экземпляр для повторной загрузки индекса."));
        }
    }

    private static bool LibraryAllowedOnWindows(JsonElement library)
    {
        if (!library.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
            return true;
        var allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.TryGetProperty("os", out var os) &&
                os.TryGetProperty("name", out var name) && name.GetString() != "windows")
                continue;
            allowed = rule.TryGetProperty("action", out var action) && action.GetString() == "allow";
        }
        return allowed;
    }

    private static string SafeMetadataPath(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new InvalidDataException("Метаданные Minecraft содержат небезопасный путь.");
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Метаданные Minecraft указывают файл за пределами экземпляра.");
        return path;
    }

    private static bool FileMatchesHash(string path, string? expectedHash)
    {
        if (string.IsNullOrWhiteSpace(expectedHash) || !File.Exists(path))
            return false;
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA1.HashData(stream));
        return string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static string SafePath(string root, string relative)
    {
        ValidateArchivePath(relative);
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Путь архива выходит за каталог назначения.");
        return path;
    }

    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: false);
        return reader.ReadToEnd();
    }

    private static InstanceSettings CreateBackupSettings(InstanceSettings settings) => new()
    {
        MaximumMemoryMb = settings.MaximumMemoryMb,
        WindowWidth = settings.WindowWidth,
        WindowHeight = settings.WindowHeight,
        Fullscreen = settings.Fullscreen,
        CloseLauncherAfterStart = settings.CloseLauncherAfterStart,
        JvmArguments = string.Empty
    };

    private sealed class BackupManifestData
    {
        public string InstanceId { get; set; } = string.Empty;
        public string InstanceName { get; set; } = string.Empty;
        public string MinecraftVersion { get; set; } = string.Empty;
        public string Loader { get; set; } = string.Empty;
        public InstanceSettings? Settings { get; set; }
        public List<string> RedactedFiles { get; set; } = [];
        public List<string> OmittedTextFiles { get; set; } = [];
    }
}

public static class SafeFileTree
{
    public static IEnumerable<string> EnumerateRegularFiles(string root)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            yield break;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
                else
                    yield return entry;
            }
        }
    }
}
