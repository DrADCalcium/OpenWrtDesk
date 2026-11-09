using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenWrtDesk.Core;

public sealed class RouterException(string message, string code = "request") : Exception(message)
{
    public string Code { get; } = code;
}

public static class J
{
    public static string Text(this JsonNode? n, string key, string fallback = "") => n?[key]?.ToString() ?? fallback;
    public static long Long(this JsonNode? n, string key) => long.TryParse(n?[key]?.ToString(), out var x) ? x : 0;
    public static double Number(this JsonNode? n, string key) => double.TryParse(n?[key]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : 0;
    public static bool Flag(this JsonNode? n, string key) => n.Text(key) is "true" or "True" or "1";
    public static JsonArray Array(this JsonNode? n, string key) => n?[key] as JsonArray ?? [];
}

public sealed class RouterClient : IDisposable
{
    public RouterProfile Profile { get; }
    private readonly Uri router;
    private readonly CookieContainer cookies = new();
    private readonly HttpClient http;
    private readonly HttpClient api;
    private readonly SemaphoreSlim loginGate = new(1, 1);
    private string session = "";
    private string csrf = "";
    private string? rpcPath;
    private Uri? apiUri;
    private string apiSecret = "";
    private bool loggedInBefore;
    public string LastCertificateFingerprint { get; private set; } = "";
    public string RpcPath => rpcPath ?? "未检测";
    public bool HasController => apiUri is not null;
    public RouterClient(RouterProfile profile, HttpMessageHandler? transport = null, HttpMessageHandler? apiTransport = null)
    {
        Profile = profile.Copy(); Profile.Validate();
        router = new Uri(Profile.Address);
        HttpClientHandler Handler(bool withCookies) => new()
        {
            UseProxy = false, AllowAutoRedirect = false, UseCookies = withCookies, CookieContainer = cookies,
            ServerCertificateCustomValidationCallback = ValidateCertificate
        };
        http = new HttpClient(transport ?? Handler(true)) { Timeout = TimeSpan.FromSeconds(12) };
        api = new HttpClient(apiTransport ?? Handler(false)) { Timeout = TimeSpan.FromSeconds(12) };
    }
    private bool ValidateCertificate(HttpRequestMessage request, X509Certificate2? cert, X509Chain? chain, SslPolicyErrors errors)
    {
        if (cert is null) return false;
        LastCertificateFingerprint = cert.GetCertHashString(HashAlgorithmName.SHA256);
        if (Profile.CertificatePin.Length > 0)
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Profile.CertificatePin), Convert.FromHexString(LastCertificateFingerprint));
        return errors == SslPolicyErrors.None;
    }
    public async Task LoginAsync(CancellationToken ct = default)
    {
        await loginGate.WaitAsync(ct);
        try
        {
            if (loggedInBefore && Profile.Identity.Length > 0)
            {
                var context = await Task.Run(NetworkDiscovery.Current, ct);
                var mac = await Task.Run(() => NetworkDiscovery.NeighborMac(Profile.Address), ct);
                var binding = Profile.Copy(); binding.AutoConnect = true;
                if (!NetworkDiscovery.CanAutoAuthenticate(binding, context.Key, mac))
                    throw new RouterException("网络或设备标识已变化，请手动重新连接后再登录", "identity");
            }
            session = ""; csrf = "";
            foreach (Cookie cookie in cookies.GetCookies(router)) cookie.Expired = true;
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["luci_username"] = Profile.Username,
                ["luci_password"] = SecretProtection.Unprotect(Profile.PasswordCipher)
            });
            using var response = await http.PostAsync(new Uri(router, "/cgi-bin/luci/"), form, ct);
            session = cookies.GetCookies(new Uri(router, "/cgi-bin/luci/")).Cast<Cookie>()
                .FirstOrDefault(c => c.Name is "sysauth_http" or "sysauth_https" or "sysauth" && !c.Expired)?.Value ?? "";
            if (session.Length == 0) throw new RouterException("登录失败，请检查用户名和密码", "auth");
            loggedInBefore = true;
            // Modern LuCI protects form writes using a token embedded in its own page.
            var page = await response.Content.ReadAsStringAsync(ct);
            if ((int)response.StatusCode is 302 or 303)
            {
                using var overview = await http.GetAsync(new Uri(router, "/cgi-bin/luci/admin/status/overview"), ct);
                if (overview.IsSuccessStatusCode) page = await overview.Content.ReadAsStringAsync(ct);
            }
            var match = Regex.Match(page, "[\"']token[\"']\\s*:\\s*[\"']([a-fA-F0-9]+)[\"']");
            if (match.Success) csrf = match.Groups[1].Value;
        }
        catch (HttpRequestException) { throw new RouterException("无法连接后台；请检查地址、网络或 HTTPS 证书", "offline"); }
        finally { loginGate.Release(); }
    }
    public async Task<JsonNode?> UbusAsync(string obj, string method, JsonObject? parameters = null, CancellationToken ct = default)
    {
        if (session.Length == 0) await LoginAsync(ct);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var paths = rpcPath is null ? new[] { "/ubus/", "/ubus", "/cgi-bin/luci/admin/ubus" } : new[] { rpcPath };
            var renew = false;
            foreach (var path in paths)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(router, path));
                request.Content = JsonContent(new { jsonrpc = "2.0", id = 1, method = "call", @params = new object[] { session, obj, method, parameters ?? new JsonObject() } });
                using var response = await SendAsync(http, request, ct);
                if (response.StatusCode == HttpStatusCode.NotFound) continue;
                if ((int)response.StatusCode is 401 or 403 && attempt == 0) { renew = true; break; }
                var data = await ReadJsonAsync(response, ct);
                if (data?["result"] is not JsonArray result || result.Count == 0)
                {
                    var errorCode = data?["error"]?.Text("code") ?? "unknown";
                    throw new RouterException($"{obj}.{method} RPC 请求被拒绝（{errorCode}）", errorCode == "-32002" ? "permission" : "unsupported");
                }
                var status = result[0]!.GetValue<int>();
                if (status == 6 && attempt == 0 && obj == "system" && method is "info" or "board") { renew = true; break; }
                if (status != 0) throw new RouterException(status switch
                {
                    6 => $"当前权限不允许 {obj}.{method}",
                    3 or 4 or 8 => $"固件未提供 {obj}.{method}",
                    _ => $"{obj}.{method} 返回状态 {status}"
                }, status == 6 ? "permission" : "unsupported");
                rpcPath = path;
                return result.Count > 1 ? result[1]?.DeepClone() : null;
            }
            if (!renew) throw new RouterException("未找到可用的 ubus 接口，请检查固件的 rpcd/uhttpd 组件", "unsupported");
            await LoginAsync(ct);
        }
        throw new RouterException("登录会话失效，请检查密码是否已改变", "auth");
    }
    private static System.Net.Http.StringContent JsonContent(object data) => new(JsonSerializer.Serialize(data), System.Text.Encoding.UTF8, "application/json");
    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken ct, bool stream = false)
    {
        try { return await client.SendAsync(request, stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new RouterException("设备响应超时", "offline"); }
        catch (HttpRequestException) { throw new RouterException("设备无法连接或证书校验失败", "offline"); }
    }
    private static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await ReadTextAsync(response, ct);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        try { return JsonNode.Parse(text); }
        catch (JsonException) { throw new RouterException("接口未返回预期数据，可能需要登录或固件暂不支持", "unsupported"); }
    }
    private static async Task<string> ReadTextAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if ((int)response.StatusCode is 401 or 403) throw new RouterException("会话或权限不足，请重新连接", "auth");
        if (!response.IsSuccessStatusCode) throw new RouterException($"接口返回 HTTP {(int)response.StatusCode}", response.StatusCode == HttpStatusCode.NotFound ? "unsupported" : "request");
        if (response.Content.Headers.ContentLength > 8 * 1024 * 1024) throw new RouterException("响应内容超过读取限制");
        var text = await response.Content.ReadAsStringAsync(ct);
        if (text.Length > 8 * 1024 * 1024) throw new RouterException("响应内容超过读取限制");
        return text;
    }
    public async Task<JsonNode?> LuciAsync(string action, Dictionary<string, string>? fields = null, bool write = false, CancellationToken ct = default)
    {
        var text = await LuciTextAsync(action, fields, write, ct);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        try
        {
            var result = JsonNode.Parse(text);
            if (result.Text("status") == "error" || result.Text("success") == "false") throw new RouterException("OpenClash 拒绝了该操作，请检查配置和运行状态");
            return result;
        }
        catch (JsonException) { throw new RouterException("OpenClash 接口不可用或会话已经失效", "unsupported"); }
    }
    public async Task<string> LuciTextAsync(string action, Dictionary<string, string>? fields = null, bool write = false, CancellationToken ct = default)
    {
        if (!Regex.IsMatch(action, "^[a-z_]+$")) throw new ArgumentException("接口名称无效");
        if (session.Length == 0) await LoginAsync(ct);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var values = fields is null ? new Dictionary<string, string>() : new(fields);
            var path = "/cgi-bin/luci/admin/services/openclash/" + action;
            if (write && csrf.Length > 0) values["token"] = csrf;
            var query = string.Join("&", values.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
            using var request = new HttpRequestMessage(write ? HttpMethod.Post : HttpMethod.Get, new Uri(router, path + (write || query.Length == 0 ? "" : "?" + query)));
            if (write) request.Content = new FormUrlEncodedContent(values);
            using var response = await SendAsync(http, request, ct);
            if ((int)response.StatusCode is 401 or 403 && attempt == 0 && !write) { await LoginAsync(ct); continue; }
            var text = await ReadTextAsync(response, ct);
            if (text.Contains("luci_password", StringComparison.OrdinalIgnoreCase))
            {
                if (!write && attempt == 0) { await LoginAsync(ct); continue; }
                throw new RouterException("后台登录会话已过期", "auth");
            }
            return text;
        }
        throw new RouterException("无法恢复后台登录会话", "auth");
    }
    public async Task DiscoverControllerAsync(JsonNode? status = null, CancellationToken ct = default)
    {
        status ??= await LuciAsync("status", ct: ct);
        var secret = SecretProtection.Unprotect(Profile.ControllerSecretCipher);
        if (Profile.ControllerAddress.Length > 0) apiUri = new Uri(Profile.ControllerAddress);
        else
        {
            var port = status.Long("cn_port");
            if (port is < 1 or > 65535)
            {
                var config = (await UbusAsync("uci", "get", new JsonObject { ["config"] = "openclash", ["section"] = "config" }, ct))?["values"];
                port = config.Long("cn_port");
                if (secret.Length == 0) secret = config.Text("dashboard_password");
            }
            if (port is < 1 or > 65535) throw new RouterException("未能读取 Mihomo 控制端口，请在设备档案中填写控制地址", "unsupported");
            apiUri = new UriBuilder("http", router.Host, (int)port).Uri;
        }
        apiSecret = secret.Length > 0 ? secret : status.Text("dase");
    }
    public async Task<JsonNode?> ClashAsync(string path, HttpMethod? method = null, JsonNode? body = null, CancellationToken ct = default)
    {
        if (apiUri is null) await DiscoverControllerAsync(ct: ct);
        method ??= HttpMethod.Get;
        using var request = new HttpRequestMessage(method, new Uri(apiUri!, path));
        if (apiSecret.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiSecret);
        if (body is not null) request.Content = JsonContent(body);
        using var response = await SendAsync(api, request, ct);
        return await ReadJsonAsync(response, ct);
    }
    public async Task<JsonNode?> SampleStreamAsync(string path, CancellationToken ct = default)
    {
        if (apiUri is null) await DiscoverControllerAsync(ct: ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(apiUri!, path));
        if (apiSecret.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiSecret);
        using var response = await SendAsync(api, request, timeout.Token, true);
        if (!response.IsSuccessStatusCode) throw new RouterException($"实时接口返回 HTTP {(int)response.StatusCode}");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(stream);
        var line = await reader.ReadLineAsync(timeout.Token);
        if (line is null || line.Length > 1024 * 1024) throw new RouterException("实时数据为空或过大");
        try { return JsonNode.Parse(line); }
        catch (JsonException) { throw new RouterException("实时数据格式不兼容", "unsupported"); }
    }
    public async Task<string> ReadFileAsync(string path, CancellationToken ct = default) =>
        (await UbusAsync("file", "read", new JsonObject { ["path"] = path }, ct)).Text("data");
    public async Task WriteFileAsync(string path, string content, CancellationToken ct = default)
    {
        var allowed = path is "/etc/config/openclash" or "/etc/openclash/custom/openclash_custom_rules.list";
        if (!allowed) throw new ArgumentException("写入路径不在允许列表中");
        await UbusAsync("file", "write", new JsonObject { ["path"] = path, ["data"] = content }, ct);
    }
    public async Task<JsonObject> UciGetAsync(CancellationToken ct = default) =>
        (await UbusAsync("uci", "get", new JsonObject { ["config"] = "openclash" }, ct))?["values"]?.AsObject() ?? throw new RouterException("未能读取 OpenClash 配置");
    public Task<JsonNode?> UciSetAsync(string section, JsonObject values, CancellationToken ct = default) =>
        UbusAsync("uci", "set", new JsonObject { ["config"] = "openclash", ["section"] = section, ["values"] = values.DeepClone() }, ct);
    public async Task<JsonNode?> UciCommitAsync(CancellationToken ct = default)
    {
        // LuCI intentionally exposes apply, not commit, in its default ACL.
        // Only apply when this dedicated session has changes to OpenClash alone.
        var pending = await UbusAsync("uci", "changes", ct: ct);
        if (pending?["changes"] is JsonObject changes && changes.Any(p => p.Key != "openclash" && p.Value is JsonArray values && values.Count > 0))
            throw new RouterException("当前会话还有其他配置的待应用修改，已取消保存");
        return await UbusAsync("uci", "apply", new JsonObject { ["rollback"] = false, ["timeout"] = 30 }, ct);
    }
    public async Task<(string Identity, string Mac)> ReadIdentityAsync(CancellationToken ct = default)
    {
        var board = await UbusAsync("system", "board", ct: ct);
        string mac = "";
        try { mac = (await UbusAsync("network.device", "status", new JsonObject { ["name"] = "br-lan" }, ct)).Text("macaddr"); }
        catch (RouterException) { }
        if (mac.Length == 0)
        {
            try { mac = (await UbusAsync("luci-rpc", "getNetworkDevices", ct: ct))?["br-lan"].Text("mac") ?? ""; }
            catch (RouterException) { }
        }
        var raw = string.Join("|", board.Text("hostname"), board.Text("model"), board.Text("board_name"), mac.ToLowerInvariant());
        return (Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw))), mac);
    }
    public async Task VerifyIdentityAsync(CancellationToken ct = default)
    {
        if (Profile.Identity.Length == 0) throw new RouterException("请先连接并登记设备身份，再修改设置", "identity");
        var actual = await ReadIdentityAsync(ct);
        if (actual.Identity != Profile.Identity) throw new RouterException("该地址的设备身份已经变化，请检查设备档案后重新登记", "identity");
    }
    public void AcceptIdentity(string identity) => Profile.Identity = identity;
    public void BindNetwork(string network, string mac) { Profile.NetworkContext = network; Profile.RouterMac = mac; }
    public void DiscardSession() { session = ""; csrf = ""; }
    public void Dispose() { http.Dispose(); api.Dispose(); loginGate.Dispose(); }
}
