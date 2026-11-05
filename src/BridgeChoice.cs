// SPDX-License-Identifier: GPL-3.0-only
using System.Windows.Controls;
namespace ProxmoxSpice;
public partial class MainWindow
{
    static ComboBox AddBridgeChoice(EditorDialog dialog,string key,string current,IReadOnlyList<string> bridges)
    {
        var values=bridges.Append(current).Append("").Distinct(StringComparer.Ordinal).OrderBy(s=>s,StringComparer.OrdinalIgnoreCase).Cast<object>().ToArray();
        var box=dialog.Choice(key+" · Bridge / VNet (blank = user-mode network)",values,current);
        box.DisplayMemberPath="";box.IsEditable=true;box.Text=current;
        box.ToolTip="Choose a network from this VM's node. The existing value is kept even if it is not returned by the server.";
        return box;
    }
}
