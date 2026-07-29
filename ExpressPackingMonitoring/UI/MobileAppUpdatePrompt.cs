using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services;
using System.Diagnostics;
using System.Windows;

namespace ExpressPackingMonitoring.UI;

internal static class MobileAppUpdatePrompt
{
    internal static void ShowLatest(Window owner, MobileAppReleaseInfo release)
    {
        bool shouldOpen = AppDialog.Confirm(
            owner,
            $"手机版最新版本为 {release.Version}（内部版本 {release.BuildNumber}）\n\n"
            + "可前往手机版仓库下载更新",
            "发现新版手机 App",
            "前往下载",
            "稍后",
            AppDialogSeverity.Information,
            isDangerous: false);

        if (shouldOpen)
            OpenDownloadPage(owner, release.DownloadUrl);
    }

    internal static void Show(Window owner, MobileAppUpdateAvailableInfo update)
    {
        string deviceName = string.IsNullOrWhiteSpace(update.DeviceName)
            ? "已连接手机"
            : update.DeviceName;
        string currentVersion = string.IsNullOrWhiteSpace(update.CurrentVersion)
            ? update.CurrentBuildNumber > 0
                ? $"内部版本 {update.CurrentBuildNumber}"
                : "版本未知（可能是旧版）"
            : $"{update.CurrentVersion}（内部版本 {update.CurrentBuildNumber}）";
        string latestVersion =
            $"{update.LatestRelease.Version}（内部版本 {update.LatestRelease.BuildNumber}）";
        bool shouldOpen = AppDialog.Confirm(
            owner,
            $"检测到 {deviceName} 正在使用 {currentVersion}\n\n"
            + $"手机版最新版本为 {latestVersion}\n"
            + "建议前往下载更新；暂不更新时仍可继续使用当前可用功能",
            "发现新版手机 App",
            "前往下载",
            "稍后",
            AppDialogSeverity.Information,
            isDangerous: false);

        if (!shouldOpen)
            return;

        OpenDownloadPage(owner, update.LatestRelease.DownloadUrl);
    }

    private static void OpenDownloadPage(Window owner, string downloadUrl)
    {
        try
        {
            Process.Start(new ProcessStartInfo(downloadUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MobileUpdate", "Open mobile app download page failed", ex);
            AppDialog.ShowMessage(
                owner,
                "打开手机版下载页面失败，请稍后重试",
                "手机 App 更新",
                AppDialogSeverity.Warning);
        }
    }
}
