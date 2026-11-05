// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Text;
namespace ProxmoxSpice;
internal sealed partial class NoVncHost
{
    public async Task CheckSerialSessionAsync(CancellationToken ct)
    {
        if(!IsSerial)throw new InvalidOperationException("Not a serial terminal.");
        string clipboard="paste ä\nsecond line";writeClipboard=t=>clipboard=t;readClipboard=()=>clipboard;
        sharing=true;readOnly=false;Send(new{type="start",user="test@pve!client",password="synthetic-ticket",sharing=true,readOnly=false,resize=true});
        await started.Task.WaitAsync(ct);
        var auth=Encoding.UTF8.GetString(await outgoing.Reader.ReadAsync(ct));
        if(auth!="test@pve!client:synthetic-ticket\n")throw new Exception("Serial ticket authentication framing failed.");
        async Task Inject(byte[] bytes){acknowledged=new(TaskCreationOptions.RunContinuationsAsynchronously);Send(new{type="data",data=Convert.ToBase64String(bytes)});await acknowledged.Task.WaitAsync(ct);}
        await Inject(new byte[]{79});await Inject(Encoding.UTF8.GetBytes("Khello\r\n"));await connected.Task.WaitAsync(ct);
        var unicode=Encoding.UTF8.GetBytes("Grüße 世界\r\n\x1b[31mred\x1b[0m\r\n");await Inject(unicode[..3]);await Inject(unicode[3..]);
        serialText=new(TaskCreationOptions.RunContinuationsAsynchronously);Send(new{type="serial-test",operation="text"});
        var text=await serialText.Task.WaitAsync(ct);
        if(!text.Contains("hello")||!text.Contains("Grüße 世界")||!text.Contains("red"))throw new Exception("Fragmented terminal text/UTF8 rendering failed.");
        while(outgoing.Reader.TryRead(out _)){}
        Send(new{type="serial-test",operation="input",text="ä\r"});
        var input=Encoding.UTF8.GetString(await outgoing.Reader.ReadAsync(ct));if(input!="0:3:ä\r")throw new Exception("UTF8 serial input length failed.");
        await Inject(Encoding.UTF8.GetBytes("\x1b[?2004h"));SendClipboard();
        var paste=Encoding.UTF8.GetString(await outgoing.Reader.ReadAsync(ct));
        if(!paste.Contains("\x1b[200~paste ä\rsecond line\x1b[201~"))throw new Exception("Bracketed terminal paste failed.");
        clipboard="focus-probe";
        await browser.CoreWebView2.ExecuteScriptAsync("menu.hidden=false; document.getElementById('paste').focus(); document.getElementById('paste').click();");
        var menuPaste=Encoding.UTF8.GetString(await outgoing.Reader.ReadAsync(ct));
        if(!menuPaste.Contains("focus-probe"))throw new Exception("Context-menu paste failed.");
        var focused=await browser.CoreWebView2.ExecuteScriptAsync("document.activeElement===term.textarea");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"serial-focus-result.txt"),"Terminal focused after context-menu paste: "+focused);
        if(focused!="true")throw new Exception("Serial terminal loses keyboard focus after context-menu paste.");
        await browser.CoreWebView2.ExecuteScriptAsync("document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',code:'Enter',keyCode:13,which:13,bubbles:true,cancelable:true}));");
        var enter=Encoding.UTF8.GetString(await outgoing.Reader.ReadAsync(ct));
        if(enter!="0:1:\r")throw new Exception("Enter after paste did not reach serial input.");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"serial-focus-result.txt"),"PASS: context-menu paste returns DOM focus to terminal textarea; subsequent synthetic Enter on the focused element emits the expected serial carriage-return packet. Synthetic clipboard text; no live guest or authentication input.");
        Send(new{type="serial-test",operation="select",length=5});Send(new{type="serial-test",operation="copy"});
        serialText=new(TaskCreationOptions.RunContinuationsAsynchronously);Send(new{type="serial-test",operation="text"});await serialText.Task.WaitAsync(ct);
        if(clipboard!="hello")throw new Exception("Terminal selection copy failed.");
        SetClipboardSharing(false);SendClipboard();await Task.Delay(100,ct);
        if(outgoing.Reader.TryRead(out _))throw new Exception("Clipboard disabled gate failed.");
        PauseInteraction();Disconnect();if(CanSendKeys||SessionOpen)throw new Exception("Serial disconnect state failed.");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"serial-smoke-result.txt"),"PASS: bundled xterm.js in WebView2; fragmented OK authentication, fragmented UTF8 and ANSI output, UTF8 byte-length input, bracketed multiline paste, selection copy through host bridge, clipboard disabled gate and disconnect. Synthetic clipboard delegates; Windows clipboard unchanged; no guest commands sent.");
    }
}

