using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenWrtDesk.Core;

public static class SecretProtection
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Pointer; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
    public static string Protect(string text) => text.Length == 0 ? "" : Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(text), true));
    public static string Unprotect(string cipher) => cipher.Length == 0 ? "" : Encoding.UTF8.GetString(Transform(Convert.FromBase64String(cipher), false));
    private static byte[] Transform(byte[] bytes, bool encrypt)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("凭据保护需要 Windows");
        var input = new Blob { Length = bytes.Length, Pointer = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Pointer, bytes.Length);
            var ok = encrypt ? CryptProtectData(ref input, "OpenWrt Desk", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException("无法读取 Windows 用户凭据；请重新填写设备密码");
            var result = new byte[output.Length];
            Marshal.Copy(output.Pointer, result, 0, result.Length);
            return result;
        }
        finally
        {
            unsafe { new Span<byte>((void*)input.Pointer, input.Length).Clear(); }
            Marshal.FreeHGlobal(input.Pointer);
            if (output.Pointer != IntPtr.Zero) LocalFree(output.Pointer);
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}

public sealed class ProfileStore
{
    public string DirectoryPath { get; }
    private string FilePath => Path.Combine(DirectoryPath, "profiles.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim gate = new(1, 1);
    public ProfileStore(string? directory = null)
    {
        DirectoryPath = directory ?? Environment.GetEnvironmentVariable("OPENWRTDESK_DATA_DIR") ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(DirectoryPath);
    }
    public async Task<AppData> LoadAsync()
    {
        if (!File.Exists(FilePath)) return new AppData();
        var data = JsonSerializer.Deserialize<AppData>(await File.ReadAllTextAsync(FilePath)) ?? throw new InvalidDataException("设备档案无法读取");
        if (data.SchemaVersion != 1) throw new InvalidDataException("此档案版本暂不兼容，请使用较新版本应用");
        data.Settings.Normalize();
        return data;
    }
    public async Task SaveAsync(AppData data)
    {
        await gate.WaitAsync();
        try
        {
            data.Settings.Normalize();
            foreach (var p in data.Profiles) p.Validate();
            await AtomicWrite(FilePath, JsonSerializer.Serialize(data, JsonOptions));
        }
        finally { gate.Release(); }
    }
    public static async Task AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temp, content, Encoding.UTF8); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public async Task ExportAsync(AppData data, string path, string? password = null)
    {
        var clone = JsonSerializer.Deserialize<AppData>(JsonSerializer.Serialize(data))!;
        foreach (var p in clone.Profiles)
        {
            p.Identity = p.NetworkContext = p.RouterMac = "";
            p.PasswordCipher = password is null ? "" : SecretProtection.Unprotect(p.PasswordCipher);
            p.ControllerSecretCipher = password is null ? "" : SecretProtection.Unprotect(p.ControllerSecretCipher);
        }
        var json = JsonSerializer.Serialize(clone, JsonOptions);
        if (password is not null)
        {
            if (password.Length < 8) throw new ArgumentException("加密导出口令至少 8 个字符");
            var salt = RandomNumberGenerator.GetBytes(16);
            var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210000, HashAlgorithmName.SHA256, 32);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var raw = Encoding.UTF8.GetBytes(json); var cipher = new byte[raw.Length]; var tag = new byte[16];
            try { using var aes = new AesGcm(key, 16); aes.Encrypt(nonce, raw, cipher, tag); }
            finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(raw); }
            json = JsonSerializer.Serialize(new { format = "OpenWrtDesk.Encrypted.v1", salt = Convert.ToBase64String(salt), nonce = Convert.ToBase64String(nonce), tag = Convert.ToBase64String(tag), cipher = Convert.ToBase64String(cipher) }, JsonOptions);
        }
        await AtomicWrite(path, json);
    }
    public async Task<List<RouterProfile>> ImportAsync(string path, string? password = null)
    {
        var info = new FileInfo(path);
        if (info.Length > 8 * 1024 * 1024) throw new InvalidDataException("档案文件过大");
        var text = await File.ReadAllTextAsync(path);
        using var doc = JsonDocument.Parse(text);
        var encrypted = doc.RootElement.TryGetProperty("format", out var format);
        if (encrypted)
        {
            if (format.GetString() != "OpenWrtDesk.Encrypted.v1" || password is null) throw new InvalidDataException("这是加密档案，请填写导出口令");
            byte[] Get(string name) => Convert.FromBase64String(doc.RootElement.GetProperty(name).GetString()!);
            var key = Rfc2898DeriveBytes.Pbkdf2(password, Get("salt"), 210000, HashAlgorithmName.SHA256, 32);
            var cipher = Get("cipher"); var plain = new byte[cipher.Length];
            try { using var aes = new AesGcm(key, 16); aes.Decrypt(Get("nonce"), cipher, Get("tag"), plain); text = Encoding.UTF8.GetString(plain); }
            catch (CryptographicException) { throw new InvalidDataException("导入口令错误，或档案已损坏"); }
            finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
        }
        var imported = JsonSerializer.Deserialize<AppData>(text) ?? throw new InvalidDataException("档案内容无效");
        if (imported.SchemaVersion != 1 || imported.Profiles.Count > 100) throw new InvalidDataException("档案版本或设备数量不受支持");
        foreach (var p in imported.Profiles)
        {
            p.Validate(); p.Id = Guid.NewGuid(); p.Identity = p.NetworkContext = p.RouterMac = "";
            p.PasswordCipher = encrypted ? SecretProtection.Protect(p.PasswordCipher) : "";
            p.ControllerSecretCipher = encrypted ? SecretProtection.Protect(p.ControllerSecretCipher) : "";
        }
        return imported.Profiles;
    }
}

public sealed class BackupStore(string root)
{
    private string DirectoryPath => Path.Combine(root, "backups");
    private sealed record Record(BackupInfo Info, string Cipher);
    public async Task<BackupInfo> SaveAsync(Guid id, string label, string kind, string content)
    {
        Directory.CreateDirectory(DirectoryPath);
        var backup = new BackupInfo(Guid.NewGuid().ToString("N"), id, label, DateTimeOffset.Now, kind);
        var record = new Record(backup, SecretProtection.Protect(content));
        await ProfileStore.AtomicWrite(Path.Combine(DirectoryPath, backup.Id + ".json"), JsonSerializer.Serialize(record));
        var old = (await ListAsync(id)).Skip(20);
        foreach (var item in old) File.Delete(Path.Combine(DirectoryPath, item.Id + ".json"));
        return backup;
    }
    public async Task<List<BackupInfo>> ListAsync(Guid id)
    {
        if (!Directory.Exists(DirectoryPath)) return [];
        var list = new List<BackupInfo>();
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            try { var r = JsonSerializer.Deserialize<Record>(await File.ReadAllTextAsync(file)); if (r?.Info.ProfileId == id) list.Add(r.Info); }
            catch (JsonException) { }
        }
        return list.OrderByDescending(x => x.CreatedAt).ToList();
    }
    public async Task<(BackupInfo Info, string Content)> ReadAsync(string id, Guid profile)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("备份编号无效");
        var r = JsonSerializer.Deserialize<Record>(await File.ReadAllTextAsync(Path.Combine(DirectoryPath, id + ".json")))!;
        if (r.Info.ProfileId != profile) throw new InvalidOperationException("备份属于其他设备");
        return (r.Info, SecretProtection.Unprotect(r.Cipher));
    }
}

public sealed class HistoryStore(string root)
{
    private readonly Dictionary<Guid, List<HistoryPoint>> points = [];
    public IReadOnlyList<HistoryPoint> Get(Guid id) => points.TryGetValue(id, out var p) ? p.ToArray() : [];
    public async Task LoadAsync(Guid id)
    {
        var path = Path.Combine(root, $"history-{id:N}.json");
        try { if (File.Exists(path)) points[id] = (JsonSerializer.Deserialize<List<HistoryPoint>>(await File.ReadAllTextAsync(path)) ?? []).TakeLast(720).ToList(); }
        catch (JsonException) { points[id] = []; }
    }
    public void Add(RouterSnapshot s)
    {
        if (!points.TryGetValue(s.ProfileId, out var list)) points[s.ProfileId] = list = [];
        list.Add(new(s.CapturedAt, s.CpuPercent, s.MemoryPercent, s.Temperature, s.Upload, s.Download));
        if (list.Count > 720) list.RemoveRange(0, list.Count - 720);
    }
    public async Task SaveAsync(Guid id) => await ProfileStore.AtomicWrite(Path.Combine(root, $"history-{id:N}.json"), JsonSerializer.Serialize(Get(id)));
}

public static partial class Redaction
{
    [GeneratedRegex(@"(?i)https?://[^\s<>""']+|(?:(?:password|secret|token|authorization|cookie|订阅地址)\s*[=:]\s*)[^\s,;]+")]
    private static partial Regex Sensitive();
    [GeneratedRegex(@"(authentication[^\r\n【]*【)[^】]*(】)", RegexOptions.IgnoreCase)]
    private static partial Regex ProxyAuthentication();
    public static string Clean(string text, params string[] knownSecrets)
    {
        foreach (var secret in knownSecrets.Where(s => s.Length > 0).OrderByDescending(s => s.Length)) text = text.Replace(secret, "[已隐藏]", StringComparison.Ordinal);
        return Sensitive().Replace(ProxyAuthentication().Replace(text, "$1[已隐藏]$2"), "[已隐藏]");
    }
}
