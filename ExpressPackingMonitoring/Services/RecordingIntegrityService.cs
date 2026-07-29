using ExpressPackingMonitoring.Config;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ExpressPackingMonitoring.Services;

public sealed class RecordingIntegritySession
{
    private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);
    private readonly object _sync = new();
    private long _lastSecond = long.MinValue;
    private string _lastCode = "";

    public RecordingIntegritySession()
    {
        SessionId = Convert.ToHexString(SHA256.HashData(_secret))[..12];
    }

    public string SessionId { get; }

    public string GetCode(DateTimeOffset timestamp, string? orderId)
    {
        long second = timestamp.ToUnixTimeSeconds();
        lock (_sync)
        {
            if (second == _lastSecond)
                return _lastCode;

            string payload = $"{SessionId}|{orderId?.Trim()}|{second}|{_lastCode}";
            byte[] hash = HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes(payload));
            _lastCode = Convert.ToHexString(hash)[..12];
            _lastSecond = second;
            return _lastCode;
        }
    }
}

public sealed record RecordingProofMetadata(
    long RecordId,
    string OrderId,
    string Mode,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int Width,
    int Height,
    int TargetFps,
    string Encoder,
    string WatermarkSessionId);

public sealed record RecordingProofResult(string ProofFilePath, string VideoSha256);

public sealed record RecordingProofPayload(
    int Version,
    string VideoFileName,
    string VideoSha256,
    long VideoSizeBytes,
    RecordingProofMetadata Recording,
    string DeviceName,
    string CreatedAtUtc);

public sealed record RecordingProofEnvelope(
    RecordingProofPayload Payload,
    string SignatureAlgorithm,
    string PublicKey,
    string Signature);

public sealed class RecordingIntegrityService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object KeyLock = new();
    private readonly string _keyPath;

    public RecordingIntegrityService()
    {
        _keyPath = Path.Combine(AppPaths.UserDataDir, "device-proof-key.dat");
    }

    public async Task<RecordingProofResult> CreateProofAsync(
        string videoFilePath,
        RecordingProofMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        string videoHash = await ComputeSha256Async(videoFilePath, cancellationToken).ConfigureAwait(false);
        long videoSize = new FileInfo(videoFilePath).Length;
        var payload = new RecordingProofPayload(
            1,
            Path.GetFileName(videoFilePath),
            videoHash,
            videoSize,
            metadata,
            Environment.MachineName,
            DateTimeOffset.UtcNow.ToString("O"));

        byte[] canonicalPayload = JsonSerializer.SerializeToUtf8Bytes(payload);
        using ECDsa signer = LoadOrCreateDeviceKey();
        byte[] signature = signer.SignData(canonicalPayload, HashAlgorithmName.SHA256);
        var envelope = new RecordingProofEnvelope(
            payload,
            "ECDSA-P256-SHA256",
            Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()),
            Convert.ToBase64String(signature));

        string proofPath = Path.ChangeExtension(videoFilePath, ".proof.json");
        string temporaryPath = proofPath + ".writing";
        byte[] document = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        await File.WriteAllBytesAsync(temporaryPath, document, cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, proofPath, overwrite: true);
        return new RecordingProofResult(proofPath, videoHash);
    }

    public static async Task<bool> VerifyProofAsync(
        string videoFilePath,
        string proofFilePath,
        CancellationToken cancellationToken = default)
    {
        RecordingProofEnvelope? envelope = JsonSerializer.Deserialize<RecordingProofEnvelope>(
            await File.ReadAllBytesAsync(proofFilePath, cancellationToken).ConfigureAwait(false));
        if (envelope == null) return false;

        string actualHash = await ComputeSha256Async(videoFilePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualHash, envelope.Payload.VideoSha256, StringComparison.OrdinalIgnoreCase))
            return false;

        using ECDsa verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(envelope.PublicKey), out _);
        return verifier.VerifyData(
            JsonSerializer.SerializeToUtf8Bytes(envelope.Payload),
            Convert.FromBase64String(envelope.Signature),
            HashAlgorithmName.SHA256);
    }

    private ECDsa LoadOrCreateDeviceKey()
    {
        lock (KeyLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);
            if (File.Exists(_keyPath))
            {
                byte[] protectedKey = File.ReadAllBytes(_keyPath);
                byte[] privateKey = ProtectedData.Unprotect(protectedKey, null, DataProtectionScope.CurrentUser);
                try
                {
                    ECDsa existing = ECDsa.Create();
                    existing.ImportPkcs8PrivateKey(privateKey, out _);
                    return existing;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(privateKey);
                }
            }

            using ECDsa created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] exported = created.ExportPkcs8PrivateKey();
            try
            {
                byte[] protectedKey = ProtectedData.Protect(exported, null, DataProtectionScope.CurrentUser);
                string temporaryPath = _keyPath + ".writing";
                File.WriteAllBytes(temporaryPath, protectedKey);
                File.Move(temporaryPath, _keyPath, overwrite: true);

                ECDsa result = ECDsa.Create();
                result.ImportPkcs8PrivateKey(exported, out _);
                return result;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(exported);
            }
        }
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
