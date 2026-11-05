// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;

namespace ProxmoxSpice;
public partial class MainWindow
{
    void VmListSizeChanged(object sender,SizeChangedEventArgs e)=>UpdateVmColumnWidths();
    void UpdateVmColumnWidths()
    {
        if(VmList==null || NameColumn==null || VmList.ActualWidth<=0)return;
        // Reserve the action buttons and scrollbar first. Text columns share
        // only the remaining space, so long names cannot push actions outside.
        var fixedWidth=VmList.Columns.Where(c=>c.Visibility==Visibility.Visible&&c!=NameColumn&&c!=ServerColumn).Sum(c=>c.Width.Value);
        var remaining=Math.Max(0,VmList.ActualWidth-SystemParameters.VerticalScrollBarWidth-4-fixedWidth);
        var serverWidth=ServerColumn.Visibility==Visibility.Visible?remaining*0.4:0;
        ServerColumn.Width=new DataGridLength(serverWidth);
        NameColumn.Width=new DataGridLength(remaining-serverWidth);
    }
}
