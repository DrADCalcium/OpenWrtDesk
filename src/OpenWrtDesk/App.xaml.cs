using Microsoft.UI.Xaml;

namespace OpenWrtDesk;

public partial class App : Application
{
    private MainWindow? window;
    public App()
    {
        try { InitializeComponent(); }
        catch (Exception error) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-error.log"), error.ToString()); throw; }
        UnhandledException += (_, e) =>
        {
            try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), $"{DateTimeOffset.Now:O} {e.Exception.GetType().Name} {e.Exception.StackTrace}\n"); } catch { }
        };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { window = new MainWindow(); window.Activate(); }
        catch (Exception error) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-error.log"), error.ToString()); throw; }
    }
}
