// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
namespace ProxmoxSpice;
internal sealed record ComponentCheck(string Name,bool Ready,string Detail,string? Download=null);
internal static class Prerequisites
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr LoadLibraryEx(string file,IntPtr reserved,uint flags);
    [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr handle);
    public static ComponentCheck[] Check(){
        var results=new List<ComponentCheck>{new("Windows desktop / .NET",true,"Running "+RuntimeInformation.FrameworkDescription+" ("+RuntimeInformation.ProcessArchitecture+").")};
        try{
            var runtime=SpiceHost.FindRuntime();
            if(runtime==null)throw new InvalidOperationException("Not found. Install VirtViewer x64 to use SPICE consoles.");
            var handle=LoadLibraryEx(System.IO.Path.Combine(runtime,"libspice-client-glib-2.0-8.dll"),IntPtr.Zero,0x100|0x1000);
            if(handle==IntPtr.Zero)throw new InvalidOperationException("The SPICE library could not load (Windows error "+Marshal.GetLastWin32Error()+"). Repair or install VirtViewer x64 and restart DALIZO.");
            FreeLibrary(handle);results.Add(new("SPICE",true,"Native SPICE library loads. Guest features are checked when connected."));
        }catch(Exception e){results.Add(new("SPICE",false,e.Message,"https://www.spice-space.org/download.html"));}
        try{
            var version=CoreWebView2Environment.GetAvailableBrowserVersionString();
            if(string.IsNullOrWhiteSpace(version))throw new InvalidOperationException();
            results.Add(new("noVNC / Serial",true,"WebView2 available: "+version));
        }catch{results.Add(new("noVNC / Serial",false,"WebView2 was not detected. Install the Evergreen Runtime and restart DALIZO.","https://developer.microsoft.com/en-us/microsoft-edge/webview2/"));}
        return results.ToArray();
    }
}
public partial class MainWindow
{
    bool checkingPrerequisites;
    async void CheckPrerequisitesClicked(object sender,RoutedEventArgs e){OptionsToggle.IsChecked=false;await CheckPrerequisites(false);}
    async Task CheckPrerequisites(bool startup){
        if(checkingPrerequisites)return;checkingPrerequisites=true;
        try{
            var checks=await Task.Run(Prerequisites.Check);
            if(closing)return;
            if(!startup||checks.Any(c=>!c.Ready))ShowPrerequisites(checks);
        }catch(Exception error){if(!closing)ReportError("Prerequisite check failed.",error);}
        finally{checkingPrerequisites=false;}
    }
    void ShowPrerequisites(ComponentCheck[] checks){
        var dialog=new EditorDialog(this,"Prerequisites","Continue","Close");
        foreach(var check in checks){
            dialog.Note(check.Name+" — "+(check.Ready?"Available":"Needs attention"));
            dialog.Hint(check.Detail);
            if(check.Download!=null){var link=new Button{Content="Open download page",HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,6,0,0)};
                link.Click+=(_,_)=>{try{Process.Start(new ProcessStartInfo(check.Download){UseShellExecute=true});}catch(Exception error){ErrorDetails.Show(dialog,"Could not open the download page.",error);}};dialog.AddField(link);}
        }
        dialog.Note("Missing console components do not prevent server management. Install only the components for the consoles you use.");
        dialog.Note("Server certificates and API permissions are checked by Test connection in the server profile. No server connection is tested by this local component check.");
        var insecure=data.Servers.Count(s=>s.IgnoreCertificateErrors);
        if(insecure>0)dialog.Note($"HTTPS certificate checking is disabled in {insecure} server profile(s).");
        dialog.ShowDialog();
    }
}
