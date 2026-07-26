using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.Themes;
using ExpressPackingMonitoring.UI;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace ExpressPackingMonitoring;

public partial class PrintWorkstationWindow : Window
{
    public sealed class MobileBackupStatusItem
    {
        public string DeviceId { get; init; } = "";
        public string DisplayText { get; init; } = "";
        public bool IsOnline { get; init; }
    }

    private enum StatusVisual
    {
        Neutral,
        Success,
        Warning,
        Error
    }

    private AppConfig _config;
    private readonly bool _openPlaybackOnStartup;
    private readonly bool _requestLanAccessOnStartup;
    private readonly NoCameraWorkstationHost _host;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly WindowCloseBehaviorController _closeBehaviorController;
    private readonly DispatcherTimer _deviceRefreshTimer;
    private StatisticsWindow? _statisticsWindow;
    private PlaybackWindow? _playbackWindow;
    private bool _loaded;
    private bool _exitRequestedFromTray;
    private bool _deploymentSetupPersisted;
    private bool _testOrderSending;
    public ObservableCollection<MobileBackupStatusItem> MobileBackupDeviceStatuses { get; } = [];

    public PrintWorkstationWindow(
        AppConfig config,
        bool openPlaybackOnStartup = true,
        bool requestLanAccessOnStartup = true,
        bool enableCloseBehaviorPrompt = true)
    {
        InitializeComponent();
        _config = config;
        _openPlaybackOnStartup = openPlaybackOnStartup;
        _requestLanAccessOnStartup = requestLanAccessOnStartup;
        _host = new NoCameraWorkstationHost(config);
        _host.MobileAppUpdateAvailable += OnMobileAppUpdateAvailable;
        _host.MobileBackupStatusChanged += OnMobileBackupStatusChanged;
        _closeBehaviorController = new WindowCloseBehaviorController(
            this,
            RequestExitFromTray,
            enableCloseBehaviorPrompt);
        _deviceRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _deviceRefreshTimer.Tick += (_, _) => RefreshDeviceSummary();
        Loaded += Window_Loaded;
        Closing += Window_Closing;
        Closed += (_, _) =>
        {
            _closeBehaviorController.Dispose();
            _deviceRefreshTimer.Stop();
            _lifetimeCts.Cancel();
            _host.MobileBackupStatusChanged -= OnMobileBackupStatusChanged;
            _host.Dispose();
            _lifetimeCts.Dispose();
        };
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        WindowCloseChoice closeChoice = _closeBehaviorController.HandleClose(
            _config,
            bypassPreference: WorkstationNetwork.IsRestartPending || _exitRequestedFromTray);
        _exitRequestedFromTray = false;
        if (closeChoice != WindowCloseChoice.Exit)
        {
            e.Cancel = true;
            return;
        }

        CloseChildWindows();
    }

    private void RequestExitFromTray()
    {
        _exitRequestedFromTray = true;
        Close();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await StartServiceAsync();
    }

    private async Task StartServiceAsync()
    {
        SetControlsEnabled(false);
        SetStatus("正在启动手机录像备份服务", "正在打开录像数据库和本机回放服务");
        try
        {
            await _host.StartAsync(
                requestLanAccess: _requestLanAccessOnStartup,
                cancellationToken: _lifetimeCts.Token);
            RefreshServiceDisplay();
            _deviceRefreshTimer.Start();
            if (_host.IsLanAvailable)
                CompleteDeploymentSetup();
            if (_openPlaybackOnStartup)
                OpenLocalPlayback();
        }
        catch (Exception ex)
        {
            LanAddressTextBlock.Text = ex.Message;
            SetStatus("服务启动失败", ex.Message, StatusVisual.Error);
        }
        finally
        {
            SetControlsEnabled(_host.IsRunning);
            RepairLanButton.IsEnabled = true;
        }
    }

    private void RefreshServiceDisplay()
    {
        StoragePathTextBlock.Text = string.IsNullOrWhiteSpace(_host.StoragePath)
            ? "手机录像存储：暂不可用"
            : $"手机录像存储：{_host.StoragePath}";
        HostIdentityTextBlock.Text = $"{GetHostName()} · {GetDisplayAddress()}";
        RefreshDeviceSummary();
        if (_host.IsLanAvailable)
        {
            LanAddressTextBlock.Text = _host.LanAccessUrl;
            SetStatus("手机录像备份服务已启动", "手机可备份录像到本机，本机和局域网设备均可回放", StatusVisual.Success);
        }
        else
        {
            LanAddressTextBlock.Text = string.IsNullOrWhiteSpace(_host.ErrorMessage)
                ? "仅本机可用"
                : _host.ErrorMessage;
            SetStatus("手机录像备份服务已启动 · 仅本机可用",
                "本机回放不受影响；需要手机备份或局域网回放时，请点击“修复局域网”",
                StatusVisual.Error);
        }

        ConnectPhoneButton.IsEnabled = _host.IsLanAvailable;
        CopyLanAddressButton.IsEnabled = _host.IsLanAvailable;
        SendTestOrderButton.IsEnabled = _host.IsLanAvailable && !_testOrderSending;
        RepairLanButton.Visibility = _host.IsLanAvailable ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetControlsEnabled(bool enabled)
    {
        OpenWebButton.IsEnabled = enabled;
        StatisticsButton.IsEnabled = _host.HasDatabase;
        PlaybackButton.IsEnabled = _host.HasDatabase;
        SettingsButton.IsEnabled = true;
        ConnectPhoneButton.IsEnabled = enabled && _host.IsLanAvailable;
        CopyLanAddressButton.IsEnabled = enabled && _host.IsLanAvailable;
        SendTestOrderButton.IsEnabled = enabled && _host.IsLanAvailable && !_testOrderSending;
    }

    private void SetStatus(string title, string hint, StatusVisual visual = StatusVisual.Neutral)
    {
        StatusTextBlock.Text = title;
        StatusHintTextBlock.Text = hint;

        string iconKey = visual switch
        {
            StatusVisual.Success => "FluentCheckIcon",
            StatusVisual.Warning => "FluentWarningIcon",
            StatusVisual.Error => "FluentDismissIcon",
            _ => "FluentHourglassIcon"
        };
        string brushKey = visual switch
        {
            StatusVisual.Success => "AccentGreen",
            StatusVisual.Warning => "AccentOrange",
            StatusVisual.Error => "AccentRed",
            _ => "AccentBlue"
        };
        if (TryFindResource(iconKey) is Geometry icon)
            StatusIconPath.Data = icon;
        if (TryFindResource(brushKey) is Brush brush)
            StatusIconPath.Fill = brush;
    }

    private void OpenLocalPlayback()
    {
        if (!WorkstationNetwork.TryOpenUrl(_host.LocalPlaybackUrl, out string error))
            SetStatus("打开本机回放失败", error, StatusVisual.Error);
    }

    private void OpenWeb_Click(object sender, RoutedEventArgs e) => OpenLocalPlayback();

    private void OpenStatistics_Click(object sender, RoutedEventArgs e)
    {
        if (_statisticsWindow is { IsLoaded: true })
        {
            _statisticsWindow.Activate();
            return;
        }

        _statisticsWindow = new StatisticsWindow(_host.Database) { Owner = this };
        _statisticsWindow.Closed += (_, _) => _statisticsWindow = null;
        _statisticsWindow.Show();
    }

    private void OpenPlayback_Click(object sender, RoutedEventArgs e)
    {
        if (_playbackWindow is { IsLoaded: true })
        {
            _playbackWindow.Activate();
            return;
        }

        _playbackWindow = new PlaybackWindow(_host.StoragePath, _host.Database, showDeletedVideos: true)
        {
            Owner = this
        };
        _playbackWindow.Closed += (_, _) => _playbackWindow = null;
        _playbackWindow.Show();
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        AppConfig clonedConfig =
            JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(_config)) ?? new AppConfig();
        var context = new SettingsContext
        {
            Capabilities = SettingsCapabilities.ForPreset(DeploymentPresets.MobileBackupHost),
            ApplyAsync = ApplySettingsAsync,
            ConnectionAddressProvider = () => _host.IsLanAvailable ? _host.LanAccessUrl : _host.LocalPlaybackUrl,
            ShowMobileConnection = ShowMobileConnection,
            CopyMobileConnectionUrl = CopyMobileConnectionUrl,
            OpenUserscriptGuide = OpenUserscriptGuide
        };
        (double diskUsagePercent, string diskUsageText) = GetDiskUsage(_host.StoragePath);
        var window = new SettingsWindow(
            context,
            clonedConfig,
            diskUsagePercent,
            diskUsageText)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void ConnectPhone_Click(object sender, RoutedEventArgs e)
    {
        ShowMobileConnection(this);
    }

    private void ShowMobileConnection(Window owner)
    {
        var dialog = new MobileConnectionWindow(
            _host.LanAccessUrl,
            _config.RequireWebAccessKey,
            _host.IsLanAvailable ? "" : "当前仅本机可用，请先修复局域网",
            canOpenSettings: false)
        {
            Owner = owner
        };
        dialog.ShowDialog();
    }

    private void CopyLanAddress_Click(object sender, RoutedEventArgs e)
    {
        CopyMobileConnectionUrl();
    }

    private void CopyMobileConnectionUrl()
    {
        string address = _host.IsLanAvailable ? _host.LanAccessUrl : _host.LocalPlaybackUrl;
        if (string.IsNullOrWhiteSpace(address)) return;
        try
        {
            Clipboard.SetDataObject(address, true);
            SetStatus(
                _host.IsLanAvailable ? "已复制局域网地址" : "已复制本机回放地址",
                "访问地址包含手机配对密钥，请勿转发给无关人员",
                StatusVisual.Success);
        }
        catch (Exception ex)
        {
            SetStatus("复制局域网地址失败", ex.Message, StatusVisual.Error);
        }
    }

    private void RefreshDeviceSummary()
    {
        UserscriptTargetStatus userscriptStatus = UserscriptTargetState.GetStatus(
            _config,
            _host.GetRecordingDevices(includeKnown: true));
        UserscriptStatusTextBlock.Text = userscriptStatus.StatusText;
        InstallUserscriptButtonText.Text = userscriptStatus.ButtonText;
        InstallUserscriptButton.IsEnabled = userscriptStatus.CurrentSignature.Length > 0;

        MobileBackupDeviceStatuses.Clear();
        if (!_host.HasDatabase)
        {
            TodayBackupCountTextBlock.Text = "0";
            TotalBackupCountTextBlock.Text = "0";
            AddEmptyDeviceStatus();
            return;
        }

        MobileBackupOverview overview = _host.Database.GetMobileBackupOverview(DateTime.Today);
        foreach (MobileBackupStatusItem status in BuildMobileBackupStatuses(
            overview.DeviceCounts,
            _host.GetRecordingDevices()))
        {
            MobileBackupDeviceStatuses.Add(status);
        }

        TodayBackupCountTextBlock.Text = overview.TodayCount.ToString();
        TotalBackupCountTextBlock.Text = overview.TotalCount.ToString();
    }

    internal static IReadOnlyList<MobileBackupStatusItem> BuildMobileBackupStatuses(
        IEnumerable<MobileBackupDailyCount> counts,
        IEnumerable<RecordingDeviceInfo> devices)
    {
        var statuses = counts.ToDictionary(
            item => item.DeviceId,
            item => (
                Name: string.IsNullOrWhiteSpace(item.DeviceName)
                    ? GetFallbackDeviceName(item.DeviceId)
                    : item.DeviceName,
                Count: item.VideoCount,
                Online: false),
            StringComparer.OrdinalIgnoreCase);

        foreach (RecordingDeviceInfo device in devices
            .Where(device => device.Online
                && string.Equals(device.DeviceType, "mobile", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(device.NodeId)))
        {
            statuses.TryGetValue(device.NodeId, out var existing);
            statuses[device.NodeId] = (
                string.IsNullOrWhiteSpace(device.NodeName)
                    ? existing.Name ?? GetFallbackDeviceName(device.NodeId)
                    : device.NodeName,
                existing.Count,
                true);
        }

        List<MobileBackupStatusItem> result = statuses
            .OrderBy(item => item.Value.Name, StringComparer.CurrentCulture)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .Select(status => new MobileBackupStatusItem
            {
                DeviceId = status.Key,
                DisplayText = $"{status.Value.Name} · 今日备份 {status.Value.Count} 个",
                IsOnline = status.Value.Online
            })
            .ToList();
        if (result.Count == 0)
        {
            result.Add(new MobileBackupStatusItem
            {
                DisplayText = "暂无手机设备在线",
                IsOnline = false
            });
        }
        return result;
    }

    private void AddEmptyDeviceStatus()
    {
        MobileBackupDeviceStatuses.Add(new MobileBackupStatusItem
        {
            DisplayText = "暂无手机设备在线",
            IsOnline = false
        });
    }

    private string GetHostName() =>
        string.IsNullOrWhiteSpace(_config.NodeName) ? Environment.MachineName : _config.NodeName;

    private string GetDisplayAddress()
    {
        string address = _host.IsLanAvailable ? _host.LanAccessUrl : "";
        return Uri.TryCreate(address, UriKind.Absolute, out Uri? uri)
            ? uri.Authority
            : "局域网服务未就绪";
    }

    private static string GetFallbackDeviceName(string _)
    {
        return "手机";
    }

    private void OnMobileBackupStatusChanged()
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (IsLoaded)
                RefreshDeviceSummary();
        });
    }

    private void OnMobileAppUpdateAvailable(MobileAppUpdateAvailableInfo update)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (IsLoaded)
                MobileAppUpdatePrompt.Show(this, update);
        });
    }

    private async void RepairLan_Click(object sender, RoutedEventArgs e)
    {
        RepairLanButton.IsEnabled = false;
        SetStatus("正在修复局域网", "Windows 可能会请求管理员授权");
        bool repaired = await _host.RepairLanAccessAsync(_lifetimeCts.Token);
        RefreshServiceDisplay();
        if (repaired && _host.IsLanAvailable)
            CompleteDeploymentSetup();
        RepairLanButton.IsEnabled = true;
        if (!repaired && !_host.IsRunning)
            SetStatus("局域网修复失败", _host.ErrorMessage, StatusVisual.Error);
    }

    private async Task<bool> ApplySettingsAsync(AppConfig nextConfig)
    {
        AppConfig previousConfig = _config;
        AppConfig.NormalizeAfterLoad(nextConfig);

        if (!string.Equals(
                previousConfig.DeploymentPreset,
                nextConfig.DeploymentPreset,
                StringComparison.OrdinalIgnoreCase))
        {
            if (!TrySaveAndActivateConfig(previousConfig, nextConfig, out string error))
            {
                SetStatus("设置保存失败", error, StatusVisual.Error);
                MessageBox.Show(this, $"配置保存失败：{error}", "设置", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            WorkstationNetwork.AskRestart(this);
            return true;
        }

        _host.UpdateConfig(nextConfig);
        SetControlsEnabled(false);
        SetStatus("正在应用设置", "正在重启本机回放和手机备份服务");
        try
        {
            _playbackWindow?.Close();
            await _host.StartAsync(_requestLanAccessOnStartup, _lifetimeCts.Token);
            if (!TrySaveAndActivateConfig(previousConfig, nextConfig, out string error))
                throw new InvalidOperationException($"配置保存失败：{error}");

            RefreshServiceDisplay();
            SetControlsEnabled(true);
            return true;
        }
        catch (Exception ex)
        {
            _host.UpdateConfig(previousConfig);
            try
            {
                await _host.StartAsync(_requestLanAccessOnStartup, _lifetimeCts.Token);
                RefreshServiceDisplay();
                SetControlsEnabled(true);
            }
            catch
            {
                SetControlsEnabled(false);
            }

            RepairLanButton.IsEnabled = true;
            SetStatus("服务重启失败", ex.Message, StatusVisual.Error);
            MessageBox.Show(this, ex.Message, "服务重启失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private bool TrySaveAndActivateConfig(
        AppConfig previousConfig,
        AppConfig nextConfig,
        out string error)
    {
        if (!WorkstationConfigStore.TrySave(nextConfig, out error))
            return false;

        _config = nextConfig;
        _host.UpdateConfig(nextConfig);
        AutoStartService.Apply(nextConfig.AutoStartOnBoot);
        if (!string.Equals(previousConfig.Theme, nextConfig.Theme, StringComparison.Ordinal) &&
            Enum.TryParse(nextConfig.Theme, out AppTheme theme))
        {
            ThemeManager.ApplyTheme(theme);
        }

        return true;
    }

    private static (double Percent, string Text) GetDiskUsage(string storagePath)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(storagePath));
            if (string.IsNullOrWhiteSpace(root)) return (0, "暂不可用");
            var drive = new DriveInfo(root);
            if (drive.TotalSize <= 0) return (0, "暂不可用");
            double percent = Math.Clamp(
                (drive.TotalSize - drive.AvailableFreeSpace) * 100d / drive.TotalSize,
                0,
                100);
            double usedGB = (drive.TotalSize - drive.AvailableFreeSpace) / 1024d / 1024d / 1024d;
            double totalGB = drive.TotalSize / 1024d / 1024d / 1024d;
            return (percent, $"{usedGB:F1} / {totalGB:F1} GB");
        }
        catch
        {
            return (0, "暂不可用");
        }
    }

    private void CloseChildWindows()
    {
        try { _statisticsWindow?.Close(); } catch { }
        try { _playbackWindow?.Close(); } catch { }
        _statisticsWindow = null;
        _playbackWindow = null;
    }

    private void InstallTool_Click(object sender, RoutedEventArgs e)
    {
        OpenUserscriptGuide();
    }

    private void OpenUserscriptGuide()
    {
        if (!_host.IsLanAvailable)
        {
            SetStatus(
                "局域网服务尚未就绪",
                "请先修复局域网服务，再生成包含全部录像设备地址的快递助手脚本",
                StatusVisual.Error);
            return;
        }

        if (!UserscriptGuideNavigation.TryOpen(_host.LanAccessUrl, out string error))
        {
            SetStatus("打开快递助手联动安装向导失败", error, StatusVisual.Error);
            return;
        }
        UserscriptTargetState.MarkGuideOpened(
            _config,
            _host.GetRecordingDevices(includeKnown: true));
        RefreshDeviceSummary();
        SetStatus("已打开订单联动安装向导", "脚本会写入最近使用过的全部录像设备", StatusVisual.Success);
    }

    private async void SendTestOrder_Click(object sender, RoutedEventArgs e)
    {
        if (_testOrderSending)
            return;

        _testOrderSending = true;
        SendTestOrderButton.IsEnabled = false;
        SendTestOrderButtonText.Text = "正在发送";
        SetStatus("正在发送测试订单", "测试订单将同时发送给当前在线的全部录像设备");
        try
        {
            WorkstationNetwork.TestOrderBroadcastResult result =
                await WorkstationNetwork.SendTestOrderToRecordingDevicesAsync(_host.LanAccessUrl);
            StatusVisual visual = !result.HasTargets
                ? StatusVisual.Error
                : result.FailureCount == 0
                    ? StatusVisual.Success
                    : StatusVisual.Warning;
            SetStatus(
                result.HasTargets
                    ? $"测试完成：成功 {result.SuccessCount} 台，失败 {result.FailureCount} 台"
                    : "测试订单发送失败",
                WorkstationNetwork.FormatTestOrderBroadcastResult(result),
                visual);
            MessageBox.Show(
                this,
                WorkstationNetwork.FormatTestOrderBroadcastResult(result),
                "发送测试订单",
                MessageBoxButton.OK,
                visual == StatusVisual.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally
        {
            _testOrderSending = false;
            SendTestOrderButton.IsEnabled = _host.IsLanAvailable;
            SendTestOrderButtonText.Text = "发送测试订单";
        }
    }

    private void SwitchWorkstation_Click(object sender, RoutedEventArgs e)
    {
        var window = new WorkstationSelectionWindow { Owner = this };
        if (window.ShowDialog() != true || string.IsNullOrWhiteSpace(window.SelectedPreset))
            return;

        if (string.Equals(
                DeploymentPresets.MobileBackupHost,
                window.SelectedPreset,
                StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("当前已选择接收手机录像", "无需重启或更改", StatusVisual.Success);
            return;
        }

        if (!WorkstationConfigStore.TryUpdate(
                config =>
                {
                    config.DeploymentPreset = window.SelectedPreset;
                    config.WorkstationRole = window.SelectedPreset == DeploymentPresets.RecordingHost
                        ? WorkstationRoles.CameraMonitor
                        : "";
                    config.EnableWebServer = DeploymentCapabilities
                        .ForPreset(window.SelectedPreset)
                        .CanRunWebServer;
                },
                out AppConfig savedConfig,
                out string error))
        {
            SetStatus("用途保存失败", error, StatusVisual.Error);
            return;
        }

        _config.DeploymentPreset = savedConfig.DeploymentPreset;
        _config.WorkstationRole = savedConfig.WorkstationRole;
        _config.EnableWebServer = savedConfig.EnableWebServer;
        WorkstationNetwork.AskRestart(this);
    }

    private void CompleteDeploymentSetup()
    {
        if (_deploymentSetupPersisted)
            return;

        if (!WorkstationConfigStore.TryUpdate(
                config =>
                {
                    config.DeploymentPreset = DeploymentPresets.MobileBackupHost;
                    config.DeploymentSchemaVersion = DeploymentPresets.CurrentSchemaVersion;
                    config.WorkstationRole = WorkstationRoles.PrintStation;
                    config.EnableWebServer = true;
                    AppConfig.MarkDeploymentSetupCompleted(config);
                },
                out AppConfig savedConfig,
                out _))
        {
            return;
        }

        _config.DeploymentPreset = savedConfig.DeploymentPreset;
        _config.DeploymentSchemaVersion = savedConfig.DeploymentSchemaVersion;
        _config.WorkstationRole = savedConfig.WorkstationRole;
        _config.EnableWebServer = savedConfig.EnableWebServer;
        _config.FirstUseWizardCompleted = savedConfig.FirstUseWizardCompleted;
        _config.DeploymentSetupVersion = savedConfig.DeploymentSetupVersion;
        _config.RecordingSetupVersion = savedConfig.RecordingSetupVersion;
        _deploymentSetupPersisted = true;
    }
}
