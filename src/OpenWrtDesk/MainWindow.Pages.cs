using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenWrtDesk.Core;
using System.Text.Json.Nodes;

namespace OpenWrtDesk;

public sealed partial class MainWindow
{
    private void RenderOverview()
    {
        PageTitle("设备总览", "硬件状态与代理服务，集中查看。"); if (NeedConnection()) return;
        var s = workspace.Snapshot!;
        if (!workspace.Online) page.Children.Add(new InfoBar { IsOpen = true, Severity = InfoBarSeverity.Warning, Message = "设备暂时离线，以下为上次读取的数据。" });
        var hero = new Grid { ColumnSpacing = 16 };
        hero.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); hero.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        hero.Children.Add(Column(Text(workspace.Current!.Client.Profile.Name, 23, true), Text(s.Model + " · " + s.Architecture, 13), Text(s.Firmware, 12)));
        var state = Column(Text(s.OpenClashRunning ? "● OpenClash 运行中" : s.OpenClashInstalled ? "○ OpenClash 已停止" : "未检测到 OpenClash", 15, true), Text("运行时间  " + Display.Uptime(s.UptimeSeconds), 13), Text($"{s.Mode}  ·  {s.CaptureMode}", 13));
        Grid.SetColumn(state, 1); hero.Children.Add(state); page.Children.Add(Card(hero));
        var metrics = new Grid { ColumnSpacing = 12 };
        for (var i = 0; i < 3; i++) metrics.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var values = new[]
        {
            Metric("CPU 使用率", Display.Percent(s.CpuPercent), "系统负载  " + string.Join(" / ", s.Load.Select(n => n.ToString("0.00")))),
            Metric("系统内存", Display.Percent(s.MemoryPercent), $"可用 {Display.Bytes(s.MemoryAvailable)} / 总计 {Display.Bytes(s.MemoryTotal)}"),
            Metric("硬件温度", s.Temperature is { } t ? $"{t:0.#} °C" : "未获取", s.TemperatureText)
        };
        for (var i = 0; i < values.Length; i++) { Grid.SetColumn(values[i], i); metrics.Children.Add(values[i]); } page.Children.Add(metrics);
        var h = workspace.History.Get(s.ProfileId);
        page.Children.Add(Card(Column(Row(Text("Mihomo 实时流量", 18, true), Text("↑ " + Display.Rate(s.Upload) + "    ↓ " + Display.Rate(s.Download))),
            new TrendChart(h.Select(p => p.Up), h.Select(p => p.Down)), Text($"青色：上传   蓝色：下载     活跃连接 {s.Connections}     内核内存 {(s.CoreMemory > 0 ? Display.Bytes(s.CoreMemory) : "内核未报告")}", 12))));
        var detail = Column(Text("网络与存储", 18, true), Text($"WAN 接口  ↑ {Display.Rate(s.WanUpload)}   ↓ {Display.Rate(s.WanDownload)}", 13));
        foreach (var n in s.Interfaces) detail.Children.Add(Text($"{(n.Up ? "●" : "○")} {n.Name}  ·  {n.Device}  ·  {n.Protocol}   {n.Addresses}", 13));
        foreach (var disk in s.Storage.Where(d => d.Total > 0)) detail.Children.Add(Text($"{disk.Mount}   已用 {Display.Bytes(disk.Used)} / {Display.Bytes(disk.Total)}", 13));
        if (s.Storage.Count == 0) detail.Children.Add(Text("存储空间：当前接口未提供数据", 12));
        page.Children.Add(Card(detail));
        page.Children.Add(Text($"Linux {s.Kernel}  ·  {s.CoreType} / {s.CoreVersion}", 12));
        if (s.Limitations.Count > 0)
        {
            var limitations = Column(); foreach (var item in s.Limitations) limitations.Children.Add(Text(item.Key + "：" + item.Value, 12));
            page.Children.Add(new Expander { Header = $"接口适配信息 · {s.Limitations.Count} 项", Content = limitations, HorizontalAlignment = HorizontalAlignment.Stretch });
        }
    }
    private static Border Metric(string title, string value, string subtitle) => Card(Column(Text(title, 13), Text(value, 32, true), Text(subtitle, 11)));

    private void RenderOpenClash()
    {
        PageTitle("OpenClash", "日常控制、配置与订阅管理。"); if (NeedConnection()) return;
        var s = workspace.Snapshot!;
        if (!s.OpenClashInstalled) { page.Children.Add(Text("当前设备未提供 OpenClash 接口，请确认插件已安装。")); return; }
        page.Children.Add(Card(Column(Text(s.OpenClashRunning ? "服务运行中" : "服务已停止", 20, true), Text(s.CoreType + " · " + s.CoreVersion, 12), Row(
            Button("启动", async () => await RunAsync((service, ct) => service.ControlAsync("start", ct))),
            Button("停止", async () => { if (await ConfirmAsync("停止 OpenClash", "将停止当前路由器的代理服务，可能影响联网。")) await RunAsync((service, ct) => service.ControlAsync("stop", ct)); }),
            Button("重启", async () => { if (await ConfirmAsync("重启 OpenClash", "服务重启期间，当前路由器的连接可能短暂中断。")) await RunAsync((service, ct) => service.ControlAsync("restart", ct)); })))));
        var mode = Choices(["rule", "global", "direct"], s.Mode); var level = Choices(["silent", "error", "warning", "info", "debug"], s.LogLevel);
        var persist = new ToggleSwitch { Header = "保存到路由器配置", IsOn = true, OnContent = "重启后保留", OffContent = "仅本次运行" };
        page.Children.Add(Card(Column(Text("常用设置", 18, true), persist,
            Row(Text("代理模式", 14), mode, Button("应用模式", async () => await RunAsync((service, ct) => service.SetModeAsync(Choice(mode), persist.IsOn, ct)))),
            Row(Text("日志等级", 14), level, Button("应用等级", async () => await RunAsync((service, ct) => service.SetLogLevelAsync(Choice(level), persist.IsOn, ct)))))));
        var configPanel = Column(Text("配置文件", 18, true));
        foreach (var config in s.Configs)
        {
            var name = Text((config.Current ? "● " : "") + config.Name, 14, config.Current); name.MaxWidth = 400;
            configPanel.Children.Add(Column(Row(name, Text(Display.Bytes(config.Size), 12)), Row(
                Button("切换", async () => { if (await ConfirmAsync("切换配置", $"将使用 {config.Name} 并重新加载代理服务。")) await RunAsync((service, ct) => service.SwitchConfigAsync(config, ct)); }),
                Button("更新订阅", async () => { if (await ConfirmAsync("更新订阅配置", "更新前会保存配置正文备份。下载完成后可点击重启使其生效。")) await RunAsync((service, ct) => service.UpdateSubscriptionAsync(config.Name, ct), "订阅更新请求已提交，配置正文已备份；请检查日志后按需重启"); }))));
        }
        if (s.Configs.Count == 0) configPanel.Children.Add(Text("没有可用的配置文件"));
        page.Children.Add(Card(configPanel));
        page.Children.Add(Card(Column(Text("订阅管理", 18, true), Text("新增或编辑订阅信息，更新配置后按需重新加载。", 12), Row(Button("查看订阅", ShowSubscriptionsAsync), Button("新增订阅", () => EditSubscriptionAsync(null))))));
        var dns = Choices(["fake-ip", "redir-host"], s.CaptureMode.StartsWith("redir-host") ? "redir-host" : "fake-ip");
        var capture = Choices(["redir", "tun", "mix"], s.CaptureMode.Contains("tun") ? "tun" : s.CaptureMode.Contains("mix") ? "mix" : "redir");
        var ipv6 = new ToggleSwitch { Header = "IPv6 代理" }; var sniff = new ToggleSwitch { Header = "域名嗅探" };
        var advanced = Column(Text("保存后会重启 OpenClash；此前配置会自动备份。", 12), Row(Text("DNS 模式"), dns), Row(Text("流量接管"), capture), ipv6, sniff,
            Button("保存并重启", async () => { if (await ConfirmAsync("应用高级设置", "将保存 DNS／接管模式、IPv6 与嗅探设置，并重启 OpenClash。")) await RunAsync((service, ct) => service.ApplyAdvancedAsync(Choice(dns), Choice(capture), ipv6.IsOn, sniff.IsOn, ct)); }));
        page.Children.Add(new Expander { Header = "高级设置", Content = advanced, HorizontalAlignment = HorizontalAlignment.Stretch });
        var current = workspace.Current;
        _ = UiAsync(async () => { var all = await current!.Client.UciGetAsync(); if (workspace.Current != current) return; ipv6.IsOn = all["config"].Flag("ipv6_enable"); sniff.IsOn = all["config"].Flag("enable_meta_sniffer"); });
        page.Children.Add(Card(Column(Text("备份与恢复", 18, true), Text("修改前的配置保存在本机，并使用当前 Windows 用户加密。", 12), Row(
            Button("创建配置备份", async () => await RunAsync(async (service, ct) => { await service.BackupSettingsAsync("手动备份", ct); }, "配置备份已保存")), Button("查看备份", ShowBackupsAsync)))));
    }
    private void RenderHistory()
    {
        PageTitle("历史与多设备", "本机保存最近 720 个采样点，最多显示最近 120 个点。");
        var summaries = Column(Text("设备摘要", 18, true));
        foreach (var p in workspace.Data.Profiles)
        {
            var summary = workspace.Summaries.FirstOrDefault(s => s.Id == p.Id);
            summaries.Children.Add(Row(Text((summary?.Online == true ? "● " : "○ ") + p.Name, 15, true), Text(summary is null ? "尚未检查" : $"{summary.Status} · CPU {Display.Percent(summary.Cpu)} · 内存 {Display.Percent(summary.Memory)}", 12), Button("连接", () => ConnectByIdAsync(p.Id))));
        }
        if (workspace.Data.Profiles.Count == 0) summaries.Children.Add(Text("先添加设备档案"));
        summaries.Children.Add(Button("检查其他已匹配设备", async () => { workspace.Data.Settings.MonitorAll = true; await workspace.MonitorOthersAsync(); RenderPage(); }));
        page.Children.Add(Card(summaries));
        if (workspace.Current is null) return;
        var history = workspace.History.Get(workspace.Current.Client.Profile.Id);
        page.Children.Add(Card(Column(Text("CPU 与内存使用率", 18, true), new TrendChart(history.Select(p => p.Cpu ?? double.NaN), history.Select(p => p.Memory ?? double.NaN), true), Text("青色：CPU   蓝色：系统内存   缺失读数留空", 12))));
        var last = history.LastOrDefault();
        page.Children.Add(Card(Column(Text("最近采样", 18, true), Text(last is null ? "等待采集" : $"{last.Time:MM-dd HH:mm:ss} · CPU {Display.Percent(last.Cpu)} · 内存 {Display.Percent(last.Memory)} · 温度 {(last.Temperature is {} t ? t.ToString("0.#") + " °C" : "未获取")}"), Button("导出历史记录", ExportHistoryAsync))));
    }
    private void RenderSettings()
    {
        PageTitle("应用设置", "调整外观、刷新频率与设备识别方式。"); var settings = workspace.Data.Settings;
        var theme = Choices(["跟随系统", "浅色", "深色"], settings.Theme == "Dark" ? "深色" : settings.Theme == "Light" ? "浅色" : "跟随系统");
        var restoringTheme = false;
        theme.SelectionChanged += async (_, _) =>
        {
            if (restoringTheme) return;
            var previous = settings.Theme;
            var selected = Choice(theme) == "深色" ? "Dark" : Choice(theme) == "浅色" ? "Light" : "Default";
            if (selected == previous) return;
            settings.Theme = selected;
            ApplyTheme(selected);
            try { await SavePreferencesAsync(); }
            catch (Exception e)
            {
                settings.Theme = previous; ApplyTheme(previous);
                restoringTheme = true;
                theme.SelectedItem = previous == "Dark" ? "深色" : previous == "Light" ? "浅色" : "跟随系统";
                restoringTheme = false; ShowError(e);
            }
        };
        var refresh = new NumberBox { Header = "前台刷新间隔（秒）", Minimum = 2, Maximum = 60, Value = settings.RefreshSeconds, Width = 240, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var background = new NumberBox { Header = "后台刷新间隔（秒）", Minimum = 15, Maximum = 300, Value = settings.BackgroundSeconds, Width = 240, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var auto = new ToggleSwitch { Header = "自动识别并连接已登记设备", IsOn = settings.AutoSelect };
        var fixedDevice = new ToggleSwitch { Header = "本次运行固定当前设备", IsOn = workspace.FixedDevice };
        var minimize = new ToggleSwitch { Header = "关闭窗口时收起到托盘", IsOn = settings.MinimizeToTray };
        var start = new ToggleSwitch { Header = "下次启动时收起到托盘", IsOn = settings.StartMinimized };
        var monitor = new ToggleSwitch { Header = "每分钟检查其他已匹配设备", IsOn = settings.MonitorAll };
        var notifications = new ToggleSwitch { Header = "离线与资源占用提醒", IsOn = settings.Notifications };
        var temp = new NumberBox { Header = "温度提醒阈值（°C）", Value = settings.TemperatureWarning, Minimum = 40, Maximum = 120, Width = 240 };
        var mem = new NumberBox { Header = "内存提醒阈值（%）", Value = settings.MemoryWarning, Minimum = 50, Maximum = 99, Width = 240 };
        page.Children.Add(Card(Column(Text("显示与刷新", 18, true), theme, Text("外观选择后立即生效并自动保存", 12), Row(refresh, background))));
        page.Children.Add(Card(Column(Text("连接与托盘", 18, true), auto, fixedDevice, minimize, start, monitor)));
        page.Children.Add(Card(Column(Text("状态提醒", 18, true), notifications, Row(temp, mem))));
        page.Children.Add(Button("保存设置", async () =>
        {
            if (new[] { refresh.Value, background.Value, temp.Value, mem.Value }.Any(double.IsNaN)) throw new ArgumentException("请填写完整的数值设置");
            settings.Theme = Choice(theme) == "深色" ? "Dark" : Choice(theme) == "浅色" ? "Light" : "Default";
            settings.RefreshSeconds = (int)refresh.Value; settings.BackgroundSeconds = (int)background.Value; settings.AutoSelect = auto.IsOn;
            settings.MinimizeToTray = minimize.IsOn; settings.StartMinimized = start.IsOn; settings.MonitorAll = monitor.IsOn; settings.Notifications = notifications.IsOn;
            settings.TemperatureWarning = temp.Value; settings.MemoryWarning = mem.Value; workspace.FixedDevice = fixedDevice.IsOn;
            await SavePreferencesAsync(); ApplyTheme(settings.Theme);
            RenderPage(); ShowNotice("应用设置已保存", InfoBarSeverity.Success);
        }, true));
        page.Children.Add(Text("OpenWrt Desk 0.1.2 · WinUI 3 原生应用\n凭据与备份由当前 Windows 用户加密。数据目录：" + workspace.Store.DirectoryPath, 12));
    }
}
