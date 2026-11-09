using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenWrtDesk.Core;

public sealed record ProgramLogBatch(string[] Lines, long Cursor, bool Replace);

public sealed partial class OpenClashProgramLogs(RouterClient client)
{
    // OpenClash writes application and core output to the same file. Its LuCI
    // endpoint separates these into oc_log and core_log; only oc_log is consumed.
    public async Task<ProgramLogBatch> ReadAsync(long cursor = 0, CancellationToken ct = default)
    {
        try
        {
            var data = await client.LuciAsync("refresh_log", new()
            {
                ["log_len"] = Math.Max(0, cursor).ToString(CultureInfo.InvariantCulture),
                ["core_refresh"] = "false"
            }, ct: ct);
            if (data?["oc_log"] is not null)
            {
                var count = Math.Max(0, data.Long("len"));
                return new(Filter(data.Text("oc_log")), count, cursor == 0 || count < cursor);
            }
            // Old plugin versions expose a single mixed log field.
            if (data?["log"] is not null) return new(Filter(data.Text("log")), 0, true);
        }
        catch (RouterException e) when (e.Code is "unsupported" or "permission") { }

        try
        {
            return new(Filter(await client.ReadFileAsync("/tmp/openclash.log", ct)), 0, true);
        }
        catch (RouterException e) when (e.Message.EndsWith("状态 5", StringComparison.Ordinal))
        {
            return new([], 0, true);
        }
    }

    public async Task WatchAsync(Action<ProgramLogBatch> onBatch, CancellationToken ct)
    {
        long cursor = 0;
        while (!ct.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();
            var batch = await ReadAsync(cursor, ct);
            onBatch(batch); cursor = batch.Cursor;
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
    }

    public static string[] Filter(string text)
    {
        return text.Split('\n').Select(line => Ansi().Replace(line.TrimEnd('\r'), ""))
            .Where(line => !string.IsNullOrWhiteSpace(line) && !CoreOrHeader().IsMatch(line))
            .TakeLast(300).Select(line => Redaction.Clean(line)).ToArray();
    }

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex Ansi();
    [GeneratedRegex("level=|^\\s*time=|\"level\"\\s*:|^\\s*(?:Sec-Fetch-Mode|User-Agent|Access-Control|Accept|Origin|Referer|Connection|Pragma|Cache-)", RegexOptions.IgnoreCase)]
    private static partial Regex CoreOrHeader();
}
