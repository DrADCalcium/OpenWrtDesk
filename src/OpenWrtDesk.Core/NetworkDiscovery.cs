using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace OpenWrtDesk.Core;

public static class NetworkDiscovery
{
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destination, uint source, byte[] mac, ref uint length);
    public static LocalNetwork Current()
    {
        var networks = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => new { Nic = n, Properties = n.GetIPProperties() })
            .Where(n => n.Properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
            .OrderBy(n => n.Nic.Id).ToArray();
        var gateways = networks.SelectMany(n => n.Properties.GatewayAddresses).Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(g => new { Address = g.Address.ToString(), Mac = NeighborMac("http://" + g.Address) }).ToArray();
        var parts = networks.Select(n => n.Nic.Id + ":" + string.Join(",", n.Properties.GatewayAddresses.Select(g => g.Address.ToString())))
            .Concat(gateways.Select(g => g.Address + ":" + g.Mac));
        var key = (gateways.Any(g => g.Mac.Length > 0) ? "gw:" : "weak:") + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts))));
        return new(key, networks.Length > 0 ? string.Join(" · ", networks.Select(n => n.Nic.Name)) : "未检测到 IPv4 网关");
    }
    public static string NeighborMac(string address)
    {
        if (!OperatingSystem.IsWindows() || !IPAddress.TryParse(new Uri(address).Host, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return "";
        try
        {
            var target = ip.GetAddressBytes();
            var onLink = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                .Any(u =>
                {
                    var local = u.Address.GetAddressBytes(); var mask = u.IPv4Mask.GetAddressBytes();
                    return Enumerable.Range(0, 4).All(i => (target[i] & mask[i]) == (local[i] & mask[i]));
                });
            if (!onLink) return "";
            var buffer = new byte[8]; uint length = (uint)buffer.Length;
            return SendARP(BitConverter.ToUInt32(target), 0, buffer, ref length) == 0 && length == 6 ? Convert.ToHexString(buffer.AsSpan(0, 6)) : "";
        }
        catch (NetworkInformationException) { return ""; }
    }
    public static async Task<bool> ReachableAsync(RouterProfile profile, CancellationToken ct)
    {
        var uri = new Uri(profile.Address);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(1500);
        try { using var client = new TcpClient(); await client.ConnectAsync(uri.Host, uri.Port, timeout.Token); return true; }
        catch (Exception e) when (e is SocketException or OperationCanceledException) { return false; }
    }
    public static bool CanAutoAuthenticate(RouterProfile p, string networkKey, string observedMac)
    {
        if (!p.AutoConnect || p.Identity.Length == 0 || p.PasswordCipher.Length == 0 || p.NetworkContext.Length == 0 || p.NetworkContext != networkKey) return false;
        if (IPAddress.TryParse(new Uri(p.Address).Host, out var local) && IPAddress.IsLoopback(local)) return true;
        // A registered certificate pin can identify an off-link HTTPS router. On-link
        // HTTP profiles need the exact previously enrolled neighbour, before login.
        if (p.RouterMac.Length > 0) return observedMac.Length > 0 && string.Equals(p.RouterMac, observedMac, StringComparison.OrdinalIgnoreCase);
        return new Uri(p.Address).Scheme == "https" && p.CertificatePin.Length > 0 || networkKey.StartsWith("gw:", StringComparison.Ordinal);
    }
}

public sealed class WorkspaceController : IDisposable
{
    public ProfileStore Store { get; }
    public BackupStore Backups { get; }
    public HistoryStore History { get; }
    public AppData Data { get; private set; } = new();
    public RouterService? Current { get; private set; }
    public RouterSnapshot? Snapshot { get; private set; }
    public bool FixedDevice { get; set; }
    public bool IsBusy { get; private set; }
    public bool Online { get; private set; }
    public string Status { get; private set; } = "请选择设备";
    public List<DeviceSummary> Summaries { get; } = [];
    public event Action? Changed;
    public event Action<string, string>? Notification;
    private readonly SemaphoreSlim gate = new(1, 1);
    private CancellationTokenSource deviceToken = new();
    private readonly Dictionary<string, DateTimeOffset> notifications = [];
    private DateTimeOffset lastDetail;
    public WorkspaceController(string? dataDirectory = null)
    {
        Store = new(dataDirectory); Backups = new(Store.DirectoryPath); History = new(Store.DirectoryPath);
    }
    public async Task LoadAsync()
    {
        Data = await Store.LoadAsync();
        foreach (var p in Data.Profiles) await History.LoadAsync(p.Id);
    }
    public async Task ConnectAsync(RouterProfile profile, bool manual, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        IsBusy = true; Changed?.Invoke();
        try
        {
            deviceToken.Cancel(); Current?.Dispose(); deviceToken.Dispose(); deviceToken = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Current = null; Snapshot = null; Online = false; Status = "正在连接 " + profile.Name; Changed?.Invoke();
            var candidate = new RouterService(new RouterClient(profile), Backups);
            try
            {
                await candidate.ConnectAsync(manual, deviceToken.Token);
                profile.Identity = candidate.Client.Profile.Identity;
                if (manual)
                {
                    profile.NetworkContext = (await Task.Run(NetworkDiscovery.Current, deviceToken.Token)).Key;
                    profile.RouterMac = await Task.Run(() => NetworkDiscovery.NeighborMac(profile.Address), deviceToken.Token);
                }
                candidate.Client.BindNetwork(profile.NetworkContext, profile.RouterMac);
                Data.Settings.SelectedProfileId = profile.Id;
                await Store.SaveAsync(Data);
                Current = candidate;
                Snapshot = await candidate.RefreshAsync(true, deviceToken.Token);
                lastDetail = DateTimeOffset.Now;
                Online = true; Status = "已连接 · " + profile.Name;
                History.Add(Snapshot); UpdateSummary(profile, Snapshot);
            }
            catch { if (Current != candidate) candidate.Dispose(); throw; }
        }
        catch (Exception e) when (e is RouterException or HttpRequestException or TaskCanceledException)
        {
            Online = false; Status = Friendly(e); throw;
        }
        finally { IsBusy = false; gate.Release(); Changed?.Invoke(); }
    }
    public async Task<bool> AutoSelectAsync(CancellationToken ct = default)
    {
        if (!Data.Settings.AutoSelect || FixedDevice || IsBusy) return false;
        var context = await Task.Run(NetworkDiscovery.Current, ct);
        var candidates = new List<RouterProfile>();
        foreach (var p in Data.Profiles.Where(p => p.NetworkContext == context.Key && p.AutoConnect))
        {
            var mac = await Task.Run(() => NetworkDiscovery.NeighborMac(p.Address), ct);
            if (NetworkDiscovery.CanAutoAuthenticate(p, context.Key, mac) && await NetworkDiscovery.ReachableAsync(p, ct)) candidates.Add(p);
        }
        // Never try different credentials for one address or guess between candidates.
        if (candidates.Count != 1)
        {
            Status = candidates.Count > 1 ? "发现多个匹配设备，请手动选择或固定当前设备" : "当前网络没有明确匹配的已登记设备，请手动连接";
            Changed?.Invoke(); return false;
        }
        if (Current?.Client.Profile.Id == candidates[0].Id && Online) return true;
        await ConnectAsync(candidates[0], false, ct); return true;
    }
    public async Task DisconnectAsync()
    {
        await gate.WaitAsync();
        try
        {
            deviceToken.Cancel(); Current?.Dispose(); Current = null; Snapshot = null;
            Online = false; Status = "已断开设备"; Data.Settings.SelectedProfileId = null;
            await Store.SaveAsync(Data);
        }
        finally { gate.Release(); Changed?.Invoke(); }
    }
    public async Task RefreshAsync(bool detailed = false)
    {
        if (Current is null || !await gate.WaitAsync(0)) return;
        try
        {
            var previousOnline = Online;
            Snapshot = await Current.RefreshAsync(detailed || DateTimeOffset.Now - lastDetail > TimeSpan.FromSeconds(30), deviceToken.Token);
            if (detailed || DateTimeOffset.Now - lastDetail > TimeSpan.FromSeconds(30)) lastDetail = DateTimeOffset.Now;
            Online = true; Status = "已连接 · " + Current.Client.Profile.Name;
            History.Add(Snapshot); UpdateSummary(Current.Client.Profile, Snapshot);
            if (!previousOnline) Notify("连接恢复", "已重新连接 " + Current.Client.Profile.Name, "reconnect");
            if (Snapshot.Temperature >= Data.Settings.TemperatureWarning) Notify("温度提醒", $"{Current.Client.Profile.Name} 温度 {Snapshot.Temperature:0.#} °C", "temperature");
            if (Snapshot.MemoryPercent >= Data.Settings.MemoryWarning) Notify("内存提醒", $"{Current.Client.Profile.Name} 内存使用 {Snapshot.MemoryPercent:0.#}%", "memory");
        }
        catch (OperationCanceledException) when (deviceToken.IsCancellationRequested) { }
        catch (Exception e)
        {
            var wasOnline = Online; Online = false; Status = Friendly(e);
            if (Current is not null) UpdateSummary(Current.Client.Profile, null, Status);
            if (wasOnline) Notify("设备离线", Current?.Client.Profile.Name + " 暂时无法连接", "offline");
        }
        finally { gate.Release(); Changed?.Invoke(); }
    }
    public async Task RunAsync(Func<RouterService, CancellationToken, Task> action)
    {
        await gate.WaitAsync(); IsBusy = true; Changed?.Invoke();
        try
        {
            var target = Current ?? throw new RouterException("请先连接设备");
            if (!Online) throw new RouterException("设备当前离线，请先重新连接");
            // Selection and all writes share this gate. The target is captured once.
            await action(target, deviceToken.Token);
            Snapshot = await target.RefreshAsync(true, deviceToken.Token); lastDetail = DateTimeOffset.Now;
            Online = true; Status = "操作完成 · 已回读设备状态"; History.Add(Snapshot);
        }
        finally { IsBusy = false; gate.Release(); Changed?.Invoke(); }
    }
    public async Task MonitorOthersAsync(CancellationToken ct = default)
    {
        if (!Data.Settings.MonitorAll || IsBusy) return;
        var context = await Task.Run(NetworkDiscovery.Current, ct);
        foreach (var p in Data.Profiles.Where(p => p.Id != Current?.Client.Profile.Id).Take(20).ToArray())
        {
            var mac = await Task.Run(() => NetworkDiscovery.NeighborMac(p.Address), ct);
            if (!NetworkDiscovery.CanAutoAuthenticate(p, context.Key, mac)) { UpdateSummary(p, null, "未匹配当前网络"); continue; }
            if (!await NetworkDiscovery.ReachableAsync(p, ct)) { UpdateSummary(p, null, "离线"); continue; }
            try
            {
                using var service = new RouterService(new RouterClient(p), Backups);
                await service.ConnectAsync(false, ct);
                var snapshot = await service.RefreshAsync(false, ct);
                History.Add(snapshot); UpdateSummary(p, snapshot);
            }
            catch (RouterException e) { UpdateSummary(p, null, e.Message); }
        }
        Changed?.Invoke();
    }
    public async Task SaveHistoryAsync()
    {
        foreach (var p in Data.Profiles.ToArray()) await History.SaveAsync(p.Id);
    }
    private void UpdateSummary(RouterProfile p, RouterSnapshot? s, string? status = null)
    {
        Summaries.RemoveAll(x => x.Id == p.Id);
        Summaries.Add(new(p.Id, p.Name, s is not null, status ?? "在线", s?.CpuPercent, s?.MemoryPercent, s?.Temperature, DateTimeOffset.Now));
    }
    private void Notify(string title, string text, string category)
    {
        var key = Current?.Client.Profile.Id + category;
        if (!Data.Settings.Notifications || notifications.TryGetValue(key, out var last) && DateTimeOffset.Now - last < TimeSpan.FromMinutes(5)) return;
        notifications[key] = DateTimeOffset.Now; Notification?.Invoke(title, text);
    }
    public static string Friendly(Exception e) => e switch
    {
        RouterException or ArgumentException or InvalidDataException or System.Security.Cryptography.CryptographicException => e.Message,
        OperationCanceledException => "请求超时或已取消",
        HttpRequestException => "无法连接设备，请检查网络与管理地址",
        UnauthorizedAccessException => "无法访问本地数据目录，请检查文件权限",
        _ => "操作未完成（" + e.GetType().Name + "），请重试并检查设备状态"
    };
    public void Dispose() { deviceToken.Cancel(); deviceToken.Dispose(); Current?.Dispose(); gate.Dispose(); }
}
