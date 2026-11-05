// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms.Integration;
namespace ProxmoxSpice;

internal sealed class SpiceHost : WindowsFormsHost
{
    static bool initialized;
    readonly NativeConsole view;
    readonly bool readOnly;
    bool disposed;
    public event Action<string>? StatusChanged;
    public event Action<string[]>? FilesDropped;
    bool imageSharing;
    public bool FileTransferAllowed {get;private set;}
    public event Action<bool>? InputAvailabilityChanged;
    public event Action<string>? ClipboardStateChanged;
    public bool CanSendKeys=>!disposed&&view.CanInput;
    public bool SessionOpen=>!disposed&&view.Backend.SessionOpen;
    internal string ConnectionStatus=>view.Backend.Status;
    public bool HasDisplay=>SessionOpen;
    public bool HasFrame=>!disposed&&view.HasFrame;
    public bool GuestAgentConnected=>view.Backend.GuestAgentConnected;
    public bool ClipboardCapability=>view.Backend.ClipboardCapability;
    public bool MonitorCapability=>view.Backend.MonitorCapability;
    public bool NativeClipboardSharing=>SessionOpen&&view.Sharing;
    public long FramesPresented=>view.FramesPresented;
    public long SnapshotRequests=>view.FramesPresented;
    public int ResizeRequests=>view.Backend.ResizeRequests;
    public (int Width,int Height) FrameSize=>(view.FrameSize.Width,view.FrameSize.Height);
    public (int Width,int Height) LastRequestedSize=>(view.RequestedSize.Width,view.RequestedSize.Height);
    public SpiceHost(bool receiveOnly=false,bool legacyPresentation=false){
        if(legacyPresentation)throw new NotSupportedException("Legacy GTK rendering is retained in the original application; it is not part of this native build.");
        Initialize();readOnly=receiveOnly;view=new NativeConsole(receiveOnly);Child=view;
        view.StateChanged+=()=>{if(disposed)return;StatusChanged?.Invoke(view.Backend.Status);InputAvailabilityChanged?.Invoke(CanSendKeys);ClipboardStateChanged?.Invoke(ClipboardCapability?"Text clipboard supported":"Install SPICE Guest Tools to enable the clipboard.");};
        view.Notice+=message=>StatusChanged?.Invoke(message);
        view.FilesDropped+=paths=>FilesDropped?.Invoke(paths);
        IsVisibleChanged+=(_,_)=>{if(!IsVisible)view.ReleaseInput();};
        Loaded+=(_,_)=>{var window=Window.GetWindow(this);if(window!=null)window.Deactivated+=OnDeactivate;};
        Unloaded+=(_,_)=>{view.ReleaseInput();var window=Window.GetWindow(this);if(window!=null)window.Deactivated-=OnDeactivate;};
    }
    void OnDeactivate(object? sender,EventArgs e)=>view.ReleaseInput();
    static void Initialize(){
        if(initialized)return;
        var explicitPath=Environment.GetEnvironmentVariable("PROXMOX_SPICE_RUNTIME");
        var candidates=new List<string>();if(!string.IsNullOrWhiteSpace(explicitPath))candidates.Add(Path.GetFullPath(explicitPath));
        candidates.AddRange(Directory.GetDirectories(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"VirtViewer*").OrderByDescending(p=>p).Select(p=>Path.Combine(p,"bin")));
        string? runtime=candidates.FirstOrDefault(p=>File.Exists(Path.Combine(p,"libspice-client-glib-2.0-8.dll")));
        if(runtime==null||!N.SetDllDirectory(runtime))throw new InvalidOperationException("Install VirtViewer x64 or set PROXMOX_SPICE_RUNTIME to its bin directory.");
        initialized=true;
    }
    public void Connect(SpiceConfig c){if(disposed)throw new ObjectDisposedException(nameof(SpiceHost));view.Connect(new(){{"host",c.Host},{"tls-port",c.TlsPort},{"password",c.Password},{"proxy",c.Proxy},{"ca",c.Ca},{"host-subject",c.Subject}});}
    public void Disconnect(){if(disposed)return;view.Disconnect();InputAvailabilityChanged?.Invoke(false);}
    public void SetClipboardSharing(bool value)=>view.SetClipboard(value,imageSharing);
    public void SetExtraSharing(bool images,bool files){imageSharing=images;FileTransferAllowed=files;view.FilesAllowed=files;view.SetClipboard(view.Sharing,images);}
    public Task SendFilesAsync(string[] files,IProgress<(long Current,long Total)> progress,CancellationToken token){if(disposed||readOnly||!FileTransferAllowed)throw new InvalidOperationException("File sending is disabled for this VM.");return view.Backend.SendFilesAsync(files,progress,token);}
    public void SetResizeEnabled(bool value)=>view.SetResize(value);
    public void PauseInteraction(){view.ReleaseInput();view.SetClipboard(false);view.SetResize(false);}
    public void FocusConsole()=>view.Focus();
    public void SendCtrlAltDelete()=>view.SendCtrlAltDelete();
    public void SendTypedKeys(uint[] keys)=>view.SendTypedKeys(keys);
    public void SendKeys(uint[] keys)=>view.SendKeys(keys);
    public void RestoreResolutionForDiagnostic(int width,int height){view.SetResize(false);if(!readOnly)view.Backend.ResizeGuest(width,height);}
    internal void WakeKeyboardForDiagnostic(){view.Backend.Key(0x2a,true);view.Backend.Key(0x2a,false);}
    internal void WakePointerOnceForDiagnostic()=>view.Backend.Position(1,1,0);
    internal void ExerciseInputRoutingForDiagnostic(){if(!CanSendKeys)throw new InvalidOperationException("Input channel unavailable.");view.Backend.Position(320,240,0);WakeKeyboardForDiagnostic();}
    internal string VerifyReceivedFrame()=>view.VerifyFrame();
    internal static void CheckInputMapping(){
        if(NativeConsole.DecodeScan(0x001e0001)!=0x1e||NativeConsole.DecodeScan(0x011d0001)!=0x11d||NativeConsole.DecodeScan(0x01530001)!=0x153)throw new Exception("Native scan-code decode failed.");
    }
    internal async Task CheckWpfRendering(Action<int> changeLayout){
        Disconnect();await Task.Delay(100);
        for(int i=0;i<6;i++){
            int color=i%2==0?0x00f06414:0x001464f0;var pixels=Enumerable.Repeat(color,127*65).ToArray();var memory=Marshal.AllocHGlobal(pixels.Length*4);
            try{Marshal.Copy(pixels,0,memory,pixels.Length);view.Backend.Frames.Write(new N.PrimarySurface{Format=32,Width=127,Height=65,Stride=127*4,Data=memory},new System.Drawing.Rectangle(0,0,127,65));}finally{Marshal.FreeHGlobal(memory);}
            changeLayout(i);await Task.Delay(100);view.Refresh();
            using var capture=new System.Drawing.Bitmap(view.Width,view.Height);view.DrawToBitmap(capture,view.ClientRectangle);
            if((capture.GetPixel(view.Width/2,view.Height/2).ToArgb()&0xffffff)!=color)throw new Exception($"Native GDI presentation pixel mismatch: iteration={i}, actual={capture.GetPixel(view.Width/2,view.Height/2)}, expected={color:X}, visible={view.Visible}, frame={view.HasFrame}, size={view.Width}x{view.Height}.");
        }
    }
    protected override void Dispose(bool disposing){if(disposing&&!disposed){disposed=true;view.Dispose();}base.Dispose(disposing);}
}


