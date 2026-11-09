using OpenWrtDesk.Core;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

Console.OutputEncoding = Encoding.UTF8;
var directory = args.ElementAtOrDefault(1) ?? Path.Combine(Environment.CurrentDirectory, "work", "checks-data");
if (args.FirstOrDefault() == "--seed")
{
    var address = RouterProfile.NormalizeAddress(args.ElementAtOrDefault(2) ?? throw new ArgumentException("Provide the router address explicitly: --seed <data-directory> <router-address>"));
    var password = Console.ReadLine() ?? throw new Exception("Password is required on stdin");
    var store = new ProfileStore(directory); var data = await store.LoadAsync();
    var profile = data.Profiles.FirstOrDefault(p => p.Address == address) ?? new RouterProfile { Name = "当前软路由", Address = address, Group = "日常使用" };
    profile.PasswordCipher = SecretProtection.Protect(password); profile.Identity = "";
    if (!data.Profiles.Contains(profile)) data.Profiles.Add(profile);
    data.Settings.SelectedProfileId = profile.Id; await store.SaveAsync(data);
    using var workspace = new WorkspaceController(directory); await workspace.LoadAsync();
    var saved = workspace.Data.Profiles.Single(p => p.Id == profile.Id); await workspace.ConnectAsync(saved, true); await workspace.RefreshAsync(true);
    Console.WriteLine($"Seeded and enrolled: {workspace.Snapshot!.Model}; profiles={workspace.Data.Profiles.Count}; online={workspace.Online}");
    await workspace.SaveHistoryAsync(); return 0;
}
if (args.FirstOrDefault() == "--diagnose")
{
    using var workspace = new WorkspaceController(directory); await workspace.LoadAsync();
    var profile = workspace.Data.Profiles.First(); await workspace.ConnectAsync(profile, true); var client = workspace.Current!.Client;
    Console.WriteLine("Network context: " + NetworkDiscovery.Current()); Console.WriteLine("Direct neighbour: " + NetworkDiscovery.NeighborMac(profile.Address));
    foreach (var pair in new[] { ("luci-rpc", "getNetworkDevices"), ("luci", "getMountPoints") })
    {
        try
        {
            var node = await client.UbusAsync(pair.Item1, pair.Item2);
            if (pair.Item2 == "getMountPoints") Console.WriteLine("mount sample: " + (node as JsonArray ?? node?["result"] as JsonArray)?.FirstOrDefault()?.ToJsonString());
            else if (node is JsonObject obj) Console.WriteLine("device schema: " + string.Join("; ", obj.Take(5).Select(p => p.Key + ":" + (p.Value is JsonObject fields ? string.Join(",", fields.Select(f => f.Key)) : p.Value?.GetType().Name))));
        }
        catch (Exception e) { Console.WriteLine(WorkspaceController.Friendly(e)); }
    }
    Console.WriteLine("memory: " + (await client.SampleStreamAsync("/memory"))?.ToJsonString());
    return 0;
}
if (args.FirstOrDefault() == "--program-logs")
{
    using var workspace = new WorkspaceController(directory); await workspace.LoadAsync();
    var profile = workspace.Data.Profiles.First(p => p.Id == workspace.Data.Settings.SelectedProfileId);
    await workspace.ConnectAsync(profile, true);
    var log = await workspace.Current!.ProgramLogs.ReadAsync();
    var second = await workspace.Current.ProgramLogs.ReadAsync(log.Cursor);
    using var cts = new CancellationTokenSource(400);
    var cancelled = false;
    try { await workspace.Current.ProgramLogs.WatchAsync(_ => { }, cts.Token); } catch (OperationCanceledException) { cancelled = true; }
    var result = new { version = "0.1.2", time = DateTimeOffset.Now, readOnly = true, lineCount = log.Lines.Length, cursor = log.Cursor,
        secondLineCount = second.Lines.Length, cancellationPassed = cancelled, coreLinesExcluded = log.Lines.All(line => !line.Contains("level=")) };
    await ProfileStore.AtomicWrite(Path.Combine(Environment.CurrentDirectory, "outputs", "program-log-validation-0.1.2.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(result)); return cancelled ? 0 : 1;
}
if (args.FirstOrDefault() == "--live")
{
    using var workspace = new WorkspaceController(directory); await workspace.LoadAsync();
    var profile = workspace.Data.Profiles.First(p => p.Id == workspace.Data.Settings.SelectedProfileId);
    await workspace.ConnectAsync(profile, true); await workspace.RefreshAsync(true);
    var service = workspace.Current!; var s = workspace.Snapshot!;
    var report = new JsonObject { ["time"] = DateTimeOffset.Now.ToString("O"), ["model"] = s.Model, ["firmware"] = s.Firmware, ["cpu"] = s.CpuPercent, ["temperature"] = s.Temperature, ["memoryTotal"] = s.MemoryTotal, ["storageMounts"] = s.Storage.Count, ["limitations"] = JsonSerializer.SerializeToNode(s.Limitations), ["checks"] = new JsonArray() };
    var list = (JsonArray)report["checks"]!;
    async Task Live(string name, Func<Task> action)
    {
        try { await action(); list.Add(new JsonObject { ["name"] = name, ["passed"] = true }); Console.WriteLine("PASS " + name); }
        catch (Exception e) { list.Add(new JsonObject { ["name"] = name, ["passed"] = false, ["reason"] = WorkspaceController.Friendly(e) }); Console.WriteLine("FAIL " + name + ": " + WorkspaceController.Friendly(e)); }
    }
    var original = await service.Client.ReadFileAsync("/etc/config/openclash");
    var initial = await service.Client.ClashAsync("/configs"); var level = initial.Text("log-level");
    var restorePoint = await service.BackupSettingsAsync("联调前的原始配置");
    try
    {
        await Live("read hardware and network", () => { if (s.MemoryTotal <= 0 || s.CpuPercent is null || s.Temperature is null || s.Interfaces.Count == 0) throw new Exception("Missing readings"); return Task.CompletedTask; });
        await Live("read subscriptions", async () => { var subs = await service.SubscriptionsAsync(); report["subscriptionCount"] = subs.Count; });
        await Live("mode PATCH and readback (same mode)", async () => await service.SetModeAsync(initial.Text("mode"), false));
        await Live("runtime log level change and readback", async () => await service.SetLogLevelAsync(level == "warning" ? "info" : "warning", false));
        await Live("restore runtime log level", async () => await service.SetLogLevelAsync(level, false));
        await Live("persistent save and backup", async () => await service.SetLogLevelAsync(level, true));
        await Live("subscription add edit delete", async () =>
        {
            var name = "OpenWrtDesk-check-" + Guid.NewGuid().ToString("N")[..8];
            await service.SaveSubscriptionAsync(new("", name, "https://example.invalid/unused", false, "OpenWrtDesk-check"));
            var added = (await service.SubscriptionsAsync()).Single(x => x.Name == name);
            await service.SaveSubscriptionAsync(added with { UserAgent = "OpenWrtDesk-check-edit" });
            await service.SaveSubscriptionAsync(added, true);
        });
        await Live("backup restore and exact file readback", async () => { await service.RestoreAsync(restorePoint); if (await service.Client.ReadFileAsync("/etc/config/openclash") != original) throw new Exception("Restored content differs"); });
        await Live("OpenClash program logs", async () => { var batch = await service.ProgramLogs.ReadAsync(); report["programLogLines"] = batch.Lines.Length; });
        await Live("automatic registered-device selection", async () => { if (!await workspace.AutoSelectAsync()) throw new Exception("Network did not match"); });
    }
    finally
    {
        if (await service.Client.ReadFileAsync("/etc/config/openclash") != original) await service.RestoreAsync(restorePoint);
        if ((await service.Client.ClashAsync("/configs")).Text("log-level") != level) await service.SetLogLevelAsync(level, false);
        report["originalConfigRestored"] = await service.Client.ReadFileAsync("/etc/config/openclash") == original;
        report["originalRuntimeRestored"] = (await service.Client.ClashAsync("/configs")).Text("log-level") == level;
        var target = args.ElementAtOrDefault(2) ?? Path.Combine(Environment.CurrentDirectory, "outputs", "live-validation.json");
        await ProfileStore.AtomicWrite(target, report.ToJsonString(new() { WriteIndented = true }));
        Console.WriteLine($"Live result: {list.Count(n => n?["passed"]?.GetValue<bool>() == true)}/{list.Count}");
    }
    return list.Any(n => n?["passed"]?.GetValue<bool>() == false) ? 1 : 0;
}

var passed = 0; var failed = 0;
async Task Check(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.GetType().Name + " " + e.Message); }
}
void Assert(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
async Task Throws<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
var run = Path.Combine(directory, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(run);
await Check("DPAPI roundtrip and ciphertext", () => { var cipher = SecretProtection.Protect("fixture-password"); Assert(!cipher.Contains("fixture-password")); Assert(SecretProtection.Unprotect(cipher) == "fixture-password"); return Task.CompletedTask; });
await Check("profile address validation", async () => { Assert(RouterProfile.NormalizeAddress("192.0.2.1:8080") == "http://192.0.2.1:8080"); foreach (var bad in new[] { "ftp://host", "http://user:pass@host", "http://host/path", "http://host?password=x" }) await Throws<ArgumentException>(() => Task.FromResult(RouterProfile.NormalizeAddress(bad))); });
await Check("plain export excludes credentials and binding", async () =>
{
    var store = new ProfileStore(run); var data = new AppData { Profiles = [new RouterProfile { PasswordCipher = SecretProtection.Protect("fixture-password"), Identity = "identity", RouterMac = "AA", NetworkContext = "network" }] };
    var path = Path.Combine(run, "plain.json"); await store.ExportAsync(data, path); var text = await File.ReadAllTextAsync(path);
    Assert(!text.Contains("fixture-password") && !text.Contains("identity") && !text.Contains(data.Profiles[0].PasswordCipher));
    var imported = await store.ImportAsync(path); Assert(imported[0].PasswordCipher == "" && imported[0].Id != data.Profiles[0].Id);
});
await Check("encrypted export portable roundtrip and wrong password", async () =>
{
    var store = new ProfileStore(run); var path = Path.Combine(run, "encrypted.json");
    var data = new AppData { Profiles = [new RouterProfile { PasswordCipher = SecretProtection.Protect("fixture-password"), ControllerSecretCipher = SecretProtection.Protect("fixture-api-key") }] };
    await store.ExportAsync(data, path, "archive-passphrase"); var text = await File.ReadAllTextAsync(path); Assert(!text.Contains("fixture-password"));
    var restored = await store.ImportAsync(path, "archive-passphrase"); Assert(SecretProtection.Unprotect(restored[0].PasswordCipher) == "fixture-password");
    await Throws<InvalidDataException>(async () => await store.ImportAsync(path, "wrong-password"));
});
await Check("history cap and persistence", async () => { var id = Guid.NewGuid(); var store = new HistoryStore(run); for (var i = 0; i < 750; i++) store.Add(new RouterSnapshot { ProfileId = id, CpuPercent = i % 100 }); Assert(store.Get(id).Count == 720); await store.SaveAsync(id); var copy = new HistoryStore(run); await copy.LoadAsync(id); Assert(copy.Get(id).Count == 720); });
await Check("backup encryption isolation retention", async () =>
{
    var store = new BackupStore(run); var id = Guid.NewGuid(); BackupInfo? last = null;
    for (var i = 0; i < 23; i++) last = await store.SaveAsync(id, "test", "uci", "fixture-password");
    Assert((await store.ListAsync(id)).Count == 20); Assert((await store.ReadAsync(last!.Id, id)).Content == "fixture-password");
    await Throws<InvalidOperationException>(async () => await store.ReadAsync(last.Id, Guid.NewGuid()));
    Assert(!Directory.EnumerateFiles(Path.Combine(run, "backups")).Any(p => File.ReadAllText(p).Contains("fixture-password")));
});
await Check("network recognition never guesses credentials", () =>
{
    var p = new RouterProfile { PasswordCipher = "cipher", Identity = "id", NetworkContext = "home", RouterMac = "AABB" };
    Assert(NetworkDiscovery.CanAutoAuthenticate(p, "home", "AABB")); Assert(!NetworkDiscovery.CanAutoAuthenticate(p, "other", "AABB")); Assert(!NetworkDiscovery.CanAutoAuthenticate(p, "home", "CCDD")); Assert(!NetworkDiscovery.CanAutoAuthenticate(p, "home", "")); return Task.CompletedTask;
});
await Check("log credential and subscription redaction", () => { var text = Redaction.Clean("password=abc token=xyz https://example.com/sub?key=secret Bearer known-key", "known-key"); Assert(!text.Contains("abc") && !text.Contains("xyz") && !text.Contains("example.com") && !text.Contains("known-key")); return Task.CompletedTask; });
await Check("config path restrictions", () => { Assert(RouterService.ValidConfigName("家庭配置.yaml")); Assert(!RouterService.ValidConfigName("../../evil.yaml")); return Task.CompletedTask; });

await using var routerServer = new FakeRouter();
var fixture = new RouterProfile { Address = routerServer.Address, Name = "Mock router", PasswordCipher = SecretProtection.Protect("fixture-password") };
using var serviceFixture = new RouterService(new RouterClient(fixture), new BackupStore(run));
await Check("LuCI cookie authentication and identity enrollment", async () => { await serviceFixture.ConnectAsync(true); Assert(serviceFixture.Client.Profile.Identity.Length == 64); });
await Check("hardware monitoring and controller authentication", async () => { var s = await serviceFixture.RefreshAsync(true); Assert(s.MemoryTotal > 0 && s.Temperature == 42.5 && s.CpuPercent == 12); Assert(routerServer.KernelOnlyRequests == 0); Assert(routerServer.ApiAuthorized); });
await Check("CPU fallback uses deltas", async () => { routerServer.DedicatedCpu = false; var second = new RouterService(new RouterClient(fixture), new BackupStore(run)); await second.ConnectAsync(true); await second.RefreshAsync(true); var s = await second.RefreshAsync(true); Assert(s.CpuPercent is > 0 and < 100); second.Dispose(); routerServer.DedicatedCpu = true; });
await Check("runtime mode and log readback", async () => { await serviceFixture.SetModeAsync("direct", false); Assert(routerServer.Mode == "direct"); await serviceFixture.SetLogLevelAsync("debug", false); Assert(routerServer.Level == "debug"); });
await Check("persistent options update backup and rollback", async () => { var original = routerServer.FileContent; var backup = await serviceFixture.BackupSettingsAsync("before"); await serviceFixture.SetModeAsync("rule", true); Assert(routerServer.Uci["config"].Text("proxy_mode") == "rule"); await serviceFixture.RestoreAsync(backup); Assert(routerServer.FileContent == original); });
await Check("pending router edits block persistent writes", async () => { routerServer.PendingChanges = true; await Throws<RouterException>(() => serviceFixture.SetLogLevelAsync("info", true)); routerServer.PendingChanges = false; });
await Check("subscription CRUD and field preservation", async () => { await serviceFixture.SaveSubscriptionAsync(new("", "fixture-sub", "https://example.invalid", false, "test")); var sub = (await serviceFixture.SubscriptionsAsync()).Single(); await serviceFixture.SaveSubscriptionAsync(sub with { Enabled = true }); Assert((await serviceFixture.SubscriptionsAsync()).Single().Enabled); await serviceFixture.SaveSubscriptionAsync(sub, true); Assert((await serviceFixture.SubscriptionsAsync()).Count == 0); });
await Check("service start stop restart readback", async () => { await serviceFixture.ControlAsync("stop"); Assert(!routerServer.Running); await serviceFixture.ControlAsync("start"); Assert(routerServer.Running); await serviceFixture.ControlAsync("restart"); Assert(routerServer.Running); });
await Check("advanced settings persistence and restart", async () => { await serviceFixture.ApplyAdvancedAsync("redir-host", "tun", true, true); Assert(routerServer.Uci["config"].Text("en_mode") == "redir-host-tun"); Assert(routerServer.Uci["config"].Text("ipv6_enable") == "1"); });
await Check("session expiration read reconnect", async () => { var net = NetworkDiscovery.Current(); serviceFixture.Client.BindNetwork(net.Key, NetworkDiscovery.NeighborMac(fixture.Address)); var before = routerServer.Logins; routerServer.ExpireOnce = true; await serviceFixture.RefreshAsync(); Assert(routerServer.Logins == before + 1); });
await Check("identity mismatch blocks every mutation", async () => { var before = routerServer.Writes; routerServer.Model = "other-device"; await Throws<RouterException>(() => serviceFixture.SetModeAsync("rule", false)); Assert(routerServer.Writes == before); routerServer.Model = "Mock Board"; });
await Check("program log source excludes kernel field", async () =>
{
    var batch = await serviceFixture.ProgramLogs.ReadAsync();
    Assert(batch.Replace && batch.Lines.Length == 1 && batch.Lines[0].Contains("OpenClash started"));
    Assert(routerServer.CoreRefreshWasFalse && routerServer.KernelOnlyRequests == 0);
});
await Check("program log incremental read and rotation", async () =>
{
    var first = await serviceFixture.ProgramLogs.ReadAsync();
    var unchanged = await serviceFixture.ProgramLogs.ReadAsync(first.Cursor); Assert(unchanged.Lines.Length == 0 && !unchanged.Replace);
    routerServer.ProgramLines.Add("2026-11-09 12:00:01 [Warning] subscription update failed");
    var next = await serviceFixture.ProgramLogs.ReadAsync(first.Cursor); Assert(next.Lines.Length == 1 && !next.Replace);
    routerServer.ProgramLines.Clear(); routerServer.ProgramLines.Add("2026-11-09 12:00:02 [Info] log rotated");
    var rotated = await serviceFixture.ProgramLogs.ReadAsync(next.Cursor); Assert(rotated.Replace && rotated.Lines.Length == 1);
});
await Check("legacy file fallback removes core output", async () =>
{
    routerServer.LegacyProgramLogs = true;
    var batch = await serviceFixture.ProgramLogs.ReadAsync();
    Assert(batch.Replace && batch.Lines.Length == 1 && batch.Lines[0].Contains("log rotated"));
    routerServer.LegacyProgramLogs = false;
});
await Check("program log filter redaction ANSI and bounds", () =>
{
    var log = "time=2026 level=info msg=kernel\n{\"level\":\"info\",\"msg\":\"kernel\"}\nUser-Agent: test\n2026-11-09 [Info] password=private https://example.invalid/sub?key=private\n";
    var lines = OpenClashProgramLogs.Filter(log); Assert(lines.Length == 1 && !lines[0].Contains("private") && !lines[0].Contains("example.invalid"));
    Assert(OpenClashProgramLogs.Filter(string.Join('\n', Enumerable.Range(0, 400).Select(i => "[Info] " + i))).Length == 300);
    Assert(OpenClashProgramLogs.Filter("\u001b[31m[Error] test\u001b[0m").Single() == "[Error] test");
    var authentication = OpenClashProgramLogs.Filter("[Tip] You have seted the authentication of SOCKS5/HTTP(S) proxy with【demo:fixture-auth-pass】...").Single();
    Assert(!authentication.Contains("demo") && !authentication.Contains("fixture-auth-pass") && authentication.Contains("[已隐藏]"));
    return Task.CompletedTask;
});
await Check("cancelled program log polling completes", async () =>
{
    using var cts = new CancellationTokenSource(300);
    await Throws<OperationCanceledException>(() => serviceFixture.ProgramLogs.WatchAsync(_ => { }, cts.Token));
    Assert(routerServer.KernelOnlyRequests == 0);
});
Console.WriteLine($"Checks: {passed} passed; {failed} failed");
await ProfileStore.AtomicWrite(Path.Combine(Environment.CurrentDirectory, "outputs", "checks-summary.json"), JsonSerializer.Serialize(new { time = DateTimeOffset.Now, passed, failed }, new JsonSerializerOptions { WriteIndented = true }));
return failed == 0 ? 0 : 1;

sealed class FakeRouter : IAsyncDisposable
{
    private readonly TestHttpListener listener = new(); private readonly CancellationTokenSource stop = new(); private readonly Task loop;
    public string Address { get; } public string Model = "Mock Board", Mode = "rule", Level = "info", FileContent = "config fixture\n", CustomRules = "# fixture\n";
    public bool Running = true, DedicatedCpu = true, PendingChanges, ExpireOnce, ApiAuthorized; public int Writes, Logins; private int cpuCounter;
    public bool LegacyProgramLogs, CoreRefreshWasFalse; public int KernelOnlyRequests;
    public List<string> ProgramLines = ["2026-11-09 12:00:00 [Info] OpenClash started"];
    public JsonObject Uci = new() { ["config"] = new JsonObject { [".type"] = "openclash", ["proxy_mode"] = "rule", ["log_level"] = "info" } };
    public FakeRouter()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop(); Address = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(Address + "/"); listener.Start(); loop = Task.Run(Accept);
    }
    private async Task Accept()
    {
        while (!stop.IsCancellationRequested)
        {
            try { var context = await listener.GetContextAsync(); _ = Handle(context); }
            catch (Exception) when (stop.IsCancellationRequested) { return; }
        }
    }
    private async Task Handle(TestHttpContext context)
    {
        try
        {
            var req = context.Request; var res = context.Response; var path = req.Url!.AbsolutePath;
            var body = await new StreamReader(req.InputStream).ReadToEndAsync(); JsonNode? result = new JsonObject();
            if (path.StartsWith("/proxies") || path == "/rules" || path == "/logs") { KernelOnlyRequests++; res.StatusCode = 500; await Reply(res, new JsonObject()); return; }
            if (path == "/cgi-bin/luci/") { Logins++; res.Headers.Add("Set-Cookie", "sysauth_http=fixture-session; Path=/"); await Reply(res, "<html>\"token\":\"abcdef0123\"</html>", "text/html"); return; }
            if (path.StartsWith("/ubus"))
            {
                var payload = JsonNode.Parse(body)!; var p = payload["params"]!.AsArray(); var obj = p[1]!.ToString(); var method = p[2]!.ToString(); var a = p[3]!;
                if (ExpireOnce && obj == "system") { ExpireOnce = false; await Reply(res, new JsonObject { ["result"] = new JsonArray(6) }); return; }
                switch (obj + "." + method)
                {
                    case "system.board": result = new JsonObject { ["model"] = Model, ["hostname"] = "mock", ["system"] = "mock cpu", ["kernel"] = "6.6", ["release"] = new JsonObject { ["description"] = "fixture firmware" } }; break;
                    case "system.info": result = new JsonObject { ["uptime"] = 100, ["load"] = new JsonArray(6553, 6553, 6553), ["memory"] = new JsonObject { ["total"] = 1024000, ["available"] = 512000 } }; break;
                    case "network.device.status": result = new JsonObject { ["macaddr"] = "AA:BB:CC:DD:EE:FF", ["statistics"] = new JsonObject { ["rx_bytes"] = 2000, ["tx_bytes"] = 1000 } }; break;
                    case "network.interface.dump": result = new JsonObject { ["interface"] = new JsonArray(new JsonObject { ["interface"] = "wan", ["up"] = true, ["l3_device"] = "eth0" }) }; break;
                    case "luci.getCPUUsage": if (!DedicatedCpu) { await Reply(res, new JsonObject { ["result"] = new JsonArray(3) }); return; } result = new JsonObject { ["cpuusage"] = "12%" }; break;
                    case "luci.getTempInfo": result = new JsonObject { ["tempinfo"] = "CPU: 42.5°C" }; break;
                    case "luci.getMountPoints": result = new JsonObject { ["result"] = new JsonArray(new JsonObject { ["mount"] = "/", ["size"] = 10000, ["used"] = 5000, ["avail"] = 5000 }) }; break;
                    case "file.read": result = new JsonObject { ["data"] = a.Text("path") switch { "/proc/stat" => $"cpu {100 + (++cpuCounter) * 30} 0 100 {500 + cpuCounter * 70} 0 0 0 0\n", "/etc/config/openclash" => FileContent, "/etc/openclash/custom/openclash_custom_rules.list" => CustomRules, "/tmp/openclash.log" => string.Join('\n', ProgramLines) + "\ntime=2026-11-09 level=info msg=kernel", _ => "fixture log" } }; break;
                    case "file.write": Writes++; if (a.Text("path") == "/etc/config/openclash") FileContent = a.Text("data"); else CustomRules = a.Text("data"); break;
                    case "uci.get": result = new JsonObject { ["values"] = Uci.DeepClone() }; break;
                    case "uci.changes": result = new JsonObject { ["changes"] = PendingChanges ? new JsonObject { ["openclash"] = new JsonArray("pending") } : new JsonObject() }; break;
                    case "uci.set": Writes++; foreach (var field in a["values"]!.AsObject()) Uci[a.Text("section")]![field.Key] = field.Value?.DeepClone(); break;
                    case "uci.add": Writes++; var id = "cfg" + Writes; var values = a["values"]!.DeepClone(); values![".type"] = a.Text("type"); Uci[id] = values; result = new JsonObject { ["section"] = id }; break;
                    case "uci.delete": Writes++; Uci.Remove(a.Text("section")); break;
                    case "uci.commit":
                    case "uci.apply": FileContent = JsonSerializer.Serialize(Uci); break;
                }
                await Reply(res, new JsonObject { ["result"] = new JsonArray(JsonValue.Create(0), result) }); return;
            }
            if (path.Contains("/openclash/"))
            {
                var action = path.Split('/').Last();
                if (action == "status") result = new JsonObject { ["clash"] = Running, ["core_type"] = "Meta", ["cn_port"] = req.Url.Port, ["dase"] = "fixture-api" };
                else if (action == "get_run_mode") result = new JsonObject { ["mode"] = "fake-ip" };
                else if (action == "config_file_list") result = new JsonObject { ["current_config"] = "/etc/openclash/config/test.yaml", ["config_files"] = new JsonArray(new JsonObject { ["name"] = "test.yaml", ["path"] = "/etc/openclash/config/test.yaml", ["size"] = 100 }) };
                else if (action == "refresh_log")
                {
                    if (LegacyProgramLogs) { res.StatusCode = 404; await Reply(res, new JsonObject()); return; }
                    CoreRefreshWasFalse = req.Url.Query.Contains("core_refresh=false");
                    var query = req.Url.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "");
                    var cursor = query.TryGetValue("log_len", out var raw) && int.TryParse(raw, out var n) ? n : 0;
                    var selected = cursor > 0 && cursor <= ProgramLines.Count ? ProgramLines.Skip(cursor) : ProgramLines;
                    result = new JsonObject { ["len"] = ProgramLines.Count, ["oc_log"] = string.Join('\n', selected), ["core_log"] = "[Info] THIS IS CORE OUTPUT", ["update"] = cursor != ProgramLines.Count };
                }
                else if (action == "action") { Writes++; Running = !body.Contains("action=stop"); }
            }
            else
            {
                ApiAuthorized = req.Headers["Authorization"] == "Bearer fixture-api";
                if (!ApiAuthorized) { res.StatusCode = 401; await Reply(res, new JsonObject()); return; }
                if (path == "/version") result = new JsonObject { ["version"] = "test-version" };
                else if (path == "/configs")
                {
                    if (req.HttpMethod == "PATCH") { Writes++; var patch = JsonNode.Parse(body)!; if (patch?["mode"] is not null) Mode = patch.Text("mode"); if (patch?["log-level"] is not null) Level = patch.Text("log-level"); }
                    result = new JsonObject { ["mode"] = Mode, ["log-level"] = Level };
                }
                else if (path == "/traffic") result = new JsonObject { ["up"] = 123, ["down"] = 456 };
                else if (path == "/memory") result = new JsonObject { ["inuse"] = 8000 };
            }
            await Reply(res, result);
        }
        catch { try { context.Response.Close(); } catch { } }
    }
    private static Task Reply(TestHttpResponse response, JsonNode? data) => Reply(response, data?.ToJsonString() ?? "{}", "application/json");
    private static async Task Reply(TestHttpResponse response, string text, string type)
    {
        response.ContentType = type; var bytes = Encoding.UTF8.GetBytes(text + "\n"); response.ContentLength64 = bytes.Length; await response.OutputStream.WriteAsync(bytes); response.Close();
    }
    public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); listener.Close(); await loop; stop.Dispose(); }
}
