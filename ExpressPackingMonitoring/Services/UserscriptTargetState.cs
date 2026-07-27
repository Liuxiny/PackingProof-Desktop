using System.Security.Cryptography;
using System.Text;
using ExpressPackingMonitoring.Config;

namespace ExpressPackingMonitoring.Services;

internal static class UserscriptTargetState
{
    internal static string BuildSignature(IEnumerable<RecordingDeviceInfo>? devices)
    {
        string payload = string.Join(
            "\n",
            (devices ?? [])
                .Where(device => device != null)
                .Select(device => device.Address?.Trim().TrimEnd('/').ToLowerInvariant() ?? "")
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal));
        return payload.Length == 0
            ? ""
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    internal static UserscriptTargetStatus GetStatus(
        AppConfig config,
        IReadOnlyList<RecordingDeviceInfo>? devices)
    {
        string currentSignature = BuildSignature(devices);
        if (currentSignature.Length == 0)
        {
            return new UserscriptTargetStatus(
                "当前没有可接收订单的录像设备",
                "安装订单联动",
                currentSignature);
        }

        if (string.IsNullOrWhiteSpace(config.LastUserscriptTargetSignature))
        {
            return new UserscriptTargetStatus(
                "尚未配置订单联动",
                "安装订单联动",
                currentSignature);
        }

        if (!string.Equals(
                config.LastUserscriptTargetSignature,
                currentSignature,
                StringComparison.Ordinal))
        {
            return new UserscriptTargetStatus(
                "录像设备地址有变化，请更新订单联动脚本",
                "更新订单联动",
                currentSignature);
        }

        return new UserscriptTargetStatus(
            "订单联动设备列表已是最新",
            "订单联动",
            currentSignature);
    }

    internal static void MarkGuideOpened(
        AppConfig config,
        IReadOnlyList<RecordingDeviceInfo>? devices)
    {
        string signature = BuildSignature(devices);
        if (signature.Length == 0)
            return;

        if (WorkstationConfigStore.TryUpdate(
                saved => saved.LastUserscriptTargetSignature = signature,
                out AppConfig persisted,
                out _))
        {
            config.LastUserscriptTargetSignature = persisted.LastUserscriptTargetSignature;
        }
    }
}

internal sealed record UserscriptTargetStatus(
    string StatusText,
    string ButtonText,
    string CurrentSignature);
