using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MaruLauncher;

public sealed record PackImportPreview(
    string Name, string MinecraftVersion, string Loader, IReadOnlyList<string> Files,
    IReadOnlyList<string> ExistingFiles, IReadOnlyList<string> Hosts);

public sealed class ModpackService
{
    private const string LauncherManifest = "marulauncher-pack.json";
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public void Export(GameInstance instance, string destination, bool includeConfig)
    {
        var gameDirectory = MinecraftService.GetGameDirectory(instance);
        var temporary = destination + ".tmp";
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                var redactedFiles = new List<string>();
                var omittedTextFiles = new List<string>();
                var included = new List<string> { "mods" };
                if (includeConfig)
                {
                    included.Add("config");
                    included.Add("resourcepacks");
                }
                foreach (var folder in included)
                {
                    var source = Path.Combine(gameDirectory, folder);
                    if (!Directory.Exists(source))
                        continue;
                    foreach (var file in SafeFileTree.EnumerateRegularFiles(source))
                    {
                        var relative = Path.GetRelativePath(gameDirectory, file);
                        var archivePath = relative.Replace(Path.DirectorySeparatorChar, '/');
                        ValidatePackPath(archivePath);
                        byte[]? sanitizedContent = null;
                        if (SecureArchiveContent.IsTextCandidate(file))
                        {
                            var sanitized = SecureArchiveContent.SanitizeTextFile(file);
                            if (!sanitized.IsSafeToInclude)
                            {
                                omittedTextFiles.Add(archivePath);
                                continue;
                            }
                            sanitizedContent = sanitized.Content;
                            if (sanitized.Redacted)
                                redactedFiles.Add(archivePath);
                        }
                        var entry = archive.CreateEntry($"overrides/{archivePath}", CompressionLevel.Optimal);
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
                var manifestEntry = archive.CreateEntry(LauncherManifest);
                using var manifestStream = manifestEntry.Open();
                JsonSerializer.Serialize(manifestStream, new LauncherPackManifest
                {
                    Name = instance.Name,
                    MinecraftVersion = instance.MinecraftVersion,
                    Loader = instance.Loader,
                    Includes = included,
                    RedactedFiles = redactedFiles,
                    OmittedTextFiles = omittedTextFiles
                }, new JsonSerializerOptions { WriteIndented = true });
            }
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public PackImportPreview Preview(string archivePath, GameInstance target)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 100_000)
            throw new InvalidDataException("Пакет содержит слишком много файлов.");
        var byName = archive.Entries.ToDictionary(entry => entry.FullName, StringComparer.OrdinalIgnoreCase);
        var selectedFiles = new List<string>();
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string name;
        string minecraftVersion;
        string loader;

        if (byName.TryGetValue(LauncherManifest, out var launcherEntry))
        {
            using var stream = launcherEntry.Open();
            var manifest = JsonSerializer.Deserialize<LauncherPackManifest>(stream)
                ?? throw new InvalidDataException("Метаданные modpack MaruLauncher повреждены.");
            name = manifest.Name;
            minecraftVersion = manifest.MinecraftVersion;
            loader = manifest.Loader;
            foreach (var entry in archive.Entries.Where(entry => entry.FullName.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase)))
            {
                var relative = entry.FullName["overrides/".Length..];
                if (string.IsNullOrEmpty(relative) || relative.EndsWith('/'))
                    continue;
                ValidatePackPath(relative);
                selectedFiles.Add(relative);
            }
        }
        else if (byName.TryGetValue("modrinth.index.json", out var indexEntry))
        {
            using var stream = indexEntry.Open();
            using var index = JsonDocument.Parse(stream);
            var root = index.RootElement;
            name = root.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "Modrinth modpack" : "Modrinth modpack";
            var dependencies = root.GetProperty("dependencies");
            minecraftVersion = dependencies.GetProperty("minecraft").GetString() ?? string.Empty;
            var loaderInfo = dependencies.EnumerateObject()
                .FirstOrDefault(property => property.Name is "fabric-loader" or "forge" or "neoforge");
            loader = loaderInfo.Name switch
            {
                "fabric-loader" => "Fabric",
                "forge" => "Forge",
                "neoforge" => "NeoForge",
                _ => "Vanilla"
            };
            foreach (var file in root.GetProperty("files").EnumerateArray())
            {
                var relative = file.GetProperty("path").GetString()
                    ?? throw new InvalidDataException("В индексе modpack отсутствует путь файла.");
                ValidatePackPath(relative);
                var clientRequired = !file.TryGetProperty("env", out var env) ||
                    !env.TryGetProperty("client", out var clientEnv) ||
                    clientEnv.GetString() != "unsupported";
                if (!clientRequired)
                    continue;
                selectedFiles.Add(relative);
                foreach (var download in file.GetProperty("downloads").EnumerateArray())
                {
                    if (Uri.TryCreate(download.GetString(), UriKind.Absolute, out var uri))
                        hosts.Add(uri.Host);
                }
            }
            foreach (var entry in archive.Entries.Where(entry =>
                entry.FullName.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase)))
            {
                var relative = entry.FullName["overrides/".Length..];
                if (string.IsNullOrEmpty(relative) || relative.EndsWith('/'))
                    continue;
                ValidatePackPath(relative);
                selectedFiles.Add(relative);
            }
        }
        else
        {
            throw new InvalidDataException("Архив не является поддерживаемым modpack MaruLauncher или Modrinth.");
        }

        var normalizedFiles = selectedFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var gameDirectory = MinecraftService.GetGameDirectory(target);
        var existing = normalizedFiles.Where(relative =>
            File.Exists(Path.Combine(gameDirectory, relative.Replace('/', Path.DirectorySeparatorChar)))).ToArray();
        return new PackImportPreview(name, minecraftVersion, loader, normalizedFiles, existing,
            hosts.OrderBy(host => host, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public async Task ImportAsync(
        GameInstance target, string archivePath, bool overwriteExisting,
        DownloadOperation operation, CancellationToken cancellationToken)
    {
        var preview = Preview(archivePath, target);
        if (preview.MinecraftVersion.Length > 0 &&
            !string.Equals(preview.MinecraftVersion, target.MinecraftVersion, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Пакет рассчитан на Minecraft {preview.MinecraftVersion}, а выбран экземпляр {target.MinecraftVersion}. " +
                "Создайте экземпляр с подходящей версией.");
        if (!string.Equals(preview.Loader, target.Loader, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Пакет требует загрузчик {preview.Loader}, а выбран экземпляр {target.Loader}. " +
                "Выберите или создайте экземпляр с подходящим загрузчиком.");
        if (preview.ExistingFiles.Count > 0 && !overwriteExisting)
            throw new InvalidOperationException("В выбранном экземпляре уже есть файлы из пакета.");

        var staging = Path.Combine(AppPaths.Root, "modpack-staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                if (archive.GetEntry("modrinth.index.json") is { } indexEntry)
                    await DownloadModrinthFilesAsync(indexEntry, staging, operation, cancellationToken);

                foreach (var entry in archive.Entries.Where(entry =>
                    entry.FullName.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = entry.FullName["overrides/".Length..];
                    if (string.IsNullOrEmpty(relative) || relative.EndsWith('/'))
                        continue;
                    ValidatePackPath(relative);
                    var destination = SafePath(staging, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    using var source = entry.Open();
                    using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
                    await source.CopyToAsync(output, cancellationToken);
                }
            }

            var gameDirectory = MinecraftService.GetGameDirectory(target);
            foreach (var relative in preview.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stagedFile = SafePath(staging, relative);
                if (!File.Exists(stagedFile))
                    continue;
                var destination = SafePath(gameDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(stagedFile, destination, overwriteExisting);
            }
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, true);
        }
    }

    private static async Task DownloadModrinthFilesAsync(
        ZipArchiveEntry indexEntry, string staging, DownloadOperation operation, CancellationToken cancellationToken)
    {
        using var stream = indexEntry.Open();
        using var index = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var files = index.RootElement.GetProperty("files").EnumerateArray().ToArray();
        for (var indexNumber = 0; indexNumber < files.Length; indexNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[indexNumber];
            var clientRequired = !file.TryGetProperty("env", out var env) ||
                !env.TryGetProperty("client", out var clientEnv) || clientEnv.GetString() != "unsupported";
            if (!clientRequired)
                continue;
            var path = file.GetProperty("path").GetString()
                ?? throw new InvalidDataException("В индексе modpack отсутствует путь файла.");
            ValidatePackPath(path);
            var urls = file.GetProperty("downloads").EnumerateArray()
                .Select(url => url.GetString()).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            if (urls.Length == 0)
                throw new InvalidDataException($"Для файла {path} не указаны ссылки загрузки.");
            var uri = new Uri(urls[0]!);
            if (uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException($"Ссылка для {path} не использует HTTPS.");
            var sha1 = file.GetProperty("hashes").TryGetProperty("sha1", out var hash)
                ? hash.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(sha1) || sha1.Length != 40)
                throw new InvalidDataException($"Для файла {path} отсутствует проверяемый SHA-1.");
            var destination = SafePath(staging, path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await DownloadCheckedAsync(uri, destination, sha1, operation, indexNumber + 1, files.Length, cancellationToken);
        }
    }

    private static async Task DownloadCheckedAsync(
        Uri uri, string target, string sha1, DownloadOperation operation,
        int current, int totalFiles, CancellationToken cancellationToken)
    {
        operation.Stage = $"Пакет: файл {current} из {totalFiles}";
        var temporary = target + ".download";
        try
        {
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new HttpRequestException($"Не удалось скачать modpack-файл (HTTP {(int)response.StatusCode}).");
            operation.TotalBytes = response.Content.Headers.ContentLength;
            operation.BytesReceived = 0;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920];
            long received = 0;
            while (true)
            {
                var count = await source.ReadAsync(buffer, cancellationToken);
                if (count == 0)
                    break;
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                received += count;
                operation.BytesReceived = received;
            }
            await output.FlushAsync(cancellationToken);
            await using var downloaded = File.OpenRead(temporary);
            var digest = Convert.ToHexString(await SHA1.HashDataAsync(downloaded, cancellationToken));
            if (!string.Equals(digest, sha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Загруженный modpack-файл {Path.GetFileName(target)} не прошёл проверку SHA-1.");
            File.Move(temporary, target, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static void ValidatePackPath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\') ||
            relative.Contains(':') || relative.Split('/').Any(part => part is "." or ".."))
            throw new InvalidDataException("Modpack содержит небезопасный путь.");
        var first = relative.Split('/')[0];
        if (first is not ("mods" or "config" or "resourcepacks" or "shaderpacks"))
            throw new InvalidDataException($"Modpack пытается изменить неподдерживаемый каталог «{first}».");
    }

    private static string SafePath(string root, string relative)
    {
        ValidatePackPath(relative);
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Путь modpack выходит за пределы каталога экземпляра.");
        return path;
    }

    private sealed class LauncherPackManifest
    {
        public string Name { get; set; } = string.Empty;
        public string MinecraftVersion { get; set; } = string.Empty;
        public string Loader { get; set; } = string.Empty;
        public List<string> Includes { get; set; } = [];
        public List<string> RedactedFiles { get; set; } = [];
        public List<string> OmittedTextFiles { get; set; } = [];
    }
}
