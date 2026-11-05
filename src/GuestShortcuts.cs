// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ProxmoxSpice;

internal sealed record GuestKey(uint Scan, uint Keysym, string Code);
internal sealed record GuestShortcut(string Label, string Help, string Group, params GuestKey[] Keys);
internal static class GuestShortcuts
{
    static readonly GuestKey Ctrl=new(0x1d,0xffe3,"ControlLeft"), Alt=new(0x38,0xffe9,"AltLeft"), Shift=new(0x2a,0xffe1,"ShiftLeft"), Super=new(0x15b,0xffeb,"MetaLeft");
    static GuestKey K(uint scan,uint sym,string code)=>new(scan,sym,code);
    internal static readonly GuestShortcut[] All = {
        new("Ctrl+Alt+Delete", "Windows security screen. Other guests may log out or reboot depending on configuration.", "", Ctrl,Alt,K(0x153,0xffff,"Delete")),
        new("Alt+Tab", "Switch to the next application; behavior depends on the guest desktop.", "Common", Alt,K(0x0f,0xff09,"Tab")),
        new("Alt+Shift+Tab", "Switch to the previous application; behavior depends on the guest desktop.", "Common", Alt,Shift,K(0x0f,0xff09,"Tab")),
        new("Escape", "Dismiss the current guest menu or dialog where supported.", "Common", K(1,0xff1b,"Escape")),
        new("Windows+E", "Open File Explorer in Windows.", "Windows", Super,K(0x12,0x65,"KeyE")),
        new("Windows+D", "Show or hide the Windows desktop.", "Windows", Super,K(0x20,0x64,"KeyD")),
        new("Windows+R", "Open the Windows Run dialog.", "Windows", Super,K(0x13,0x72,"KeyR")),
        new("Ctrl+Shift+Escape", "Open Windows Task Manager.", "Windows", Ctrl,Shift,K(1,0xff1b,"Escape")),
        new("Super", "Open the desktop overview or launcher; depends on the Linux desktop and its settings.", "Linux", Super),
        new("Alt+F2", "Open the command launcher on desktops such as GNOME; desktop-dependent.", "Linux", Alt,K(0x3c,0xffbf,"F2")),
        new("Ctrl+Alt+T", "Common terminal shortcut on some Linux desktops; may be unassigned.", "Linux", Ctrl,Alt,K(0x14,0x74,"KeyT")),
        new("Ctrl+Alt+F1", "Request virtual terminal 1. Availability and graphical-session placement depend on the guest.", "Linux", Ctrl,Alt,K(0x3b,0xffbe,"F1")),
        new("Ctrl+Alt+F2", "Request virtual terminal 2. Availability and graphical-session placement depend on the guest.", "Linux", Ctrl,Alt,K(0x3c,0xffbf,"F2")),
        new("Ctrl+Alt+F3", "Request virtual terminal 3. Availability and graphical-session placement depend on the guest.", "Linux", Ctrl,Alt,K(0x3d,0xffc0,"F3")),
        new("Ctrl+Alt+F7", "Request virtual terminal 7. Some Linux systems use this for the graphical session; others do not.", "Linux", Ctrl,Alt,K(0x41,0xffc4,"F7"))
    };
}

public partial class MainWindow
{
    readonly ContextMenu guestKeysMenu=new();
    readonly MenuItem TypeClipboardButton=new(){Header="Type clipboard as keystrokes…"};
    readonly MenuItem CancelTypingButton=new(){Header="Stop typing",Visibility=Visibility.Collapsed};
    void InitializeGuestShortcuts()
    {
        guestKeysMenu.Style=(Style)FindResource("UnifiedMenu");
        guestKeysMenu.Resources[typeof(MenuItem)]=FindResource("UnifiedMenuItem");
        MenuItem Item(GuestShortcut shortcut) {
            var item=new MenuItem {Header=shortcut.Label,ToolTip=shortcut.Help};
            item.Click+=(_,_)=>SendGuestShortcut(shortcut);
            return item;
        }
        guestKeysMenu.Items.Add(Item(GuestShortcuts.All[0]));
        guestKeysMenu.Items.Add(new Separator());
        TypeClipboardButton.Click+=TypeClipboardClicked;CancelTypingButton.Click+=CancelTypingClicked;
        guestKeysMenu.Items.Add(TypeClipboardButton);guestKeysMenu.Items.Add(CancelTypingButton);
        guestKeysMenu.Items.Add(new Separator());
        foreach(var group in GuestShortcuts.All.Skip(1).GroupBy(s=>s.Group)) {
            guestKeysMenu.Items.Add(new MenuItem {Header=group.Key,Style=(Style)FindResource("MenuSection"),IsEnabled=false,Focusable=false});
            foreach(var shortcut in group)guestKeysMenu.Items.Add(Item(shortcut));
        }
        Closed+=(_,_)=>guestKeysMenu.IsOpen=false;
    }
    void ShowGuestShortcuts(object sender,RoutedEventArgs e)
    {
        if(!HasDisplayedConsole || !(host?.CanSendKeys==true || vncHost is {CanSendKeys:true,IsSerial:false}))return;
        ManagementToggle.IsChecked=false;OptionsToggle.IsChecked=false;ShowSidebar(false);
        guestKeysMenu.PlacementTarget=CtrlAltDeleteButton;
        guestKeysMenu.Placement=PlacementMode.Bottom;
        guestKeysMenu.IsOpen=true;
    }
    void SendGuestShortcut(GuestShortcut shortcut)
    {
        guestKeysMenu.IsOpen=false;ManagementToggle.IsChecked=false;
        if(!HasDisplayedConsole)return;
        try {
            if(host?.CanSendKeys==true)host.SendKeys(shortcut.Keys.Select(k=>k.Scan).ToArray());
            else if(vncHost is {CanSendKeys:true,IsSerial:false})vncHost.SendKeys(shortcut.Keys);
            else return;
            Status(shortcut.Label+" sent to the VM.");
        } catch(Exception error) {Status("Could not send the key combination: "+error.Message);}
    }
}
