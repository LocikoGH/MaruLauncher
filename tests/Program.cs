using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.IO.Compression;
using System.Runtime.InteropServices;
using MaruLauncher;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Concurrent downloads of the same target share one completed download", ConcurrentDownloadsShareResultAsync),
    ("Different library targets download in parallel", DifferentTargetsDownloadInParallelAsync),
    ("Transient Windows file-sharing locks are retried", TransientFileLockIsRetriedAsync),
    ("A failed download can be retried and removes its temporary file", RetryAfterFailureAsync),
    ("Invalid downloads are discarded and abandoned downloads are cleaned", FailedDownloadsAreCleanedAsync),
    ("Repeated installations of one game directory are serialized", RepeatedInstallationsAreSerializedAsync),
    ("Modern Windows native classifiers are routed and extracted correctly", WindowsNativeGroupsAreDetectedAsync)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add(test.Name);
        Console.Error.WriteLine($"FAIL {test.Name}: {exception}");
    }
}

Console.WriteLine($"{tests.Length - failures.Count}/{tests.Length} tests passed.");
return failures.Count == 0 ? 0 : 1;

static async Task ConcurrentDownloadsShareResultAsync()
{
    using var directory = new TemporaryDirectory();
    var target = Path.Combine(directory.Path, "library.jar");
    var data = Encoding.UTF8.GetBytes("library payload");
    var sha1 = Convert.ToHexString(SHA1.HashData(data));
    var downloads = 0;
    var activeDownloads = 0;
    var maximumActiveDownloads = 0;

    async Task<long> Download(string temporary, CancellationToken token)
    {
        Interlocked.Increment(ref downloads);
        var active = Interlocked.Increment(ref activeDownloads);
        UpdateMaximum(ref maximumActiveDownloads, active);
        try
        {
            await Task.Delay(150, token);
            await File.WriteAllBytesAsync(temporary, data, token);
            return data.Length;
        }
        finally
        {
            Interlocked.Decrement(ref activeDownloads);
        }
    }

    var tasks = Enumerable.Range(0, 2).Select(_ =>
        FileOperationCoordinator.DownloadAsync(target, sha1, data.Length, CancellationToken.None, Download));
    await Task.WhenAll(tasks);

    Assert(downloads == 1, $"Expected one download, got {downloads}.");
    Assert(maximumActiveDownloads == 1, $"Expected one active download, got {maximumActiveDownloads}.");
    Assert(File.ReadAllBytes(target).SequenceEqual(data), "The completed library content differs.");
    Assert(!Directory.EnumerateFiles(directory.Path, "*.download").Any(), "A temporary file was left behind.");
}

static async Task RetryAfterFailureAsync()
{
    using var directory = new TemporaryDirectory();
    var target = Path.Combine(directory.Path, "retry.jar");
    var data = Encoding.UTF8.GetBytes("retry payload");
    var sha1 = Convert.ToHexString(SHA1.HashData(data));
    var attempts = 0;

    async Task<long> Download(string temporary, CancellationToken token)
    {
        if (Interlocked.Increment(ref attempts) == 1)
        {
            await File.WriteAllTextAsync(temporary, "partial", token);
            throw new IOException("simulated network interruption");
        }
        await File.WriteAllBytesAsync(temporary, data, token);
        return data.Length;
    }

    await AssertThrowsAsync<IOException>(() =>
        FileOperationCoordinator.DownloadAsync(target, sha1, data.Length, CancellationToken.None, Download));
    Assert(!Directory.EnumerateFiles(directory.Path, "*.download").Any(),
        "The failed attempt did not clean up its temporary file.");
    await FileOperationCoordinator.DownloadAsync(target, sha1, data.Length, CancellationToken.None, Download);
    Assert(attempts == 2, $"Expected two attempts, got {attempts}.");
    Assert(File.ReadAllBytes(target).SequenceEqual(data), "The retried library content differs.");
}

static async Task DifferentTargetsDownloadInParallelAsync()
{
    using var directory = new TemporaryDirectory();
    var activeDownloads = 0;
    var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    async Task<long> Download(string temporary, CancellationToken token)
    {
        if (Interlocked.Increment(ref activeDownloads) == 2)
            bothStarted.TrySetResult();
        try
        {
            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            await File.WriteAllTextAsync(temporary, "independent library", token);
            return 19;
        }
        finally
        {
            Interlocked.Decrement(ref activeDownloads);
        }
    }

    await Task.WhenAll(
        FileOperationCoordinator.DownloadAsync(
            Path.Combine(directory.Path, "first.jar"), null, null, CancellationToken.None, Download),
        FileOperationCoordinator.DownloadAsync(
            Path.Combine(directory.Path, "second.jar"), null, null, CancellationToken.None, Download));
}

static async Task TransientFileLockIsRetriedAsync()
{
    using var directory = new TemporaryDirectory();
    var target = Path.Combine(directory.Path, "locked.jar");
    var data = Encoding.UTF8.GetBytes("temporarily locked payload");
    var sha1 = Convert.ToHexString(SHA1.HashData(data));

    await FileOperationCoordinator.DownloadAsync(
        target, sha1, data.Length, CancellationToken.None,
        async (temporary, token) =>
        {
            await File.WriteAllBytesAsync(temporary, data, token);
            var heldFile = new FileStream(
                temporary, FileMode.Open, FileAccess.Read, FileShare.None);
            _ = Task.Run(async () =>
            {
                await Task.Delay(350);
                await heldFile.DisposeAsync();
            });
            return data.Length;
        });

    Assert(File.ReadAllBytes(target).SequenceEqual(data),
        "The destination was not committed after the temporary lock was released.");
    Assert(!Directory.EnumerateFiles(directory.Path, "*.download").Any(),
        "A temporary file remained after the successful retry.");
}

static async Task FailedDownloadsAreCleanedAsync()
{
    using var directory = new TemporaryDirectory();
    var target = Path.Combine(directory.Path, "bad.jar");
    var abandoned = target + ".old.download";
    await File.WriteAllTextAsync(abandoned, "stale");
    File.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow.AddDays(-2));
    var data = Encoding.UTF8.GetBytes("wrong payload");
    var expectedSha1 = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes("expected payload")));

    await AssertThrowsAsync<InvalidDataException>(async () =>
    {
        await FileOperationCoordinator.DownloadAsync(
            target, expectedSha1, null, CancellationToken.None,
            async (temporary, token) =>
            {
                await File.WriteAllBytesAsync(temporary, data, token);
                return data.Length;
            });
    });

    Assert(!File.Exists(abandoned), "An abandoned temporary file was not removed.");
    Assert(!Directory.EnumerateFiles(directory.Path, "*.download").Any(),
        "An invalid download temporary file was not removed.");
    Assert(!File.Exists(target), "Invalid bytes were committed to the destination.");
}

static async Task RepeatedInstallationsAreSerializedAsync()
{
    using var directory = new TemporaryDirectory();
    var activeInstallations = 0;
    var maximumActiveInstallations = 0;

    async Task Install()
    {
        await FileOperationCoordinator.RunInstallationAsync(
            directory.Path, CancellationToken.None, async () =>
            {
                var active = Interlocked.Increment(ref activeInstallations);
                UpdateMaximum(ref maximumActiveInstallations, active);
                try
                {
                    await Task.Delay(100);
                }
                finally
                {
                    Interlocked.Decrement(ref activeInstallations);
                }
            });
    }

    await Task.WhenAll(Install(), Install());
    Assert(maximumActiveInstallations == 1,
        $"Repeated installs overlapped ({maximumActiveInstallations} simultaneous operations).");
}

static async Task WindowsNativeGroupsAreDetectedAsync()
{
    AssertNativeGroup("org.lwjgl:lwjgl:3.4.3:natives-windows", "lwjgl");
    AssertNativeGroup("net.java.dev.jna:jna:5.16.0:natives-windows", "jna");
    AssertNativeGroup("io.netty:netty-transport-native:4.2.0:natives-windows", "netty");
    AssertNativeGroup("com.mojang:jtracy:1.14.38:natives-windows", "java");
    if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
        Assert(MinecraftService.GetWindowsNativeGroup(
            JsonNode.Parse("""{"name":"org.lwjgl:lwjgl:3.4.3:natives-windows-arm64"}""")!.AsObject()) is null,
            "An ARM64 native classifier was selected for a non-ARM64 process.");
    Assert(MinecraftService.GetWindowsNativeGroup(
        JsonNode.Parse("""{"name":"org.lwjgl:lwjgl:3.4.3"}""")!.AsObject()) is null,
        "A regular library was treated as a native library.");

    using var directory = new TemporaryDirectory();
    var gameDirectory = Path.Combine(directory.Path, "game");
    var libraryPath = Path.Combine(gameDirectory, "libraries", "org", "lwjgl", "native.jar");
    Directory.CreateDirectory(Path.GetDirectoryName(libraryPath)!);
    var archivePath = libraryPath;
    using (var stream = File.Create(archivePath))
    using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
    {
        using (var native = new StreamWriter(archive.CreateEntry("org/lwjgl/native.dll").Open()))
            native.Write("native payload");
        using (var archiveMetadata = new StreamWriter(archive.CreateEntry("META-INF/test.SF").Open()))
            archiveMetadata.Write("signature");
    }

    var clientDirectory = Path.Combine(gameDirectory, "versions", "26.3");
    Directory.CreateDirectory(clientDirectory);
    await File.WriteAllTextAsync(Path.Combine(clientDirectory, "26.3.jar"), "client");
    var metadata = JsonNode.Parse(
        """{"id":"26.3","libraries":[{"name":"org.lwjgl:lwjgl:3.4.3:natives-windows","downloads":{"artifact":{"path":"org/lwjgl/native.jar","url":"https://example.invalid/native.jar"}}}]}""")!.AsObject();
    var target = Path.Combine(gameDirectory, "natives", "test", "lwjgl");
    var classpath = await MinecraftService.BuildClasspathAsync(
        metadata, gameDirectory, Path.Combine(gameDirectory, "natives", "test"),
        new DownloadOperation(), CancellationToken.None);

    Assert(File.ReadAllText(Path.Combine(target, "org", "lwjgl", "native.dll")) == "native payload",
        "The native DLL was not extracted into the JVM library path.");
    Assert(!File.Exists(Path.Combine(target, "META-INF", "test.SF")),
        "Native archive metadata was extracted unexpectedly.");
    Assert(classpath.Split(Path.PathSeparator).Contains(libraryPath, StringComparer.OrdinalIgnoreCase),
        "The native JAR was not retained on the classpath for LWJGL resource loading.");
}

static void AssertNativeGroup(string coordinate, string expectedGroup)
{
    var library = JsonNode.Parse($"{{\"name\":\"{coordinate}\"}}")!.AsObject();
    var actual = MinecraftService.GetWindowsNativeGroup(library);
    Assert(actual == expectedGroup, $"Expected {coordinate} in {expectedGroup}, got {actual ?? "none"}.");
}

static void UpdateMaximum(ref int maximum, int value)
{
    int observed;
    do
    {
        observed = Volatile.Read(ref maximum);
        if (observed >= value)
            return;
    } while (Interlocked.CompareExchange(ref maximum, value, observed) != observed);
}

static async Task AssertThrowsAsync<TException>(Func<Task> action) where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), $"MaruLauncher.Tests.{Guid.NewGuid():N}");

    public TemporaryDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        Directory.Delete(Path, recursive: true);
    }
}
