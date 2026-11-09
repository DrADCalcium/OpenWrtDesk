using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenWrtDesk.Core;

namespace OpenWrtDesk;

public sealed partial class MainWindow
{
    private void RenderLogs()
    {
        PageTitle("运行日志", "仅查看 OpenClash 程序日志，自动刷新间隔 3 秒，保留最近 300 行。");
        if (NeedConnection()) return;
        liveLogBox = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, Height = 480, TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"), FontSize = 12, PlaceholderText = "暂无 OpenClash 程序日志"
        };
        var filter = Input("筛选", logFilter, "输入关键词");
        filter.TextChanged += (_, _) => { logFilter = filter.Text; UpdateLogBox(); };
        page.Children.Add(Row(
            Button("刷新日志", RefreshProgramLogAsync),
            Button("自动刷新", StartLogsAsync),
            Button("暂停", () => { StopLogs(); return Task.CompletedTask; }),
            Button("导出脱敏日志", ExportLogAsync)));
        page.Children.Add(filter); page.Children.Add(liveLogBox); UpdateLogBox();
        _ = StartLogsAsync();
    }

    private async Task RefreshProgramLogAsync()
    {
        StopLogs();
        var target = workspace.Current ?? throw new RouterException("请先连接设备");
        var batch = await target.ProgramLogs.ReadAsync();
        if (workspace.Current == target && selectedPage == "logs") ApplyLogBatch(batch);
    }

    private Task StartLogsAsync()
    {
        StopLogs();
        var target = workspace.Current;
        if (target is null) return Task.CompletedTask;
        var source = logToken = new CancellationTokenSource();
        var ct = source.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await target.ProgramLogs.WatchAsync(batch => DispatcherQueue.TryEnqueue(() =>
                {
                    if (logToken == source && workspace.Current == target && selectedPage == "logs") ApplyLogBatch(batch);
                }), ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { DispatcherQueue.TryEnqueue(() => { if (logToken == source) { StopLogs(); ShowError(e); } }); }
        });
        return Task.CompletedTask;
    }
    private void StopLogs() { var old = logToken; logToken = null; old?.Cancel(); old?.Dispose(); }
    private void ApplyLogBatch(ProgramLogBatch batch)
    {
        if (batch.Replace) logLines.Clear();
        foreach (var line in batch.Lines) logLines.Enqueue(line);
        while (logLines.Count > 300) logLines.Dequeue();
        UpdateLogBox();
    }
    private void UpdateLogBox()
    {
        if (liveLogBox is not null) liveLogBox.Text = string.Join('\n', logLines.Where(l => l.Contains(logFilter, StringComparison.OrdinalIgnoreCase)));
    }
}
