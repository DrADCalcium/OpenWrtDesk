using System.Text.Json.Serialization;

namespace OpenWrtDesk.Core;

public sealed class RouterProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "我的软路由";
    public string Group { get; set; } = "常用设备";
    public string Address { get; set; } = "http://192.168.1.1";
    public string Username { get; set; } = "root";
    public string PasswordCipher { get; set; } = "";
    public string ControllerAddress { get; set; } = "";
    public string ControllerSecretCipher { get; set; } = "";
    public string CertificatePin { get; set; } = "";
    public string Identity { get; set; } = "";
    public string NetworkContext { get; set; } = "";
    public string RouterMac { get; set; } = "";
    public int Order { get; set; }
    public bool AutoConnect { get; set; } = true;
    [JsonIgnore] public string Label => $"{Group} · {Name}";
    public RouterProfile Copy() => System.Text.Json.JsonSerializer.Deserialize<RouterProfile>(System.Text.Json.JsonSerializer.Serialize(this))!;
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("请填写设备名称");
        Address = NormalizeAddress(Address);
        if (string.IsNullOrWhiteSpace(Username)) throw new ArgumentException("请填写用户名");
        if (ControllerAddress.Length > 0) ControllerAddress = NormalizeAddress(ControllerAddress);
        var pin = CertificatePin.Replace(":", "").Replace(" ", "");
        if (pin.Length > 0 && (pin.Length != 64 || !pin.All(Uri.IsHexDigit))) throw new ArgumentException("证书 SHA-256 指纹应为 64 位十六进制字符");
        CertificatePin = pin.ToUpperInvariant();
    }
    public static string NormalizeAddress(string text)
    {
        text = text.Trim();
        if (!text.Contains("://")) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/")
            throw new ArgumentException("请输入管理地址和端口，例如 http://192.168.1.1，地址中不要包含密码或其他路径");
        return uri.GetLeftPart(UriPartial.Authority);
    }
}

public sealed class AppSettings
{
    public string Theme { get; set; } = "Default";
    public int RefreshSeconds { get; set; } = 5;
    public int BackgroundSeconds { get; set; } = 30;
    public bool AutoSelect { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool MonitorAll { get; set; }
    public bool Notifications { get; set; } = true;
    public double TemperatureWarning { get; set; } = 80;
    public double MemoryWarning { get; set; } = 90;
    public Guid? SelectedProfileId { get; set; }
    public double WindowWidth { get; set; } = 1240;
    public double WindowHeight { get; set; } = 880;
    public void Normalize()
    {
        RefreshSeconds = Math.Clamp(RefreshSeconds, 2, 60);
        BackgroundSeconds = Math.Clamp(BackgroundSeconds, 15, 300);
        WindowWidth = Math.Clamp(WindowWidth, 860, 2200);
        WindowHeight = Math.Clamp(WindowHeight, 620, 1600);
        TemperatureWarning = Math.Clamp(TemperatureWarning, 40, 120);
        MemoryWarning = Math.Clamp(MemoryWarning, 50, 99);
    }
}

public sealed class AppData
{
    public int SchemaVersion { get; set; } = 1;
    public List<RouterProfile> Profiles { get; set; } = [];
    public AppSettings Settings { get; set; } = new();
}

public sealed record NetworkInterfaceInfo(string Name, string Device, bool Up, string Protocol, string Addresses, long RxBytes, long TxBytes);
public sealed record StorageInfo(string Mount, long Total, long Used, long Available);
public sealed record ConfigInfo(string Name, string Path, long Size, bool Current);
public sealed record SubscriptionInfo(string Section, string Name, string Address, bool Enabled, string UserAgent);
public sealed record BackupInfo(string Id, Guid ProfileId, string Label, DateTimeOffset CreatedAt, string Kind);
public sealed record HistoryPoint(DateTimeOffset Time, double? Cpu, double? Memory, double? Temperature, double Up, double Down);
public sealed record DeviceSummary(Guid Id, string Name, bool Online, string Status, double? Cpu, double? Memory, double? Temperature, DateTimeOffset CheckedAt);
public sealed record LocalNetwork(string Key, string Description);

public sealed class RouterSnapshot
{
    public Guid ProfileId { get; init; }
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;
    public string Model { get; set; } = "—";
    public string Hostname { get; set; } = "—";
    public string Architecture { get; set; } = "—";
    public string Firmware { get; set; } = "—";
    public string Kernel { get; set; } = "—";
    public double UptimeSeconds { get; set; }
    public double[] Load { get; set; } = [];
    public double? CpuPercent { get; set; }
    public double? Temperature { get; set; }
    public string TemperatureText { get; set; } = "未获取";
    public long MemoryTotal { get; set; }
    public long MemoryAvailable { get; set; }
    public double? MemoryPercent => MemoryTotal > 0 ? (1 - (double)MemoryAvailable / MemoryTotal) * 100 : null;
    public bool OpenClashInstalled { get; set; }
    public bool OpenClashRunning { get; set; }
    public string CoreVersion { get; set; } = "—";
    public string Mode { get; set; } = "—";
    public string CaptureMode { get; set; } = "—";
    public string LogLevel { get; set; } = "—";
    public string CurrentConfig { get; set; } = "—";
    public string CoreType { get; set; } = "—";
    public long CoreMemory { get; set; }
    public double Upload { get; set; }
    public double Download { get; set; }
    public double WanUpload { get; set; }
    public double WanDownload { get; set; }
    public int Connections { get; set; }
    public List<NetworkInterfaceInfo> Interfaces { get; set; } = [];
    public List<StorageInfo> Storage { get; set; } = [];
    public List<ConfigInfo> Configs { get; set; } = [];
    public Dictionary<string, string> Limitations { get; set; } = [];
}

public static class Display
{
    public static string Bytes(double value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var index = 0;
        while (Math.Abs(value) >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return $"{value:0.#} {units[index]}";
    }
    public static string Rate(double value) => Bytes(value) + "/s";
    public static string Percent(double? value) => value is { } x ? $"{x:0.#}%" : "未获取";
    public static string Uptime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{t.Days} 天 {t.Hours} 时 {t.Minutes} 分";
    }
}
