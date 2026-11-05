// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
namespace ProxmoxSpice;

internal sealed partial class NoVncHost : Border, IDisposable
{
    const string Origin="https://console.proxmox.invalid";
    readonly WebView2 browser=new();
    readonly CancellationTokenSource lifetime=new();
    readonly TaskCompletionSource ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource started=new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource connected=new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Channel<byte[]> outgoing=Channel.CreateBounded<byte[]>(new BoundedChannelOptions(128) { SingleReader=true, SingleWriter=true });
    TaskCompletionSource<string>? serialText;
    Action<string> writeClipboard=text=>Clipboard.SetText(text);
    Func<string?> readClipboard=()=>Clipboard.ContainsText()?Clipboard.GetText():null;
    TaskCompletionSource? acknowledged;
    TaskCompletionSource<FrameStats>? stats;
    ClientWebSocket? socket;
    bool disposed, sharing=true, readOnly, resizeEnabled=true;
    Window? resizeWindow;
    long receivedBytes, sentBytes, pointerMessages;
    public string TransportStats => $"received={receivedBytes}; sent={sentBytes}; pointerMessages={pointerMessages}";
    public bool CanSendKeys { get; private set; }
    public event Action<string>? StatusChanged;
    public event Action<Exception>? ErrorOccurred;
    public event Action<bool>? InputAvailabilityChanged;
    public event Action? ReleaseFocus;
    public bool IsSerial {get;}
    readonly string serialPort;
    string Page=>IsSerial?"/serial.html":"/index.html";
    string Label=>IsSerial?"Serial":"noVNC";
    public NoVncHost(string? serial=null) {
        IsSerial=serial!=null;serialPort=serial??"serial0";
        Child=browser;
        IsVisibleChanged += (_,_) => ApplyResize();
        Loaded += (_,_) => {
            var window=Window.GetWindow(this);
            if(resizeWindow!=window) {
                if(resizeWindow!=null)resizeWindow.StateChanged-=ResizeWindowChanged;
                resizeWindow=window;
                if(resizeWindow!=null)resizeWindow.StateChanged+=ResizeWindowChanged;
            }
            ApplyResize();
        };
    }
    void ResizeWindowChanged(object? sender, EventArgs e) => ApplyResize();
    bool EffectiveResize => resizeEnabled && !readOnly && IsVisible && resizeWindow?.WindowState!=WindowState.Minimized;
    void ApplyResize() => Send(new {type="resize",enabled=EffectiveResize});
    public void SetResizeEnabled(bool enabled) { resizeEnabled=enabled; ApplyResize(); }
    public async Task InitializeAsync(CancellationToken ct)
    {
        try
        {
            var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ProxmoxSpice","WebView2");
            var environment=await CoreWebView2Environment.CreateAsync(null,folder);
            ct.ThrowIfCancellationRequested(); lifetime.Token.ThrowIfCancellationRequested();
            await browser.EnsureCoreWebView2Async(environment);
            ct.ThrowIfCancellationRequested(); lifetime.Token.ThrowIfCancellationRequested();
            var core=browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled=false; core.Settings.AreDefaultContextMenusEnabled=false;
            core.Settings.AreBrowserAcceleratorKeysEnabled=false; core.Settings.IsStatusBarEnabled=false;
            core.Settings.IsPasswordAutosaveEnabled=false; core.Settings.IsGeneralAutofillEnabled=false;
            core.Settings.IsZoomControlEnabled=false; core.Settings.AreHostObjectsAllowed=false;
            core.SetVirtualHostNameToFolderMapping("console.proxmox.invalid",Path.Combine(AppContext.BaseDirectory,"Web"),CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting+=(_,e)=>{ if(e.Uri!=Origin+Page)e.Cancel=true; };
            core.NewWindowRequested+=(_,e)=>e.Handled=true;
            core.DownloadStarting+=(_,e)=>e.Cancel=true;
            core.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
            core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
            core.WebResourceRequested+=(_,e)=> {
                if(!Uri.TryCreate(e.Request.Uri,UriKind.Absolute,out var u)||u.GetLeftPart(UriPartial.Authority)!=Origin)
                    e.Response=environment.CreateWebResourceResponse(null,403,"Forbidden","");
            };
            core.WebMessageReceived+=MessageReceived;
            core.ProcessFailed+=(_,_)=>Fail("The noVNC browser process ended. Please reconnect.");
            core.Navigate(Origin+Page);
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct,lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await ready.Task.WaitAsync(timeout.Token);
        }
        catch(WebView2RuntimeNotFoundException) { throw new InvalidOperationException("Install Microsoft Edge WebView2 Runtime to use noVNC."); }
    }
    public async Task ConnectAsync(ProxmoxApi api,Vm vm,CancellationToken ct,bool receiveOnly=false)
    {
        readOnly=receiveOnly;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct,lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var ticket=IsSerial?await api.GetSerialTicketAsync(vm,serialPort,timeout.Token):await api.GetVncTicketAsync(vm,timeout.Token);
        socket=await api.OpenVncSocketAsync(vm,ticket,timeout.Token);
        Send(new {type="start",user=ticket.User,password=ticket.Password,sharing,readOnly,resize=EffectiveResize});
        await started.Task.WaitAsync(timeout.Token);
        _=ReceiveLoop(); _=SendLoop();
        await connected.Task.WaitAsync(timeout.Token);
        if(IsSerial)_=SerialKeepAlive();
    }
    void Send(object message) { if(!disposed && browser.CoreWebView2!=null)browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message)); }
    void MessageReceived(object? sender,CoreWebView2WebMessageReceivedEventArgs e)
    {
        if(disposed || e.Source!=Origin+Page)return;
        try
        {
            if(e.WebMessageAsJson.Length>1500000) { Fail("noVNC message too large."); return; }
            using var doc=JsonDocument.Parse(e.WebMessageAsJson); var m=doc.RootElement;
            switch(m.GetProperty("type").GetString())
            {
                case "ready": ready.TrySetResult(); break;
                case "started": started.TrySetResult(); break;
                case "connected": CanSendKeys=!readOnly; InputAvailabilityChanged?.Invoke(CanSendKeys); connected.TrySetResult(); StatusChanged?.Invoke(Label+" connected."+(IsSerial?" Log in inside the guest. Linux must provide a login on "+serialPort+".":"")); break;
                case "send":
                    var bytes=Convert.FromBase64String(m.GetProperty("data").GetString()!);
                    if(bytes.Length>65536 || !outgoing.Writer.TryWrite(bytes))Fail("The noVNC send buffer is full. Please reconnect.");
                    break;
                case "paste-request": if(IsSerial)SendClipboard(); break;
                case "serial-text": serialText?.TrySetResult(m.GetProperty("text").GetString()??"");break;
                case "ack": acknowledged?.TrySetResult(); break;
                case "clipboard":
                    if(sharing&&!readOnly&&CanSendKeys) {
                        var text=m.GetProperty("text").GetString();
                        if(text?.Length<=1048576)try { writeClipboard(text); } catch { StatusChanged?.Invoke("Windows clipboard is currently unavailable."); }
                    } break;
                case "release": ReleaseFocus?.Invoke(); break;
                case "stats": stats?.TrySetResult(new(m.GetProperty("width").GetInt32(),m.GetProperty("height").GetInt32(),m.GetProperty("nonblack").GetInt64(),m.GetProperty("queued").GetInt32(),m.GetProperty("rects").GetInt32(),m.GetProperty("encoding").GetInt32())); break;
                case "authfailed": Fail(Label+" authentication rejected. Check token support and VM.Console permissions."); break;
                case "error": Fail("noVNC processing failed. Please reconnect."); break;
                case "close": case "disconnected": if(!lifetime.IsCancellationRequested)Fail("noVNC disconnected. Click Connect to reconnect."); break;
            }
        }
        catch { Fail("Invalid noVNC message."); }
    }
    async Task ReceiveLoop()
    {
        var buffer=new byte[65536];
        try {
            while(!lifetime.IsCancellationRequested) {
                var result=await socket!.ReceiveAsync(buffer.AsMemory(),lifetime.Token);
                if(result.MessageType==WebSocketMessageType.Close) { Fail("The server closed the noVNC connection."); break; }
                if(result.MessageType!=WebSocketMessageType.Binary)throw new InvalidOperationException();
                receivedBytes+=result.Count;
                acknowledged=new(TaskCreationOptions.RunContinuationsAsynchronously);
                Send(new {type="data",data=Convert.ToBase64String(buffer,0,result.Count)});
                await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(15),lifetime.Token);
            }
        }
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested) { }
        catch(Exception error) { if(!lifetime.IsCancellationRequested)Fail(Label+" data connection interrupted. Please reconnect.",error); }
    }
    async Task SendLoop()
    {
        try { await foreach(var bytes in outgoing.Reader.ReadAllAsync(lifetime.Token)) { await socket!.SendAsync(bytes.AsMemory(),WebSocketMessageType.Binary,true,lifetime.Token); sentBytes+=bytes.Length; if(bytes.Length==6 && bytes[0]==5)pointerMessages++; } }
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested) { }
        catch(Exception error) { if(!lifetime.IsCancellationRequested)Fail("The noVNC input connection was interrupted. Please reconnect.",error); }
    }
    void Fail(string message,Exception? cause=null)
    {
        if(disposed||lifetime.IsCancellationRequested)return;
        var failure=new InvalidOperationException(message,cause);
        connected.TrySetException(failure);
        Disconnect();
        StatusChanged?.Invoke(message);
        ErrorOccurred?.Invoke(failure);
    }
    async Task SerialKeepAlive(){try{using var tick=new PeriodicTimer(TimeSpan.FromSeconds(20));while(await tick.WaitForNextTickAsync(lifetime.Token)){if(!outgoing.Writer.TryWrite(new byte[]{50})){Fail("Serial input buffer is full.");return;}}}catch(OperationCanceledException){}}
    public bool SessionOpen => !disposed && !lifetime.IsCancellationRequested && connected.Task.IsCompletedSuccessfully;
    public void PauseInteraction() { SetResizeEnabled(false); SetClipboardSharing(false); Send(new {type="blur"}); }
    public void SetClipboardSharing(bool enabled) { sharing=enabled; Send(new {type="sharing",enabled}); }
    public void FocusConsole(){if(!disposed&&IsVisible){browser.Focus();Send(new{type="focus"});}}
    public void SendClipboard()
    {
        if(!sharing||!CanSendKeys)return;
        var text=readClipboard();if(text!=null) { if(text.Length>1048576)throw new InvalidOperationException("Text exceeds 1 MiB."); if(IsSerial && IsVisible)browser.Focus(); Send(new {type="paste",text}); }
    }
    public void SendCtrlAltDelete() { if(IsSerial)throw new InvalidOperationException("Use terminal key input for serial consoles."); if(CanSendKeys)Send(new {type="cad"}); }
    public void SendKeys(GuestKey[] keys) {
        if(IsSerial)throw new InvalidOperationException("Desktop key combinations are unavailable on a serial terminal.");
        if(!CanSendKeys)throw new InvalidOperationException("No active input channel.");
        Send(new {type="keys",keys=keys.Select(k=>new {keysym=k.Keysym,code=k.Code}).ToArray()});
    }
    public async Task<FrameStats> GetFrameStatsAsync(CancellationToken ct)
    {
        stats=new(TaskCreationOptions.RunContinuationsAsynchronously); Send(new {type="stats"}); return await stats.Task.WaitAsync(ct);
    }
    public sealed record FrameStats(int Width,int Height,long Nonblack,int Queued,int Rects,int Encoding);
    public void Disconnect()
    {
        CanSendKeys=false; InputAvailabilityChanged?.Invoke(false);
        if(!lifetime.IsCancellationRequested) { try { Send(new {type="disconnect"}); } catch { } lifetime.Cancel(); }
        outgoing.Writer.TryComplete(); socket?.Abort(); socket?.Dispose(); socket=null;
    }
    public void Dispose() { if(disposed)return; if(resizeWindow!=null)resizeWindow.StateChanged-=ResizeWindowChanged; Disconnect(); disposed=true; browser.Dispose(); }
}









