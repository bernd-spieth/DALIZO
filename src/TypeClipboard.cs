// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
namespace ProxmoxSpice;
public partial class MainWindow
{
    CancellationTokenSource? typingCancellation;
    void CancelTypingClicked(object sender,RoutedEventArgs e){guestKeysMenu.IsOpen=false;typingCancellation?.Cancel();}
    async void TypeClipboardClicked(object sender,RoutedEventArgs e)
    {
        if(typingCancellation!=null||!HasDisplayedConsole||vncHost?.IsSerial==true)return;
        guestKeysMenu.IsOpen=false;ManagementToggle.IsChecked=false;ShowSidebar(false);
        var vm=targetVm;var spice=host;var vnc=vncHost;
        try {
            if(!Clipboard.ContainsText()){Status("The clipboard contains no text.");return;}
            string text=Clipboard.GetText();
            var dialog=new EditorDialog(this,"Type clipboard as keystrokes","Type text");
            dialog.Note($"Destination: {vm!.Name} · VM {vm.VmId}. {text.Length} characters. Clipboard content is not shown or logged.");
            var layout=dialog.Choice("Guest keyboard layout",new object[]{"German (DE)","English (US)"},"German (DE)");layout.DisplayMemberPath="";
            var speed=dialog.Choice("Delay between keystrokes",new object[]{"50 ms","100 ms","20 ms"},"50 ms");speed.DisplayMemberPath="";
            dialog.Note("Focus the destination input field in the guest before opening this dialog. The selected layout must match the guest; turn off Caps Lock. Do not type manually while sending. No SPICE agent is needed. Unsupported characters are rejected before any typing starts.");
            var controls=dialog.Check("Allow Enter and Tab from clipboard text",false);
            dialog.Note("Enter can submit a password or execute a command. No additional Enter is appended. Use the keyboard menu > Stop typing to cancel.");
            List<GuestKey[]>? chords=null;
            dialog.Validate=()=>{chords=ClipboardKeystrokes.Build(text,layout.SelectedIndex==0,controls.IsChecked==true);return true;};
            if(dialog.ShowDialog()!=true||chords==null)return;
            text="";
            using var cancel=new CancellationTokenSource();typingCancellation=cancel;UpdateConnectButtons();
            int delay=int.Parse(((string)speed.SelectedItem).Split(' ')[0]);
            try {
                spice?.FocusConsole();vnc?.FocusConsole();
                foreach(var chord in chords){
                    cancel.Token.ThrowIfCancellationRequested();
                    if(closing||!HasDisplayedConsole||targetVm?.Id!=vm.Id||host!=spice||vncHost!=vnc)throw new OperationCanceledException();
                    if(spice?.CanSendKeys==true)spice.SendTypedKeys(chord.Select(k=>k.Scan).ToArray());
                    else if(vnc is {CanSendKeys:true,IsSerial:false})vnc.SendKeys(chord);
                    else throw new OperationCanceledException();
                    await Task.Delay(delay,cancel.Token);
                }
                Status("Clipboard text sent as keystrokes. No Enter was appended.");
            } catch(OperationCanceledException){Status("Typing stopped. Already sent characters remain in the guest.");}
            finally {typingCancellation=null;UpdateConnectButtons();}
        } catch(Exception error){ReportError("Could not type clipboard text:",error);}
    }
}


