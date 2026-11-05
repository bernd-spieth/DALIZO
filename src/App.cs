// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Windows;
namespace ProxmoxSpice;

internal static class App
{
    [STAThread]
    static int Main(string[] args)
    {
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        var smoke = args.Contains("--smoke-test");
        int liveIndex = Array.IndexOf(args,"--live-render-test");
        string? liveVm = liveIndex >= 0 && liveIndex + 1 < args.Length ? args[liveIndex+1] : null;
        using var instance = new Mutex(true, (smoke || liveVm != null) ? "Local\\ProxmoxSpice-Smoke-" + Guid.NewGuid() : "Local\\DALIZO-Current", out var first);
        if (!first) { MessageBox.Show("DALIZO is already running.", "DALIZO"); return 0; }
        try
        {
            if (smoke) { File.Delete(Path.Combine(AppContext.BaseDirectory,"smoke-error.txt")); File.Delete(Path.Combine(AppContext.BaseDirectory,"smoke-result.txt")); }
            if (liveVm != null) File.Delete(Path.Combine(AppContext.BaseDirectory,"live-render-result.txt"));
            var file = smoke ? Path.Combine(AppContext.BaseDirectory, "smoke-data", "profiles.json") :
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DalizoCurrent", "profiles.json");
            int profileIndex = Array.IndexOf(args, "--profile-path");
            if (profileIndex >= 0 && profileIndex+1 < args.Length) file = args[profileIndex+1];
            // Separate native profiles: import once, never write to GTK/WPF originals.
            if(!smoke && profileIndex<0 && !File.Exists(file)) {
                string root=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var source=new[]{"DalizoUnifiedMenusTest","DalizoInteractiveBurstTest","DalizoShortcutsTest","DalizoPacingTest","DalizoNative","ProxmoxSpice","DalizoPython"}.Select(folder=>Path.Combine(root,folder,"profiles.json")).FirstOrDefault(File.Exists);
                if(source!=null) { var imported=new ProfileStore(source).Load();new ProfileStore(file).Save(imported); }
            }
            var store = new ProfileStore(file);
            var app = new Application();
            app.DispatcherUnhandledException += (_, e) => { if (smoke) File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-error.txt"), e.Exception.ToString()); else MessageBox.Show("The action could not be completed. " + e.Exception.Message, "DALIZO"); e.Handled = true; };
            int result = app.Run(new MainWindow(store, smoke, liveVm));
            if (smoke && File.Exists(Path.Combine(AppContext.BaseDirectory,"smoke-error.txt"))) return 1;
            if (liveVm != null && (!File.Exists(Path.Combine(AppContext.BaseDirectory,"live-render-result.txt")) || !File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"live-render-result.txt")).StartsWith("PASS:"))) return 1;
            return result;
        }
        catch (Exception e)
        {
            if (smoke) File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-error.txt"), e.ToString());
            else MessageBox.Show("Startup failed. Existing profiles will not be overwritten.\n" + e.Message, "DALIZO");
            return 1;
        }
    }
}




