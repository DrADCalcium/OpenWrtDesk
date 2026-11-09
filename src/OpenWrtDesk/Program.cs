using Microsoft.UI.Xaml;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace OpenWrtDesk;

internal static class Program
{
    internal static event Action? RestoreRequested;
    private static void Trace(string text) { try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup-trace.log"), DateTimeOffset.Now.ToString("O") + " " + text + "\n"); } catch { } }
    [STAThread]
    private static void Main()
    {
        var data = Path.GetFullPath(Environment.GetEnvironmentVariable("OPENWRTDESK_DATA_DIR") ?? Path.Combine(AppContext.BaseDirectory, "data"));
        var key = "OpenWrtDesk-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data.ToUpperInvariant())))[..24];
        using var instance = new Mutex(true, "Local\\" + key, out var created);
        if (!created)
        {
            try { using var pipe = new NamedPipeClientStream(".", key, PipeDirection.Out); pipe.Connect(3000); pipe.WriteByte(1); }
            catch { }
            return;
        }
        using var lifetime = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(key, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(lifetime.Token);
                    var signal = new byte[1]; if (await server.ReadAsync(signal, lifetime.Token) == 1) RestoreRequested?.Invoke();
                }
                catch (OperationCanceledException) { return; }
                catch { await Task.Delay(200, lifetime.Token); }
            }
        });
        try
        {
            Trace("Starting");
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Trace("COM initialized");
            Application.Start(parameters =>
            {
                try
                {
                    Trace("XAML callback");
                    SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
                    _ = new App(); Trace("Application created");
                }
                catch (Exception e) { Trace(e.ToString()); throw; }
            });
        }
        catch (Exception e) { Trace(e.ToString()); }
        finally { lifetime.Cancel(); instance.ReleaseMutex(); }
    }
}
