// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Text;
using Microsoft.Web.WebView2.Core;
namespace ProxmoxSpice;
internal sealed partial class NoVncHost
{
    public void WakeForDiagnostic() => Send(new {type="wake",diagnostic=true});
    public void ShiftForDiagnostic() => Send(new {type="shift-test",diagnostic=true});
    public async Task CheckSyntheticSessionAsync(CancellationToken ct, Func<Task>? switchSession = null)
    {
        sharing=false; readOnly=false;
        Send(new {type="start",user="synthetic",password="synthetic",sharing=false,readOnly=false});
        await started.Task.WaitAsync(ct);
        async Task Inject(byte[] bytes) {
            acknowledged=new(TaskCreationOptions.RunContinuationsAsynchronously);
            Send(new {type="data",data=Convert.ToBase64String(bytes)});
            await acknowledged.Task.WaitAsync(ct);
        }
        var banner=Encoding.ASCII.GetBytes("RFB 003.008\n");
        await Inject(banner);
        var reply=await outgoing.Reader.ReadAsync(ct);
        if(!reply.SequenceEqual(banner))throw new InvalidOperationException("noVNC protocol version not returned through host bridge.");
        await Inject(new byte[]{1,1});
        if(!(await outgoing.Reader.ReadAsync(ct)).SequenceEqual(new byte[]{1}))throw new InvalidOperationException("noVNC security handshake failed.");
        await Inject(new byte[]{0,0,0,0});
        if(!(await outgoing.Reader.ReadAsync(ct)).SequenceEqual(new byte[]{1}))throw new InvalidOperationException("noVNC shared session not requested.");
        // ServerInit: 64x48, 32bpp/24bit RGB little endian, true color, name 'Test'.
        await Inject(new byte[]{0,64,0,48,32,24,0,1,0,255,0,255,0,255,16,8,0,0,0,0,0,0,0,4,84,101,115,116});
        await connected.Task.WaitAsync(ct);
        var frame=new byte[16+64*48*4];
        frame[3]=1; frame[9]=64; frame[11]=48;
        for(int i=16;i<frame.Length;i+=4) { frame[i]=33;frame[i+1]=90;frame[i+2]=180; }
        // Split mid-header and mid-pixel to test streaming across arbitrary WebSocket fragments.
        await Inject(frame[..7]); await Inject(frame[7..23]); await Inject(frame[23..]);
        await Task.Delay(200,ct);
        var result=await GetFrameStatsAsync(ct);
        if(result.Width!=64||result.Height!=48||result.Nonblack!=3072)throw new InvalidOperationException("noVNC synthetic framebuffer mismatch.");
        while(outgoing.Reader.TryRead(out _)) { }
        SendCtrlAltDelete(); await Task.Delay(150,ct);
        var keys=new List<byte[]>(); while(outgoing.Reader.TryRead(out var packet))keys.Add(packet);
        var keyBytes=keys.SelectMany(k=>k).ToArray();
        if(keyBytes.Length!=48 || Enumerable.Range(0,6).Any(i=>keyBytes[i*8]!=4))throw new InvalidOperationException("noVNC Ctrl+Alt+Delete did not send six VNC key events.");
        foreach(var shortcut in GuestShortcuts.All) {
            SendKeys(shortcut.Keys); await Task.Delay(60,ct);
            var packets=new List<byte[]>();while(outgoing.Reader.TryRead(out var packet))packets.Add(packet);
            var bytes=packets.SelectMany(p=>p).ToArray();
            var expected=shortcut.Keys.Concat(shortcut.Keys.Reverse()).ToArray();
            if(bytes.Length!=expected.Length*8)throw new InvalidOperationException("Shortcut event count: "+shortcut.Label);
            for(int i=0;i<expected.Length;i++) {
                uint sym=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(i*8+4,4));
                if(bytes[i*8]!=4 || bytes[i*8+1]!=(i<shortcut.Keys.Length?1:0) || sym!=expected[i].Keysym)
                    throw new InvalidOperationException("Shortcut key order/release mismatch: "+shortcut.Label);
            }
        }
        foreach(var chord in ClipboardKeystrokes.Build("Az@ä€",true,false)) {
            SendKeys(chord);await Task.Delay(60,ct);
            var packets=new List<byte[]>();while(outgoing.Reader.TryRead(out var packet))packets.Add(packet);
            var bytes=packets.SelectMany(p=>p).ToArray();var expected=chord.Concat(chord.Reverse()).ToArray();
            if(bytes.Length!=expected.Length*8)throw new Exception("Typed text event count failed.");
            for(int i=0;i<expected.Length;i++)if(bytes[i*8]!=4 || bytes[i*8+1]!=(i<chord.Length?1:0) || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(i*8+4,4))!=expected[i].Keysym)throw new Exception("Typed text VNC key events failed.");
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"typing-vnc-result.txt"),"PASS: typed DE sample traverses the real bundled noVNC renderer as exact key press/release packets with clipboard sharing disabled. Synthetic RFB peer; no live VM.");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"shortcuts-result.txt"),"PASS: all "+GuestShortcuts.All.Length+" shortcut payloads traversed the bundled noVNC renderer; exact keysyms, press order and reverse releases verified against synthetic RFB output. No host or guest input generated.");
        Send(new {type="paste",text="synthetic-disabled"}); await Task.Delay(100,ct);
        if(outgoing.Reader.TryRead(out _))throw new InvalidOperationException("noVNC clipboard sent while disabled.");
        SetClipboardSharing(true); Send(new {type="paste",text="synthetic-text"});
        var clipboard=await outgoing.Reader.ReadAsync(ct);
        if(clipboard[0]!=6 || Encoding.ASCII.GetString(clipboard[8..])!="synthetic-text")throw new InvalidOperationException("noVNC clipboard encoding failed.");
        using(var image=File.Create(Path.Combine(AppContext.BaseDirectory,"novnc-preview.png")))
            await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,image);
        if(switchSession!=null)await switchSession();
        // No resize packets before the server advertises ExtendedDesktopSize.
        while(outgoing.Reader.TryRead(out _)) { }
        SetResizeEnabled(true); await Task.Delay(200,ct);
        while(outgoing.Reader.TryRead(out var packet))if(packet[0]==251)throw new InvalidOperationException("Resize sent to a server without resize support");
        // Framebuffer update: ExtendedDesktopSize (-308), one 64x48 screen.
        var desktop = new byte[]{0,0,0,1, 0,0,0,0,0,64,0,48,255,255,254,204,
            1,0,0,0, 0,0,0,0,0,0,0,0,0,64,0,48,0,0,0,0};
        await Inject(desktop); await Task.Delay(250,ct);
        var resizePackets = new List<byte[]>(); while(outgoing.Reader.TryRead(out var packet))resizePackets.Add(packet);
        if(!resizePackets.Any(p=>p[0]==251))throw new InvalidOperationException("noVNC did not request remote resize after capability advertisement");
        SetResizeEnabled(false);
        // Rejected resize: pending request completes; scaling remains available.
        desktop[5]=1;desktop[7]=1;
        await Inject(desktop); await Task.Delay(150,ct);
        while(outgoing.Reader.TryRead(out var packet))if(packet[0]==251)throw new InvalidOperationException("noVNC resized while disabled");
        var fallback = await GetFrameStatsAsync(ct);
        if(!SessionOpen || fallback.Width!=64 || fallback.Height!=48)throw new InvalidOperationException("Rejected resize broke noVNC fallback");
        SetResizeEnabled(true); await Task.Delay(200,ct);
        var resumed = new List<byte[]>();while(outgoing.Reader.TryRead(out var packet))resumed.Add(packet);
        if(!resumed.Any(p=>p[0]==251))throw new InvalidOperationException("noVNC resize did not resume");
        PauseInteraction(); await Inject(desktop); await Task.Delay(150,ct);
        while(outgoing.Reader.TryRead(out var packet))if(packet[0]==251)throw new InvalidOperationException("Background console issued resize request");
        Disconnect();
        if(CanSendKeys)throw new InvalidOperationException("noVNC input enabled after disconnect.");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"novnc-smoke-result.txt"),"PASS: bundled noVNC in WebView2; RFB handshake; fragmented raw framebuffer 64x48 with 3072 non-black pixels; Ctrl+Alt+Delete six key events; clipboard gate and text encoding; disconnect guard. Synthetic data only; no VM or Windows clipboard accessed.");
    }
}




