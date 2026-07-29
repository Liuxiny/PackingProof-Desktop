using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace ExpressPackingMonitoring.UI
{
    public class VideoItem
    {
        public long RecordId { get; set; }
        public string DisplayName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public string OrderId { get; set; } = "";
        public string Mode { get; set; } = "";
        public string Duration { get; set; } = "";
        public string FileSize { get; set; } = "";
        public string StopReason { get; set; } = "";
        public string VideoCodec { get; set; } = "";
        public string VideoEncoder { get; set; } = "";
        public string SourceDisplay { get; set; } = "";
        public string ProofDisplay { get; set; } = "";
        public string PhotoPath { get; set; } = "";
        public ImageSource? PhotoThumbnail { get; set; }
        public bool HasPhoto { get; set; }
        public DateTime? PhotoCapturedAt { get; set; }
        public int PhotoWidth { get; set; }
        public int PhotoHeight { get; set; }
        public long TotalSizeBytes { get; set; }
        public bool CanDelete { get; set; }
        public string DeleteDisabledReason { get; set; } = "";
        public bool IsDeletePending { get; set; }
        public bool IsMissing { get; set; }
        public bool IsDeleted { get; set; }
        public string DeleteReason { get; set; } = "";
        public DateTime? DeletedAt { get; set; }
        public FileInfo? File { get; set; }

        public string EncoderDisplay
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(VideoEncoder))
                    return EncodingHelper.GetEncoderLabel(VideoEncoder);
                if (!string.IsNullOrWhiteSpace(VideoCodec))
                    return EncodingHelper.GetCodecLabel(VideoCodec);
                return "";
            }
        }

        public string StatusText
        {
            get
            {
                if (IsDeleted)
                {
                    string reason = string.IsNullOrEmpty(DeleteReason) ? "已删除" : DeleteReason;
                    string time = DeletedAt?.ToString("MM-dd HH:mm") ?? "";
                    return $"已清理 ({reason} {time})";
                }

                if (IsDeletePending)
                    return "等待网络删除";

                return IsMissing ? "文件已丢失" : "";
            }
        }

        public bool IsUnavailable => IsDeleted || IsMissing;
    }

    public partial class PlaybackWindow : Window
    {
        private readonly string _folderPath;
        private readonly VideoDatabase? _db;
        private readonly bool _showDeletedVideos;
        private readonly RecordingDeletionService? _deletionService;
        private readonly Func<long, bool>? _isCurrentRecording;
        private readonly PhotoThumbnailCache _photoThumbnailCache = new(120);
        private readonly DispatcherTimer _timer;
        private readonly DispatcherTimer _searchTimer;
        private readonly string[] _videoExtensions = [".mp4", ".mkv"];
        private const int PageSize = 50;
        private LibVLC? _libVLC;
        private LibVLCSharp.Shared.MediaPlayer? _mediaPlayer;
        private List<VideoItem> _allVideos = new();
        private bool _isDragging;
        private bool _isPlaying;
        private bool _isLoadingVideos;
        private bool _isClosing;
        private bool _videoLoadLoopRunning;
        private bool _playerInitializationFailed;
        private bool _playerInitializing;
        private int _currentPage = 1;
        private int _totalVideos;
        private int _videoLoadRequestVersion;
        private VideoLoadRequest? _pendingVideoLoad;
        private CancellationTokenSource? _videoLoadCancellation;
        private long _currentMediaLengthMs;
        private readonly SemaphoreSlim _playerSemaphore = new SemaphoreSlim(1, 1);

        internal PlaybackWindow(
            string folderPath,
            VideoDatabase? db = null,
            bool showDeletedVideos = true,
            RecordingDeletionService? deletionService = null,
            Func<long, bool>? isCurrentRecording = null)
        {
            InitializeComponent();
            _folderPath = folderPath;
            _db = db;
            _showDeletedVideos = showDeletedVideos;
            _deletionService = deletionService;
            _isCurrentRecording = isCurrentRecording;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _timer.Tick += Timer_Tick;
            _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _searchTimer.Tick += SearchTimer_Tick;

            BtnTogglePlay.IsEnabled = false;
            TimelineSlider.IsEnabled = false;
            TimeLabel.Text = "正在加载列表...";
            Loaded += PlaybackWindow_Loaded;
            UpdateLocateButtonState();
        }

        private void PlaybackWindow_Loaded(object sender, RoutedEventArgs e)
        {
            RequestVideoLoad();
        }

        private void DateFilterChanged(object sender, SelectionChangedEventArgs e)
        {
            RequestVideoLoad(1);
        }

        private void TextFilterChanged(object sender, TextChangedEventArgs e)
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        private void SearchTimer_Tick(object? sender, EventArgs e)
        {
            _searchTimer.Stop();
            RequestVideoLoad(1);
        }

        private void BtnClearSearch_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = "";
        }

        private void RequestVideoLoad(int? requestedPage = null)
        {
            if (!IsLoaded || _isClosing)
                return;

            DateTime? start = DpStartDate.SelectedDate;
            DateTime? end = DpEndDate.SelectedDate;
            if (start.HasValue && end.HasValue && start > end)
                (start, end) = (end, start);
            string? keyword = SearchBox?.Text.Trim();
            int page = Math.Max(1, requestedPage ?? _currentPage);

            _videoLoadCancellation?.Cancel();
            _videoLoadCancellation?.Dispose();
            _videoLoadCancellation = new CancellationTokenSource();
            _pendingVideoLoad = new VideoLoadRequest(start, end, keyword, page, _videoLoadCancellation.Token);
            _videoLoadRequestVersion++;
            if (!_videoLoadLoopRunning)
                _ = ProcessVideoLoadQueueAsync();
        }

        private async Task ProcessVideoLoadQueueAsync()
        {
            _videoLoadLoopRunning = true;
            _isLoadingVideos = true;
            SetLoadingState(true, "正在加载列表...");
            try
            {
                while (!_isClosing && _pendingVideoLoad is VideoLoadRequest request)
                {
                    _pendingVideoLoad = null;
                    int requestVersion = _videoLoadRequestVersion;
                    (List<VideoItem> Items, int Total) result;
                    try
                    {
                        result = await Task.Run(() =>
                            BuildVideoPage(request.Start, request.End, request.Keyword, request.Page, request.CancellationToken),
                            request.CancellationToken);
                        if (!IsCurrentLoadRequest(requestVersion, _videoLoadRequestVersion, _isClosing))
                            continue;

                        int pageCount = GetPageCount(result.Total);
                        int normalizedPage = pageCount == 0 ? 1 : Math.Min(request.Page, pageCount);
                        if (pageCount > 0 && normalizedPage != request.Page)
                        {
                            result = await Task.Run(() =>
                                BuildVideoPage(request.Start, request.End, request.Keyword, normalizedPage, request.CancellationToken),
                                request.CancellationToken);
                            if (!IsCurrentLoadRequest(requestVersion, _videoLoadRequestVersion, _isClosing))
                                continue;
                        }

                        _currentPage = normalizedPage;
                    }
                    catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
                    {
                        continue;
                    }
                    catch (Exception ex)
                    {
                        if (!IsCurrentLoadRequest(requestVersion, _videoLoadRequestVersion, _isClosing))
                            continue;

                        _allVideos = new List<VideoItem>();
                        _totalVideos = 0;
                        _currentPage = 1;
                        ShowCurrentPage();
                        AppDialog.ShowMessage(this, $"加载回放列表失败：{ex.Message}", "回放错误", AppDialogSeverity.Warning);
                        continue;
                    }

                    _allVideos = result.Items;
                    _totalVideos = result.Total;
                    ShowCurrentPage();
                }
            }
            finally
            {
                _isLoadingVideos = false;
                _videoLoadLoopRunning = false;
                SetLoadingState(false, "00:00:00 / 00:00:00");
                if (!_isClosing && _pendingVideoLoad.HasValue)
                    _ = ProcessVideoLoadQueueAsync();
            }
        }

        private (List<VideoItem> Items, int Total) BuildVideoPage(
            DateTime? start,
            DateTime? end,
            string? keyword,
            int page,
            CancellationToken cancellationToken)
        {
            var videos = new List<VideoItem>();
            if (_db != null)
            {
                try
                {
                    var result = _db.QueryVideosPaged(
                        start,
                        end,
                        string.IsNullOrEmpty(keyword) ? null : keyword,
                        page,
                        PageSize,
                        includeDeleted: _showDeletedVideos,
                        searchMode: VideoSearchMode.ExactOrderIdentifiers);
                    if (result.Total == 0 && !string.IsNullOrWhiteSpace(keyword))
                    {
                        result = _db.QueryVideosPaged(
                            start,
                            end,
                            keyword,
                            page,
                            PageSize,
                            includeDeleted: _showDeletedVideos,
                            searchMode: VideoSearchMode.OrderIdentifierContains);
                    }
                    IReadOnlyDictionary<long, RecordingDeleteJob> pendingDeletes = _db
                        .GetPendingRecordingDeleteJobs(1000)
                        .ToDictionary(job => job.RecordId);
                    foreach (var record in result.Records)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        bool deleted = record.IsDeleted;
                        string videoPath = VideoFileResolver.Resolve(record);
                        bool missing = !deleted && !File.Exists(videoPath);
                        FileInfo? info = (deleted || missing) ? null : new FileInfo(videoPath);
                        string photoPath = record.ResolvedPhotoPath;
                        bool hasPhoto = !deleted && !string.IsNullOrWhiteSpace(photoPath) && File.Exists(photoPath);
                        bool currentRecording = _isCurrentRecording?.Invoke(record.Id) == true;
                        bool deletePending = pendingDeletes.ContainsKey(record.Id);
                        videos.Add(new VideoItem
                        {
                            RecordId = record.Id,
                            DisplayName = GetOrderDisplayName(record.TrackingNumber, record.OrderId, record.FileName),
                            FullPath = videoPath,
                            OrderId = record.OrderId,
                            Mode = record.Mode,
                            Duration = record.DurationSeconds > 0 ? $"{(int)record.DurationSeconds}s" : "",
                            FileSize = (deleted || missing) ? FormatFileSize(record.FileSizeBytes) : FormatFileSize(info!.Length),
                            StopReason = GetStopReasonDisplay(record.SourceType, record.StopReason),
                            VideoCodec = record.VideoCodec,
                            VideoEncoder = record.VideoEncoder,
                            SourceDisplay = GetSourceDisplay(
                                record.SourceType,
                                record.SourceDeviceId,
                                record.SourceDeviceName),
                            ProofDisplay = GetProofDisplay(record),
                            PhotoPath = photoPath,
                            PhotoThumbnail = hasPhoto ? _photoThumbnailCache.Get(photoPath, cancellationToken) : null,
                            HasPhoto = hasPhoto,
                            PhotoCapturedAt = record.PhotoCapturedAt,
                            PhotoWidth = record.PhotoWidth,
                            PhotoHeight = record.PhotoHeight,
                            TotalSizeBytes = record.FileSizeBytes + record.PhotoFileSizeBytes,
                            CanDelete = _deletionService != null && !deleted && !currentRecording && !deletePending,
                            DeleteDisabledReason = currentRecording
                                ? "当前正在录制，不能删除"
                                : deletePending
                                    ? "删除任务正在处理"
                                    : _deletionService == null ? "当前来源不支持数据库删除" : "",
                            IsDeletePending = deletePending,
                            IsMissing = missing,
                            IsDeleted = deleted,
                            DeleteReason = record.DeleteReason,
                            DeletedAt = record.DeletedAt,
                            File = info
                        });
                    }
                    return (videos, result.Total);
                }
                catch
                {
                    LoadVideosFromFileSystem(videos, start, end);
                }
            }
            else
            {
                LoadVideosFromFileSystem(videos, start, end);
            }

            if (!_showDeletedVideos)
                videos = videos.Where(v => !v.IsDeleted && !v.IsMissing).ToList();
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                string normalized = keyword.Trim();
                videos = videos.Where(v =>
                    v.DisplayName.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                    (v.OrderId?.Contains(normalized, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
            }
            int total = videos.Count;
            return (videos.Skip((page - 1) * PageSize).Take(PageSize).ToList(), total);
        }

        private static string GetProofDisplay(VideoRecord record)
        {
            if (string.IsNullOrWhiteSpace(record.ProofFilePath))
                return string.Equals(record.SourceType, "pc", StringComparison.OrdinalIgnoreCase)
                    ? "旧录像·无证明"
                    : "";
            return record.ArchiveStatus is VideoArchiveStatus.Verified or VideoArchiveStatus.LocalDeleted
                ? "已签名·已归档"
                : "已签名";
        }

        private void LoadVideosFromFileSystem(List<VideoItem> videos, DateTime? start, DateTime? end)
        {
            if (!Directory.Exists(_folderPath))
                return;

            DateTime startDate = start?.Date ?? DateTime.MinValue.Date;
            DateTime endDate = end?.Date ?? DateTime.MaxValue.Date;
            foreach (var dateFolder in Directory.EnumerateDirectories(_folderPath))
            {
                string folderName = Path.GetFileName(dateFolder);
                if (!DateTime.TryParse(folderName, out var folderDate))
                    continue;

                if (folderDate.Date < startDate || folderDate.Date > endDate)
                    continue;

                foreach (var file in EnumerateVideoFiles(dateFolder))
                {
                    videos.Add(new VideoItem
                    {
                        DisplayName = GetOrderDisplayName("", "", file.Name),
                        FullPath = file.FullName,
                        FileSize = FormatFileSize(file.Length),
                        File = file
                    });
                }
            }

            videos.Sort((a, b) => DateTime.Compare(b.File?.CreationTime ?? DateTime.MinValue, a.File?.CreationTime ?? DateTime.MinValue));
        }

        internal static string GetOrderDisplayName(string? trackingNumber, string? orderId, string? fileName)
        {
            if (!string.IsNullOrWhiteSpace(trackingNumber))
                return trackingNumber.Trim();
            if (!string.IsNullOrWhiteSpace(orderId))
                return orderId.Trim();

            string stem = Path.GetFileNameWithoutExtension(fileName ?? "").Trim();
            int separatorIndex = stem.IndexOf('_');
            string parsedOrderId = separatorIndex > 0 ? stem[..separatorIndex] : stem;
            return string.IsNullOrWhiteSpace(parsedOrderId) ? "未识别面单" : parsedOrderId;
        }

        internal static string GetSourceDisplay(
            string? sourceType,
            string? sourceDeviceId,
            string? sourceDeviceName)
        {
            if (!string.Equals(sourceType, "external", StringComparison.OrdinalIgnoreCase))
                return "来源：电脑";

            return $"来源：{GetSourceDeviceDisplayName(sourceDeviceId, sourceDeviceName)}";
        }

        internal static string GetSourceDeviceDisplayName(string? sourceDeviceId, string? sourceDeviceName)
        {
            string storedName = sourceDeviceName?.Trim() ?? "";
            if (storedName.Length > 0)
                return storedName;

            string normalizedId = new((sourceDeviceId ?? "")
                .Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());
            if (normalizedId.Length > 0)
            {
                string suffix = normalizedId.Length <= 6 ? normalizedId : normalizedId[^6..];
                return $"设备 {suffix}";
            }

            return "手机设备";
        }

        internal static string GetStopReasonDisplay(string? sourceType, string? stopReason)
        {
            string value = stopReason?.Trim() ?? "";
            if (string.Equals(sourceType, "external", StringComparison.OrdinalIgnoreCase)
                && string.Equals(value.Replace(" ", ""), "APP备份", StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }

            return value;
        }

        private IEnumerable<FileInfo> EnumerateVideoFiles(string folderPath)
        {
            var dir = new DirectoryInfo(folderPath);
            foreach (string extension in _videoExtensions)
            {
                foreach (var file in dir.GetFiles($"*{extension}"))
                    yield return file;
            }
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes}B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F0}KB";
            return $"{bytes / (1024.0 * 1024.0):F1}MB";
        }

        private void ShowCurrentPage()
        {
            VideoList.ItemsSource = _allVideos;
            int pageCount = GetPageCount();
            PageStatusText.Text = pageCount == 0
                ? "共 0 条"
                : $"第 {_currentPage} / {pageCount} 页，共 {_totalVideos} 条";
            BtnPreviousPage.IsEnabled = !_isLoadingVideos && pageCount > 0 && _currentPage > 1;
            BtnNextPage.IsEnabled = !_isLoadingVideos && pageCount > 0 && _currentPage < pageCount;
            UpdateLocateButtonState();
        }

        private int GetPageCount() => GetPageCount(_totalVideos);

        private static int GetPageCount(int totalVideos) =>
            totalVideos <= 0 ? 0 : (totalVideos + PageSize - 1) / PageSize;

        private void BtnPreviousPage_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage <= 1) return;
            RequestVideoLoad(_currentPage - 1);
        }

        private void BtnNextPage_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage >= GetPageCount()) return;
            RequestVideoLoad(_currentPage + 1);
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            _isClosing = true;
            _pendingVideoLoad = null;
            _videoLoadRequestVersion++;
            _videoLoadCancellation?.Cancel();
            _videoLoadCancellation?.Dispose();
            _videoLoadCancellation = null;

            // 1. 停止计时器
            _timer?.Stop();
            _searchTimer?.Stop();
            _photoThumbnailCache.Clear();

            // 2. 彻底释放 LibVLC 资源（注意顺序）
            if (_mediaPlayer != null)
            {
                try
                {
                    // 重要：先解除事件订阅，防止销毁时触发回调导致死锁
                    _mediaPlayer.LengthChanged -= MediaPlayer_LengthChanged;
                    _mediaPlayer.TimeChanged -= MediaPlayer_TimeChanged;
                    _mediaPlayer.EndReached -= MediaPlayer_EndReached;
                    _mediaPlayer.EncounteredError -= MediaPlayer_EncounteredError;

                    if (_mediaPlayer.IsPlaying)
                    {
                        _mediaPlayer.Stop();
                    }

                    // 断开视图连接
                    PlayerView.MediaPlayer = null;

                    _mediaPlayer.Dispose();
                    _mediaPlayer = null;
                }
                catch { }
            }

            if (_libVLC != null)
            {
                try
                {
                    _libVLC.Dispose();
                    _libVLC = null;
                }
                catch { }
            }
        }

        private async void VideoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (VideoList.SelectedItem is not VideoItem video)
            {
                UpdateLocateButtonState();
                return;
            }

            // 增加 100ms 的防抖，防止极速连点
            await Task.Delay(100);
            if (VideoList.SelectedItem != video) return; // 如果选中的已经变了，就不执行了

            if (video.IsDeleted)
            {
                string reason = string.IsNullOrEmpty(video.DeleteReason) ? "系统清理" : video.DeleteReason;
                string time = video.DeletedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "未知";
                AppDialog.ShowMessage(
                    this,
                    $"该视频已被覆盖删除，无法播放。\n\n单号: {video.OrderId}\n删除原因: {reason}\n删除时间: {time}\n原始大小: {video.FileSize}\n录制时长: {video.Duration}",
                    "视频已删除", AppDialogSeverity.Information);
                UpdateLocateButtonState(video);
                return;
            }

            if (video.IsMissing)
            {
                AppDialog.ShowMessage(
                    this,
                    $"视频文件已被外部删除或移动，无法播放。\n\n单号: {video.OrderId}\n路径: {video.FullPath}\n原始大小: {video.FileSize}\n录制时长: {video.Duration}",
                    "文件丢失", AppDialogSeverity.Warning);
                UpdateLocateButtonState(video);
                return;
            }

            PlaySelectedVideo(video);
            UpdateLocateButtonState(video);
        }

        private async void PlaySelectedVideo(VideoItem video)
        {
            // 1. 尝试获取信号量，如果已经在切换中，则直接返回，防止疯狂点击导致的排队
            if (!await _playerSemaphore.WaitAsync(0)) return;

            try
            {
                if (!await EnsurePlayerReadyAsync())
                    return;

                // UI 状态立即重置
                _timer.Stop();
                _currentMediaLengthMs = 0;
                TimelineSlider.Maximum = 0;
                TimelineSlider.Value = 0;
                TimeLabel.Text = "正在切换视频...";

                // 2. 在后台线程执行阻塞的 Stop 操作
                await Task.Run(() =>
                {
                    _mediaPlayer?.Stop();
                });

                // 3. 准备新媒体
                using var media = new Media(_libVLC!, new Uri(video.FullPath));

                // 增加一些优化参数，减少内存压力
                media.AddOption(":file-caching=300"); // 减小缓存

                if (!_mediaPlayer!.Play(media))
                    throw new InvalidOperationException("播放器未能启动该文件。");

                _timer.Start();
                UpdatePlayState(true);
            }
            catch (Exception ex)
            {
                UpdatePlayState(false);
                AppDialog.ShowMessage(this, $"视频播放失败：{ex.Message}", "播放错误", AppDialogSeverity.Warning);
            }
            finally
            {
                // 4. 释放信号量，允许下一次切换
                _playerSemaphore.Release();
            }
        }

        private void BtnTogglePlay_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaPlayer?.Media == null)
                return;

            if (_isPlaying)
            {
                _mediaPlayer.Pause();
                _timer.Stop();
                UpdatePlayState(false);
            }
            else
            {
                _mediaPlayer.SetPause(false);
                _timer.Start();
                UpdatePlayState(true);
            }
        }

        private void BtnLocateFile_Click(object sender, RoutedEventArgs e)
        {
            if (VideoList.SelectedItem is not VideoItem video || video.IsUnavailable || string.IsNullOrWhiteSpace(video.FullPath))
            {
                AppDialog.ShowMessage(this, "请先选择一个可用视频。", "定位文件", AppDialogSeverity.Information);
                return;
            }

            try
            {
                string argument = $"/select,\"{video.FullPath}\"";
                Process.Start("explorer.exe", argument);
            }
            catch (Exception ex)
            {
                AppDialog.ShowMessage(this, $"无法打开文件管理器：{ex.Message}", "定位失败", AppDialogSeverity.Warning);
            }
        }

        private void BtnShowPhoto_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if ((sender as FrameworkElement)?.DataContext is not VideoItem video
                || !video.HasPhoto
                || string.IsNullOrWhiteSpace(video.PhotoPath)
                || !File.Exists(video.PhotoPath))
            {
                AppDialog.ShowMessage(this, "该录像没有可用的原始照片", "查看照片", AppDialogSeverity.Information);
                return;
            }

            var viewer = new RecordingPhotoViewer(
                video.PhotoPath,
                video.PhotoWidth,
                video.PhotoHeight,
                video.PhotoCapturedAt)
            {
                Owner = this
            };
            viewer.ShowDialog();
        }

        private async void BtnDeleteVideo_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if ((sender as FrameworkElement)?.DataContext is not VideoItem video)
                return;
            if (!video.CanDelete || _deletionService == null)
            {
                AppDialog.ShowMessage(
                    this,
                    string.IsNullOrWhiteSpace(video.DeleteDisabledReason) ? "当前录像不能删除" : video.DeleteDisabledReason,
                    "无法删除",
                    AppDialogSeverity.Information);
                return;
            }

            string locations = video.FullPath.StartsWith(@"\\", StringComparison.Ordinal) ? "网络路径" : "本地路径";
            if (!AppDialog.Confirm(
                    this,
                    $"确定永久删除这段录像及其关联资产吗？\n\n单号：{video.DisplayName}\n录像时间：{video.File?.CreationTime:yyyy-MM-dd HH:mm:ss}\n合计大小：{FormatFileSize(video.TotalSizeBytes)}\n位置：{locations}\n范围：视频、原始照片、联合证明和所属临时文件\n\n网络离线时将保存删除任务，联网后继续处理。",
                    "二次确认删除",
                    confirmText: "永久删除",
                    cancelText: "取消",
                    severity: AppDialogSeverity.Warning,
                    isDangerous: true))
            {
                return;
            }

            if (ReferenceEquals(VideoList.SelectedItem, video))
            {
                _timer.Stop();
                try { _mediaPlayer?.Stop(); } catch { }
                UpdatePlayState(false);
            }

            try
            {
                video.CanDelete = false;
                RecordingDeletionResult result = await _deletionService.DeleteAsync(
                    video.RecordId,
                    CancellationToken.None);
                _photoThumbnailCache.Remove(video.PhotoPath);
                AppDialog.ShowMessage(
                    this,
                    result.Message,
                    result.Completed ? "删除完成" : result.WaitingForNetwork ? "等待网络删除" : "删除未完成",
                    result.Completed ? AppDialogSeverity.Information : AppDialogSeverity.Warning);
                RequestVideoLoad(_currentPage);
            }
            catch (Exception ex)
            {
                AppDialog.ShowMessage(this, $"删除失败：{ex.Message}", "删除失败", AppDialogSeverity.Warning);
                RequestVideoLoad(_currentPage);
            }
        }

        private void UpdatePlayState(bool isPlaying)
        {
            _isPlaying = isPlaying;
            PlayStateIcon.Data = (Geometry)FindResource(isPlaying ? "FluentPauseIcon" : "FluentPlayIcon");
            PlayStateText.Text = isPlaying ? "暂停" : "播放";
            BtnTogglePlay.ToolTip = isPlaying ? "暂停" : "播放";
        }

        private void MediaPlayer_LengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
        {
            _currentMediaLengthMs = e.Length;
            Dispatcher.Invoke(() => TimelineSlider.Maximum = Math.Max(0, e.Length / 1000.0));
        }

        private void MediaPlayer_TimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
        {
            if (_isDragging || _mediaPlayer == null)
                return;

            Dispatcher.BeginInvoke(() =>
            {
                if (!this.IsLoaded) return;
                TimelineSlider.Value = Math.Max(0, e.Time / 1000.0);
                TimeLabel.Text = $"{TimeSpan.FromMilliseconds(e.Time):hh\\:mm\\:ss} / {TimeSpan.FromMilliseconds(_currentMediaLengthMs):hh\\:mm\\:ss}";
            });
        }

        private void MediaPlayer_EndReached(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _timer.Stop();
                UpdatePlayState(false);
                TimelineSlider.Value = 0;
            });
        }

        private void MediaPlayer_EncounteredError(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _timer.Stop();
                UpdatePlayState(false);
                AppDialog.ShowMessage(this, "播放器解码失败，请确认视频文件完整。", "播放错误", AppDialogSeverity.Warning);
            });
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_isDragging || _mediaPlayer?.Media == null)
                return;

            TimeLabel.Text = $"{TimeSpan.FromMilliseconds(_mediaPlayer.Time):hh\\:mm\\:ss} / {TimeSpan.FromMilliseconds(_currentMediaLengthMs):hh\\:mm\\:ss}";
        }

        private void TimelineSlider_DragStarted(object sender, DragStartedEventArgs e)
        {
            _isDragging = true;
            if (_mediaPlayer?.IsPlaying == true)
                _mediaPlayer.Pause();
        }

        private void TimelineSlider_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (_mediaPlayer == null)
                return;

            _isDragging = false;
            _mediaPlayer.Time = (long)(TimelineSlider.Value * 1000);
            _mediaPlayer.SetPause(false);
            _timer.Start();
            UpdatePlayState(true);
        }

        private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isDragging)
            {
                TimeLabel.Text = $"{TimeSpan.FromSeconds(e.NewValue):hh\\:mm\\:ss} / {TimeSpan.FromMilliseconds(_currentMediaLengthMs):hh\\:mm\\:ss}";
            }
        }

        private void UpdateLocateButtonState(VideoItem? video = null)
        {
            var current = video ?? VideoList.SelectedItem as VideoItem;
            BtnLocateFile.IsEnabled = current != null && !current.IsUnavailable;
        }

        private async Task<bool> EnsurePlayerReadyAsync()
        {
            if (_playerInitializationFailed)
                return false;

            if (_mediaPlayer != null)
                return true;

            if (_playerInitializing)
                return false;

            _playerInitializing = true;
            TimeLabel.Text = "正在加载播放器...";
            BtnTogglePlay.IsEnabled = false;
            TimelineSlider.IsEnabled = false;

            try
            {
                LibVLC libVLC = null!;
                LibVLCSharp.Shared.MediaPlayer mediaPlayer = null!;

                await Task.Run(() =>
                {
                    Core.Initialize();
                    libVLC = new LibVLC("--avcodec-hw=any");
                    mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(libVLC);
                });

                _libVLC = libVLC;
                _mediaPlayer = mediaPlayer;
                _mediaPlayer.LengthChanged += MediaPlayer_LengthChanged;
                _mediaPlayer.TimeChanged += MediaPlayer_TimeChanged;
                _mediaPlayer.EndReached += MediaPlayer_EndReached;
                _mediaPlayer.EncounteredError += MediaPlayer_EncounteredError;
                PlayerView.MediaPlayer = _mediaPlayer;
                BtnTogglePlay.IsEnabled = true;
                TimelineSlider.IsEnabled = true;
                return true;
            }
            catch (Exception ex)
            {
                _playerInitializationFailed = true;
                AppDialog.ShowMessage(this, $"播放器初始化失败：{ex.Message}\n\n回放列表仍可查看，但当前机器暂时无法内置播放。", "回放错误", AppDialogSeverity.Warning);
                return false;
            }
            finally
            {
                _playerInitializing = false;
            }
        }

        private void SetLoadingState(bool loading, string statusText)
        {
            BtnPreviousPage.IsEnabled = !loading && _currentPage > 1;
            BtnNextPage.IsEnabled = !loading && _currentPage < GetPageCount();
            TimeLabel.Text = statusText;
        }

        internal static bool IsCurrentLoadRequest(int requestVersion, int currentRequestVersion, bool isClosing) =>
            !isClosing && requestVersion == currentRequestVersion;

        private readonly record struct VideoLoadRequest(
            DateTime? Start,
            DateTime? End,
            string? Keyword,
            int Page,
            CancellationToken CancellationToken);
    }

    internal sealed class PhotoThumbnailCache
    {
        private readonly int _capacity;
        private readonly object _sync = new();
        private readonly Dictionary<string, BitmapSource> _items = new(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> _order = new();

        public PhotoThumbnailCache(int capacity)
        {
            _capacity = Math.Max(10, capacity);
        }

        public BitmapSource? Get(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (_items.TryGetValue(path, out BitmapSource? cached))
                    return cached;
            }

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 160;
                image.StreamSource = stream;
                image.EndInit();
                cancellationToken.ThrowIfCancellationRequested();
                image.Freeze();
                lock (_sync)
                {
                    _items[path] = image;
                    _order.Enqueue(path);
                    while (_items.Count > _capacity && _order.TryDequeue(out string? oldest))
                        _items.Remove(oldest);
                }
                return image;
            }
            catch
            {
                return null;
            }
        }

        public void Remove(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            lock (_sync) _items.Remove(path);
        }

        public void Clear()
        {
            lock (_sync)
            {
                _items.Clear();
                _order.Clear();
            }
        }

        internal int Count
        {
            get { lock (_sync) return _items.Count; }
        }
    }

    internal sealed class RecordingPhotoViewer : Window
    {
        private readonly ScrollViewer _scrollViewer;
        private readonly Image _image;
        private readonly ScaleTransform _scale = new(1, 1);
        private Point? _dragStart;
        private double _horizontalStart;
        private double _verticalStart;

        public RecordingPhotoViewer(string photoPath, int width, int height, DateTime? capturedAt)
        {
            Title = "原始照片";
            Width = 1000;
            Height = 760;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Brushes.Black;

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _scrollViewer = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                PanningMode = PanningMode.Both,
                Cursor = Cursors.Hand,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            _image = new Image
            {
                Source = LoadOriginal(photoPath),
                Stretch = Stretch.None,
                LayoutTransform = _scale
            };
            _scrollViewer.Content = _image;
            _scrollViewer.PreviewMouseWheel += OnMouseWheel;
            _scrollViewer.PreviewMouseLeftButtonDown += OnMouseDown;
            _scrollViewer.PreviewMouseMove += OnMouseMove;
            _scrollViewer.PreviewMouseLeftButtonUp += OnMouseUp;
            root.Children.Add(_scrollViewer);

            var info = new TextBlock
            {
                Text = $"{width} × {height}    拍摄时间：{capturedAt:yyyy-MM-dd HH:mm:ss.fff}    滚轮缩放，按住左键拖动，双击适应窗口",
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
                Padding = new Thickness(12, 8, 12, 8)
            };
            Grid.SetRow(info, 1);
            root.Children.Add(info);
            Content = root;
            Loaded += (_, _) => FitToWindow();
        }

        private static BitmapSource LoadOriginal(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            double next = Math.Clamp(_scale.ScaleX * (e.Delta > 0 ? 1.15 : 1 / 1.15), 0.1, 8);
            _scale.ScaleX = next;
            _scale.ScaleY = next;
            e.Handled = true;
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                FitToWindow();
                e.Handled = true;
                return;
            }
            _dragStart = e.GetPosition(_scrollViewer);
            _horizontalStart = _scrollViewer.HorizontalOffset;
            _verticalStart = _scrollViewer.VerticalOffset;
            _scrollViewer.CaptureMouse();
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragStart == null || e.LeftButton != MouseButtonState.Pressed) return;
            Point current = e.GetPosition(_scrollViewer);
            _scrollViewer.ScrollToHorizontalOffset(_horizontalStart + _dragStart.Value.X - current.X);
            _scrollViewer.ScrollToVerticalOffset(_verticalStart + _dragStart.Value.Y - current.Y);
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            _dragStart = null;
            _scrollViewer.ReleaseMouseCapture();
        }

        private void FitToWindow()
        {
            if (_image.Source is not BitmapSource source) return;
            double availableWidth = Math.Max(1, _scrollViewer.ViewportWidth - 24);
            double availableHeight = Math.Max(1, _scrollViewer.ViewportHeight - 24);
            double fit = Math.Min(1, Math.Min(
                availableWidth / Math.Max(1, source.PixelWidth),
                availableHeight / Math.Max(1, source.PixelHeight)));
            _scale.ScaleX = fit;
            _scale.ScaleY = fit;
            _scrollViewer.ScrollToHorizontalOffset(0);
            _scrollViewer.ScrollToVerticalOffset(0);
        }
    }
}
