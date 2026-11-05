// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// One native HWND and an owned GDI frame; neither GTK nor WPF copies/presents pixels.
sealed partial class NativeConsole : Control
{
    public readonly SpiceWorker Backend=new();
    readonly GdiPresenter presenter=new();
    readonly System.Windows.Forms.Timer timer=new(){Interval=1};
    readonly Stopwatch presentationClock=Stopwatch.StartNew();
    double nextPresentation;
    bool pendingPresentation;
    const int FrameAvailableMessage=0x8000+71;
    IntPtr notificationHandle;
    int frameNotificationQueued;
    volatile bool acceptFrameNotifications;
    long interactiveFrameUntil,interactiveFrameStarted,pendingFrameTimestamp;
    [DllImport("user32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    static extern bool PostMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
    void FrameAvailable(){
        if(!acceptFrameNotifications)return;
        var window=Volatile.Read(ref notificationHandle);
        if(window==IntPtr.Zero||Interlocked.Exchange(ref frameNotificationQueued,1)!=0)return;
        if(!PostMessage(window,FrameAvailableMessage,IntPtr.Zero,IntPtr.Zero))
            Interlocked.Exchange(ref frameNotificationQueued,0);
    }
    protected override void OnHandleCreated(EventArgs e){
        base.OnHandleCreated(e);Volatile.Write(ref notificationHandle,Handle);
        acceptFrameNotifications=Visible;FrameAvailable();
    }
    protected override void OnHandleDestroyed(EventArgs e){
        acceptFrameNotifications=false;Volatile.Write(ref notificationHandle,IntPtr.Zero);
        Interlocked.Exchange(ref frameNotificationQueued,0);base.OnHandleDestroyed(e);
    }
    void ExpectInteractiveFrame(){interactiveFrameStarted=Stopwatch.GetTimestamp();interactiveFrameUntil=Environment.TickCount64+250;}

    readonly bool engineDiagnostics=Array.IndexOf(Environment.GetCommandLineArgs(),"--engine-diagnostics")>=0;
    readonly HashSet<uint> held=new();
    readonly StringBuilder unicode=new();
    N.PrimarySurface surface;
    Bitmap? bitmap;
    int buttons;
    bool packet,disposed,clipboard=true,resize=true,receiveOnly,pasteKey;
    long unicodeDue,clipboardDue,resizeDue,clipboardPauseUntil;
    uint clipboardSequence;
    ClipboardPayload? pendingClipboard;
    bool imageClipboard;
    public bool ImageSharing=>imageClipboard&&!receiveOnly;
    bool wasClipboardReady;
    Size desired,requested;
    string lastStatus="";
    bool lastReady,lastAgent,lastClipboard,lastMonitor,lastOpen;
    long keyMessages,charMessages,packetMessages,diagnosticDue;
    public event Action? StateChanged;
    public event Action<string>? Notice;
    public long FramesPresented {get;private set;}
    public bool HasFrame=>bitmap!=null;
    public Size FrameSize=>new(surface.Width,surface.Height);
    public Size RequestedSize=>requested;
    public bool Sharing=>clipboard&&!receiveOnly;
    public bool CanInput=>!receiveOnly&&Backend.InputReady&&Visible;
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern short VkKeyScan(char character);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint key,uint map);
    public NativeConsole(bool readOnly=false){
        receiveOnly=readOnly;
        AllowDrop=true;DragEnter+=FileDragEnter;DragDrop+=FileDragDrop;
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.Opaque|ControlStyles.Selectable,true);
        BackColor=Color.Black;Dock=DockStyle.Fill;TabStop=true;
        Backend.Frames.Changed+=FrameAvailable;
        timer.Tick+=(_,_)=>Tick();timer.Start();
        LostFocus+=(_,_)=>ReleaseInput();
        VisibleChanged+=(_,_)=>{
            acceptFrameNotifications=Visible;
            timer.Interval=Visible?1:100;
            if(!Visible)ReleaseInput();
            else { nextPresentation=0;Invalidate();FrameAvailable(); }
        };
    }
    public void Connect(Dictionary<string,string> config){Disconnect();Backend.Connect(config);Backend.SetClipboardSharing(Sharing,ImageSharing);}
    public void Disconnect(){ReleaseInput();unicode.Clear();pendingClipboard=null;Backend.Disconnect();ClearFrame();desired=requested=Size.Empty;}
    public void SetClipboard(bool enabled,bool images=false){clipboard=enabled;imageClipboard=images;pendingClipboard=null;wasClipboardReady=false;Backend.SetClipboardSharing(Sharing,ImageSharing);}
    public void SetResize(bool enabled){resize=enabled;desired=requested=Size.Empty;}
    public void ReleaseInput(){foreach(uint key in held)Backend.Key(key,false);held.Clear();for(int b=1;b<=3;b++)if((buttons&(1<<(b-1)))!=0)Backend.Button(b,0,false);buttons=0;Capture=false;}
    public void SendTypedKeys(uint[] keys){if(!CanInput)throw new InvalidOperationException("No active input channel.");ReleaseInput();ExpectInteractiveFrame();Backend.SendChord(keys);}
    public void SendCtrlAltDelete()=>SendKeys(new uint[]{0x1d,0x38,0x153});
    public void SendKeys(uint[] keys){if(!CanInput)throw new InvalidOperationException("No active input channel.");ReleaseInput();ExpectInteractiveFrame();Backend.SendChord(keys);Focus();}
    protected override bool IsInputKey(Keys keyData)=>true;
    protected override bool IsInputChar(char c)=>true;
    protected override bool ProcessDialogKey(Keys keyData)=>false;
    protected override void WndProc(ref Message m){
        if(m.Msg==FrameAvailableMessage){
            Interlocked.Exchange(ref frameNotificationQueued,0);
            if(!disposed&&Visible){try{PresentAvailableFrame();}catch(Exception ex){Notice?.Invoke(ex.Message);}}
            m.Result=IntPtr.Zero;return;
        }
        if(m.Msg is 0x100 or 0x101 or 0x104 or 0x105){
            keyMessages++;
            bool down=m.Msg is 0x100 or 0x104;int vk=(int)m.WParam;
            if(down)packet=vk==0xe7;
            if(vk==0xe7){packetMessages++;base.WndProc(ref m);return;}
            if(vk==0x56&&!down&&pasteKey){pasteKey=false;m.Result=IntPtr.Zero;return;}
            if(down&&vk==0x56&&CanInput&&(Sharing||ImageSharing)&&Backend.ClipboardCapability&&(held.Contains(0x1d)||held.Contains(0x11d))){
                // Capture at the paste shortcut, not at the next polling tick.
                // Automation and clipboard managers may restore the host contents
                // immediately after sending Ctrl+V.
                pasteKey=true;clipboardPauseUntil=Environment.TickCount64+350;
                try{var payload=ClipboardPayload.ReadHost(Sharing,ImageSharing);if(payload!=null){pendingClipboard=null;Backend.OfferPayload(payload,true);clipboardSequence=GetClipboardSequenceNumber();wasClipboardReady=true;}else{Backend.SendChord(new uint[]{0x1d,0x2f});}}
                catch(Exception ex){Notice?.Invoke("Paste failed: "+ex.Message);}
                m.Result=IntPtr.Zero;return;
            }
            if(CanInput){uint scan=DecodeScan(m.LParam.ToInt64());
                if(scan==0){scan=MapVirtualKey((uint)vk,4);if((scan&0xff00)==0xe000)scan=(scan&255)|0x100;}
                if(scan!=0){if(down)held.Add(scan);else held.Remove(scan);ExpectInteractiveFrame();Backend.Key(scan,down);}
            }
            m.Result=IntPtr.Zero;return;
        }
        if(m.Msg==0x102||m.Msg==0x106){
            charMessages++;
            if(packet&&CanInput){unicode.Append((char)(int)m.WParam);unicodeDue=Environment.TickCount64+80;}
            m.Result=IntPtr.Zero;return;
        }
        if(m.Msg==0x109){if((int)m.WParam==0xffff)m.Result=(IntPtr)1;else if(CanInput){unicode.Append(char.ConvertFromUtf32((int)m.WParam));unicodeDue=Environment.TickCount64+80;}return;}
        base.WndProc(ref m);
    }
    internal static uint DecodeScan(long lParam)=> (uint)((lParam>>16)&0xff)|((lParam&(1L<<24))!=0?0x100u:0);
    void FlushUnicode(){
        if(unicode.Length==0||Environment.TickCount64<unicodeDue)return;
        string text=unicode.ToString();unicode.Clear();if(!CanInput)return;
        // VK_PACKET is Unicode text, never a virtual key to pass to MapVirtualKey.
        // Paste the complete batch without altering the Windows host clipboard.
        if(Sharing&&Backend.ClipboardCapability){clipboardPauseUntil=Environment.TickCount64+350;Backend.OfferClipboard(text,true);return;}
        var strokes=new List<short>();foreach(char c in text){short v=VkKeyScan(c);if(v==-1){Notice?.Invoke("Unicode text requires clipboard sharing and SPICE Guest Tools.");return;}strokes.Add(v);}
        foreach(short v in strokes){
            uint scan=MapVirtualKey((uint)(v&255),4);if((scan&0xff00)==0xe000)scan=(scan&255)|0x100;
            int mods=(v>>8)&7;uint[] masks={0x2a,0x1d,0x38};
            for(int i=0;i<3;i++)if((mods&(1<<i))!=0)Backend.Key(masks[i],true);
            Backend.Key(scan,true);Backend.Key(scan,false);
            for(int i=2;i>=0;i--)if((mods&(1<<i))!=0)Backend.Key(masks[i],false);
        }
    }
    void Tick(){
        if(disposed)return;
        try{
            if(engineDiagnostics&&Environment.TickCount64>=diagnosticDue){diagnosticDue=Environment.TickCount64+1000;System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory,"engine-diagnostics.txt"),$"keyMessages={keyMessages}; charMessages={charMessages}; packets={packetMessages}; input={CanInput}; focused={Focused}; visible={Visible}; agent={Backend.GuestAgentConnected}; clipboard={Backend.ClipboardCapability}; monitor={Backend.MonitorCapability}; sharing={Sharing}; dispatched={Backend.Metrics()["input_commands_dispatched"]}");}
            FlushUnicode();
            if(Visible){
                PresentAvailableFrame();
                ResizeGuest();
            }
            if(Environment.TickCount64>=clipboardDue){clipboardDue=Environment.TickCount64+150;SyncClipboard();}
            if(lastStatus!=Backend.Status||lastReady!=Backend.InputReady||lastAgent!=Backend.GuestAgentConnected||lastClipboard!=Backend.ClipboardCapability||lastMonitor!=Backend.MonitorCapability||lastOpen!=Backend.SessionOpen){
                lastStatus=Backend.Status;lastReady=Backend.InputReady;lastAgent=Backend.GuestAgentConnected;lastClipboard=Backend.ClipboardCapability;lastMonitor=Backend.MonitorCapability;lastOpen=Backend.SessionOpen;StateChanged?.Invoke();
            }
        }catch(Exception ex){Notice?.Invoke(ex.Message);}
    }
    void PresentAvailableFrame(){
                if(Backend.Frames.Read(ref surface,out var changed,out var changedAt,out bool replaced)){
                    pendingFrameTimestamp=changedAt;Backend.Trace.Read(changed,changedAt);
                    if(replaced){presenter.Reset();bitmap?.Dispose();bitmap=null;if(surface.Data!=IntPtr.Zero)bitmap=new Bitmap(surface.Width,surface.Height,surface.Stride,PixelFormat.Format32bppRgb,surface.Data);}
                    if(bitmap!=null)presenter.Changed(changed);pendingPresentation=true;
                }
                // Only pending images force a paint; exposure paints still use the cached image.
                if(pendingPresentation && ((Environment.TickCount64<interactiveFrameUntil && pendingFrameTimestamp>=interactiveFrameStarted) || presentationClock.Elapsed.TotalSeconds>=nextPresentation)){
                    pendingPresentation=false;
                    // Keep the complete short response burst responsive, not only its first damage.
                    nextPresentation=presentationClock.Elapsed.TotalSeconds+1.0/60;
                    Invalidate();Update();
                }
    }
    void SyncClipboard(){
        if(Environment.TickCount64<clipboardPauseUntil)return;
        bool ready=(Sharing||ImageSharing)&&Visible&&Backend.SessionOpen&&Backend.ClipboardCapability;
        if(!ready){wasClipboardReady=false;pendingClipboard=null;return;}
        if(Backend.TryClipboard(out var incoming))pendingClipboard=incoming;
        try{
            if(pendingClipboard!=null){
                pendingClipboard.WriteHost();
                clipboardSequence=GetClipboardSequenceNumber();pendingClipboard=null;wasClipboardReady=true;return;
            }
            uint seq=GetClipboardSequenceNumber();
            if(!wasClipboardReady||seq!=clipboardSequence){
                var value=ClipboardPayload.ReadHost(Sharing,ImageSharing);
                if(GetClipboardSequenceNumber()!=seq)return; // Retry a concurrent host clipboard change.
                Backend.OfferPayload(value);clipboardSequence=seq;wasClipboardReady=true;
            }
        }catch(ExternalException){/* Clipboard busy; retry. */}
        catch(Exception ex){pendingClipboard=null;clipboardSequence=GetClipboardSequenceNumber();wasClipboardReady=true;Notice?.Invoke("Clipboard: "+ex.Message);}
    }
    void ResizeGuest(){
        if(!resize||receiveOnly||!Backend.MonitorCapability||!HasFrame||Width<64||Height<64)return;
        Size size=new(Math.Clamp(Width/8*8,640,8192),Math.Clamp(Height/2*2,480,8192));
        if(size!=desired){desired=size;resizeDue=Environment.TickCount64+650;}
        if(Environment.TickCount64<resizeDue||size==requested)return;
        requested=size;if(FrameSize!=size)Backend.ResizeGuest(size.Width,size.Height);
    }
    Rectangle Destination(){if(bitmap==null)return Rectangle.Empty;double f=Math.Min((double)Width/surface.Width,(double)Height/surface.Height);int w=(int)(surface.Width*f),h=(int)(surface.Height*f);return new((Width-w)/2,(Height-h)/2,w,h);}
    bool Position(int x,int y,bool clamp=false){var r=Destination();if(!CanInput||r.Width<=0||r.Height<=0||!clamp&&!r.Contains(x,y))return false;Backend.Position(Math.Clamp((x-r.X)*surface.Width/r.Width,0,surface.Width-1),Math.Clamp((y-r.Y)*surface.Height/r.Height,0,surface.Height-1),buttons);return true;}
    static int ButtonNumber(MouseButtons b)=>b==MouseButtons.Left?1:b==MouseButtons.Middle?2:b==MouseButtons.Right?3:0;
    protected override void OnMouseMove(MouseEventArgs e){Position(e.X,e.Y,Capture);base.OnMouseMove(e);}
    protected override void OnMouseDown(MouseEventArgs e){if(CanInput&&e.Button==MouseButtons.Left){var r=Destination();if(r.Contains(e.Location))Backend.Trace.Begin((e.X-r.X)*surface.Width/r.Width,(e.Y-r.Y)*surface.Height/r.Height);}Focus();if(Position(e.X,e.Y)){int b=ButtonNumber(e.Button);if(b!=0){buttons|=1<<(b-1);Capture=true;ExpectInteractiveFrame();Backend.Button(b,buttons,true);}}base.OnMouseDown(e);}
    protected override void OnMouseUp(MouseEventArgs e){if(e.Button==MouseButtons.Left)Backend.Trace.Mark("release_ui");int b=ButtonNumber(e.Button);if(b!=0){buttons&=~(1<<(b-1));ExpectInteractiveFrame();Backend.Button(b,buttons,false);if(buttons==0)Capture=false;}base.OnMouseUp(e);}
    protected override void OnMouseWheel(MouseEventArgs e){if(CanInput){ExpectInteractiveFrame();int b=e.Delta>0?4:5;for(int i=0;i<Math.Max(1,Math.Abs(e.Delta)/120);i++){Backend.Button(b,buttons,true);Backend.Button(b,buttons,false);}}base.OnMouseWheel(e);}
    protected override void OnMouseCaptureChanged(EventArgs e){if(!Capture&&buttons!=0)ReleaseInput();base.OnMouseCaptureChanged(e);}
    protected override void OnResize(EventArgs e){base.OnResize(e);Invalidate();}
    protected override void OnPaint(PaintEventArgs e){
        var r=Destination();if(bitmap==null){e.Graphics.Clear(Color.Black);return;}
        if(r.Top>0)e.Graphics.FillRectangle(Brushes.Black,0,0,Width,r.Top);
        if(r.Bottom<Height)e.Graphics.FillRectangle(Brushes.Black,0,r.Bottom,Width,Height-r.Bottom);
        if(r.Left>0)e.Graphics.FillRectangle(Brushes.Black,0,r.Top,r.Left,r.Height);
        if(r.Right<Width)e.Graphics.FillRectangle(Brushes.Black,r.Right,r.Top,Width-r.Right,r.Height);
        Backend.Trace.PaintBegin();presenter.Draw(e.Graphics,r,surface);FramesPresented++;Backend.Trace.PaintEnd();
    }
    void ClearFrame(){interactiveFrameUntil=0;pendingPresentation=false;nextPresentation=0;presenter.Reset();bitmap?.Dispose();bitmap=null;FrameMailbox.Free(ref surface);Invalidate();}
    public string VerifyFrame(){if(!HasFrame)throw new InvalidOperationException("No frame received.");int nonblack=0;for(int y=0;y<surface.Height;y+=8)for(int x=0;x<surface.Width;x+=8)if((Marshal.ReadInt32(surface.Data,y*surface.Stride+x*4)&0xffffff)!=0)nonblack++;if(nonblack==0)throw new InvalidOperationException("Frame samples are black.");return $"Native frame {surface.Width}x{surface.Height}; nonblack samples={nonblack}; presentations={FramesPresented}";}
    protected override void Dispose(bool disposing){if(disposing&&!disposed){disposed=true;acceptFrameNotifications=false;Volatile.Write(ref notificationHandle,IntPtr.Zero);Backend.Frames.Changed-=FrameAvailable;timer.Stop();timer.Dispose();ReleaseInput();Backend.Dispose();ClearFrame();presenter.Dispose();}base.Dispose(disposing);}
}

