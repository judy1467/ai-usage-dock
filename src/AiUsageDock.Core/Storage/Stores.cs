using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiUsageDock.Core.Abstractions;
using AiUsageDock.Core.Models;

namespace AiUsageDock.Core.Storage;

public sealed record PollSettings(int DeepSeek = 300, int Antigravity = 30);

public sealed record DockSettings(
    string Monitor = "primary",
    int OffsetX = 0,
    double Opacity = 0.96,
    string Theme = "dark",
    bool HideOnFullscreen = true);

public sealed record AlertSettings(decimal LowBalance = 2.0m, double LowQuota = 0.15, bool Enabled = true);

public sealed record AppSettings(
    int Schema = 1,
    PollSettings? PollSeconds = null,
    IReadOnlyList<string>? Pinned = null,
    string Currency = "USD",
    DockSettings? Dock = null,
    bool StartWithWindows = false,
    AlertSettings? Alerts = null)
{
    public static AppSettings CreateDefault() => new(
        Schema: 1,
        PollSeconds: new PollSettings(),
        Pinned: new[] { "gemini-4-argon", "gemini-3.8-flash", "claude-opus-5-5" },
        Currency: "USD",
        Dock: new DockSettings(),
        StartWithWindows: false,
        Alerts: new AlertSettings());
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;

    public SettingsStore(string directoryPath)
    {
        Directory.CreateDirectory(directoryPath);
        _path = Path.Combine(directoryPath, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return AppSettings.CreateDefault();
            }

            var json = File.ReadAllText(_path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
            {
                return AppSettings.CreateDefault();
            }

            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? AppSettings.CreateDefault();
        }
        catch (Exception)
        {
            return AppSettings.CreateDefault();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var json = JsonSerializer.Serialize(settings, Options);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        File.Move(tmp, _path, overwrite: true);
    }
}

public sealed class InMemorySecretStore : ISecretStore
{
    private string? _apiKey;

    public InMemorySecretStore(string? initialKey = null)
    {
        _apiKey = initialKey;
    }

    public string? GetDeepSeekApiKey() => _apiKey;

    public void SetDeepSeekApiKey(string? apiKey) => _apiKey = apiKey;

    public void Clear() => _apiKey = null;
}

[SupportedOSPlatform("windows")]
public sealed class FileSecretStore : ISecretStore
{
    // v0.1 레거시(XOR) 마이그레이션 전용. 신규 저장에는 절대 쓰지 않는다.
    private static readonly byte[] LegacyXorKey = Encoding.UTF8.GetBytes("ai-usage-dock.v1.secret");

    private readonly string _path;

    public FileSecretStore(string directoryPath)
    {
        Directory.CreateDirectory(directoryPath);
        _path = Path.Combine(directoryPath, "secret.dat");
    }

    public string? GetDeepSeekApiKey()
    {
        try
        {
            if (File.Exists(_path))
            {
                var bytes = File.ReadAllBytes(_path);
                if (bytes.Length > 0)
                {
                    // 1) DPAPI(CurrentUser) 우선
                    var text = TryUnprotect(bytes);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }

                    // 2) 구버전 XOR 파일이면 읽어서 DPAPI로 재저장 (무손실 마이그레이션)
                    var legacy = TryLegacyUnxor(bytes);
                    if (!string.IsNullOrWhiteSpace(legacy))
                    {
                        SetDeepSeekApiKey(legacy);
                        return legacy;
                    }
                }
            }
        }
        catch (Exception)
        {
            // ignore and fall back to the environment
        }

        var fromEnv = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv;
    }

    public void SetDeepSeekApiKey(string? apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            Clear();
            return;
        }

        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(apiKey),
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);

        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, protectedBytes);
        File.Move(tmp, _path, overwrite: true);
    }

    public void Clear()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            // 삭제 전에 같은 길이의 0으로 덮어쓴다 (best effort).
            var length = (int)new FileInfo(_path).Length;
            File.WriteAllBytes(_path, new byte[length]);
            File.Delete(_path);
        }
        catch (Exception)
        {
            // best effort
        }
    }

    private static string? TryUnprotect(byte[] blob)
    {
        try
        {
            var plain = ProtectedData.Unprotect(blob, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static string? TryLegacyUnxor(byte[] blob)
    {
        try
        {
            var output = new byte[blob.Length];
            for (var i = 0; i < blob.Length; i++)
            {
                output[i] = (byte)(blob[i] ^ LegacyXorKey[i % LegacyXorKey.Length]);
            }

            var text = Encoding.UTF8.GetString(output);
            return text.StartsWith("sk-", StringComparison.Ordinal) ? text : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

public sealed class HistoryStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private readonly string _path;
    private readonly object _gate = new();

    public HistoryStore(string directoryPath)
    {
        Directory.CreateDirectory(directoryPath);
        _path = Path.Combine(directoryPath, "history.jsonl");
    }

    public void Append(BalanceHistoryPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);

        var line = JsonSerializer.Serialize(
            new HistoryLine(point.Timestamp.ToUniversalTime(), point.Currency, point.Total),
            Options);

        lock (_gate)
        {
            File.AppendAllText(_path, line + "\n", new UTF8Encoding(false));
        }
    }

    public IReadOnlyList<BalanceHistoryPoint> LoadRecent(DateTimeOffset cutoffUtc)
    {
        var results = new List<BalanceHistoryPoint>();

        string[] lines;
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return results;
            }

            try
            {
                lines = File.ReadAllLines(_path, Encoding.UTF8);
            }
            catch (Exception)
            {
                return results;
            }
        }

        var cutoff = cutoffUtc.ToUniversalTime();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var dto = JsonSerializer.Deserialize<HistoryLine>(line, Options);
                if (dto is null)
                {
                    continue;
                }

                var ts = dto.Ts.ToUniversalTime();
                if (ts < cutoff)
                {
                    continue;
                }

                results.Add(new BalanceHistoryPoint(ts, dto.Currency, dto.Total));
            }
            catch (Exception)
            {
                // skip malformed line
            }
        }

        return results;
    }

    private sealed record HistoryLine(
        [property: JsonPropertyName("ts")] DateTimeOffset Ts,
        [property: JsonPropertyName("currency")] string Currency,
        [property: JsonPropertyName("total")] decimal Total);
}
