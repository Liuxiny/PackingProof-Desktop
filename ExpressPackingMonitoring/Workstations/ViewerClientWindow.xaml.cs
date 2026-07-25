using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.UI;

namespace ExpressPackingMonitoring;

public partial class ViewerClientWindow : Window
{
    private readonly AppConfig _config;
    private readonly DispatcherTimer _onlineTimer;
    private CancellationTokenSource? _searchCancellation;
    private PackingProofNodeInfo? _boundHost;
    private bool _deploymentSetupPersisted;

    public ViewerClientWindow(AppConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        InitializeComponent();
        _onlineTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _onlineTimer.Tick += async (_, _) => await RefreshBoundHostAsync();
        Loaded += ViewerClientWindow_Loaded;
        Closed += (_, _) =>
        {
            _onlineTimer.Stop();
            _searchCancellation?.Cancel();
            _searchCancellation?.Dispose();
        };
    }

    private async void ViewerClientWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshBoundHostAsync();
        if (_boundHost == null)
            await SearchHostsAsync();
        _onlineTimer.Start();
    }

    private async Task RefreshBoundHostAsync()
    {
        string address = _config.LastKnownHostAddress;
        if (string.IsNullOrWhiteSpace(address))
        {
            SetOffline("尚未绑定主机");
            return;
        }

        PackingProofNodeInfo? node = await WorkstationNetwork.GetNodeInfoAsync(address);
        if (node == null
            || (!string.IsNullOrWhiteSpace(_config.LastKnownHostNodeId)
                && !string.Equals(node.NodeId, _config.LastKnownHostNodeId, StringComparison.OrdinalIgnoreCase)))
        {
            SetOffline("主机离线或身份已变化");
            return;
        }

        _boundHost = node;
        CompleteDeploymentSetup(node);
        IReadOnlyList<RecordingDeviceInfo> devices =
            await WorkstationNetwork.GetRecordingDevicesAsync(node.Address);
        HostNameText.Text = node.NodeName;
        HostAddressText.Text = node.Address;
        OnlineStatusText.Text = "在线";
        OnlineStatusText.Foreground = TryFindResource("AccentGreen") as Brush ?? Brushes.Green;
        CapabilitiesText.Text = node.CapabilitySummary;
        RecorderCountText.Text = devices.Count.ToString();
        OpenWebButton.IsEnabled = true;
    }

    private void SetOffline(string status)
    {
        _boundHost = null;
        HostNameText.Text = string.IsNullOrWhiteSpace(_config.LastKnownHostNodeId) ? "尚未绑定" : "已绑定主机";
        HostAddressText.Text = string.IsNullOrWhiteSpace(_config.LastKnownHostAddress)
            ? "—"
            : _config.LastKnownHostAddress;
        OnlineStatusText.Text = status;
        OnlineStatusText.Foreground = TryFindResource("TextSecondary") as Brush ?? Brushes.Gray;
        CapabilitiesText.Text = "—";
        RecorderCountText.Text = "0";
        OpenWebButton.IsEnabled = false;
    }

    private async Task SearchHostsAsync()
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        DiscoveryPanel.Visibility = Visibility.Visible;
        HostsList.ItemsSource = null;
        SearchStatusText.Text = "正在搜索局域网中的 PackingProof 主机";
        var progress = new Progress<string>(message => SearchStatusText.Text = message);
        try
        {
            IReadOnlyList<PackingProofNodeInfo> hosts = await WorkstationNetwork.FindHostsAsync(
                _config.LastKnownHostAddress,
                _config.WebServerPort,
                progress,
                _searchCancellation.Token);
            HostsList.ItemsSource = hosts;
            SearchStatusText.Text = hosts.Count == 0
                ? "没有发现主机，可以检查网络后重新搜索或手动输入地址"
                : $"找到 {hosts.Count} 台 PackingProof 主机，请选择要绑定的主机";
        }
        catch (OperationCanceledException)
        {
            SearchStatusText.Text = "搜索已取消";
        }
    }

    private async Task BindHostAsync(PackingProofNodeInfo node)
    {
        if (!node.IsValidHost)
        {
            SearchStatusText.Text = "该地址不是有效的 PackingProof 主机";
            return;
        }

        if (!WorkstationConfigStore.TryUpdate(
                config =>
                {
                    config.DeploymentPreset = DeploymentPresets.ViewerClient;
                    config.DeploymentSchemaVersion = DeploymentPresets.CurrentSchemaVersion;
                    config.WorkstationRole = "";
                    config.EnableWebServer = false;
                    config.LastKnownHostNodeId = node.NodeId;
                    config.LastKnownHostAddress = node.Address;
                    AppConfig.MarkDeploymentSetupCompleted(config);
                },
                out AppConfig saved,
                out string error))
        {
            SearchStatusText.Text = $"保存主机绑定失败：{error}";
            return;
        }

        _config.LastKnownHostNodeId = saved.LastKnownHostNodeId;
        _config.LastKnownHostAddress = saved.LastKnownHostAddress;
        _config.FirstUseWizardCompleted = saved.FirstUseWizardCompleted;
        _config.DeploymentSetupVersion = saved.DeploymentSetupVersion;
        _deploymentSetupPersisted = true;
        _boundHost = node;
        DiscoveryPanel.Visibility = Visibility.Collapsed;
        await RefreshBoundHostAsync();
    }

    private void OpenWeb_Click(object sender, RoutedEventArgs e)
    {
        string address = _boundHost?.Address ?? _config.LastKnownHostAddress;
        if (!WorkstationNetwork.TryOpenUrl(address, out string error))
            MessageBox.Show(this, error, "打开录像网页失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private async void SearchHosts_Click(object sender, RoutedEventArgs e) => await SearchHostsAsync();

    private async void ChangeHost_Click(object sender, RoutedEventArgs e) => await SearchHostsAsync();

    private void InstallUserscript_Click(object sender, RoutedEventArgs e)
    {
        string address = _boundHost?.Address ?? _config.LastKnownHostAddress;
        if (string.IsNullOrWhiteSpace(address))
        {
            MessageBox.Show(this, "请先搜索并绑定 PackingProof 主机", "安装快递助手联动",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string url = $"{address.TrimEnd('/')}/kuaidizs-install-guide?refresh={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        if (!WorkstationNetwork.TryOpenUrl(url, out string error))
            MessageBox.Show(this, error, "安装快递助手联动失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void SwitchPurpose_Click(object sender, RoutedEventArgs e)
    {
        var selector = new WorkstationSelectionWindow { Owner = this };
        if (selector.ShowDialog() != true || string.IsNullOrWhiteSpace(selector.SelectedPreset))
            return;

        if (string.Equals(
                DeploymentPresets.ViewerClient,
                selector.SelectedPreset,
                StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "当前已经是连接已有主机用途", "切换用途",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.Equals(
                DeploymentPresets.RecordingHost,
                selector.SelectedPreset,
                StringComparison.OrdinalIgnoreCase))
        {
            if (!FirstUseSetupWizardWindow.TryConfigureRecordingHost(
                    _config,
                    this,
                    out AppConfig recordingConfig))
            {
                return;
            }

            if (!WorkstationConfigStore.TrySave(recordingConfig, out string recordingError))
            {
                MessageBox.Show(this, $"用途保存失败：{recordingError}", "切换用途",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            WorkstationNetwork.AskRestart(this);
            return;
        }

        if (!WorkstationConfigStore.TryUpdate(
                config =>
                {
                    config.DeploymentPreset = selector.SelectedPreset;
                    config.DeploymentSchemaVersion = DeploymentPresets.CurrentSchemaVersion;
                    config.WorkstationRole = selector.SelectedPreset == DeploymentPresets.RecordingHost
                        ? WorkstationRoles.CameraMonitor
                        : WorkstationRoles.PrintStation;
                    config.EnableWebServer = DeploymentCapabilities
                        .ForPreset(selector.SelectedPreset)
                        .CanRunWebServer;
                },
                out AppConfig savedConfig,
                out string error))
        {
            MessageBox.Show(this, $"用途保存失败：{error}", "切换用途",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _config.DeploymentPreset = savedConfig.DeploymentPreset;
        _config.DeploymentSchemaVersion = savedConfig.DeploymentSchemaVersion;
        _config.WorkstationRole = savedConfig.WorkstationRole;
        _config.EnableWebServer = savedConfig.EnableWebServer;
        WorkstationNetwork.AskRestart(this);
    }

    private void CompleteDeploymentSetup(PackingProofNodeInfo node)
    {
        if (_deploymentSetupPersisted)
            return;

        if (!WorkstationConfigStore.TryUpdate(
                config =>
                {
                    config.DeploymentPreset = DeploymentPresets.ViewerClient;
                    config.DeploymentSchemaVersion = DeploymentPresets.CurrentSchemaVersion;
                    config.WorkstationRole = "";
                    config.EnableWebServer = false;
                    config.LastKnownHostNodeId = node.NodeId;
                    config.LastKnownHostAddress = node.Address;
                    AppConfig.MarkDeploymentSetupCompleted(config);
                },
                out AppConfig saved,
                out _))
        {
            return;
        }

        _config.DeploymentPreset = saved.DeploymentPreset;
        _config.DeploymentSchemaVersion = saved.DeploymentSchemaVersion;
        _config.WorkstationRole = saved.WorkstationRole;
        _config.EnableWebServer = saved.EnableWebServer;
        _config.LastKnownHostNodeId = saved.LastKnownHostNodeId;
        _config.LastKnownHostAddress = saved.LastKnownHostAddress;
        _config.FirstUseWizardCompleted = saved.FirstUseWizardCompleted;
        _config.DeploymentSetupVersion = saved.DeploymentSetupVersion;
        _deploymentSetupPersisted = true;
    }

    private async void BindSelected_Click(object sender, RoutedEventArgs e)
    {
        if (HostsList.SelectedItem is PackingProofNodeInfo node)
            await BindHostAsync(node);
    }

    private async void HostsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HostsList.SelectedItem is PackingProofNodeInfo node)
            await BindHostAsync(node);
    }

    private async void BindManual_Click(object sender, RoutedEventArgs e)
    {
        string address = ManualAddressTextBox.Text;
        SearchStatusText.Text = "正在验证手动输入的主机地址";
        PackingProofNodeInfo? node = await WorkstationNetwork.GetNodeInfoAsync(address);
        if (node == null)
        {
            SearchStatusText.Text = "该地址未返回合法的 PackingProof 主机身份";
            return;
        }

        await BindHostAsync(node);
    }
}
