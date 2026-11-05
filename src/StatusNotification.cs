// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace ProxmoxSpice;
public partial class MainWindow
{
    readonly DispatcherTimer statusNotificationTimer=new() {Interval=TimeSpan.FromSeconds(5)};
    void InitializeStatusNotification()
    {
        StatusPopup.CustomPopupPlacementCallback=(size,target,offset)=>new[] {
            new CustomPopupPlacement(new Point(Math.Max(0,target.Width-size.Width-12),12),PopupPrimaryAxis.None)
        };
        statusNotificationTimer.Tick+=(_,_)=>HideStatusNotification();
        Loaded+=(_,_)=>ShowStatusNotification();
        LocationChanged+=(_,_)=>RepositionStatusNotification();
        SizeChanged+=(_,_)=>RepositionStatusNotification();
        Deactivated+=(_,_)=>HideStatusNotification();
        Closed+=(_,_)=>HideStatusNotification();
    }
    void RepositionStatusNotification()
    {
        StatusBar.MaxWidth=Math.Max(100,Math.Min(460,WorkspaceGrid.ActualWidth-32));
        if(!StatusPopup.IsOpen)return;
        var offset=StatusPopup.HorizontalOffset;
        StatusPopup.HorizontalOffset=offset+1; StatusPopup.HorizontalOffset=offset;
    }
    void ShowStatusClicked(object sender,RoutedEventArgs e) => ShowStatusNotification();
    void ShowStatusNotification()
    {
        if(closing || !IsLoaded || !IsActive || SidebarOverlay.IsOpen)return;
        RepositionStatusNotification();
        StatusPopup.IsOpen=true;
        statusNotificationTimer.Stop();statusNotificationTimer.Start();
    }
    void HideStatusNotification()
    {
        statusNotificationTimer.Stop();StatusPopup.IsOpen=false;
    }
}

