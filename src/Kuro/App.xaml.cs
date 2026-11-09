using System.IO;
using System.Windows;
using Kuro.Core;
using Kuro.AI;

namespace Kuro;
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, a) => { Directory.CreateDirectory(Store.Home); File.WriteAllText(Path.Combine(Store.Home, "last-error.txt"), a.Exception.ToString()); MessageBox.Show(a.Exception.Message, "Kuro · Something needs attention"); a.Handled = true; };
        if (e.Args.Length == 2 && e.Args[0] == "--provision-exa")
        {
            try
            {
                var match = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(e.Args[1]), @"EXA_API_KEY=([A-Za-z0-9_-]+)");
                if (!match.Success) { Shutdown(2); return; }
                Credentials.Write("Exa", match.Groups[1].Value); Shutdown(0);
            }
            catch { Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--self-test"))
        {
            try { await Verification.Run(); Shutdown(0); }
            catch (Exception ex) { Directory.CreateDirectory("artifacts"); File.WriteAllText("artifacts/test-failure.txt", ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--create-example"))
        {
            try { await Example.Create(); Shutdown(0); }
            catch (Exception ex) { Directory.CreateDirectory("artifacts"); File.WriteAllText("artifacts/example-failure.txt", ex.ToString()); Shutdown(1); }
            return;
        }
        Preferences.Current = Preferences.Load();
        if (Preferences.Current.DesignVersion < 1) { Preferences.Current.DesignVersion = 1; Preferences.Current.Theme = "Dark"; }
        var themeArg = Array.IndexOf(e.Args, "--theme");
        if (themeArg >= 0 && themeArg + 1 < e.Args.Length && e.Args[themeArg + 1] is "Light" or "Dark" or "Follow Windows") Preferences.Current.Theme = e.Args[themeArg + 1];
        Theme.Apply(Preferences.Current.Theme);
        var window = new MainWindow(); MainWindow = window;
        var path = e.Args.FirstOrDefault(arg => Path.GetExtension(arg).Equals(".kuro", StringComparison.OrdinalIgnoreCase) && File.Exists(arg));
        if (path == null || !window.LoadProject(path)) window.ShowWelcome();
        window.Show();
        var frameArg = Array.IndexOf(e.Args, "--frame"); var selectionArg = Array.IndexOf(e.Args, "--select");
        if (frameArg >= 0 && frameArg + 1 < e.Args.Length && long.TryParse(e.Args[frameArg + 1], out var frame))
            _ = window.Dispatcher.BeginInvoke(() => window.OpenAt(frame, selectionArg >= 0 && selectionArg + 1 < e.Args.Length ? e.Args[selectionArg + 1] : null));
    }
}
