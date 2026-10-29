using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;

namespace MaruLauncher;

internal static class FileOperationCoordinator
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DownloadLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> InstallationLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);
    private const int MaximumAttempts = 25;

    internal static async Task<T> RunInstallationAsync<T>(
        string gameDirectory, CancellationToken cancellationToken, Func<Task<T>> action)
    {
        var key = Path.GetFullPath(gameDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var semaphore = InstallationLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            semaphore.Release();
        }
    }

    internal static async Task RunInstallationAsync(
        string gameDirectory, CancellationToken cancellationToken, Func<Task> action)
    {
        await RunInstallationAsync(gameDirectory, cancellationToken, async () =>
        {
            await action();
            return true;
        });
    }

    internal static async Task<long> DownloadAsync(
        string targetPath, string? expectedSha1, long? expectedSize, CancellationToken cancellationToken,
        Func<string, CancellationToken, Task<long>> download)
    {
        var target = Path.GetFullPath(targetPath);
        var semaphore = DownloadLocks.GetOrAdd(target, static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            if (await IsValidFileAsync(target, expectedSha1, expectedSize, cancellationToken))
                return 0;

            var directory = Path.GetDirectoryName(target)
                ?? throw new InvalidOperationException($"Не удалось определить каталог файла «{target}».");
            Directory.CreateDirectory(directory);
            await RemoveAbandonedDownloadsAsync(target, cancellationToken);
            var temporary = target + $".{Guid.NewGuid():N}.download";
            Exception? downloadFailure = null;
            try
            {
                var downloaded = await download(temporary, cancellationToken);
                var actualSize = new FileInfo(temporary).Length;
                if (expectedSize.HasValue && actualSize != expectedSize.Value)
                    throw new InvalidDataException(
                        $"Размер файла «{target}» не совпадает с метаданными: ожидалось {expectedSize.Value}, получено {actualSize} байт.");
                if (!string.IsNullOrWhiteSpace(expectedSha1) &&
                    !await HasSha1Async(temporary, expectedSha1, cancellationToken))
                    throw new InvalidDataException($"Файл «{target}» не прошёл проверку SHA-1.");

                await MoveWithRetryAsync(temporary, target, cancellationToken);
                return downloaded;
            }
            catch (Exception exception)
            {
                downloadFailure = exception;
                LauncherLog.Write("download", exception, $"Target: {target}");
                throw;
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    try
                    {
                        await DeleteWithRetryAsync(temporary, CancellationToken.None);
                    }
                    catch (Exception cleanupException)
                    {
                        LauncherLog.Write("download-cleanup", cleanupException,
                            $"Temporary file: {temporary}; target: {target}");
                        if (downloadFailure is null)
                            throw new IOException(
                                $"Не удалось очистить временный файл «{temporary}»: {cleanupException.Message}",
                                cleanupException);
                    }
                }
            }
        }
        catch (IOException exception) when (IsFileSharingViolation(exception))
        {
            var message =
                $"Файл занят другим процессом при загрузке «{target}». Повторите операцию после закрытия использующей его программы. Причина: {exception.Message}";
            LauncherLog.Write("download-sharing-violation", exception, $"Target: {target}");
            throw new IOException(message, exception);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static async Task<bool> IsValidFileAsync(
        string path, string? expectedSha1, long? expectedSize, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return false;
        if (!expectedSize.HasValue && string.IsNullOrWhiteSpace(expectedSha1))
            return true;
        if (expectedSize.HasValue && new FileInfo(path).Length != expectedSize.Value)
            return false;
        return string.IsNullOrWhiteSpace(expectedSha1) ||
            await HasSha1Async(path, expectedSha1, cancellationToken);
    }

    private static async Task RemoveAbandonedDownloadsAsync(string target, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(target)!;
        var prefix = Path.GetFileName(target) + ".";
        IEnumerable<string> temporaryFiles;
        try
        {
            temporaryFiles = Directory.EnumerateFiles(directory, prefix + "*.download").ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LauncherLog.Write("download-temp-scan", exception, $"Target: {target}");
            return;
        }

        foreach (var temporary in temporaryFiles)
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(temporary) < TimeSpan.FromDays(1))
                    continue;
                await DeleteWithRetryAsync(temporary, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LauncherLog.Write("download-temp-cleanup", exception, $"Temporary file: {temporary}");
            }
        }
    }

    private static async Task MoveWithRetryAsync(
        string source, string destination, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(source, destination, true);
                return;
            }
            catch (IOException exception) when (IsFileSharingViolation(exception) && attempt < MaximumAttempts - 1)
            {
                await Task.Delay(RetryDelay, cancellationToken);
            }
            catch (IOException exception) when (IsFileSharingViolation(exception))
            {
                throw new IOException(
                    $"Временный файл «{source}» или целевой файл «{destination}» остаётся заблокированным после {MaximumAttempts} попыток.",
                    exception);
            }
        }
    }

    private static async Task DeleteWithRetryAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException exception) when (IsFileSharingViolation(exception) && attempt < MaximumAttempts - 1)
            {
                await Task.Delay(RetryDelay, cancellationToken);
            }
            catch (IOException exception) when (IsFileSharingViolation(exception))
            {
                throw new IOException(
                    $"Временный файл «{path}» остаётся заблокированным после {MaximumAttempts} попыток очистки.",
                    exception);
            }
        }
    }

    private static bool IsFileSharingViolation(IOException exception) =>
        (exception.HResult & 0xFFFF) is 32 or 33;

    private static async Task<bool> HasSha1Async(
        string path, string expected, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
                var actual = Convert.ToHexString(await SHA1.HashDataAsync(stream, cancellationToken));
                return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
            }
            catch (IOException exception) when (IsFileSharingViolation(exception) && attempt < MaximumAttempts - 1)
            {
                await Task.Delay(RetryDelay, cancellationToken);
            }
            catch (IOException exception) when (IsFileSharingViolation(exception))
            {
                throw new IOException(
                    $"Не удалось проверить SHA-1 файла «{path}»: файл заблокирован после {MaximumAttempts} попыток.",
                    exception);
            }
        }
    }
}
