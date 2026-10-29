using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace MaruLauncher;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private StateStore _store = null!;
    private readonly MinecraftService _minecraft = new();
    private readonly ModpackService _modpacks = new();
    private readonly ObservableCollection<DownloadOperation> _downloads = [];
    private readonly HashSet<string> _busyInstances = new(StringComparer.Ordinal);
    private bool _initializing;

    private GameInstance? SelectedInstance => InstancesList.SelectedItem as GameInstance;
    private LauncherProfile? SelectedProfile => ActiveProfileCombo.SelectedItem as LauncherProfile;

    public MainWindow()
    {
        InitializeComponent();
        try
        {
            _store = new StateStore();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            ThemedDialog.Show(this,
                $"Не удалось прочитать данные MaruLauncher. Файл настроек не был заменён.\n\n{exception.Message}",
                "Ошибка хранилища", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        var removedLegacyAccounts = NormalizeLoadedData();
        _initializing = true;
        DownloadsList.ItemsSource = _downloads;
        ThemeComboBox.ItemsSource = new[]
        {
            new ThemeOption(ThemeChoice.Dark, "Тёмная"),
            new ThemeOption(ThemeChoice.Light, "Светлая"),
            new ThemeOption(ThemeChoice.System, "Системная (Windows)")
        };
        ThemeComboBox.DisplayMemberPath = nameof(ThemeOption.Label);
        ThemeComboBox.SelectedValuePath = nameof(ThemeOption.Choice);
        ThemeComboBox.SelectedValue = _store.Data.Settings.Theme;
        RefreshProfileViews();
        RefreshInstancesList();
        _initializing = false;
        ((App)Application.Current).ApplyTheme(_store.Data.Settings.Theme);
        if (removedLegacyAccounts)
            PersistState();
        _minecraft.DiagnosticWriteFailed += message =>
            Dispatcher.Invoke(() => SetStatus(message, true));
        _minecraft.LaunchFailed += message =>
            Dispatcher.Invoke(() => SetStatus(message, true));

        Loaded += async (_, _) =>
        {
            UpdateAllViews();
            await RefreshVersionsAsync(showErrors: false);
            RefreshDiagnostics();
        };
    }

    private bool NormalizeLoadedData()
    {
        _store.Data.Settings ??= new LauncherSettings();
        _store.Data.Profiles ??= [];
        _store.Data.Instances ??= [];
        LegacyTokenCleanup.DeleteTokenFiles();
        var removedLegacyAccounts = _store.Data.Profiles.RemoveAll(profile => profile.Type == AccountType.Microsoft) > 0;
        if (_store.Data.ActiveProfileId is { } activeProfileId &&
            !_store.Data.Profiles.Any(profile => profile.Id == activeProfileId))
        {
            _store.Data.ActiveProfileId = _store.Data.Profiles.FirstOrDefault()?.Id;
            removedLegacyAccounts = true;
        }
        foreach (var instance in _store.Data.Instances)
        {
            instance.Settings ??= new InstanceSettings();
            if (string.IsNullOrWhiteSpace(instance.Id))
                instance.Id = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(instance.RootPath))
                instance.RootPath = Path.Combine(AppPaths.Instances, instance.Id);
        }
        return removedLegacyAccounts;
    }

    private void UpdateAllViews()
    {
        RefreshProfileViews();
        RefreshInstancesList();
        UpdateHome();
        UpdateInstanceSettings();
        RefreshMods();
        RefreshDiagnostics();
    }

    private void RefreshProfileViews()
    {
        var activeId = _store.Data.ActiveProfileId;
        ActiveProfileCombo.ItemsSource = null;
        ProfilesList.ItemsSource = null;
        ActiveProfileCombo.ItemsSource = _store.Data.Profiles;
        ProfilesList.ItemsSource = _store.Data.Profiles;
        ActiveProfileCombo.SelectedItem = _store.Data.Profiles.FirstOrDefault(profile => profile.Id == activeId)
            ?? _store.Data.Profiles.FirstOrDefault();
        if (ActiveProfileCombo.SelectedItem is LauncherProfile active)
            _store.Data.ActiveProfileId = active.Id;
        UpdateProfileSummary();
    }

    private void RefreshInstancesList()
    {
        var selectedId = SelectedInstance?.Id ?? _store.Data.ActiveInstanceId;
        var search = InstanceSearchInput?.Text?.Trim();
        var instances = _store.Data.Instances
            .OrderByDescending(instance => instance.IsFavorite)
            .ThenBy(instance => instance.Name, StringComparer.CurrentCultureIgnoreCase)
            .Where(instance => string.IsNullOrEmpty(search) ||
                instance.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .ToArray();
        _initializing = true;
        InstancesList.ItemsSource = null;
        InstancesList.ItemsSource = instances;
        InstancesList.SelectedItem = instances.FirstOrDefault(instance => instance.Id == selectedId);
        FavoritesList.ItemsSource = _store.Data.Instances
            .Where(instance => instance.IsFavorite)
            .OrderBy(instance => instance.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var selectedInstance = SelectedInstance;
        _initializing = false;
        UpdateSelectedInstance(selectedInstance);
        UpdateHome();
    }

    private void UpdateHome()
    {
        var last = _store.Data.Instances.FirstOrDefault(instance => instance.Id == _store.Data.LastInstanceId);
        LastInstanceTitle.Text = last?.Name ?? "Пока нет запусков";
        LastInstanceDetails.Text = last is null
            ? "Создайте экземпляр и запустите игру — он появится здесь."
            : $"{last.MinecraftVersion} · {last.Loader} · {last.Status}";
        LaunchLastButton.IsEnabled = last is not null && SelectedProfile is not null;
        var profile = SelectedProfile;
        HomeProfileName.Text = profile?.Name ?? "Профиль не выбран";
        HomeProfileType.Text = profile is null ? "Создайте локальный Offline-профиль." : "Offline";

        if (last is null)
        {
            HomeLastResult.Text = "Запусков пока не было";
            return;
        }
        var resultPath = Path.Combine(last.RootPath, "diagnostics", "latest-launch.json");
        if (!File.Exists(resultPath))
        {
            HomeLastResult.Text = "Результат завершённого запуска пока не записан.";
            return;
        }
        try
        {
            var result = JsonSerializer.Deserialize<LaunchResult>(File.ReadAllText(resultPath), JsonOptions);
            HomeLastResult.Text = result is null ? "Не удалось прочитать результат запуска."
                : result.ExitCode is null ? $"Запуск {result.StartedAt:g}: процесс ещё не завершён."
                : $"Запуск {result.StartedAt:g}: код завершения {result.ExitCode}.";
        }
        catch (JsonException)
        {
            HomeLastResult.Text = "Файл результата запуска повреждён; откройте страницу диагностики.";
        }
        catch (IOException exception)
        {
            HomeLastResult.Text = $"Не удалось прочитать результат запуска: {exception.Message}";
        }
    }

    private void UpdateProfileSummary()
    {
        var profile = SelectedProfile;
        ActiveProfileLabel.Text = "Offline:";
        HomeProfileName.Text = profile?.Name ?? "Профиль не выбран";
        HomeProfileType.Text = profile is null ? "Создайте локальный Offline-профиль." : "Offline";
    }

    private void UpdateSelectedInstance(GameInstance? instance)
    {
        InstanceDetailsPanel.IsEnabled = instance is not null;
        _store.Data.ActiveInstanceId = instance?.Id;
        SelectedInstanceTitle.Text = instance?.Name ?? "Выберите экземпляр";
        SelectedInstanceDetails.Text = instance is null
            ? "Версия, загрузчик и состояние установки отображаются здесь."
            : $"{instance.MinecraftVersion} · {instance.Loader} · {instance.Status}\n{MinecraftService.GetGameDirectory(instance)}";
        FavoriteButton.Content = instance?.IsFavorite == true ? "Убрать из избранного" : "В избранное";
        ModsInstanceLabel.Text = instance is null
            ? "Выберите экземпляр на вкладке «Экземпляры»"
            : $"{instance.Name} · {instance.MinecraftVersion} · {instance.Loader}";
        BackupInstanceLabel.Text = instance is null
            ? "Выберите экземпляр на вкладке «Экземпляры»"
            : $"{instance.Name} · {instance.MinecraftVersion} · {instance.Loader}";
        UpdateInstanceSettings();
        RefreshMods();
        if (!_initializing)
        {
            PersistState();
            UpdateHome();
            RefreshDiagnostics();
        }
    }

    private void UpdateInstanceSettings()
    {
        var instance = SelectedInstance;
        InstanceSettingsLabel.Text = instance is null
            ? "Сначала выберите экземпляр"
            : $"{instance.Name} · {instance.MinecraftVersion} · {instance.Loader}";
        var enabled = instance is not null;
        MemoryInput.IsEnabled = enabled;
        WidthInput.IsEnabled = enabled;
        HeightInput.IsEnabled = enabled;
        JavaPathInput.IsEnabled = enabled;
        GameDirectoryInput.IsEnabled = enabled;
        JvmArgumentsInput.IsEnabled = enabled;
        FullscreenCheckBox.IsEnabled = enabled;
        CloseLauncherCheckBox.IsEnabled = enabled;
        if (instance is null)
            return;
        MemoryInput.Text = instance.Settings.MaximumMemoryMb.ToString();
        WidthInput.Text = instance.Settings.WindowWidth.ToString();
        HeightInput.Text = instance.Settings.WindowHeight.ToString();
        var configuredJavaPath = instance.Settings.JavaPath;
        var bundledJavaPath = MinecraftService.FindBundledJavaPath();
        var bundledJavaRelativePath = Path.Combine("java", "bin", "javaw.exe");
        var useBundledJava = string.IsNullOrWhiteSpace(configuredJavaPath);
        if (!useBundledJava && bundledJavaPath is not null && Path.IsPathRooted(configuredJavaPath))
        {
            try
            {
                var configuredFullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredJavaPath));
                useBundledJava = !File.Exists(configuredFullPath) ||
                    string.Equals(configuredFullPath, bundledJavaPath, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                useBundledJava = false;
            }
        }
        JavaPathInput.Text = useBundledJava && bundledJavaPath is not null
            ? bundledJavaRelativePath
            : configuredJavaPath;
        GameDirectoryInput.Text = instance.Settings.GameDirectory;
        JvmArgumentsInput.Text = instance.Settings.JvmArguments;
        FullscreenCheckBox.IsChecked = instance.Settings.Fullscreen;
        CloseLauncherCheckBox.IsChecked = instance.Settings.CloseLauncherAfterStart;
    }

    private void RefreshMods()
    {
        var instance = SelectedInstance;
        if (instance is null)
        {
            ModsList.ItemsSource = null;
            return;
        }
        try
        {
            var directory = Path.Combine(MinecraftService.GetGameDirectory(instance), "mods");
            ModsList.ItemsSource = Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*.jar", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName).OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray()
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ModsList.ItemsSource = Array.Empty<string>();
            SetStatus($"Не удалось перечислить моды: {exception.Message}", true);
        }
    }

    private async Task RefreshVersionsAsync(bool showErrors)
    {
        try
        {
            SetStatus("Проверка списка версий Minecraft…");
            var versions = await _minecraft.GetReleaseVersionsAsync(CancellationToken.None);
            VersionComboBox.ItemsSource = versions.Select(version => version.Id).ToArray();
            if (VersionComboBox.SelectedItem is null && versions.Count > 0)
                VersionComboBox.SelectedIndex = 0;
            SetStatus($"Доступны версии Minecraft: {versions.Count}.");
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            SetStatus($"Не удалось получить версии Minecraft: {exception.Message}", true);
            if (showErrors)
                ShowError(exception.Message);
        }
    }

    private void PersistState()
    {
        try
        {
            _store.Save();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            SetStatus($"Не удалось сохранить настройки: {exception.Message}", true);
            ShowError($"Изменение пока действует только до закрытия приложения.\n\n{exception.Message}");
        }
    }

    private Task RunUiActionAsync(Func<Task> action)
    {
        return RunUiActionCoreAsync(action);
    }

    private async Task RunUiActionCoreAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            SetStatus(exception.Message, true);
            ShowError(exception.Message);
        }
    }

    private static bool IsExpected(Exception exception) =>
        exception is InvalidOperationException or IOException or UnauthorizedAccessException or
            InvalidDataException or HttpRequestException or JsonException or
            Win32Exception or ArgumentException or FormatException or TimeoutException or OperationCanceledException or
            NotSupportedException or SocketException;

    private void ShowError(string message) =>
        ThemedDialog.Show(this, message, "MaruLauncher", MessageBoxButton.OK, MessageBoxImage.Warning);

    private void SetStatus(string message, bool error = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = error
            ? (System.Windows.Media.Brush)FindResource("DangerBrush")
            : (System.Windows.Media.Brush)FindResource("MutedBrush");
    }

    private bool PreventChangeWhileBusy(GameInstance instance)
    {
        if (!_busyInstances.Contains(instance.Id))
            return false;
        ShowError($"Дождитесь завершения текущей операции для экземпляра «{instance.Name}».");
        return true;
    }

    private Task QueueOperation(
        string name, GameInstance? instance, Func<DownloadOperation, CancellationToken, Task> work,
        bool showProgressWindow = false)
    {
        if (instance is not null && !_busyInstances.Add(instance.Id))
        {
            ShowError($"Для экземпляра «{instance.Name}» уже выполняется операция. Дождитесь её завершения.");
            return Task.CompletedTask;
        }
        var operation = new DownloadOperation
        {
            Name = name,
            InstanceName = instance?.Name ?? "Лаунчер",
            InstanceId = instance?.Id,
            RetryAction = _ => QueueOperation(name, instance, work, showProgressWindow)
        };
        _downloads.Insert(0, operation);
        if (!showProgressWindow)
            return RunOperationAsync(operation, work);
        return RunOperationWithProgressWindowAsync(operation, work);
    }

    private async Task RunOperationWithProgressWindowAsync(
        DownloadOperation operation, Func<DownloadOperation, CancellationToken, Task> work)
    {
        var progressWindow = new OperationProgressWindow(operation, this);
        progressWindow.Show();
        try
        {
            await RunOperationAsync(operation, work);
        }
        finally
        {
            progressWindow.Close();
        }
    }

    private async Task RunOperationAsync(
        DownloadOperation operation, Func<DownloadOperation, CancellationToken, Task> work)
    {
        try
        {
            await work(operation, operation.Cancellation.Token);
            operation.Stage = "Завершено";
            operation.IsComplete = true;
            SetStatus($"{operation.Name}: завершено.");
            RefreshInstancesList();
            RefreshMods();
            RefreshDiagnostics();
        }
        catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested)
        {
            operation.Stage = "Отменено";
            operation.IsComplete = true;
            SetStatus($"{operation.Name}: отменено.");
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            operation.Error = exception.Message;
            operation.Stage = "Ошибка";
            operation.IsComplete = true;
            SetStatus($"{operation.Name}: {exception.Message}", true);
        }
        catch (OperationCanceledException)
        {
            operation.Error = "Операция прервана по тайм-ауту или сетевой ошибке.";
            operation.Stage = "Ошибка";
            operation.IsComplete = true;
            SetStatus(operation.Error, true);
        }
        finally
        {
            if (operation.InstanceId is not null)
                _busyInstances.Remove(operation.InstanceId);
        }
    }

    private void ActiveProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || SelectedProfile is not { } profile)
            return;
        _store.Data.ActiveProfileId = profile.Id;
        UpdateProfileSummary();
        UpdateHome();
        PersistState();
    }

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfilesList.SelectedItem is LauncherProfile profile)
            OfflineNameInput.Text = profile.Type == AccountType.Offline ? profile.Name : string.Empty;
    }

    private void SetActiveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not LauncherProfile profile)
        {
            ShowError("Сначала выберите профиль.");
            return;
        }
        ActiveProfileCombo.SelectedItem = profile;
    }

    private void CreateOfflineProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = OfflineNameInput.Text.Trim();
        if (!OfflineIdentity.IsValidName(name))
        {
            ShowError("Имя Offline-профиля должно содержать от 3 до 16 латинских букв, цифр или подчёркиваний.");
            return;
        }
        if (_store.Data.Profiles.Any(profile => profile.Type == AccountType.Offline &&
            string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            ShowError("Offline-профиль с таким именем уже существует.");
            return;
        }
        var created = new LauncherProfile
        {
            Name = name,
            Type = AccountType.Offline,
            Uuid = OfflineIdentity.CreateUuid(name)
        };
        _store.Data.Profiles.Add(created);
        RefreshProfileViews();
        ActiveProfileCombo.SelectedItem = created;
        ProfilesList.SelectedItem = created;
        PersistState();
        SetStatus($"Создан Offline-профиль {name}. Он не является аккаунтом Microsoft и не подтверждает лицензию.");
    }

    private void RenameOfflineProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not LauncherProfile profile || profile.Type != AccountType.Offline)
        {
            ShowError("Выберите Offline-профиль для переименования.");
            return;
        }
        var newName = OfflineNameInput.Text.Trim();
        if (!OfflineIdentity.IsValidName(newName))
        {
            ShowError("Имя Offline-профиля должно содержать от 3 до 16 латинских букв, цифр или подчёркиваний.");
            return;
        }
        if (_store.Data.Profiles.Any(other => other.Id != profile.Id && other.Type == AccountType.Offline &&
            string.Equals(other.Name, newName, StringComparison.OrdinalIgnoreCase)))
        {
            ShowError("Offline-профиль с таким именем уже существует.");
            return;
        }
        if (ThemedDialog.Show(this,
            "Переименование изменит offline UUID. Сохранения и инвентарь игрока в мирах могут отображаться как данные другого игрока. Продолжить?",
            "Переименовать профиль", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        profile.Name = newName;
        profile.Uuid = OfflineIdentity.CreateUuid(newName);
        RefreshProfileViews();
        ProfilesList.SelectedItem = profile;
        ActiveProfileCombo.SelectedItem = profile;
        PersistState();
        SetStatus($"Offline-профиль переименован в {newName}.");
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not LauncherProfile profile)
        {
            ShowError("Сначала выберите профиль.");
            return;
        }
        if (ThemedDialog.Show(this,
            $"Вы действительно хотите удалить локальный профиль «{profile.Name}»? Игровые файлы и миры не удаляются.",
            "Удалить профиль", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        _store.Data.Profiles.Remove(profile);
        if (_store.Data.ActiveProfileId == profile.Id)
            _store.Data.ActiveProfileId = _store.Data.Profiles.FirstOrDefault()?.Id;
        RefreshProfileViews();
        PersistState();
    }

    private async void RefreshVersions_Click(object sender, RoutedEventArgs e) =>
        await RefreshVersionsAsync(showErrors: true);

    private async void CreateInstance_Click(object sender, RoutedEventArgs e)
    {
        await RunUiActionAsync(async () =>
        {
            var name = InstanceNameInput.Text.Trim();
            var version = VersionComboBox.SelectedItem as string;
            var installFabric = AutoInstallFabricCheckBox.IsChecked == true;
            if (string.IsNullOrWhiteSpace(name) || name.Length > 48 ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidOperationException("Введите название экземпляра длиной 1–48 символов без недопустимых символов пути.");
            if (_store.Data.Instances.Any(instance =>
                string.Equals(instance.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Экземпляр с таким названием уже существует.");
            if (string.IsNullOrWhiteSpace(version))
                throw new InvalidOperationException("Сначала загрузите список версий Minecraft и выберите версию.");
            var id = Guid.NewGuid().ToString("N");
            var instance = new GameInstance
            {
                Id = id,
                Name = name,
                MinecraftVersion = version,
                Loader = "Vanilla",
                RootPath = Path.Combine(AppPaths.Instances, id)
            };
            Directory.CreateDirectory(instance.RootPath);
            _store.Data.Instances.Add(instance);
            _store.Data.ActiveInstanceId = id;
            InstanceNameInput.Clear();
            RefreshInstancesList();
            InstancesList.SelectedItem = _store.Data.Instances.First(item => item.Id == id);
            PersistState();
            var operationName = installFabric
                ? $"Установка Minecraft и Fabric для {name}"
                : $"Установка Minecraft для {name}";
            await QueueOperation(operationName, instance, async (operation, token) =>
            {
                await _minecraft.InstallVanillaAsync(instance, operation, token);
                if (installFabric)
                    await _minecraft.InstallFabricAsync(instance, "latest", operation, token);
                PersistState();
            }, showProgressWindow: true);
        });
    }

    private void InstancesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
            return;
        UpdateSelectedInstance(SelectedInstance);
        UpdateHome();
    }

    private void FavoritesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || FavoritesList.SelectedItem is not GameInstance favorite)
            return;
        _initializing = true;
        InstancesList.SelectedItem = _store.Data.Instances.FirstOrDefault(item => item.Id == favorite.Id);
        _initializing = false;
        UpdateSelectedInstance(SelectedInstance);
    }

    private void InstanceSearchInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing)
            return;
        RefreshInstancesList();
    }

    private void ToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedInstance is not { } instance)
            return;
        if (PreventChangeWhileBusy(instance))
            return;
        instance.IsFavorite = !instance.IsFavorite;
        RefreshInstancesList();
        InstancesList.SelectedItem = _store.Data.Instances.FirstOrDefault(item => item.Id == instance.Id);
        PersistState();
        UpdateHome();
    }

    private void OpenInstances_Click(object sender, RoutedEventArgs e) => NavigationTabs.SelectedIndex = 1;

    private async void LaunchLastButton_Click(object sender, RoutedEventArgs e)
    {
        var last = _store.Data.Instances.FirstOrDefault(instance => instance.Id == _store.Data.LastInstanceId);
        if (last is null)
        {
            ShowError("Сначала выберите и запустите экземпляр Minecraft.");
            return;
        }
        await LaunchAsync(last);
    }

    private async void LaunchFavorite_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is GameInstance instance)
            await LaunchAsync(instance);
    }

    private async void LaunchSelected_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedInstance is { } instance)
            await LaunchAsync(instance);
        else
            ShowError("Сначала выберите экземпляр.");
    }

    private async Task LaunchAsync(GameInstance instance)
    {
        await RunUiActionAsync(async () =>
        {
            if (SelectedProfile is not { } profile)
                throw new InvalidOperationException("Выберите активный профиль на верхней панели.");
            MinecraftService.ValidateLaunchSettings(instance);
            if (instance.Settings.MaximumMemoryMb >= GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024) * 0.75)
            {
                if (ThemedDialog.Show(this,
                    "Выделенный объём памяти высок относительно доступной памяти Windows. Игра может испытывать нехватку ресурсов. Продолжить?",
                    "Предупреждение о памяти", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
            }
            await QueueOperation($"Запуск {instance.Name}", instance,
                (operation, token) => LaunchAndRecordAsync(instance, profile, operation, token));
        });
    }

    private async Task LaunchAndRecordAsync(
        GameInstance instance, LauncherProfile profile, DownloadOperation operation, CancellationToken cancellationToken)
    {
        var processId = await _minecraft.LaunchAsync(instance, profile, operation, cancellationToken);
        _store.Data.LastInstanceId = instance.Id;
        _store.Data.ActiveInstanceId = instance.Id;
        PersistState();
        UpdateHome();
        if (instance.Settings.CloseLauncherAfterStart)
            Dispatcher.Invoke(Close);
        else
            SetStatus($"Minecraft запущен (PID {processId}).");
    }

    private async void DeleteInstance_Click(object sender, RoutedEventArgs e)
    {
        var instance = (sender as FrameworkElement)?.Tag as GameInstance ?? SelectedInstance;
        if (instance is null)
            return;
        if (PreventChangeWhileBusy(instance))
            return;
        await RunUiActionAsync(async () =>
        {
            var root = Path.GetFullPath(instance.RootPath);
            var instancesRoot = Path.GetFullPath(AppPaths.Instances);
            if (!Guid.TryParseExact(instance.Id, "N", out _) ||
                !string.Equals(Path.GetDirectoryName(root), instancesRoot, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(root), instance.Id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Папка экземпляра не является отдельной папкой внутри хранилища MaruLauncher.");
            if (Directory.Exists(root))
            {
                EnsureNoReparsePoints(root);
                if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Папка экземпляра является ссылкой; рекурсивное удаление отменено.");
            }
            if (ThemedDialog.Show(this,
                $"Удалить экземпляр «{instance.Name}» и все файлы внутри его папки, включая миры?\n\n{root}\n\nВнешний каталог игры, если он задан отдельно, не удаляется. Это действие нельзя отменить.",
                "Удалить экземпляр и файлы", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            if (Directory.Exists(root))
                await Task.Run(() => Directory.Delete(root, true));
            _store.Data.Instances.Remove(instance);
            if (_store.Data.LastInstanceId == instance.Id)
                _store.Data.LastInstanceId = null;
            if (_store.Data.ActiveInstanceId == instance.Id)
                _store.Data.ActiveInstanceId = null;
            RefreshInstancesList();
            PersistState();
        });
    }

    private static void EnsureNoReparsePoints(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("В папке экземпляра обнаружена файловая ссылка. Удалите или проверьте её отдельно.");
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(path);
            }
        }
    }

    private async void InstallFabric_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedInstance is not { } instance)
        {
            ShowError("Сначала выберите экземпляр.");
            return;
        }
        if (PreventChangeWhileBusy(instance))
            return;
        var fabricVersion = FabricVersionInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(fabricVersion))
        {
            ShowError("Укажите Fabric Loader version или оставьте значение latest.");
            return;
        }
        await QueueOperation($"Установка Fabric для {instance.Name}", instance,
            async (operation, token) =>
            {
                await _minecraft.InstallFabricAsync(instance, fabricVersion, operation, token);
                PersistState();
            }, showProgressWindow: true);
    }

    private async void InstallFabricApi_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedInstance is not { } instance)
        {
            ShowError("Сначала выберите экземпляр.");
            return;
        }
        await QueueOperation($"Установка Fabric API для {instance.Name}", instance,
            (operation, token) => _minecraft.InstallFabricApiAsync(instance, operation, token),
            showProgressWindow: true);
    }

    private async void OpenGameDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedInstance is not { } instance)
            return;
        await RunUiActionAsync(() =>
        {
            var directory = MinecraftService.GetGameDirectory(instance);
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            return Task.CompletedTask;
        });
    }

    private async void OpenModsDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedInstance is not { } instance)
            return;
        await RunUiActionAsync(() =>
        {
            var directory = Path.Combine(MinecraftService.GetGameDirectory(instance), "mods");
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            return Task.CompletedTask;
        });
    }

    private async void AddMod_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedInstance is not { } instance)
        {
            ShowError("Сначала выберите экземпляр.");
            return;
        }
        if (PreventChangeWhileBusy(instance))
            return;
        var dialog = new OpenFileDialog
        {
            Title = "Добавить JAR-мод (источник выбран пользователем)",
            Filter = "Minecraft mod JAR (*.jar)|*.jar",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true)
            return;
        await RunUiActionAsync(async () =>
        {
            var directory = Path.Combine(MinecraftService.GetGameDirectory(instance), "mods");
            Directory.CreateDirectory(directory);
            foreach (var source in dialog.FileNames)
            {
                var destination = Path.Combine(directory, Path.GetFileName(source));
                var overwrite = File.Exists(destination);
                if (overwrite &&
                    ThemedDialog.Show(this, $"Файл {Path.GetFileName(destination)} уже существует. Заменить его?",
                        "Подтвердить замену", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    continue;
                await Task.Run(() => File.Copy(source, destination, overwrite));
            }
            RefreshMods();
            SetStatus($"Моды добавлены в {directory}. JAR-файлы могут содержать исполняемый код.");
        });
    }

    private async void RemoveMod_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedInstance is not { } instance || ModsList.SelectedItem is not string filename)
        {
            ShowError("Выберите мод JAR в списке.");
            return;
        }
        if (PreventChangeWhileBusy(instance))
            return;
        var directory = Path.GetFullPath(Path.Combine(MinecraftService.GetGameDirectory(instance), "mods"));
        var path = Path.GetFullPath(Path.Combine(directory, filename));
        if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            ShowError("Недопустимый путь к файлу мода.");
            return;
        }
        if (ThemedDialog.Show(this, $"Удалить файл {filename} из папки модов?", "Удалить мод",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        await RunUiActionAsync(() =>
        {
            File.Delete(path);
            RefreshMods();
            SetStatus($"Удалён файл {filename}.");
            return Task.CompletedTask;
        });
    }

    private async void CheckBuild_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedInstance is not { } instance)
        {
            ShowError("Сначала выберите экземпляр.");
            return;
        }
        if (PreventChangeWhileBusy(instance))
            return;
        await RunUiActionAsync(async () =>
        {
            SetStatus($"Проверка сборки {instance.Name}…");
            var issues = await Task.Run(() => InstanceTools.CheckBuild(instance));
            BuildIssuesList.ItemsSource = issues;
            var errors = issues.Count(issue => issue.Severity == IssueSeverity.Error);
            var warnings = issues.Count(issue => issue.Severity == IssueSeverity.Warning);
            SetStatus($"Проверка завершена: ошибок {errors}, предупреждений {warnings}, сообщений {issues.Count - errors - warnings}.");
        });
    }

    private async void ImportModpack_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedInstance is not { } instance)
        {
            ShowError("Сначала выберите экземпляр.");
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = "Выбрать modpack",
            Filter = "Modpack (*.mrpack;*.zip)|*.mrpack;*.zip",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
            return;
        await RunUiActionAsync(async () =>
        {
            var preview = _modpacks.Preview(dialog.FileName, instance);
            var files = string.Join(Environment.NewLine, preview.Files.Take(18));
            if (preview.Files.Count > 18)
                files += $"{Environment.NewLine}… и ещё {preview.Files.Count - 18} файлов";
            var sources = preview.Hosts.Count == 0 ? "Только файлы из выбранного архива."
                : $"Источники загрузки: {string.Join(", ", preview.Hosts)}";
            var mismatch = preview.MinecraftVersion.Length == 0 ? string.Empty
                : $"\nMinecraft: {preview.MinecraftVersion}; загрузчик: {preview.Loader}.";
            var overwrite = preview.ExistingFiles.Count > 0
                ? $"\nСовпадающих существующих файлов, которые будут заменены: {preview.ExistingFiles.Count}."
                : "\nСуществующие файлы не будут заменены.";
            var confirm = ThemedDialog.Show(this,
                $"Импортировать «{preview.Name}»?{mismatch}{overwrite}\n\n{sources}\n\nФайлы модов могут исполнять код внутри Minecraft. Ничего не запускается во время импорта.\n\nФайлы пакета:\n{files}",
                "Подтверждение импорта modpack", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
                return;
            await QueueOperation($"Импорт modpack в {instance.Name}", instance,
                (operation, token) => _modpacks.ImportAsync(instance, dialog.FileName,
                    overwriteExisting: true, operation, token));
        });
    }

    private async void ExportModpack_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedInstance is not { } instance)
        {
            ShowError("Сначала выберите экземпляр.");
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "Экспортировать modpack",
            Filter = "MaruLauncher modpack (*.zip)|*.zip",
            FileName = $"{instance.Name}.zip",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true)
            return;
        var includeConfig = ThemedDialog.Show(this,
            "Включить конфигурации и ресурспаки? Миры, скриншоты, токены и пароли не включаются.",
            "Состав modpack", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (includeConfig == MessageBoxResult.Cancel)
            return;
        await QueueOperation($"Экспорт modpack {instance.Name}", instance,
            (_, token) => Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                _modpacks.Export(instance, dialog.FileName, includeConfig == MessageBoxResult.Yes);
            }, token));
    }

    private void CancelDownload_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DownloadOperation operation || operation.IsComplete)
            return;
        if (operation.Stage.Contains("Установка Fabric", StringComparison.OrdinalIgnoreCase) ||
            operation.Stage == "Запуск Minecraft")
        {
            ShowError("Эту операцию нельзя безопасно отменить на текущем этапе.");
            return;
        }
        operation.Cancellation.Cancel();
    }

    private async void RetryDownload_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DownloadOperation operation ||
            operation.Error is null || operation.RetryAction is null)
            return;
        await operation.RetryAction(CancellationToken.None);
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || ThemeComboBox.SelectedValue is not ThemeChoice choice)
            return;
        _store.Data.Settings.Theme = choice;
        ((App)Application.Current).ApplyTheme(choice);
        PersistState();
    }

    private void SaveInstanceSettings_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedInstance is not { } instance)
        {
            ShowError("Сначала выберите экземпляр.");
            return;
        }
        if (PreventChangeWhileBusy(instance))
            return;
        if (!int.TryParse(MemoryInput.Text, out var memory) ||
            !int.TryParse(WidthInput.Text, out var width) ||
            !int.TryParse(HeightInput.Text, out var height))
        {
            ShowError("ОЗУ и разрешение должны быть целыми числами.");
            return;
        }
        var settings = new InstanceSettings
        {
            MaximumMemoryMb = memory,
            WindowWidth = width,
            WindowHeight = height,
            JavaPath = JavaPathInput.Text.Trim(),
            GameDirectory = GameDirectoryInput.Text.Trim(),
            JvmArguments = JvmArgumentsInput.Text.Trim(),
            Fullscreen = FullscreenCheckBox.IsChecked == true,
            CloseLauncherAfterStart = CloseLauncherCheckBox.IsChecked == true,
            FabricVersion = instance.Settings.FabricVersion,
            FabricProfileId = instance.Settings.FabricProfileId
        };
        var old = instance.Settings;
        instance.Settings = settings;
        try
        {
            MinecraftService.ValidateLaunchSettings(instance);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or
            NotSupportedException or IOException)
        {
            instance.Settings = old;
            ShowError(exception.Message);
            return;
        }
        PersistState();
        UpdateSelectedInstance(instance);
        SetStatus($"Настройки экземпляра {instance.Name} сохранены.");
    }

    private void BrowseJava_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выбрать Java",
            Filter = "Java executable (javaw.exe;java.exe)|javaw.exe;java.exe|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
            JavaPathInput.Text = dialog.FileName;
    }

    private async void CreateBackup_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedInstance is not { } instance)
        {
            ShowError("Сначала выберите экземпляр.");
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "Сохранить резервную копию",
            Filter = "ZIP-архив (*.zip)|*.zip",
            FileName = $"{instance.Name}-backup-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true)
            return;
        var selection = new BackupSelection(
            BackupModsCheck.IsChecked == true,
            BackupConfigCheck.IsChecked == true,
            BackupResourcePacksCheck.IsChecked == true,
            BackupScreenshotsCheck.IsChecked == true,
            BackupWorldsCheck.IsChecked == true,
            BackupSettingsCheck.IsChecked == true);
        await QueueOperation($"Резервная копия {instance.Name}", instance,
            (operation, token) => Task.Run(() =>
            {
                operation.Stage = "Создание архива";
                InstanceTools.CreateBackup(instance, dialog.FileName, selection, token);
            }, token));
    }

    private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedInstance is not { } instance)
        {
            ShowError("Сначала выберите экземпляр.");
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = "Выбрать резервную копию",
            Filter = "ZIP-архив (*.zip)|*.zip",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
            return;
        await RunUiActionAsync(async () =>
        {
            var contents = InstanceTools.InspectBackup(dialog.FileName, instance);
            var files = string.Join(Environment.NewLine, contents.Files.Take(24));
            if (contents.Files.Count > 24)
                files += $"{Environment.NewLine}… и ещё {contents.Files.Count - 24} файлов";
            var overwriteText = contents.ExistingFiles.Count == 0
                ? "Совпадающих существующих файлов нет."
                : $"Будут заменены существующие файлы: {contents.ExistingFiles.Count}.";
            var confirm = ThemedDialog.Show(this,
                $"Восстановить архив экземпляра «{contents.InstanceName}» ({contents.MinecraftVersion}, {contents.Loader}) в «{instance.Name}»?\n{overwriteText}\n" +
                $"Настройки в архиве: {(contents.Settings is null ? "нет" : "есть")}; секретные значения скрыты: {contents.RedactedFiles.Count}; большие/нечитаемые текстовые файлы пропущены: {contents.OmittedTextFiles.Count}.\n" +
                "Из настроек восстанавливаются только объём ОЗУ, разрешение, полноэкранный режим и автозакрытие. Путь Java, каталог игры, профиль Fabric и аргументы JVM остаются без изменений из соображений безопасности.\n\n" +
                $"Файлы:\n{files}\n\nПродолжить?",
                "Проверка резервной копии", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
                return;
            if (contents.Settings is not null)
            {
                var probe = new GameInstance
                {
                    Name = instance.Name,
                    MinecraftVersion = instance.MinecraftVersion,
                    Loader = instance.Loader,
                    RootPath = instance.RootPath,
                    Settings = contents.Settings
                };
                MinecraftService.ValidateLaunchSettings(probe);
            }
            InstanceSettings? restoredSettings = null;
            await QueueOperation($"Восстановление {instance.Name}", instance, async (operation, token) =>
            {
                await Dispatcher.InvokeAsync(() => operation.Stage = "Проверка и восстановление архива");
                restoredSettings = await Task.Run(() =>
                {
                    return InstanceTools.RestoreBackup(dialog.FileName, instance,
                        overwriteExisting: contents.ExistingFiles.Count > 0, token);
                }, token);
            });
            if (restoredSettings is not null)
            {
                restoredSettings.JavaPath = instance.Settings.JavaPath;
                restoredSettings.GameDirectory = instance.Settings.GameDirectory;
                restoredSettings.FabricProfileId = instance.Settings.FabricProfileId;
                restoredSettings.FabricVersion = instance.Settings.FabricVersion;
                restoredSettings.JvmArguments = instance.Settings.JvmArguments;
                instance.Settings = restoredSettings;
                PersistState();
                UpdateInstanceSettings();
            }
        });
    }

    private void RefreshDiagnostics_Click(object sender, RoutedEventArgs e) => RefreshDiagnostics();

    private void RefreshDiagnostics()
    {
        try
        {
            RefreshDiagnosticsCore();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            Win32Exception or ArgumentException or InvalidDataException)
        {
            DiagnosticsText.Text = $"Не удалось собрать диагностику:\n{exception.Message}";
            SetStatus($"Диагностика недоступна: {exception.Message}", true);
        }
    }

    private void RefreshDiagnosticsCore()
    {
        var instance = SelectedInstance;
        if (instance is null)
        {
            DiagnosticsText.Text = "Выберите экземпляр для просмотра диагностики.";
            return;
        }
        var gameDirectory = MinecraftService.GetGameDirectory(instance);
        var logPath = Path.Combine(gameDirectory, "logs", "latest.log");
        var javaProcessLogPath = Path.Combine(instance.RootPath, "diagnostics", "java-process.log");
        var resultPath = Path.Combine(instance.RootPath, "diagnostics", "latest-launch.json");
        LaunchResult? result = null;
        if (File.Exists(resultPath))
        {
            try
            {
                result = JsonSerializer.Deserialize<LaunchResult>(File.ReadAllText(resultPath), JsonOptions);
            }
            catch (JsonException)
            {
                SetStatus("Файл результата последнего запуска повреждён.", true);
            }
        }

        var javaPath = result?.JavaPath ?? instance.Settings.JavaPath;
        var javaVersion = "не определена";
        if (string.IsNullOrWhiteSpace(javaPath))
            javaPath = MinecraftService.FindBundledJavaPath() ?? "автоматический поиск";
        if (File.Exists(javaPath))
        {
            try
            {
                javaVersion = FileVersionInfo.GetVersionInfo(javaPath).ProductVersion ?? "версия неизвестна";
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or
                UnauthorizedAccessException)
            {
                javaVersion = $"не удалось прочитать: {exception.Message}";
            }
        }

        var builder = new StringBuilder();
        builder.AppendLine("MaruLauncher — диагностический отчёт");
        builder.AppendLine($"Экземпляр: {instance.Name}");
        builder.AppendLine($"Minecraft: {instance.MinecraftVersion}");
        builder.AppendLine($"Загрузчик: {instance.Loader}");
        builder.AppendLine($"Java: {javaVersion}");
        builder.AppendLine($"Java path: {javaPath}");
        builder.AppendLine($"Максимум ОЗУ игре: {instance.Settings.MaximumMemoryMb} МБ");
        builder.AppendLine($"Доступно физической памяти Windows: {GetAvailableMemoryMb()} МБ");
        builder.AppendLine($"Каталог игры: {gameDirectory}");
        builder.AppendLine($"Путь к логу: {logPath}");
        builder.AppendLine($"Вывод Java: {javaProcessLogPath}");
        builder.AppendLine(result is null
            ? "Последний результат запуска: отсутствует"
            : $"Последний запуск: {result.StartedAt:g}; код завершения: {result.ExitCode?.ToString() ?? "игра ещё запущена"}");
        if (result?.ExitCode is int exitCode && exitCode != 0)
            builder.AppendLine("Игра завершилась с ненулевым кодом; этот факт сам по себе не определяет причину сбоя.");
        builder.AppendLine();
        builder.AppendLine("Последний вывод Java:");
        builder.AppendLine("---------------------");
        if (File.Exists(javaProcessLogPath))
        {
            try
            {
                foreach (var line in ReadLastLines(javaProcessLogPath, 100))
                    builder.AppendLine(SanitizeDiagnosticText(line));
            }
            catch (IOException exception)
            {
                builder.AppendLine($"Не удалось прочитать вывод Java: {exception.Message}");
            }
            catch (UnauthorizedAccessException exception)
            {
                builder.AppendLine($"Нет доступа к выводу Java: {exception.Message}");
            }
        }
        else
        {
            builder.AppendLine("Данные появятся после следующей попытки запуска.");
        }
        builder.AppendLine();
        builder.AppendLine("Последние строки latest.log (секреты отфильтрованы):");
        builder.AppendLine("--------------------------------------------------");
        if (File.Exists(logPath))
        {
            try
            {
                foreach (var line in ReadLastLines(logPath, 160))
                    builder.AppendLine(SanitizeDiagnosticText(line));
            }
            catch (IOException exception)
            {
                builder.AppendLine($"Не удалось прочитать лог: {exception.Message}");
            }
            catch (UnauthorizedAccessException exception)
            {
                builder.AppendLine($"Нет доступа к логу: {exception.Message}");
            }
        }
        else
        {
            builder.AppendLine("Файл latest.log пока отсутствует.");
        }
        builder.AppendLine();
        builder.AppendLine("Диагностические подсказки не устанавливают причину сбоя с полной уверенностью.");
        DiagnosticsText.Text = builder.ToString();
        UpdateHome();
    }

    private async void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedInstance is not { } instance)
            return;
        await RunUiActionAsync(() =>
        {
            var path = Path.Combine(MinecraftService.GetGameDirectory(instance), "logs", "latest.log");
            if (!File.Exists(path))
                throw new FileNotFoundException("Лог отсутствует. Запустите Minecraft, чтобы создать его.");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return Task.CompletedTask;
        });
    }

    private async void OpenLogsDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedInstance is not { } instance)
            return;
        await RunUiActionAsync(() =>
        {
            var path = Path.Combine(MinecraftService.GetGameDirectory(instance), "logs");
            if (!Directory.Exists(path))
                throw new DirectoryNotFoundException("Каталог логов ещё не создан.");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return Task.CompletedTask;
        });
    }

    private async void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DiagnosticsText.Text))
            RefreshDiagnostics();
        if (ThemedDialog.Show(this,
            "Перед копированием проверьте отчёт. Даже после фильтрации токенов логи могут содержать имена файлов, пути, имена игроков и другие персональные сведения. Копировать отчёт в буфер обмена?",
            "Персональные сведения в отчёте", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        await RunUiActionAsync(() =>
        {
            Clipboard.SetText(DiagnosticsText.Text);
            SetStatus("Диагностический отчёт скопирован после предупреждения о персональных сведениях.");
            return Task.CompletedTask;
        });
    }

    private static IEnumerable<string> ReadLastLines(string path, int count)
    {
        var buffer = new Queue<string>(count);
        foreach (var line in File.ReadLines(path))
        {
            if (buffer.Count == count)
                buffer.Dequeue();
            buffer.Enqueue(line);
        }
        return buffer;
    }

    private static string SanitizeDiagnosticText(string text)
    {
        text = Regex.Replace(text,
            @"(?is)<(?<tag>[A-Za-z0-9_.:-]*?(?:token|password|passwd|secret|api[_-]?key|authorization|credential)[A-Za-z0-9_.:-]*)\b[^>]*>.*?</\k<tag>\s*>",
            match => $"<{match.Groups["tag"].Value}>[REDACTED]</{match.Groups["tag"].Value}>");
        text = Regex.Replace(text,
            @"(?i)(?<key>[\w.""'-]*(?:token|password|passwd|secret|api[_-]?key|authorization|credential)[\w.""'-]*\s*[:=]\s*[""']?)(?<value>[^\s""',}]+)",
            "${key}[REDACTED]");
        text = Regex.Replace(text, @"(?i)\bBearer\s+[A-Za-z0-9._~+/\-=]+", "Bearer [REDACTED]");
        text = Regex.Replace(text, @"XBL3\.0\s+x=[^;\s]+;[^\s""']+", "XBL3.0 [REDACTED]");
        text = Regex.Replace(text,
            @"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b", "[REDACTED JWT]");
        return text;
    }

    private static ulong GetAvailableMemoryMb()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return status.AvailablePhysical / (1024 * 1024);
    }

    private sealed record ThemeOption(ThemeChoice Choice, string Label);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
}
