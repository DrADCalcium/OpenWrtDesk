using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenWrtDesk.Core;

public sealed class RouterService : IDisposable
{
    public RouterClient Client { get; }
    public OpenClashProgramLogs ProgramLogs { get; }
    private readonly BackupStore backups;
    private readonly SemaphoreSlim mutation = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> unsupportedUntil = [];
    private readonly Dictionary<string, string> issues = [];
    private (long Total, long Idle)? previousCpu;
    private readonly Dictionary<string, (long Rx, long Tx, DateTimeOffset At)> counters = [];
    public RouterSnapshot? Last { get; private set; }
    public RouterService(RouterClient client, BackupStore backupStore) { Client = client; backups = backupStore; ProgramLogs = new(client); }
    public async Task ConnectAsync(bool enroll, CancellationToken ct = default)
    {
        await Client.LoginAsync(ct);
        var identity = await Client.ReadIdentityAsync(ct);
        if (Client.Profile.Identity.Length > 0 && Client.Profile.Identity != identity.Identity)
            throw new RouterException("该地址对应的路由器已变化。请编辑档案并重新登记设备身份", "identity");
        if (Client.Profile.Identity.Length == 0 && !enroll) throw new RouterException("尚未登记此设备，请手动连接一次", "identity");
        Client.AcceptIdentity(identity.Identity);
    }
    private async Task<JsonNode?> Optional(string name, Func<Task<JsonNode?>> action)
    {
        if (unsupportedUntil.TryGetValue(name, out var until) && until > DateTimeOffset.Now) return null;
        try { var data = await action(); issues.Remove(name); unsupportedUntil.Remove(name); return data; }
        catch (RouterException e)
        {
            issues[name] = e.Message;
            if (e.Code is "permission" or "unsupported") unsupportedUntil[name] = DateTimeOffset.Now.AddMinutes(5);
            return null;
        }
        catch (OperationCanceledException) { throw; }
    }
    public async Task<RouterSnapshot> RefreshAsync(bool detailed = false, CancellationToken ct = default)
    {
        var board = await Client.UbusAsync("system", "board", ct: ct);
        var info = await Client.UbusAsync("system", "info", ct: ct);
        var s = new RouterSnapshot
        {
            ProfileId = Client.Profile.Id, Hostname = board.Text("hostname"), Model = board.Text("model"),
            Architecture = board.Text("system"), Kernel = board.Text("kernel"), Firmware = board?["release"].Text("description") ?? "—",
            UptimeSeconds = info.Number("uptime"), Load = info.Array("load").Select(n => double.TryParse(n?.ToString(), out var x) ? x / 65535.0 : 0).ToArray(),
            MemoryTotal = info?["memory"].Long("total") ?? 0,
            MemoryAvailable = info?["memory"].Long("available") ?? 0
        };
        if (s.MemoryAvailable == 0) s.MemoryAvailable = (info?["memory"].Long("free") ?? 0) + (info?["memory"].Long("buffered") ?? 0) + (info?["memory"].Long("cached") ?? 0);
        var cpu = await Optional("CPU 专用接口", () => Client.UbusAsync("luci", "getCPUUsage", ct: ct));
        if (cpu is not null && TryNumber(cpu.Text("cpuusage"), out var usage)) s.CpuPercent = Math.Clamp(usage, 0, 100);
        else
        {
            var file = await Optional("CPU 文件接口", () => Client.UbusAsync("file", "read", new JsonObject { ["path"] = "/proc/stat" }, ct));
            if (file is not null)
            {
                var values = file.Text("data").Split('\n')[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8).Select(x => long.TryParse(x, out var n) ? n : 0).ToArray();
                if (values.Length >= 5)
                {
                    var next = (Total: values.Sum(), Idle: values[3] + values[4]);
                    if (previousCpu is { } prev && next.Total > prev.Total)
                        s.CpuPercent = Math.Clamp(100.0 * (1 - (double)(next.Idle - prev.Idle) / (next.Total - prev.Total)), 0, 100);
                    previousCpu = next;
                }
            }
        }
        var temp = await Optional("温度专用接口", () => Client.UbusAsync("luci", "getTempInfo", ct: ct));
        var temperatureText = temp.Text("tempinfo");
        if (TryNumber(temperatureText, out var temperature)) { s.Temperature = temperature; s.TemperatureText = temperatureText; }
        else
        {
            var file = await Optional("温度文件接口", () => Client.UbusAsync("file", "read", new JsonObject { ["path"] = "/sys/class/thermal/thermal_zone0/temp" }, ct));
            if (file is not null && TryNumber(file.Text("data"), out var raw)) { s.Temperature = raw / 1000; s.TemperatureText = $"{s.Temperature:0.#} °C"; }
        }
        var network = await Optional("网络接口", () => Client.UbusAsync("network.interface", "dump", ct: ct));
        var deviceMap = await Optional("网卡统计", () => Client.UbusAsync("luci-rpc", "getNetworkDevices", ct: ct));
        foreach (var item in network.Array("interface"))
        {
            var deviceName = item.Text("l3_device"); var name = item.Text("interface");
            var device = deviceMap?[deviceName];
            if (device is null && deviceName.Length > 0) device = await Optional("网卡 " + deviceName, () => Client.UbusAsync("network.device", "status", new JsonObject { ["name"] = deviceName }, ct));
            var stats = device?["stats"] ?? device?["statistics"];
            var rx = stats.Long("rx_bytes"); var tx = stats.Long("tx_bytes");
            var addresses = string.Join(" · ", item.Array("ipv4-address").Concat(item.Array("ipv6-address")).Select(n => n.Text("address")));
            s.Interfaces.Add(new(name, deviceName, item.Flag("up"), item.Text("proto"), addresses, rx, tx));
            if ((name.StartsWith("wan", StringComparison.OrdinalIgnoreCase) || item.Array("route").Any(r => r.Text("target") == "0.0.0.0")) && item.Flag("up"))
            {
                var now = DateTimeOffset.Now;
                if (counters.TryGetValue(deviceName, out var prev))
                {
                    var sec = Math.Max(.1, (now - prev.At).TotalSeconds);
                    s.WanDownload += Math.Max(0, rx - prev.Rx) / sec; s.WanUpload += Math.Max(0, tx - prev.Tx) / sec;
                }
                counters[deviceName] = (rx, tx, now);
            }
        }
        if (detailed || Last is null)
        {
            var storage = await Optional("存储空间", () => Client.UbusAsync("luci", "getMountPoints", ct: ct));
            var mounts = storage as JsonArray ?? storage?["result"] as JsonArray ?? storage?["mountpoints"] as JsonArray ?? [];
            foreach (var m in mounts)
            {
                var size = m.Long("size"); if (size == 0) size = m.Long("total");
                var mount = m.Text("mount", m.Text("mountpoint"));
                if (s.Storage.Any(d => d.Mount == mount) || mount.Contains("/overlay2/", StringComparison.Ordinal)) continue;
                s.Storage.Add(new(mount, size, m?["used"] is null ? Math.Max(0, size - m.Long("free")) : m.Long("used"), m.Long("avail")));
            }
        }
        else s.Storage = Last.Storage;
        var status = await Optional("OpenClash 状态", () => Client.LuciAsync("status", ct: ct));
        s.OpenClashInstalled = status is not null;
        s.OpenClashRunning = status.Flag("clash"); s.CoreType = status.Text("core_type", "—");
        if (s.OpenClashInstalled)
        {
            try { await Client.DiscoverControllerAsync(status, ct); }
            catch (RouterException e) { issues["Mihomo 控制接口"] = e.Message; }
            s.CaptureMode = (await Optional("捕获模式", () => Client.LuciAsync("get_run_mode", ct: ct))).Text("mode", "—");
            if (detailed || Last is null)
            {
                var list = await Optional("OpenClash 配置列表", () => Client.LuciAsync("config_file_list", ct: ct));
                s.CurrentConfig = list.Text("current_config", "—");
                s.Configs = list.Array("config_files").Select(n => new ConfigInfo(n.Text("name"), n.Text("path"), n.Long("size"), n.Text("path") == s.CurrentConfig || n.Text("name") == Path.GetFileName(s.CurrentConfig))).ToList();
                if (s.Configs.Count == 0)
                {
                    var legacy = await Optional("旧版配置列表", () => Client.LuciAsync("config_name", ct: ct));
                    s.Configs = legacy.Array("config_name").Select(n => new ConfigInfo(n.Text("name"), "/etc/openclash/config/" + n.Text("name"), 0, false)).ToList();
                }
            }
            else { s.Configs = Last.Configs; s.CurrentConfig = Last.CurrentConfig; }
        }
        if (s.OpenClashRunning && Client.HasController)
        {
            var configs = await Optional("Mihomo 配置", () => Client.ClashAsync("/configs", ct: ct));
            s.Mode = configs.Text("mode", "—"); s.LogLevel = configs.Text("log-level", "—");
            s.CoreVersion = (await Optional("Mihomo 版本", () => Client.ClashAsync("/version", ct: ct))).Text("version", "—");
            var traffic = await Optional("实时流量", () => Client.SampleStreamAsync("/traffic", ct));
            s.Upload = traffic.Number("up"); s.Download = traffic.Number("down");
            var memory = await Optional("内核内存", () => Client.SampleStreamAsync("/memory", ct)); s.CoreMemory = memory.Long("inuse");
            var connections = await Optional("连接统计", () => Client.ClashAsync("/connections", ct: ct)); s.Connections = connections.Array("connections").Count;
        }
        s.Limitations = new(issues);
        if (s.CpuPercent is not null) { s.Limitations.Remove("CPU 专用接口"); s.Limitations.Remove("CPU 文件接口"); }
        if (s.Temperature is not null) { s.Limitations.Remove("温度专用接口"); s.Limitations.Remove("温度文件接口"); }
        Last = s; return s;
    }
    public static bool TryNumber(string text, out double number)
    {
        var match = Regex.Match(text, @"-?\d+(?:\.\d+)?");
        return double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }
    private async Task Mutate(Func<CancellationToken, Task> action, CancellationToken ct)
    {
        await mutation.WaitAsync(ct);
        try { await Client.VerifyIdentityAsync(ct); await action(ct); unsupportedUntil.Clear(); }
        finally { mutation.Release(); }
    }
    public async Task<BackupInfo> BackupSettingsAsync(string label, CancellationToken ct = default)
    {
        await Client.VerifyIdentityAsync(ct);
        var raw = await Client.ReadFileAsync("/etc/config/openclash", ct);
        if (raw.Length == 0) throw new RouterException("读取到空配置，已取消写入");
        return await backups.SaveAsync(Client.Profile.Id, label, "uci", raw);
    }
    private async Task UpdateUciAsync(JsonObject fields, CancellationToken ct)
    {
        var pending = await Client.UbusAsync("uci", "changes", new JsonObject { ["config"] = "openclash" }, ct);
        if (pending?["changes"] is JsonObject changes && changes.Any(p => p.Value is JsonArray a && a.Count > 0))
            throw new RouterException("路由器还有未应用的 OpenClash 修改，请先在原管理页面处理后再操作");
        var all = await Client.UciGetAsync(ct);
        try
        {
            await Client.UciSetAsync("config", fields, ct);
            var overwrite = all.FirstOrDefault(p => p.Value.Text(".type") == "overwrite");
            if (overwrite.Key is not null) await Client.UciSetAsync(overwrite.Key, fields, ct);
            await Client.UciCommitAsync(ct);
        }
        catch
        {
            try { await Client.UbusAsync("uci", "revert", new JsonObject { ["config"] = "openclash" }, CancellationToken.None); } catch { }
            Client.DiscardSession();
            throw;
        }
        var saved = await Client.UciGetAsync(ct);
        foreach (var p in fields)
            if (saved["config"]?[p.Key]?.ToJsonString() != p.Value?.ToJsonString()) throw new RouterException("配置保存后的回读不一致，请使用备份恢复");
    }
    public Task SetModeAsync(string mode, bool persist, CancellationToken ct = default) => Mutate(async token =>
    {
        if (mode is not ("rule" or "global" or "direct")) throw new ArgumentException("模式无效");
        if (persist) { await BackupSettingsAsync("修改代理模式前", token); await UpdateUciAsync(new JsonObject { ["proxy_mode"] = mode }, token); }
        await Client.ClashAsync("/configs", HttpMethod.Patch, new JsonObject { ["mode"] = mode }, token);
        if ((await Client.ClashAsync("/configs", ct: token)).Text("mode") != mode) throw new RouterException("代理模式回读不一致");
    }, ct);
    public Task SetLogLevelAsync(string level, bool persist, CancellationToken ct = default) => Mutate(async token =>
    {
        if (level is not ("silent" or "error" or "warning" or "info" or "debug")) throw new ArgumentException("日志等级无效");
        if (persist) { await BackupSettingsAsync("修改日志等级前", token); await UpdateUciAsync(new JsonObject { ["log_level"] = level }, token); }
        await Client.ClashAsync("/configs", HttpMethod.Patch, new JsonObject { ["log-level"] = level }, token);
        if ((await Client.ClashAsync("/configs", ct: token)).Text("log-level") != level) throw new RouterException("日志等级回读不一致");
    }, ct);
    public Task ControlAsync(string action, CancellationToken ct = default) => Mutate(async token =>
    {
        if (action is not ("start" or "stop" or "restart")) throw new ArgumentException("服务操作无效");
        await BackupSettingsAsync("服务操作前：" + action, token);
        await Client.LuciAsync("action", new() { ["action"] = action }, true, token);
        await WaitRunningAsync(action != "stop", token);
    }, ct);
    private async Task WaitRunningAsync(bool expected, CancellationToken token)
    {
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(1500, token);
            try
            {
                var state = await Client.LuciAsync("status", ct: token);
                if (state.Flag("clash") == expected)
                {
                    if (expected) { await Client.DiscoverControllerAsync(state, token); await Client.ClashAsync("/version", ct: token); }
                    return;
                }
            }
            catch (RouterException) { }
        }
        throw new RouterException("操作已发送，服务尚未达到预期状态，请检查启动日志");
    }
    public Task SwitchConfigAsync(ConfigInfo config, CancellationToken ct = default) => Mutate(async token =>
    {
        if (!ValidConfigName(config.Name)) throw new ArgumentException("配置名称无效");
        await BackupSettingsAsync("切换配置前", token);
        await Client.LuciAsync("switch_config", new() { ["config_file"] = "/etc/openclash/config/" + config.Name }, true, token);
        var list = await Client.LuciAsync("config_file_list", ct: token);
        if (Path.GetFileName(list.Text("current_config")) != config.Name) throw new RouterException("配置切换尚未确认，请刷新并检查服务状态");
    }, ct);
    public Task UpdateSubscriptionAsync(string name, CancellationToken ct = default) => Mutate(async token =>
    {
        if (!ValidConfigName(name)) throw new ArgumentException("配置名称无效");
        var path = "/etc/openclash/config/" + name;
        var original = await Client.ReadFileAsync(path, token);
        await backups.SaveAsync(Client.Profile.Id, "更新订阅前：" + name, "config:" + name, original);
        await Client.LuciAsync("update_config", new() { ["filename"] = name, ["no_restart"] = "1" }, true, token);
    }, ct);
    public async Task<List<SubscriptionInfo>> SubscriptionsAsync(CancellationToken ct = default) => (await Client.UciGetAsync(ct))
        .Where(p => p.Value.Text(".type") == "config_subscribe")
        .Select(p => new SubscriptionInfo(p.Key, p.Value.Text("name"), p.Value.Text("address"), p.Value.Flag("enabled"), p.Value.Text("sub_ua"))).ToList();
    public Task SaveSubscriptionAsync(SubscriptionInfo subscription, bool delete = false, CancellationToken ct = default) => Mutate(async token =>
    {
        if (!ValidConfigName(subscription.Name.EndsWith(".yaml") || subscription.Name.EndsWith(".yml") ? subscription.Name : subscription.Name + ".yaml")) throw new ArgumentException("订阅名称只支持字母、数字、汉字及常见文件名字符");
        if (!delete && (!Uri.TryCreate(subscription.Address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))) throw new ArgumentException("请填写有效的 HTTP(S) 订阅地址");
        await BackupSettingsAsync(delete ? "删除订阅前" : "保存订阅前", token);
        var all = await Client.UciGetAsync(token);
        var section = subscription.Section;
        if (section.Length > 0 && all[section].Text(".type") != "config_subscribe") throw new RouterException("订阅已变化，请刷新后重试");
        if (delete)
        {
            if (section.Length == 0) throw new ArgumentException("请先选择订阅");
            await Client.UbusAsync("uci", "delete", new JsonObject { ["config"] = "openclash", ["section"] = section }, token);
        }
        else
        {
            var values = new JsonObject { ["name"] = subscription.Name, ["address"] = subscription.Address, ["enabled"] = subscription.Enabled ? "1" : "0", ["sub_ua"] = subscription.UserAgent };
            if (section.Length == 0)
            {
                var added = await Client.UbusAsync("uci", "add", new JsonObject { ["config"] = "openclash", ["type"] = "config_subscribe", ["values"] = values }, token);
                section = added.Text("section");
            }
            else await Client.UciSetAsync(section, values, token);
        }
        try { await Client.UciCommitAsync(token); }
        catch { Client.DiscardSession(); throw; }
        var saved = await SubscriptionsAsync(token);
        if (delete ? saved.Any(s => s.Section == section) : !saved.Any(s => s.Section == section && s.Name == subscription.Name && s.Address == subscription.Address && s.Enabled == subscription.Enabled)) throw new RouterException("订阅保存后的回读不一致");
    }, ct);
    public Task ApplyAdvancedAsync(string dnsMode, string capture, bool ipv6, bool sniffer, CancellationToken ct = default) => Mutate(async token =>
    {
        if (dnsMode is not ("fake-ip" or "redir-host") || capture is not ("redir" or "tun" or "mix")) throw new ArgumentException("运行模式无效");
        await BackupSettingsAsync("高级设置修改前", token);
        var enMode = dnsMode + (capture == "redir" ? "" : "-" + capture);
        await UpdateUciAsync(new JsonObject { ["operation_mode"] = dnsMode, ["en_mode"] = enMode, ["ipv6_enable"] = ipv6 ? "1" : "0", ["enable_meta_sniffer"] = sniffer ? "1" : "0" }, token);
        await Client.LuciAsync("action", new() { ["action"] = "restart" }, true, token);
        await WaitRunningAsync(true, token);
    }, ct);
    public Task RestoreAsync(BackupInfo backup, CancellationToken ct = default) => Mutate(async token =>
    {
        var saved = await backups.ReadAsync(backup.Id, Client.Profile.Id);
        if (saved.Info.Kind == "uci")
        {
            await BackupSettingsAsync("恢复备份前", token);
            await Client.WriteFileAsync("/etc/config/openclash", saved.Content, token);
            if (await Client.ReadFileAsync("/etc/config/openclash", token) != saved.Content) throw new RouterException("备份恢复回读不一致");
        }
        else if (saved.Info.Kind == "rules")
        {
            await backups.SaveAsync(Client.Profile.Id, "恢复规则前", "rules", await Client.ReadFileAsync("/etc/openclash/custom/openclash_custom_rules.list", token));
            await Client.WriteFileAsync("/etc/openclash/custom/openclash_custom_rules.list", saved.Content, token);
            if (await Client.ReadFileAsync("/etc/openclash/custom/openclash_custom_rules.list", token) != saved.Content) throw new RouterException("规则恢复回读不一致");
        }
        else if (saved.Info.Kind.StartsWith("config:"))
        {
            var name = saved.Info.Kind[7..]; if (!ValidConfigName(name)) throw new RouterException("备份中的文件名无效");
            var path = "/etc/openclash/config/" + name;
            var current = await Client.ReadFileAsync(path, token);
            await backups.SaveAsync(Client.Profile.Id, "恢复配置正文前：" + name, "config:" + name, current);
            await Client.UbusAsync("file", "write", new JsonObject { ["path"] = path, ["data"] = saved.Content }, token);
            if (await Client.ReadFileAsync(path, token) != saved.Content) throw new RouterException("配置正文恢复回读不一致");
        }
        else throw new RouterException("未知备份类型");
    }, ct);
    public static bool ValidConfigName(string name) => name.Length <= 120 && !name.Contains("..") && Regex.IsMatch(name, @"^[\p{L}\p{N}_ .()-]+\.ya?ml$");
    public void Dispose() { Client.Dispose(); mutation.Dispose(); }
}
