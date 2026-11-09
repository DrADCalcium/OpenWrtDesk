using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenWrtDesk.Core;
using Windows.Graphics;

namespace OpenWrtDesk;

public sealed partial class MainWindow : Window
{
    private readonly WorkspaceController workspace = new();
    private readonly Grid root = new();
    private readonly NavigationView navigation = new() { IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed, PaneDisplayMode = NavigationViewPaneDisplayMode.Left, OpenPaneLength = 210 };
    private readonly StackPanel page = new() { Spacing = 20, Padding = new Thickness(30, 24, 30, 32), MaxWidth = 1440, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox devicePicker = new() { MinWidth = 230, MaxWidth = 360, PlaceholderText = "选择路由器", DisplayMemberPath = "Label" };
    private readonly TextBlock statusText = Text("正在准备工作区", 12);
    private readonly TextBlock connectionText = Text("未连接", 12);
    private readonly InfoBar notice = new() { IsOpen = false, IsClosable = true };
    private readonly ProgressRing progress = new() { Width = 18, Height = 18, IsActive = false };
    private readonly DispatcherQueueTimer timer;
    private TrayIcon? tray;
    private string selectedPage = "overview";
    private bool initialized, updatingPicker, tickBusy, exiting, hidden, windowActive = true, dialogOpen, selectingNavigation;
    private int failures;
    private DateTimeOffset nextRefresh, nextAuto, nextMonitor, nextHistory;
    private CancellationTokenSource? logToken;
    private string logFilter = "";
    private readonly Queue<string> logLines = new();
    private TextBox? liveLogBox;
    private Guid? renderedDevice;

    public MainWindow()
    {
        Title = "OpenWrt Desk";
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Style = ResourceStyle("DeskRootStyle");
        var top = new Grid { Padding = new Thickness(22, 14, 22, 14), ColumnSpacing = 14, Style = ResourceStyle("DeskHeaderStyle") };
        top.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); top.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); top.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        top.Children.Add(Row(new FontIcon { Glyph = "\uE968", Foreground = Accent(), FontSize = 24 }, Text("OpenWrt Desk", 19, true)));
        var pickerRow = Row(devicePicker, Button("连接", ConnectSelectedAsync), Button("管理设备", () => { Navigate("devices"); return Task.CompletedTask; }));
        Grid.SetColumn(pickerRow, 1); pickerRow.HorizontalAlignment = HorizontalAlignment.Center; top.Children.Add(pickerRow);
        var statusRow = Row(progress, connectionText); Grid.SetColumn(statusRow, 2); top.Children.Add(statusRow);
        root.Children.Add(top); Grid.SetRow(notice, 1); root.Children.Add(notice);
        AddNav("overview", "总览", "\uE80F"); AddNav("openclash", "OpenClash", "\uE713");
        AddNav("logs", "运行日志", "\uE9D9"); AddNav("history", "历史与多设备", "\uE9D2"); AddNav("devices", "设备档案", "\uE8B7");
        navigation.SelectionChanged += (_, args) =>
        {
            if (!initialized || selectingNavigation) return;
            var key = args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag?.ToString();
            if (key is not null) Navigate(key, false);
        };
        navigation.Content = new ScrollViewer { Content = page, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(navigation, 2); root.Children.Add(navigation);
        var footer = new Grid { Padding = new Thickness(20, 7, 20, 7) };
        footer.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); footer.Children.Add(statusText);
        var refresh = Button("刷新", async () => { await workspace.RefreshAsync(true); RenderPage(); }); Grid.SetColumn(refresh, 1); footer.Children.Add(refresh);
        Grid.SetRow(footer, 3); root.Children.Add(footer); Content = root;
        SystemBackdrop = new MicaBackdrop(); AppWindow.Resize(new SizeInt32(1240, 880));
        root.ActualThemeChanged += (_, _) => UpdateCaptionTheme();
        var icon = Path.Combine(AppContext.BaseDirectory, "app.ico"); if (File.Exists(icon)) AppWindow.SetIcon(icon);
        timer = DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromSeconds(1); timer.Tick += async (_, _) => await TickAsync();
        AppWindow.Closing += async (_, e) =>
        {
            if (!exiting && initialized && workspace.Data.Settings.MinimizeToTray && tray is not null) { e.Cancel = true; hidden = true; AppWindow.Hide(); StopLogs(); return; }
            exiting = true; timer.Stop(); StopLogs(); tray?.Dispose();
            try { await workspace.SaveHistoryAsync(); await SavePreferencesAsync(); } catch { }
            workspace.Dispose();
        };
        Activated += (_, e) => windowActive = e.WindowActivationState != WindowActivationState.Deactivated;
        AppWindow.Changed += (_, args) =>
        {
            if (args.DidSizeChange && initialized && !hidden) { workspace.Data.Settings.WindowWidth = AppWindow.Size.Width; workspace.Data.Settings.WindowHeight = AppWindow.Size.Height; }
        };
        devicePicker.SelectionChanged += (sender, args) => { if (!updatingPicker && initialized && !workspace.IsBusy) _ = ConnectSelectedAsync(); };
        workspace.Changed += () => DispatcherQueue.TryEnqueue(UpdateStatus);
        workspace.Notification += (title, message) => DispatcherQueue.TryEnqueue(() => { tray?.Notify(title, message); ShowNotice(message, InfoBarSeverity.Warning, title); });
        root.Loaded += async (_, _) => { if (!initialized) await InitializeAsync(); };
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += NetworkChanged;
        Closed += (_, _) => System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= NetworkChanged;
        Program.RestoreRequested += RestoreRequested;
        Closed += (_, _) => Program.RestoreRequested -= RestoreRequested;
    }
    private void RestoreRequested() => DispatcherQueue.TryEnqueue(ShowWindow);
    private void NetworkChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() => nextAuto = DateTimeOffset.Now.AddSeconds(3));
    private async Task InitializeAsync()
    {
        try
        {
            await workspace.LoadAsync();
            ApplyTheme(workspace.Data.Settings.Theme);
            AppWindow.Resize(new SizeInt32((int)workspace.Data.Settings.WindowWidth, (int)workspace.Data.Settings.WindowHeight)); ReloadDevices();
            tray = new TrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(this), DispatcherQueue, ShowWindow, ExitApp, p => _ = ConnectByIdAsync(p), () => workspace.Data.Profiles);
            initialized = true; Navigate("overview"); timer.Start();
            nextAuto = DateTimeOffset.Now.AddSeconds(1); nextMonitor = DateTimeOffset.Now.AddMinutes(1); nextHistory = DateTimeOffset.Now.AddMinutes(1);
            if (workspace.Data.Settings.StartMinimized) { hidden = true; AppWindow.Hide(); }
        }
        catch (Exception e) { initialized = true; ShowError(e); Navigate("devices"); timer.Start(); }
    }
    private void AddNav(string key, string name, string glyph) => navigation.MenuItems.Add(new NavigationViewItem { Content = name, Tag = key, Icon = new FontIcon { Glyph = glyph } });
    private void Navigate(string key, bool select = true)
    {
        if (selectedPage == "logs" && key != "logs") StopLogs(); selectedPage = key;
        if (select)
        {
            selectingNavigation = true;
            navigation.SelectedItem = key == "settings" ? navigation.SettingsItem : navigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag?.ToString() == key);
            selectingNavigation = false;
        }
        RenderPage();
    }
    private void ReloadDevices()
    {
        updatingPicker = true; devicePicker.ItemsSource = workspace.Data.Profiles.OrderBy(p => p.Order).ToList();
        devicePicker.SelectedItem = workspace.Data.Profiles.FirstOrDefault(p => p.Id == workspace.Data.Settings.SelectedProfileId); updatingPicker = false;
    }
    private async Task ConnectByIdAsync(Guid id)
    {
        var p = workspace.Data.Profiles.FirstOrDefault(p => p.Id == id); if (p is null) return;
        updatingPicker = true; devicePicker.SelectedItem = p; updatingPicker = false; await ConnectSelectedAsync();
    }
    private async Task ConnectSelectedAsync()
    {
        if (workspace.IsBusy || devicePicker.SelectedItem is not RouterProfile profile) return; StopLogs();
        await UiAsync(async () =>
        {
            await workspace.ConnectAsync(profile, true); failures = 0; nextRefresh = DateTimeOffset.Now.AddSeconds(2); nextAuto = DateTimeOffset.Now.AddSeconds(30); RenderPage();
        });
    }
    private async Task TickAsync()
    {
        if (!initialized || tickBusy || exiting || dialogOpen) return; tickBusy = true;
        try
        {
            var now = DateTimeOffset.Now;
            if (now >= nextAuto)
            {
                nextAuto = now.AddSeconds(workspace.Online ? 30 : Math.Min(120, 10 + failures * 15));
                try { var before = workspace.Current?.Client.Profile.Id; if (await workspace.AutoSelectAsync() && before != workspace.Current?.Client.Profile.Id) { StopLogs(); ReloadDevices(); RenderPage(); } }
                catch (Exception e) { ShowError(e); }
            }
            if (workspace.Current is not null && now >= nextRefresh)
            {
                await workspace.RefreshAsync(); failures = workspace.Online ? 0 : Math.Min(6, failures + 1);
                var seconds = hidden || !windowActive ? workspace.Data.Settings.BackgroundSeconds : workspace.Data.Settings.RefreshSeconds;
                if (!workspace.Online) seconds = Math.Max(seconds, Math.Min(120, 5 * (1 << failures)));
                nextRefresh = DateTimeOffset.Now.AddSeconds(seconds); if (selectedPage is "overview" or "history") RenderPage(); UpdateStatus();
            }
            if (now >= nextMonitor) { nextMonitor = now.AddMinutes(1); await workspace.MonitorOthersAsync(); if (selectedPage == "history") RenderPage(); }
            if (now >= nextHistory) { nextHistory = now.AddMinutes(1); await workspace.SaveHistoryAsync(); }
        }
        catch (Exception e) { ShowError(e); }
        finally { tickBusy = false; }
    }
    private void UpdateStatus()
    {
        statusText.Text = workspace.Status + (workspace.Snapshot is { } s ? $"  ·  更新于 {s.CapturedAt:HH:mm:ss}" : "");
        connectionText.Text = workspace.IsBusy ? "处理中" : workspace.Online ? "● 已连接" : "○ 未连接";
        connectionText.Style = ResourceStyle("DeskSecondaryTextStyle");
        if (workspace.Online) connectionText.Foreground = Accent(); else connectionText.ClearValue(TextBlock.ForegroundProperty);
        progress.IsActive = workspace.IsBusy; devicePicker.IsEnabled = !workspace.IsBusy;
        tray?.SetTooltip("OpenWrt Desk · " + (workspace.Online ? workspace.Current?.Client.Profile.Name : "未连接"));
    }
    private async Task UiAsync(Func<Task> action)
    {
        try { await action(); } catch (OperationCanceledException) { ShowNotice("请求已取消或超时，请检查设备状态", InfoBarSeverity.Warning); } catch (Exception e) { ShowError(e); } finally { UpdateStatus(); }
    }
    private async Task RunAsync(Func<RouterService, CancellationToken, Task> action, string success = "设置已应用，并已重新读取设备状态")
    {
        var expected = workspace.Current?.Client.Profile.Id;
        if (expected is null) { ShowNotice("请先选择并连接路由器"); return; } StopLogs();
        await UiAsync(async () =>
        {
            await workspace.RunAsync(async (service, ct) => { if (service.Client.Profile.Id != expected) throw new RouterException("设备已经切换，请重新操作"); await action(service, ct); });
            ShowNotice(success, InfoBarSeverity.Success); RenderPage();
        });
    }
    private void ShowNotice(string message, InfoBarSeverity severity = InfoBarSeverity.Informational, string title = "") { notice.Title = title; notice.Message = message; notice.Severity = severity; notice.IsOpen = true; }
    private void ShowError(Exception e) => ShowNotice(WorkspaceController.Friendly(e), InfoBarSeverity.Error);
    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 470 }, PrimaryButtonText = "继续", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, RequestedTheme = root.ActualTheme };
        dialogOpen = true; try { return await dialog.ShowAsync() == ContentDialogResult.Primary; } finally { dialogOpen = false; }
    }
    private void ShowWindow() { hidden = false; AppWindow.Show(); Activate(); nextRefresh = DateTimeOffset.MinValue; }
    private async void ExitApp() { exiting = true; try { await workspace.SaveHistoryAsync(); await SavePreferencesAsync(); } catch { } Close(); }
    private Task SavePreferencesAsync() => workspace.Store.SaveAsync(workspace.Data);
    private static SolidColorBrush Accent() => new(Windows.UI.Color.FromArgb(255, 0, 151, 137));
    private static Style ResourceStyle(string key) => (Style)Application.Current.Resources[key];
    private static TextBlock Text(string value, double size = 14, bool bold = false) => new() { Text = value, FontSize = size, FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private static StackPanel Row(params UIElement[] items) { var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center }; foreach (var item in items) p.Children.Add(item); return p; }
    private static StackPanel Column(params UIElement[] items) { var p = new StackPanel { Spacing = 12 }; foreach (var item in items) p.Children.Add(item); return p; }
    private static Border Card(UIElement content) => new() { Style = ResourceStyle("DeskCardStyle"), Child = content };
    private Button Button(string text, Func<Task> action, bool accent = false)
    {
        var b = new Button { Content = text, VerticalAlignment = VerticalAlignment.Center }; AutomationProperties.SetName(b, text);
        if (accent) b.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        b.Click += async (_, _) => { b.IsEnabled = false; try { await UiAsync(action); } finally { b.IsEnabled = true; } }; return b;
    }
    private static ComboBox Choices(IEnumerable<string> items, string? selected = null, double width = 170) { var list = items.ToList(); return new ComboBox { ItemsSource = list, SelectedItem = list.Contains(selected ?? "") ? selected : list.FirstOrDefault(), MinWidth = width }; }
    private static string Choice(ComboBox box) => box.SelectedItem?.ToString() ?? "";
    private static TextBox Input(string header, string value = "", string placeholder = "") => new() { Header = header, Text = value, PlaceholderText = placeholder, HorizontalAlignment = HorizontalAlignment.Stretch };
    private void PageTitle(string title, string subtitle) { page.Children.Add(Text(title, 30, true)); var sub = Text(subtitle, 13); sub.Style = ResourceStyle("DeskSecondaryTextStyle"); page.Children.Add(sub); }
    private bool NeedConnection()
    {
        if (workspace.Snapshot is not null && workspace.Current is not null) return false;
        page.Children.Add(Card(Column(new FontIcon { Glyph = "\uE968", FontSize = 42, Foreground = Accent(), HorizontalAlignment = HorizontalAlignment.Left }, Text("连接你的 OpenWrt", 22, true), Text("在设备档案中保存路由器地址和登录信息。首次连接后，应用会登记设备身份并识别当前网络。"), Button("添加设备", () => EditProfileAsync(null), true)))); return true;
    }
    private void RenderPage()
    {
        page.Children.Clear();
        if (renderedDevice != workspace.Current?.Client.Profile.Id) { StopLogs(); logLines.Clear(); liveLogBox = null; renderedDevice = workspace.Current?.Client.Profile.Id; }
        switch (selectedPage) { case "overview": RenderOverview(); break; case "openclash": RenderOpenClash(); break; case "logs": RenderLogs(); break; case "history": RenderHistory(); break; case "devices": RenderDevices(); break; case "settings": RenderSettings(); break; }
        UpdateStatus();
    }
}
