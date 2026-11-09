using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenWrtDesk.Core;
using System.Text.Json;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace OpenWrtDesk;

public sealed partial class MainWindow
{
    private void RenderDevices()
    {
        PageTitle("设备档案", "按场景保存路由器，首次手动连接后登记设备身份。");
        page.Children.Add(Row(Button("添加设备", () => EditProfileAsync(null), true), Button("导入档案", ImportProfilesAsync), Button("导出档案", ExportProfilesAsync), Button("断开当前设备", async () => { StopLogs(); workspace.FixedDevice = true; await workspace.DisconnectAsync(); ReloadDevices(); RenderPage(); })));
        foreach (var profile in workspace.Data.Profiles.OrderBy(p => p.Order))
        {
            var item = new Grid { ColumnSpacing = 16 }; item.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); item.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            item.Children.Add(Column(Text(profile.Name, 19, true), Text(profile.Group + " · " + profile.Address, 13), Text(profile.Identity.Length == 0 ? "首次连接后登记身份" : "已登记设备身份 · " + (profile.AutoConnect ? "允许自动识别" : "手动连接"), 11)));
            var actions = Row(Button("连接", () => ConnectByIdAsync(profile.Id)), Button("编辑", () => EditProfileAsync(profile)), Button("复制", async () =>
            {
                var copy = profile.Copy(); copy.Id = Guid.NewGuid(); copy.Name += " 副本"; copy.Identity = copy.RouterMac = copy.NetworkContext = "";
                copy.Order = workspace.Data.Profiles.Count; workspace.Data.Profiles.Add(copy); await SavePreferencesAsync(); ReloadDevices(); RenderPage();
            }), Button("上移", async () =>
            {
                var ordered = workspace.Data.Profiles.OrderBy(p => p.Order).ToList(); var index = ordered.FindIndex(p => p.Id == profile.Id);
                if (index > 0) { (ordered[index - 1], ordered[index]) = (ordered[index], ordered[index - 1]); for (var i = 0; i < ordered.Count; i++) ordered[i].Order = i; await SavePreferencesAsync(); ReloadDevices(); RenderPage(); }
            }), Button("删除", async () =>
            {
                if (workspace.IsBusy) throw new RouterException("当前正在处理设备操作，请稍后再试");
                if (!await ConfirmAsync("删除设备档案", "仅删除本机保存的“" + profile.Name + "”连接档案。")) return;
                if (workspace.Current?.Client.Profile.Id == profile.Id) { StopLogs(); await workspace.DisconnectAsync(); }
                workspace.Data.Profiles.Remove(profile); await SavePreferencesAsync(); ReloadDevices(); RenderPage();
            }));
            page.Children.Add(Card(Column(item, actions)));
        }
        if (workspace.Data.Profiles.Count == 0) page.Children.Add(Card(Column(Text("添加第一台软路由", 22, true), Text("填写管理地址、用户名和密码，即可直接查看状态和调整 OpenClash。"))));
    }
    private async Task EditProfileAsync(RouterProfile? existing)
    {
        if (workspace.IsBusy) throw new RouterException("当前正在处理设备操作，请稍后再试");
        var model = existing?.Copy() ?? new RouterProfile { Order = workspace.Data.Profiles.Count };
        var name = Input("设备名称", model.Name); var group = Input("场景 / 分组", model.Group);
        var address = Input("OpenWrt 管理地址", model.Address, "http://192.168.1.1"); var username = Input("用户名", model.Username);
        var password = new PasswordBox { Header = "登录密码", PlaceholderText = model.PasswordCipher.Length > 0 ? "已保存，留空保持原密码" : "输入路由器密码" };
        var controller = Input("Mihomo 控制地址（选填，留空自动读取）", model.ControllerAddress, "http://192.168.1.1:9090");
        var secret = new PasswordBox { Header = "Mihomo 密钥（选填）", PlaceholderText = model.ControllerSecretCipher.Length > 0 ? "已保存，留空保持" : "留空从 OpenClash 读取" };
        var pin = Input("HTTPS 证书 SHA-256 指纹（选填）", model.CertificatePin);
        var auto = new ToggleSwitch { Header = "允许在已登记的网络中自动连接", IsOn = model.AutoConnect };
        var reenroll = new CheckBox { Content = "重新登记设备身份（更换硬件或管理地址后使用）" };
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsOpen = false };
        var content = Column(name, group, address, username, password, new Expander { Header = "控制接口与证书", HorizontalAlignment = HorizontalAlignment.Stretch, Content = Column(controller, secret, pin) }, auto, reenroll, error);
        var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = existing is null ? "添加路由器" : "编辑路由器", Content = new ScrollViewer { Content = content, MaxHeight = 560, Width = 480 }, PrimaryButtonText = "保存并连接", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary, RequestedTheme = root.ActualTheme };
        dialog.PrimaryButtonClick += (sender, args) =>
        {
            try
            {
                model.Name = name.Text.Trim(); model.Group = group.Text.Trim(); model.Address = address.Text; model.Username = username.Text.Trim(); model.ControllerAddress = controller.Text.Trim(); model.CertificatePin = pin.Text.Trim(); model.AutoConnect = auto.IsOn; model.Validate();
                if (password.Password.Length > 0) model.PasswordCipher = SecretProtection.Protect(password.Password);
                if (secret.Password.Length > 0) model.ControllerSecretCipher = SecretProtection.Protect(secret.Password);
                if (model.PasswordCipher.Length == 0) throw new ArgumentException("请填写登录密码");
                if (reenroll.IsChecked == true || existing?.Address != model.Address || existing?.Username != model.Username) model.Identity = model.NetworkContext = model.RouterMac = "";
            }
            catch (Exception e) { args.Cancel = true; error.Message = WorkspaceController.Friendly(e); error.IsOpen = true; }
        };
        dialogOpen = true; ContentDialogResult result;
        try { result = await dialog.ShowAsync(); } finally { dialogOpen = false; }
        if (result != ContentDialogResult.Primary) return;
        if (existing is null) workspace.Data.Profiles.Add(model);
        else { var index = workspace.Data.Profiles.FindIndex(p => p.Id == existing.Id); workspace.Data.Profiles[index] = model; }
        workspace.Data.Settings.SelectedProfileId = model.Id;
        await SavePreferencesAsync(); ReloadDevices(); await ConnectByIdAsync(model.Id); RenderPage();
    }
    private async Task<StorageFile?> SaveFileAsync(string suggested, string extension, string label)
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = suggested };
        picker.FileTypeChoices.Add(label, new List<string> { extension }); WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        dialogOpen = true; try { return await picker.PickSaveFileAsync(); } finally { dialogOpen = false; }
    }
    private async Task<StorageFile?> OpenFileAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary }; picker.FileTypeFilter.Add(".json");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        dialogOpen = true; try { return await picker.PickSingleFileAsync(); } finally { dialogOpen = false; }
    }
    private async Task<(bool Proceed, string? Password)> ArchivePasswordAsync(bool export)
    {
        var password = new PasswordBox { Header = export ? "填写口令则加密导出并包含凭据；留空仅导出设备信息" : "加密档案的导入口令；普通档案留空", Width = 440 };
        var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = export ? "导出设备档案" : "导入设备档案", Content = password, PrimaryButtonText = "继续", CloseButtonText = "取消", RequestedTheme = root.ActualTheme };
        dialogOpen = true; try { var result = await dialog.ShowAsync(); return (result == ContentDialogResult.Primary, password.Password.Length == 0 ? null : password.Password); } finally { dialogOpen = false; }
    }
    private async Task ExportProfilesAsync()
    {
        var choice = await ArchivePasswordAsync(true); if (!choice.Proceed) return;
        var file = await SaveFileAsync("OpenWrtDesk-设备档案", ".json", "设备档案"); if (file is null) return;
        await workspace.Store.ExportAsync(workspace.Data, file.Path, choice.Password); ShowNotice(choice.Password is null ? "设备档案已导出，密码与控制密钥已排除" : "加密档案已导出，可使用口令在其他 Windows 用户下导入", InfoBarSeverity.Success);
    }
    private async Task ImportProfilesAsync()
    {
        var file = await OpenFileAsync(); if (file is null) return; var choice = await ArchivePasswordAsync(false); if (!choice.Proceed) return;
        var imported = await workspace.Store.ImportAsync(file.Path, choice.Password);
        foreach (var p in imported) { p.Order = workspace.Data.Profiles.Count; workspace.Data.Profiles.Add(p); }
        await SavePreferencesAsync(); ReloadDevices(); RenderPage(); ShowNotice($"已导入 {imported.Count} 台设备，请手动连接以登记当前网络", InfoBarSeverity.Success);
    }
    private async Task ExportLogAsync()
    {
        var file = await SaveFileAsync("OpenWrtDesk-脱敏日志", ".txt", "文本日志"); if (file is null) return;
        await File.WriteAllTextAsync(file.Path, string.Join('\n', logLines.Select(l => Redaction.Clean(l)))); ShowNotice("脱敏日志已导出", InfoBarSeverity.Success);
    }
    private async Task ExportHistoryAsync()
    {
        var id = workspace.Current?.Client.Profile.Id; if (id is null) return;
        var file = await SaveFileAsync("OpenWrtDesk-历史记录", ".json", "历史记录"); if (file is null) return;
        await File.WriteAllTextAsync(file.Path, JsonSerializer.Serialize(workspace.History.Get(id.Value), new JsonSerializerOptions { WriteIndented = true })); ShowNotice("历史记录已导出", InfoBarSeverity.Success);
    }
    private async Task ShowSubscriptionsAsync()
    {
        var service = workspace.Current ?? throw new RouterException("请先连接设备"); var subscriptions = await service.SubscriptionsAsync();
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 340, MinWidth = 440 };
        foreach (var sub in subscriptions) list.Items.Add(new ListViewItem { Content = $"{(sub.Enabled ? "●" : "○")} {sub.Name}", Tag = sub });
        var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = "订阅列表", Content = list, PrimaryButtonText = "编辑所选", SecondaryButtonText = "新增", CloseButtonText = "关闭", RequestedTheme = root.ActualTheme };
        dialogOpen = true; ContentDialogResult result; try { result = await dialog.ShowAsync(); } finally { dialogOpen = false; }
        if (workspace.Current != service) throw new RouterException("设备已经切换，请重新操作");
        if (result == ContentDialogResult.Primary && list.SelectedItem is ListViewItem { Tag: SubscriptionInfo selected }) await EditSubscriptionAsync(selected);
        else if (result == ContentDialogResult.Secondary) await EditSubscriptionAsync(null);
    }
    private async Task EditSubscriptionAsync(SubscriptionInfo? subscription)
    {
        var target = workspace.Current?.Client.Profile.Id ?? throw new RouterException("请先连接设备");
        var name = Input("订阅名称", subscription?.Name ?? ""); var address = Input("订阅地址", subscription?.Address ?? "");
        var agent = Input("User-Agent", subscription?.UserAgent ?? "clash-verge/v2.4.5"); var enabled = new ToggleSwitch { Header = "启用此订阅", IsOn = subscription?.Enabled ?? true };
        var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = subscription is null ? "新增订阅" : "编辑订阅", Content = Column(name, address, agent, enabled), PrimaryButtonText = "保存", SecondaryButtonText = subscription is null ? "" : "删除", CloseButtonText = "取消", RequestedTheme = root.ActualTheme };
        dialogOpen = true; ContentDialogResult result; try { result = await dialog.ShowAsync(); } finally { dialogOpen = false; }
        if (result == ContentDialogResult.None) return;
        if (workspace.Current?.Client.Profile.Id != target) throw new RouterException("设备已经切换，请重新操作");
        var delete = result == ContentDialogResult.Secondary;
        if (delete && !await ConfirmAsync("删除订阅", "将删除路由器中的订阅信息，修改前会保存备份。")) return;
        var model = new SubscriptionInfo(subscription?.Section ?? "", name.Text.Trim(), address.Text.Trim(), enabled.IsOn, agent.Text.Trim());
        await RunAsync((service, ct) => service.SaveSubscriptionAsync(model, delete, ct), "订阅信息已保存并回读确认；按需更新对应配置");
    }
    private async Task ShowBackupsAsync()
    {
        var id = workspace.Current?.Client.Profile.Id ?? throw new RouterException("请先连接设备");
        var backups = await workspace.Backups.ListAsync(id); var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 360, MinWidth = 440 };
        foreach (var b in backups) list.Items.Add(new ListViewItem { Content = $"{b.CreatedAt:MM-dd HH:mm:ss}  {b.Label}", Tag = b });
        var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = "本机备份", Content = backups.Count == 0 ? Text("尚无备份。修改设置时将自动创建。") : list, PrimaryButtonText = backups.Count > 0 ? "恢复所选" : "", CloseButtonText = "关闭", RequestedTheme = root.ActualTheme };
        dialogOpen = true; ContentDialogResult result; try { result = await dialog.ShowAsync(); } finally { dialogOpen = false; }
        if (result != ContentDialogResult.Primary || list.SelectedItem is not ListViewItem { Tag: BackupInfo selected }) return;
        if (workspace.Current?.Client.Profile.Id != id) throw new RouterException("设备已经切换，请重新操作");
        if (await ConfirmAsync("恢复备份", "恢复前会再备份当前内容。恢复后请按需重启 OpenClash，使保存的配置生效。")) await RunAsync((service, ct) => service.RestoreAsync(selected, ct), "备份已恢复并回读确认，请按需重启 OpenClash");
    }
}
