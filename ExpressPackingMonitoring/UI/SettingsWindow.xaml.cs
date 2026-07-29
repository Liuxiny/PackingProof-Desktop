#nullable disable
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Audio;
using System;
using System.Windows;
using System.Collections.Generic;
using ExpressPackingMonitoring.ViewModels;
using AForge.Video.DirectShow;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Input;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using ExpressPackingMonitoring.Localization;
using System.Windows.Media.Imaging;
using ExpressPackingMonitoring.Services;
using NAudio.CoreAudioApi;
using System.Text.Json;

namespace ExpressPackingMonitoring.UI
{
    public class CameraInfo { public int Index { get; set; } public string Name { get; set; } public string Moniker { get; set; } public override string ToString() => Name; }
    public class ResOption { public string Name { get; set; } public int Width { get; set; } public int Height { get; set; } public override string ToString() => Name; }
    public class MicInfo
    {
        public string Name { get; set; }
        public string Moniker { get; set; }
        public override string ToString() => Name;
    }
    public class FpsOption { public int Fps { get; set; } public string Label { get; set; } public override string ToString() => Label; }
    public class EdgeVoiceOption { public string ShortName { get; set; } public string DisplayName { get; set; } public override string ToString() => DisplayName; }

    public sealed class CqpToQualitySliderConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double cqp = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return Math.Clamp((51 - cqp) * 2.0, 0, 100);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double quality = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
            int cqp = (int)Math.Round(51 - Math.Clamp(quality, 0, 100) / 2.0);
            return Math.Clamp(cqp, 1, 51);
        }
    }

    public sealed class IntSliderValueConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return 0d;
            return System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double sliderValue = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return (int)Math.Round(sliderValue);
        }
    }

    public sealed class VideoQualityLabelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double quality = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (quality < 34) return "更省空间";
            if (quality < 67) return "标准（推荐）";
            return "更清晰";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }

    public sealed class AnyTrueConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            return values.Any(value => value is bool boolean && boolean);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            targetTypes.Select(_ => Binding.DoNothing).ToArray();
    }

    public partial class SettingsWindow : Window
    {
        public SettingsContext Context { get; }
        public SettingsCapabilities Capabilities => Context.Capabilities;
        public string ConnectionAddress => Context.ConnectionAddressProvider?.Invoke() ?? "尚未准备";
        public AppConfig Config { get; set; }
        public double CurrentDiskUsagePercent { get; set; }
        public string CurrentDiskUsageText { get; set; }
        public int SuggestedRetentionDays { get; set; }
        public string StorageRetentionWarningText { get; set; }
        public string AppVersion { get; } = ExpressPackingMonitoring.Config.AppVersion.Current;
        public string AppBuildDate { get; } = ExpressPackingMonitoring.Config.AppVersion.BuildDateText;
        public ImageSource AppIconImage { get; } = GetLargestAppIconImage();
        public List<EdgeVoiceOption> EdgeVoiceOptions { get; } = new()
        {
            new EdgeVoiceOption { ShortName = "zh-CN-XiaoxiaoNeural", DisplayName = "晓晓 - 女声" },
            new EdgeVoiceOption { ShortName = "zh-CN-XiaoyiNeural", DisplayName = "晓伊 - 女声" },
            new EdgeVoiceOption { ShortName = "zh-CN-YunjianNeural", DisplayName = "云健 - 男声" },
            new EdgeVoiceOption { ShortName = "zh-CN-YunxiNeural", DisplayName = "云希 - 男声" },
            new EdgeVoiceOption { ShortName = "zh-CN-YunxiaNeural", DisplayName = "云夏 - 男声" },
            new EdgeVoiceOption { ShortName = "zh-CN-YunyangNeural", DisplayName = "云扬 - 男声" },
            new EdgeVoiceOption { ShortName = "zh-CN-liaoning-XiaobeiNeural", DisplayName = "辽宁晓北 - 女声" },
            new EdgeVoiceOption { ShortName = "zh-CN-shaanxi-XiaoniNeural", DisplayName = "陕西晓妮 - 女声" },
            new EdgeVoiceOption { ShortName = "zh-HK-HiuGaaiNeural", DisplayName = "粤语 HiuGaai - 女声" },
            new EdgeVoiceOption { ShortName = "zh-HK-WanLungNeural", DisplayName = "粤语 WanLung - 男声" },
            new EdgeVoiceOption { ShortName = "zh-TW-HsiaoChenNeural", DisplayName = "台湾晓臻 - 女声" },
            new EdgeVoiceOption { ShortName = "zh-TW-YunJheNeural", DisplayName = "台湾云哲 - 男声" },
            new EdgeVoiceOption { ShortName = "en-US-JennyNeural", DisplayName = "Jenny - Female (US)" },
            new EdgeVoiceOption { ShortName = "en-US-AriaNeural", DisplayName = "Aria - Female (US)" },
            new EdgeVoiceOption { ShortName = "en-US-GuyNeural", DisplayName = "Guy - Male (US)" },
            new EdgeVoiceOption { ShortName = "en-US-DavisNeural", DisplayName = "Davis - Male (US)" }
        };

        private string _originalTheme;
        private string _originalLanguage;
        private bool _isRecording;
        private bool _isLoadingDevices;
        private bool _isSyncingVoiceEngine;
        private bool _isSyncingScannerModes;
        private CancellationTokenSource _performanceCts;
        private RecordingPerformanceResult _lastPerformanceResult;

        public SettingsWindow(MainViewModel mainVM, AppConfig clonedConfig, double diskUsagePercent, string diskUsageText, bool isRecording = false)
            : this(SettingsContext.ForCameraWorkstation(mainVM), clonedConfig, diskUsagePercent, diskUsageText, isRecording)
        {
        }

        public SettingsWindow(SettingsContext context, AppConfig clonedConfig, double diskUsagePercent, string diskUsageText, bool isRecording = false)
        {
            Context = context ?? throw new ArgumentNullException(nameof(context));
            _originalTheme = clonedConfig.Theme;
            _originalLanguage = clonedConfig.Language;
            _isRecording = isRecording;
            Config = clonedConfig;
            AppConfig.NormalizeAfterLoad(Config);
            InitializeComponent();

            CurrentDiskUsagePercent = diskUsagePercent;
            CurrentDiskUsageText = diskUsageText;
            SuggestedRetentionDays = Math.Max(StorageRetentionPolicy.MinimumRetentionDays,
                Context.SuggestedRetentionDaysProvider?.Invoke() ?? Config.MaxRetentionDays);
            StorageRetentionWarningText = Context.StorageRetentionWarningProvider?.Invoke() ?? "";

            this.DataContext = this;
            if (Capabilities.IsRecordingDevice)
                SyncVoiceEngineComboBoxFromConfig();

            if (Capabilities.CanRecordPcVideo)
            {
                // GPU编码器使用缓存，可立即加载
                LoadVideoEncoders();
                if (Config.ZoomScale < 1.2 || Config.ZoomScale > 4.0) Config.ZoomScale = 1.5;
            }

            if (Capabilities.CanConfigureStorage)
            {
                EnsurePrimaryStorageLocationExists();
                // 如果没有数据项，构造1个默认项，UI DataGrid 绑定后自动显示
                if (Config.StorageLocations.Count == 0)
                {
                    Config.StorageLocations.Add(new StorageLocation());
                }
                SortStorageLocationsByPriority();
                RefreshStoragePriorities();
                UpdateStorageButtonStates();
            }

            // 从注册表读取实际的开机自启动状态
            Config.AutoStartOnBoot = AutoStartService.IsEnabled();

            // 窗口加载后异步枚举设备，避免阻塞UI线程
            this.Loaded += SettingsWindow_Loaded;
        }

        private void GlobalKeyboardCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_isSyncingScannerModes) return;

            try
            {
                _isSyncingScannerModes = true;
                Config.EnableGlobalKeyboard = true;
                Config.EnableScannerAutoSubmit = false;
                if (ScannerAutoSubmitCheckBox != null)
                {
                    ScannerAutoSubmitCheckBox.IsChecked = false;
                }
            }
            finally
            {
                _isSyncingScannerModes = false;
            }
        }

        private void ScannerAutoSubmitCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_isSyncingScannerModes) return;

            try
            {
                _isSyncingScannerModes = true;
                Config.EnableScannerAutoSubmit = true;
                Config.EnableGlobalKeyboard = false;
                if (GlobalKeyboardCheckBox != null)
                {
                    GlobalKeyboardCheckBox.IsChecked = false;
                }
            }
            finally
            {
                _isSyncingScannerModes = false;
            }
        }

        private void SyncScannerModeControlsFromConfig()
        {
            if (GlobalKeyboardCheckBox == null || ScannerAutoSubmitCheckBox == null)
                return;

            try
            {
                _isSyncingScannerModes = true;
                GlobalKeyboardCheckBox.IsChecked = Config.EnableGlobalKeyboard;
                ScannerAutoSubmitCheckBox.IsChecked = Config.EnableScannerAutoSubmit;
            }
            finally
            {
                _isSyncingScannerModes = false;
            }
        }

        private void EnsurePrimaryStorageLocationExists()
        {
            if (Config.StorageLocations == null) Config.StorageLocations = new List<StorageLocation>();
            if (Config.StorageLocations.Count == 0)
            {
                Config.StorageLocations.Add(new StorageLocation());
            }
        }

        private async void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (Capabilities.CanUseCamera || Capabilities.CanRecordAudio)
            {
                _isLoadingDevices = true;
                try
                {
                    await LoadAllDevicesAsync();
                }
                finally
                {
                    _isLoadingDevices = false;
                }

                // 加载断句关键词到文本框
                if (Config.TtsBreakWords != null && Config.TtsBreakWords.Count > 0)
                    TtsBreakWordsTextBox.Text = string.Join("\n", Config.TtsBreakWords);

                if (_isRecording)
                {
                    CameraComboBox.IsEnabled = false;
                    ResComboBox.IsEnabled = false;
                    FpsComboBox.IsEnabled = false;
                    CameraComboBox.ToolTip = "录制中不可修改，停止录制后再更改";
                    ResComboBox.ToolTip = "录制中不可修改，停止录制后再更改";
                    FpsComboBox.ToolTip = "录制中不可修改，停止录制后再更改";
                }
            }
        }

        private void ThemeComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ThemeComboBox.SelectedItem is System.Windows.Controls.ComboBoxItem item && item.Tag != null)
            {
                string t = item.Tag.ToString();
                if (Enum.TryParse<ExpressPackingMonitoring.Themes.AppTheme>(t, out var themeEnum))
                {
                    ExpressPackingMonitoring.Themes.ThemeManager.ApplyTheme(themeEnum);
                }
            }
        }

        /// <summary>
        /// 在独立 STA 线程上运行 DirectShow COM 操作，避免与 AForge 摄像头线程冲突。
        /// </summary>
        private static System.Threading.Tasks.Task<T> RunOnStaThread<T>(Func<T> func)
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<T>();
            var thread = new Thread(() =>
            {
                try { tcs.SetResult(func()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return tcs.Task;
        }

        private async System.Threading.Tasks.Task LoadAllDevicesAsync()
        {
            var config = Config;
            var result = await RunOnStaThread(() =>
            {
                var cams = new List<CameraInfo>();
                var micList = new List<MicInfo>();
                var resList = new List<ResOption>();
                var fpsList = new List<int>();

                try
                {
                    var videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                    for (int i = 0; i < videoDevices.Count; i++)
                        cams.Add(new CameraInfo { Index = i, Name = $"[{i}] {videoDevices[i].Name}", Moniker = videoDevices[i].MonikerString });

                    string targetMoniker = config.CameraMonikerString;
                    int targetIndex = -1;
                    if (!string.IsNullOrEmpty(targetMoniker))
                    {
                        for (int i = 0; i < videoDevices.Count; i++)
                        {
                            if (videoDevices[i].MonikerString == targetMoniker)
                            {
                                targetIndex = i;
                                break;
                            }
                        }
                    }

                    if (targetIndex == -1 && config.CameraIndex >= 0 && config.CameraIndex < videoDevices.Count)
                    {
                        targetIndex = config.CameraIndex;
                    }

                    if (targetIndex != -1)
                    {
                        var device = new VideoCaptureDevice(videoDevices[targetIndex].MonikerString);
                        resList = device.VideoCapabilities
                            .Select(c => new { c.FrameSize.Width, c.FrameSize.Height })
                            .Distinct()
                            .OrderByDescending(r => r.Width * r.Height)
                            .Select(r => new ResOption
                            {
                                Name = $"{r.Width}x{r.Height}{GetResLabel(r.Width, r.Height)}",
                                Width = r.Width,
                                Height = r.Height
                            })
                            .ToList();

                        fpsList = device.VideoCapabilities
                            .Select(c => c.AverageFrameRate)
                            .Where(f => f > 0)
                            .Distinct()
                            .OrderBy(f => f)
                            .ToList();
                    }
                }
                catch { }

                try
                {
                    using var enumerator = new MMDeviceEnumerator();
                    var audioDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                    for (int i = 0; i < audioDevices.Count; i++)
                        micList.Add(new MicInfo { Name = audioDevices[i].FriendlyName, Moniker = audioDevices[i].ID });
                }
                catch { }

                return (Cameras: cams, Mics: micList, Resolutions: resList, FpsValues: fpsList);
            });

            // 更新摄像头
            var cameras = result.Cameras;
            if (cameras.Count == 0)
                cameras.Add(new CameraInfo { Index = 0, Name = "[0] 未检测到摄像头" });
            CameraComboBox.ItemsSource = cameras;
            CameraComboBox.SelectedValue = config.CameraIndex;

            // 更新麦克风
            var mics = result.Mics;
            if (mics.Count == 0)
                mics.Add(new MicInfo { Name = "未检测到麦克风" });
            MicComboBox.ItemsSource = mics;
            var firstAvailableMic = mics.FirstOrDefault(IsAvailableMic);
            if (string.IsNullOrEmpty(config.AudioDeviceName) && firstAvailableMic != null)
            {
                config.AudioDeviceName = firstAvailableMic.Name;
                config.AudioDeviceMoniker = firstAvailableMic.Moniker ?? "";
            }
            SelectMicByConfig(mics);

            // 更新分辨率
            var resolutions = result.Resolutions;
            if (resolutions.Count == 0)
            {
                resolutions = new List<ResOption>
                {
                    new ResOption { Name = "720P - 省空间", Width = 1280, Height = 720 },
                    new ResOption { Name = "1080P - 高清", Width = 1920, Height = 1080 },
                    new ResOption { Name = "2K - 超清", Width = 2560, Height = 1440 },
                    new ResOption { Name = "4K - 极清", Width = 3840, Height = 2160 }
                };
            }
            ResComboBox.ItemsSource = resolutions;
            var resMatch = resolutions.FirstOrDefault(r => r.Width == config.FrameWidth && r.Height == config.FrameHeight);
            ResComboBox.SelectedItem = resMatch ?? resolutions.FirstOrDefault();

            // 更新帧率
            var fpsValues = result.FpsValues;
            var fpsCbiList = new List<ComboBoxItem>();
            if (fpsValues.Count == 0)
                fpsValues = new List<int> { 10, 15, 20, 25, 30 };
            foreach (var fps in fpsValues)
                fpsCbiList.Add(new ComboBoxItem { Content = $"{fps} FPS", Tag = fps });
            FpsComboBox.ItemsSource = fpsCbiList;
            var fpsMatch = fpsCbiList.FirstOrDefault(i => (int)i.Tag == config.Fps);
            FpsComboBox.SelectedItem = fpsMatch ?? fpsCbiList.FirstOrDefault();
        }

        private async System.Threading.Tasks.Task LoadCameraCapabilitiesAsync(int cameraIndex, int currentWidth, int currentHeight, int currentFps)
        {
            var result = await RunOnStaThread(() =>
            {
                var resList = new List<ResOption>();
                var fpsList = new List<int>();
                try
                {
                    var videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                    if (cameraIndex >= 0 && cameraIndex < videoDevices.Count)
                    {
                        var device = new VideoCaptureDevice(videoDevices[cameraIndex].MonikerString);
                        resList = device.VideoCapabilities
                            .Select(c => new { c.FrameSize.Width, c.FrameSize.Height })
                            .Distinct()
                            .OrderByDescending(r => r.Width * r.Height)
                            .Select(r => new ResOption
                            {
                                Name = $"{r.Width}x{r.Height}{GetResLabel(r.Width, r.Height)}",
                                Width = r.Width,
                                Height = r.Height
                            })
                            .ToList();

                        fpsList = device.VideoCapabilities
                            .Select(c => c.AverageFrameRate)
                            .Where(f => f > 0)
                            .Distinct()
                            .OrderBy(f => f)
                            .ToList();
                    }
                }
                catch { }
                return (Resolutions: resList, FpsValues: fpsList);
            });

            var resolutions = result.Resolutions;
            if (resolutions.Count == 0)
            {
                resolutions = new List<ResOption>
                {
                    new ResOption { Name = "720P - 省空间", Width = 1280, Height = 720 },
                    new ResOption { Name = "1080P - 高清", Width = 1920, Height = 1080 },
                    new ResOption { Name = "2K - 超清", Width = 2560, Height = 1440 },
                    new ResOption { Name = "4K - 极清", Width = 3840, Height = 2160 }
                };
            }
            ResComboBox.ItemsSource = resolutions;
            var resMatch = resolutions.FirstOrDefault(r => r.Width == currentWidth && r.Height == currentHeight);
            ResComboBox.SelectedItem = resMatch ?? resolutions.FirstOrDefault();

            var fpsValues = result.FpsValues;
            var fpsCbiList = new List<ComboBoxItem>();
            if (fpsValues.Count == 0)
                fpsValues = new List<int> { 10, 15, 20, 25, 30 };
            foreach (var fps in fpsValues)
                fpsCbiList.Add(new ComboBoxItem { Content = $"{fps} FPS", Tag = fps });
            FpsComboBox.ItemsSource = fpsCbiList;
            var fpsMatch = fpsCbiList.FirstOrDefault(i => (int)i.Tag == currentFps);
            FpsComboBox.SelectedItem = fpsMatch ?? fpsCbiList.FirstOrDefault();
        }

        private static string GetResLabel(int w, int h)
        {
            if (w == 1280 && h == 720) return " (720P)";
            if (w == 1920 && h == 1080) return " (1080P)";
            if (w == 2560 && h == 1440) return " (2K)";
            if (w == 3840 && h == 2160) return " (4K)";
            return "";
        }

        private async void CameraComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoadingDevices) return;
            if (CameraComboBox.SelectedItem is CameraInfo cam)
            {
                // 加载该摄像头的独立配置（如果存在）
                int w = Config.FrameWidth;
                int h = Config.FrameHeight;
                int fps = Config.Fps;

                if (!string.IsNullOrEmpty(cam.Moniker) && Config.CameraConfigs.TryGetValue(cam.Moniker, out var settings))
                {
                    w = settings.FrameWidth;
                    h = settings.FrameHeight;
                    fps = settings.Fps;
                    Config.AudioDeviceName = settings.AudioDeviceName ?? "";
                    Config.AudioDeviceMoniker = settings.AudioDeviceMoniker ?? "";
                    Config.AudioSyncOffsetMs = settings.AudioSyncOffsetMs;

                    // 切换麦克风 UI 选中项
                    if (MicComboBox.ItemsSource is List<MicInfo> mics)
                    {
                        SelectMicByConfig(mics);
                    }
                }

                await LoadCameraCapabilitiesAsync(cam.Index, w, h, fps);
            }
        }

        private void LoadVideoEncoders()
        {
            var encoders = MainViewModel.CachedEncoderOptions
                ?? new List<GpuEncoderOption>
                {
                    new GpuEncoderOption { Value = "auto", DisplayName = "自动选择（根据本机实测）" }
                };
            VideoEncoderComboBox.ItemsSource = encoders;
            string normalized = AppConfig.NormalizeVideoEncoder(Config.VideoEncoder);
            var match = encoders.FirstOrDefault(e => e.Value == normalized)
                     ?? encoders.FirstOrDefault(e => e.Value == "auto")
                     ?? encoders.FirstOrDefault();
            VideoEncoderComboBox.SelectedItem = match;
        }

        private static string NormalizeGpuSetting(string setting) => EncodingHelper.NormalizeGpuSetting(setting);

        private void BtnBrowsePath_Click(object sender, RoutedEventArgs e)
        {
            EnsurePrimaryStorageLocationExists();
            var primary = Config.StorageLocations[0];

            string selectedPath = SelectStoragePath(primary.Path);
            if (string.IsNullOrWhiteSpace(selectedPath)) return;

            if (!TryPrepareStoragePath(selectedPath, out string errorMessage))
            {
                AppDialog.ShowMessage(this, $"无法创建或写入目录：\n{selectedPath}\n\n原因：{errorMessage}", "存储错误", AppDialogSeverity.Warning);
                return;
            }

            primary.Path = selectedPath;
        }

        private bool IsPathWritable(string path)
        {
            return TryPrepareStoragePath(path, out _);
        }

        private void BtnAddStorage_Click(object sender, RoutedEventArgs e)
        {
            string selectedPath = SelectStoragePath();
            if (string.IsNullOrWhiteSpace(selectedPath)) return;

            if (Config.StorageLocations.Any(x => AreSameStoragePath(x.Path, selectedPath)))
            {
                AppDialog.ShowMessage(this, "该路径已在列表中。", "提示", AppDialogSeverity.Information);
                return;
            }

            if (!TryPrepareStoragePath(selectedPath, out string errorMessage))
            {
                AppDialog.ShowMessage(this, $"无法创建或写入目录：\n{selectedPath}\n\n原因：{errorMessage}", "存储错误", AppDialogSeverity.Warning);
                return;
            }

            Config.StorageLocations.Add(new StorageLocation
            {
                Path = selectedPath,
                ReserveGB = StorageSpacePolicy.GetMinimumReserveGB(selectedPath),
                Priority = Config.StorageLocations.Count
            });

            RefreshStoragePriorities();
            StorageDataGrid.Items.Refresh();
            StorageDataGrid.SelectedIndex = Config.StorageLocations.Count - 1;
            UpdateStorageButtonStates();
        }

        private string SelectStoragePath(string initialPath = null)
        {
            var dialog = new StoragePathSelectionDialog(initialPath)
            {
                Owner = this
            };

            if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.SelectedPath))
                return "";

            return dialog.SelectedPath;
        }

        public void SelectTab(string header)
        {
            if (SettingsTabs.Items
                .OfType<TabItem>()
                .FirstOrDefault(item => string.Equals(item.Header?.ToString(), header, StringComparison.Ordinal)) is TabItem target)
            {
                SettingsTabs.SelectedItem = target;
            }
        }

        private void UseSuggestedRetention_Click(object sender, RoutedEventArgs e)
        {
            Config.MaxRetentionDays = Math.Max(StorageRetentionPolicy.MinimumRetentionDays, SuggestedRetentionDays);
        }

        private async void RunPerformanceAssessment_Click(object sender, RoutedEventArgs e)
        {
            if (_isRecording || Context.RunPerformanceAssessmentAsync == null) return;
            if (CameraComboBox.SelectedItem is not CameraInfo camera
                || ResComboBox.SelectedItem is not ResOption resolution
                || FpsComboBox.SelectedItem is not ComboBoxItem fpsItem
                || fpsItem.Tag is not int fps
                || VideoEncoderComboBox.SelectedItem is not GpuEncoderOption encoderOption)
            {
                AppDialog.ShowMessage(this, "请先选择摄像头、分辨率、帧率和编码器", "性能检测", AppDialogSeverity.Warning);
                return;
            }

            string encoder = Context.ResolveEncoderForAssessment?.Invoke(encoderOption.Value) ?? encoderOption.Value;
            if (string.IsNullOrWhiteSpace(encoder) || encoder == "auto")
            {
                AppDialog.ShowMessage(this, "没有可用于性能检测的已验证编码器，请先重新检测编码器", "性能检测", AppDialogSeverity.Warning);
                return;
            }

            List<CameraRecordingMode> modes = new VideoCaptureDevice(camera.Moniker).VideoCapabilities
                .Select(capability => new CameraRecordingMode(
                    capability.FrameSize.Width,
                    capability.FrameSize.Height,
                    Math.Max(1, capability.AverageFrameRate)))
                .Distinct()
                .ToList();
            var request = new RecordingPerformanceRequest(
                camera.Moniker,
                resolution.Width,
                resolution.Height,
                fps,
                encoder,
                modes);

            bool paused = false;
            _performanceCts?.Cancel();
            _performanceCts?.Dispose();
            _performanceCts = new CancellationTokenSource();
            PerformanceDetectButton.IsEnabled = false;
            ApplyPerformanceRecommendationButton.Visibility = Visibility.Collapsed;
            PerformanceResultText.Text = "正在进行约 5 秒的真实采集与编码测试…";
            try
            {
                paused = Context.SuspendCameraForSetupWizard?.Invoke() ?? true;
                if (!paused)
                    throw new InvalidOperationException("无法暂停当前摄像头预览");

                RecordingPerformanceResult result = await Context.RunPerformanceAssessmentAsync(request, _performanceCts.Token);
                _lastPerformanceResult = result;
                PerformanceResultText.Text = result.Success
                    ? result.Summary
                    : $"性能检测失败：{result.Error}";
                if (result.Success && !result.MeetsTarget)
                    ApplyPerformanceRecommendationButton.Visibility = Visibility.Visible;
            }
            catch (OperationCanceledException)
            {
                PerformanceResultText.Text = "性能检测已取消";
            }
            catch (Exception ex)
            {
                PerformanceResultText.Text = $"性能检测失败：{ex.Message}";
            }
            finally
            {
                if (paused)
                    Context.ResumeCameraAfterSetupWizard?.Invoke();
                PerformanceDetectButton.IsEnabled = !_isRecording;
            }
        }

        private void ApplyPerformanceRecommendation_Click(object sender, RoutedEventArgs e)
        {
            if (_lastPerformanceResult == null) return;
            Config.FrameWidth = _lastPerformanceResult.RecommendedWidth;
            Config.FrameHeight = _lastPerformanceResult.RecommendedHeight;
            Config.Fps = _lastPerformanceResult.RecommendedFps;
            EncodingHelper.ApplyEncoderSelectionToConfig(Config, _lastPerformanceResult.RecommendedEncoder);

            if (ResComboBox.ItemsSource is IEnumerable<ResOption> resolutions)
                ResComboBox.SelectedItem = resolutions.FirstOrDefault(item =>
                    item.Width == Config.FrameWidth && item.Height == Config.FrameHeight);
            if (FpsComboBox.ItemsSource is IEnumerable<ComboBoxItem> fpsItems)
                FpsComboBox.SelectedItem = fpsItems.FirstOrDefault(item => item.Tag is int value && value == Config.Fps);
            if (VideoEncoderComboBox.ItemsSource is IEnumerable<GpuEncoderOption> encoders)
                VideoEncoderComboBox.SelectedItem = encoders.FirstOrDefault(item => item.Value == Config.VideoEncoder);
            PerformanceResultText.Text += "；已填入推荐配置，请保存设置";
            ApplyPerformanceRecommendationButton.Visibility = Visibility.Collapsed;
        }

        private void SelectLocalRecordingBuffer_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new StoragePathSelectionDialog(Config.LocalRecordingBufferPath) { Owner = this };
            if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.SelectedPath)) return;
            if (!StorageLocationResolver.IsValidLocalBufferPath(dialog.SelectedPath, out string reason))
            {
                AppDialog.ShowMessage(this, reason, "本地缓冲目录无效", AppDialogSeverity.Warning);
                return;
            }
            Config.LocalRecordingBufferPath = dialog.SelectedPath;
            LocalRecordingBufferTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        }

        private bool TryPrepareStoragePath(string path, out string errorMessage)
        {
            try
            {
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                string testFile = Path.Combine(path, ".write_test_" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
                errorMessage = "";
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private void BtnRemoveStorage_Click(object sender, RoutedEventArgs e)
        {
            if (StorageDataGrid.SelectedItem is StorageLocation selected)
            {
                if (Config.StorageLocations.Count <= 1)
                {
                    AppDialog.ShowMessage(this, "至少需要保留一个存储路径。", "警告", AppDialogSeverity.Warning);
                    return;
                }

                bool shouldRemove = AppDialog.Confirm(
                    this,
                    $"确定要移除路径: {selected.Path} 吗？\n注意：此操作不会删除物理文件，但系统将不再管理该目录。",
                    "确认移除",
                    "移除",
                    "取消",
                    AppDialogSeverity.Warning,
                    isDangerous: true);
                if (shouldRemove)
                {
                    int selectedIndex = StorageDataGrid.SelectedIndex;
                    Config.StorageLocations.Remove(selected);
                    RefreshStoragePriorities();
                    StorageDataGrid.Items.Refresh();
                    if (Config.StorageLocations.Count > 0)
                    {
                        StorageDataGrid.SelectedIndex = Math.Min(selectedIndex, Config.StorageLocations.Count - 1);
                    }
                    UpdateStorageButtonStates();
                }
            }
            else
            {
                AppDialog.ShowMessage(this, "请先在列表中选中要移除的行。", "提示", AppDialogSeverity.Information);
            }
        }

        private void StorageDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateStorageButtonStates();
        }

        private void StorageReserveEditor_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: StorageLocation location })
            {
                location.EffectiveReserveGB = location.EffectiveReserveGB;
                StorageDataGrid.Items.Refresh();
            }
        }

        private void BtnMoveStorageUp_Click(object sender, RoutedEventArgs e)
        {
            MoveSelectedStorage(-1);
        }

        private void BtnMoveStorageDown_Click(object sender, RoutedEventArgs e)
        {
            MoveSelectedStorage(1);
        }

        private void MoveSelectedStorage(int direction)
        {
            if (StorageDataGrid?.SelectedItem is not StorageLocation selected) return;

            int oldIndex = Config.StorageLocations.IndexOf(selected);
            int newIndex = oldIndex + direction;
            if (oldIndex < 0 || newIndex < 0 || newIndex >= Config.StorageLocations.Count) return;

            Config.StorageLocations.RemoveAt(oldIndex);
            Config.StorageLocations.Insert(newIndex, selected);
            RefreshStoragePriorities();
            StorageDataGrid.Items.Refresh();
            StorageDataGrid.SelectedIndex = newIndex;
            UpdateStorageButtonStates();
        }

        private void SortStorageLocationsByPriority()
        {
            if (Config.StorageLocations == null || Config.StorageLocations.Count <= 1) return;

            var ordered = Config.StorageLocations
                .Select((location, index) => new { Location = location, Index = index })
                .OrderBy(x => x.Location.Priority)
                .ThenBy(x => x.Index)
                .Select(x => x.Location)
                .ToList();

            Config.StorageLocations.Clear();
            Config.StorageLocations.AddRange(ordered);
        }

        private void RefreshStoragePriorities()
        {
            if (Config.StorageLocations == null) return;

            for (int i = 0; i < Config.StorageLocations.Count; i++)
            {
                Config.StorageLocations[i].Priority = i;
            }
        }

        private void UpdateStorageButtonStates()
        {
            if (RemoveStorageButton == null) return;

            bool hasSelection = StorageDataGrid?.SelectedItem is StorageLocation;
            int selectedIndex = StorageDataGrid?.SelectedIndex ?? -1;
            int count = Config.StorageLocations?.Count ?? 0;

            RemoveStorageButton.IsEnabled = hasSelection;
            if (MoveStorageUpButton != null) MoveStorageUpButton.IsEnabled = hasSelection && selectedIndex > 0;
            if (MoveStorageDownButton != null) MoveStorageDownButton.IsEnabled = hasSelection && selectedIndex >= 0 && selectedIndex < count - 1;
        }

        private async void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (await SaveAndApplyAsync())
            {
                DialogResult = true;
                Close();
            }
        }

        private void ShowAdvancedSettingsCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox toggle) return;
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                new Action(() => Keyboard.Focus(toggle)));
        }

        private async void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            await SaveAndApplyAsync();
        }

        private async Task<bool> SaveAndApplyAsync()
        {
            Keyboard.ClearFocus();
            if (Capabilities.CanRecordAudio)
                SyncSelectedMicToConfig();

            if (Capabilities.CanUseCamera && !ValidateCameraIdleNoSleepPeriods())
                return false;

            // 0. 验证音频
            if (Capabilities.CanRecordAudio &&
                Config.EnableAudioRecording &&
                string.IsNullOrEmpty(Config.AudioDeviceName))
            {
                bool shouldContinue = AppDialog.Confirm(
                    this,
                    "已开启录制声音，但未选择麦克风。录制可能会失败或没有声音。\n\n是否继续保存？",
                    "音频提醒",
                    "继续保存",
                    "返回设置",
                    AppDialogSeverity.Warning,
                    isDangerous: false);
                if (!shouldContinue) return false;
            }

            // 1. 强制提交 DataGrid 中的未完成编辑
            if (Capabilities.CanConfigureStorage)
            {
                StorageDataGrid.CommitEdit(DataGridEditingUnit.Row, true);
                StorageDataGrid.CommitEdit(DataGridEditingUnit.Cell, true);
                RefreshStoragePriorities();

                if (Config.MaxRetentionDays < StorageRetentionPolicy.MinimumRetentionDays)
                {
                    AppDialog.ShowMessage(this, "录像最长保留天数最短为 15 天", "存储设置无效", AppDialogSeverity.Warning);
                    return false;
                }
                if (Config.EnableMaxStorageUsage && Config.MaxStorageUsageGB <= 0)
                {
                    AppDialog.ShowMessage(this, "启用最大占用后，请填写大于 0 GB 的容量", "存储设置无效", AppDialogSeverity.Warning);
                    return false;
                }
                bool hasNetworkStorage = Config.StorageLocations.Any(location =>
                    !string.IsNullOrWhiteSpace(location.Path)
                    && StorageLocationResolver.IsNetworkPath(location.Path));
                if (hasNetworkStorage
                    && !StorageLocationResolver.IsValidLocalBufferPath(Config.LocalRecordingBufferPath, out string bufferError))
                {
                    AppDialog.ShowMessage(this, bufferError, "本地缓冲目录无效", AppDialogSeverity.Warning);
                    return false;
                }
            }

            // 2. 手动同步部分控件（防止可焦点未切换时绑定未更新）
            if (Capabilities.CanUseCamera && CameraComboBox.SelectedItem is CameraInfo cam)
            {
                Config.CameraMonikerString = cam.Moniker;
                Config.CameraIndex = cam.Index;

                if (ResComboBox.SelectedItem is ResOption selectedRes)
                {
                    Config.FrameWidth = selectedRes.Width;
                    Config.FrameHeight = selectedRes.Height;
                }

                if (FpsComboBox.SelectedItem is ComboBoxItem fpsItem && fpsItem.Tag is int fps)
                {
                    Config.Fps = fps;
                }

                // 更新此摄像头的独立配置
                if (!string.IsNullOrEmpty(cam.Moniker))
                {
                    Config.CameraConfigs[cam.Moniker] = new CameraSettings
                    {
                        FrameWidth = Config.FrameWidth,
                        FrameHeight = Config.FrameHeight,
                        Fps = Config.Fps,
                        AudioDeviceName = Config.AudioDeviceName,
                        AudioDeviceMoniker = Config.AudioDeviceMoniker,
                        AudioSyncOffsetMs = Config.AudioSyncOffsetMs
                    };
                }
            }

            if (Capabilities.CanRecordPcVideo && VideoEncoderComboBox.SelectedItem is GpuEncoderOption encoderOption)
            {
                EncodingHelper.ApplyEncoderSelectionToConfig(Config, encoderOption.Value);
            }

            // 保存断句关键词
            if (Capabilities.IsRecordingDevice)
            {
                Config.TtsBreakWords = TtsBreakWordsTextBox.Text
                    .Split(new[] { '\r', '\n', '，', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(w => w.Trim())
                    .Where(w => w.Length > 0)
                    .Distinct()
                    .ToList();
            }

            // 3. 校验并保存
            if (Capabilities.IsRecordingDevice &&
                AppLanguage.Resolve(Config.Language) == AppLanguage.Chinese)
            {
                Config.EdgeTtsVoiceZhHans = Config.EdgeTtsVoice;
                Config.EdgeTtsWarningVoiceZhHans = Config.EdgeTtsWarningVoice;
            }
            else if (Capabilities.IsRecordingDevice)
            {
                Config.EdgeTtsVoiceEnUs = Config.EdgeTtsVoice;
                Config.EdgeTtsWarningVoiceEnUs = Config.EdgeTtsWarningVoice;
            }
            AppConfig.NormalizeAfterLoad(Config);

            if (Capabilities.CanRecordPcVideo && !ValidateEncoderSelectionBeforeSave())
                return false;

            AutoStartService.Apply(Config.AutoStartOnBoot);
            Context.SetPreviewZoomScale?.Invoke(null);
            var appliedConfig = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(Config)) ?? new AppConfig();
            bool applied = await Context.ApplyAsync(appliedConfig);
            if (applied)
            {
                _originalTheme = Config.Theme;
                if (_originalLanguage != Config.Language)
                {
                    AppDialog.ShowMessage(
                        this,
                        AppLanguage.Get("RestartSaved"),
                        AppLanguage.Get("RestartRequired"),
                        AppDialogSeverity.Information);
                    _originalLanguage = Config.Language;
                }
            }
            return applied;
        }

        private bool ValidateCameraIdleNoSleepPeriods()
        {
            if (!TryNormalizeCameraIdleNoSleepPeriod(
                    1,
                    Config.CameraIdleNoSleepStart1,
                    Config.CameraIdleNoSleepEnd1,
                    out string start1,
                    out string end1))
            {
                return false;
            }

            if (!TryNormalizeCameraIdleNoSleepPeriod(
                    2,
                    Config.CameraIdleNoSleepStart2,
                    Config.CameraIdleNoSleepEnd2,
                    out string start2,
                    out string end2))
            {
                return false;
            }

            Config.CameraIdleNoSleepStart1 = start1;
            Config.CameraIdleNoSleepEnd1 = end1;
            Config.CameraIdleNoSleepStart2 = start2;
            Config.CameraIdleNoSleepEnd2 = end2;
            return true;
        }

        private bool TryNormalizeCameraIdleNoSleepPeriod(
            int periodNumber,
            string start,
            string end,
            out string normalizedStart,
            out string normalizedEnd)
        {
            if (AppConfig.TryNormalizeCameraIdlePeriod(start, end, out normalizedStart, out normalizedEnd))
                return true;

            string message = string.Format(
                CultureInfo.CurrentCulture,
                AppLanguage.Translate("不休眠时段 {0} 请填写完整的 HH:mm 开始和结束时间，或全部留空"),
                periodNumber);
            AppDialog.ShowMessage(
                this,
                message,
                AppLanguage.Translate("时间格式错误"),
                AppDialogSeverity.Warning);
            return false;
        }

        private async void RunSetupWizard_Click(object sender, RoutedEventArgs e)
        {
            if (!Capabilities.CanUseCamera ||
                Context.SuspendCameraForSetupWizard == null ||
                Context.ResumeCameraAfterSetupWizard == null)
                return;

            Keyboard.ClearFocus();
            SyncSelectedMicToConfig();

            bool pausedCamera = false;
            try
            {
                if (!_isRecording)
                {
                    pausedCamera = Context.SuspendCameraForSetupWizard();
                    if (!pausedCamera)
                        return;
                }

                var wizard = new FirstUseSetupWizardWindow(Config) { Owner = this };
                if (wizard.ShowDialog() == true && !wizard.WasSkipped)
                {
                    Config.FirstUseWizardCompleted = true;
                    AppConfig.NormalizeAfterLoad(Config);
                    SyncScannerModeControlsFromConfig();
                    _isLoadingDevices = true;
                    try
                    {
                        await LoadAllDevicesAsync();
                    }
                    finally
                    {
                        _isLoadingDevices = false;
                    }
                }
            }
            finally
            {
                if (pausedCamera)
                    Context.ResumeCameraAfterSetupWizard();
            }
        }

        private void ZoomScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ShouldPreviewZoomScale(IsLoaded, Context))
                Context.SetPreviewZoomScale?.Invoke(e.NewValue);
        }

        internal static bool ShouldPreviewZoomScale(bool isLoaded, SettingsContext context) =>
            isLoaded && context?.Capabilities.CanRecordPcVideo == true;

        private void SyncVoiceEngineComboBoxFromConfig()
        {
            if (VoiceEngineComboBox == null) return;

            _isSyncingVoiceEngine = true;
            VoiceEngineComboBox.SelectedValue = Config.EnableAiTts
                ? NormalizeVoiceEngine(Config.AiTtsEngine)
                : "System";
            _isSyncingVoiceEngine = false;
        }

        private void VoiceEngineComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSyncingVoiceEngine || Config == null) return;

            string engine = VoiceEngineComboBox.SelectedValue?.ToString() ?? "System";
            if (string.Equals(engine, "System", StringComparison.OrdinalIgnoreCase))
            {
                Config.EnableAiTts = false;
                return;
            }

            Config.EnableAiTts = true;
            Config.AiTtsEngine = NormalizeVoiceEngine(engine);
        }

        private static string NormalizeVoiceEngine(string engine)
        {
            return string.Equals(engine, "Kokoro", StringComparison.OrdinalIgnoreCase) ? "Kokoro" : "Edge";
        }

        private void InstallTool_Click(object sender, RoutedEventArgs e)
        {
            Context.OpenUserscriptGuide?.Invoke();
        }

        private void ShowMobileConnection_Click(object sender, RoutedEventArgs e)
        {
            Context.ShowMobileConnection?.Invoke(this);
        }

        private void CopyMobileConnectionUrl_Click(object sender, RoutedEventArgs e)
        {
            Context.CopyMobileConnectionUrl?.Invoke();
        }

        private void SelectMicByConfig(List<MicInfo> mics)
        {
            var micMatch = mics.FirstOrDefault(m => !string.IsNullOrEmpty(Config.AudioDeviceMoniker)
                                                    && m.Moniker == Config.AudioDeviceMoniker)
                        ?? mics.FirstOrDefault(m => m.Name == Config.AudioDeviceName);
            if (micMatch != null)
            {
                MicComboBox.SelectedItem = micMatch;
                if (IsAvailableMic(micMatch))
                {
                    Config.AudioDeviceName = micMatch.Name;
                    Config.AudioDeviceMoniker = micMatch.Moniker ?? "";
                }
            }
        }

        private void SyncSelectedMicToConfig()
        {
            if (MicComboBox.SelectedItem is MicInfo mic && IsAvailableMic(mic))
            {
                Config.AudioDeviceName = mic.Name;
                Config.AudioDeviceMoniker = mic.Moniker ?? "";
            }
            else
            {
                Config.AudioDeviceName = "";
                Config.AudioDeviceMoniker = "";
            }
        }

        private static bool IsAvailableMic(MicInfo mic)
        {
            return mic != null
                && !string.IsNullOrWhiteSpace(mic.Name)
                && mic.Name != "未检测到麦克风";
        }

        protected override void OnClosed(EventArgs e)
        {
            _isClosing = true;
            var migrationCts = Interlocked.Exchange(ref _migrationCts, null);
            try { migrationCts?.Cancel(); } catch (ObjectDisposedException) { }
            try { _performanceCts?.Cancel(); } catch (ObjectDisposedException) { }
            _performanceCts?.Dispose();
            Context.SetPreviewZoomScale?.Invoke(null);
            _previewSpeechService?.Stop();
            _previewSpeechService?.Dispose();
            _previewSpeechService = null;
            base.OnClosed(e);
        }

        private bool ValidateEncoderSelectionBeforeSave()
        {
            string encoder = AppConfig.NormalizeVideoEncoder(Config.VideoEncoder);
            var validated = MainViewModel.ValidatedEncoders ?? new HashSet<string>();
            if (encoder == "auto" || validated.Contains(encoder))
                return true;
            AppDialog.ShowMessage(
                this,
                "所选编码器不可用，请重新检测或选择其他编码器",
                "编码器不可用",
                AppDialogSeverity.Warning);
            return false;
        }

        private void SyncEncoderComboboxes(string encoder)
        {
            if (VideoEncoderComboBox.ItemsSource is IEnumerable<GpuEncoderOption> encoders)
                VideoEncoderComboBox.SelectedItem = encoders.FirstOrDefault(i => i.Value == encoder);
        }


        private void OpenRepository_Click(object sender, RoutedEventArgs e)
        {
            OpenExternalUrl("https://github.com/m-RNA/ExpressPackingMonitoring");
        }

        private void OpenLicense_Click(object sender, RoutedEventArgs e)
        {
            OpenExternalUrl("https://github.com/m-RNA/ExpressPackingMonitoring/blob/main/LICENSE");
        }

        internal static bool AreSameStoragePath(string firstPath, string secondPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(firstPath) || string.IsNullOrWhiteSpace(secondPath))
                    return false;

                string first = Path.TrimEndingDirectorySeparator(Path.GetFullPath(firstPath.Trim()));
                string second = Path.TrimEndingDirectorySeparator(Path.GetFullPath(secondPath.Trim()));
                return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(
                    firstPath?.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    secondPath?.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        private static ImageSource GetLargestAppIconImage()
        {
            var decoder = BitmapDecoder.Create(
                new Uri("pack://application:,,,/app.ico", UriKind.Absolute),
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            BitmapFrame frame = decoder.Frames
                .OrderByDescending(x => x.PixelWidth * x.PixelHeight)
                .First();
            frame.Freeze();
            return frame;
        }

        private static void OpenExternalUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppDialog.ShowMessage(null, $"无法打开链接：{ex.Message}", "打开链接失败", AppDialogSeverity.Warning);
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            if (Config.Theme != _originalTheme)
            {
                if (Enum.TryParse<ExpressPackingMonitoring.Themes.AppTheme>(_originalTheme, out var themeEnum))
                {
                    ExpressPackingMonitoring.Themes.ThemeManager.ApplyTheme(themeEnum);
                }
            }
            this.DialogResult = false;
            this.Close();
        }

        private SpeechService _previewSpeechService;

        private void BtnTtsPreview_Click(object sender, RoutedEventArgs e)
        {
            Keyboard.ClearFocus();

            string text = TtsPreviewTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                TtsPreviewStatus.Text = "请输入预览文本";
                return;
            }

            // 显示预处理后的文本
            string processed = SpeechService.PreprocessTextForTts(text);
            TtsPreviewStatus.Text = $"断句: {processed}";

            // 初始化或复用预览用 SpeechService
            if (_previewSpeechService == null)
            {
                _previewSpeechService = new SpeechService
                {
                    EnableSoundPrompt = true,
                    MaximizeVolumeForSpeech = Config.MaximizeVolumeForSpeech,
                    EnableAiTts = Config.EnableAiTts,
                    AiTtsEngine = Config.AiTtsEngine,
                    AiTtsSpeakerId = Config.AiTtsSpeakerId,
                    AiTtsWarningSpeakerId = Config.AiTtsWarningSpeakerId,
                    AiTtsSpeed = Config.AiTtsSpeed,
                    EdgeTtsVoice = Config.EdgeTtsVoice,
                    EdgeTtsWarningVoice = Config.EdgeTtsWarningVoice,
                };
                _previewSpeechService.PlaybackError += OnPreviewSpeechError;
                // 同步当前编辑中的断句关键词
                var words = TtsBreakWordsTextBox.Text
                    .Split(new[] { '\r', '\n', '，', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(w => w.Trim()).Where(w => w.Length > 0);
                _previewSpeechService.UpdateBreakWords(words);
                if (Config.EnableAiTts)
                    _previewSpeechService.InitAiTts();
            }
            else
            {
                // 更新参数
                _previewSpeechService.EnableAiTts = Config.EnableAiTts;
                _previewSpeechService.MaximizeVolumeForSpeech = Config.MaximizeVolumeForSpeech;
                _previewSpeechService.AiTtsEngine = Config.AiTtsEngine;
                _previewSpeechService.AiTtsSpeakerId = Config.AiTtsSpeakerId;
                _previewSpeechService.AiTtsWarningSpeakerId = Config.AiTtsWarningSpeakerId;
                _previewSpeechService.AiTtsSpeed = Config.AiTtsSpeed;
                _previewSpeechService.EdgeTtsVoice = Config.EdgeTtsVoice;
                _previewSpeechService.EdgeTtsWarningVoice = Config.EdgeTtsWarningVoice;
                var words = TtsBreakWordsTextBox.Text
                    .Split(new[] { '\r', '\n', '，', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(w => w.Trim()).Where(w => w.Length > 0);
                _previewSpeechService.UpdateBreakWords(words);
            }

            _previewSpeechService.Preview(text);
        }

        private void BtnTtsStop_Click(object sender, RoutedEventArgs e)
        {
            _previewSpeechService?.Stop();
            TtsPreviewStatus.Text = "已停止";
        }

        private void OnPreviewSpeechError(string message)
        {
            Dispatcher.InvokeAsync(() => TtsPreviewStatus.Text = $"试听失败：{message}");
        }

        private CancellationTokenSource _migrationCts;
        private bool _isClosing;

        private async void BtnMigrateMkv_Click(object sender, RoutedEventArgs e)
        {
            if (!Capabilities.CanRecordPcVideo || Context.BatchConvertMkvToMp4Async == null)
                return;

            var runningMigration = _migrationCts;
            if (runningMigration != null)
            {
                // 正在迁移中，点击取消
                runningMigration.Cancel();
                return;
            }

            var migrationCts = new CancellationTokenSource();
            if (Interlocked.CompareExchange(ref _migrationCts, migrationCts, null) != null)
            {
                migrationCts.Dispose();
                return;
            }

            BtnMigrateMkv.Content = "取消合并";
            MigrationProgress.Visibility = Visibility.Visible;
            MigrationStatusText.Text = "正在扫描 MKV 记录...";

            var progress = new Progress<string>(msg =>
            {
                if (!_isClosing)
                    MigrationStatusText.Text = msg;
            });

            try
            {
                MkvBatchConversionResult result =
                    await Context.BatchConvertMkvToMp4Async(progress, migrationCts.Token);
                if (!_isClosing)
                {
                    MigrationStatusText.Text =
                        $"合并完成：成功 {result.SuccessCount}，失败 {result.FailureCount}，跳过 {result.SkippedCount}，长期失败 {result.SuppressedCount}";
                }
            }
            catch (OperationCanceledException)
            {
                if (!_isClosing)
                    MigrationStatusText.Text = "合并已取消";
            }
            catch (Exception ex)
            {
                if (!_isClosing)
                    MigrationStatusText.Text = $"合并出错：{ex.Message}";
            }
            finally
            {
                Interlocked.CompareExchange(ref _migrationCts, null, migrationCts);
                migrationCts.Dispose();
                if (!_isClosing)
                {
                    BtnMigrateMkv.Content = "开始合并";
                    MigrationProgress.Visibility = Visibility.Collapsed;
                }
            }
        }
    }
}
