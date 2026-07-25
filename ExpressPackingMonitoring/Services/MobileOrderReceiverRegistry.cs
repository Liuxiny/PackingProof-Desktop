using ExpressPackingMonitoring.Config;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ExpressPackingMonitoring.Services;

internal sealed class MobileOrderReceiverRegistry
{
    internal const int OrderReceiverPort = 5280;
    private const int MaxAddresses = 7;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    private static readonly TimeSpan ActiveRetention = TimeSpan.FromMinutes(5);
    private readonly string _path;
    private readonly Func<DateTime> _utcNow;
    private readonly object _sync = new();
    private List<Entry> _entries;

    internal MobileOrderReceiverRegistry(string? path = null, Func<DateTime>? utcNow = null)
    {
        _path = path ?? GetDefaultPath();
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _entries = Load(_path);
    }

    internal void Register(
        IPAddress? remoteAddress,
        string? nodeId = null,
        string? nodeName = null,
        int? orderReceiverPort = null,
        IEnumerable<string>? capabilities = null)
    {
        string? address = NormalizePrivateIpv4(remoteAddress);
        if (address == null) return;

        lock (_sync)
        {
            DateTime now = _utcNow();
            Entry? existing = _entries.FirstOrDefault(item =>
                string.Equals(item.Address, address, StringComparison.OrdinalIgnoreCase));
            _entries.RemoveAll(item =>
                string.Equals(item.Address, address, StringComparison.OrdinalIgnoreCase)
                || now - item.LastSeenUtc > Retention);

            string normalizedNodeId = nodeId?.Trim() ?? "";
            if (normalizedNodeId.Length == 0)
                normalizedNodeId = existing?.NodeId ?? CreateFallbackNodeId(address);
            string normalizedNodeName = nodeName?.Trim() ?? "";
            if (normalizedNodeName.Length == 0)
                normalizedNodeName = existing?.NodeName ?? $"手机录像设备 {address}";
            int normalizedPort = orderReceiverPort is > 0 and <= 65535
                ? orderReceiverPort.Value
                : existing?.Port is > 0 and <= 65535
                    ? existing.Port
                    : OrderReceiverPort;
            string[] normalizedCapabilities = (capabilities ?? existing?.Capabilities ??
                [PackingProofCapabilities.Recording, PackingProofCapabilities.OrderReceiver])
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            _entries.Insert(0, new Entry
            {
                Address = address,
                LastSeenUtc = now,
                NodeId = normalizedNodeId,
                NodeName = normalizedNodeName,
                Port = normalizedPort,
                Capabilities = normalizedCapabilities
            });
            if (_entries.Count > MaxAddresses)
                _entries.RemoveRange(MaxAddresses, _entries.Count - MaxAddresses);
            try { Save(); } catch { }
        }
    }

    internal IReadOnlyList<string> GetAuthorities()
    {
        lock (_sync)
        {
            DateTime now = _utcNow();
            return _entries
                .Where(item => now - item.LastSeenUtc <= Retention)
                .OrderByDescending(item => item.LastSeenUtc)
                .Select(item => $"{item.Address}:{OrderReceiverPort}")
                .ToArray();
        }
    }

    internal static IReadOnlyList<string> GetDefaultAuthorities() =>
        new MobileOrderReceiverRegistry().GetAuthorities();

    internal IReadOnlyList<MobileOrderReceiverInfo> GetRecordingDevices()
    {
        lock (_sync)
        {
            DateTime now = _utcNow();
            return _entries
                .Where(item => now - item.LastSeenUtc <= ActiveRetention)
                .OrderByDescending(item => item.LastSeenUtc)
                .Select(item => new MobileOrderReceiverInfo(
                    item.NodeId,
                    item.NodeName,
                    item.Address,
                    item.Port is > 0 and <= 65535 ? item.Port : OrderReceiverPort,
                    item.Capabilities?.Length > 0
                        ? item.Capabilities
                        : [PackingProofCapabilities.Recording, PackingProofCapabilities.OrderReceiver],
                    Online: true))
                .ToArray();
        }
    }

    internal static string GetDefaultPath() =>
        Path.Combine(AppPaths.CacheDir, "mobile-backup", "order-receivers.json");

    private static string? NormalizePrivateIpv4(IPAddress? address)
    {
        if (address == null) return null;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return null;

        byte[] bytes = address.GetAddressBytes();
        bool isPrivate = bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
        return isPrivate ? address.ToString() : null;
    }

    private static string CreateFallbackNodeId(string address)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"packingproof-mobile:{address}"));
        return new Guid(hash.AsSpan(0, 16)).ToString("D");
    }

    private static List<Entry> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new List<Entry>();
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? new List<Entry>();
        }
        catch
        {
            return new List<Entry>();
        }
    }

    private void Save()
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        string temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_entries));
        File.Move(temporaryPath, _path, true);
    }

    private sealed class Entry
    {
        public string Address { get; set; } = "";
        public DateTime LastSeenUtc { get; set; }
        public string NodeId { get; set; } = "";
        public string NodeName { get; set; } = "";
        public int Port { get; set; } = OrderReceiverPort;
        public string[] Capabilities { get; set; } = [];
    }
}

internal sealed record MobileOrderReceiverInfo(
    string NodeId,
    string NodeName,
    string Address,
    int Port,
    IReadOnlyList<string> Capabilities,
    bool Online);
