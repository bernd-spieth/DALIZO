// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows.Controls;
namespace ProxmoxSpice;
public partial class MainWindow
{
    sealed record VmSortPreference(string Property, ListSortDirection Direction);
    VmSortPreference vmSort = new("Name", ListSortDirection.Ascending);
    string VmSortPath => store.FilePath + ".vm-sort.json";
    static bool ValidSort(VmSortPreference? value) => value != null &&
        value.Property is "Name" or "VmId" or "ServerName" or "ConsoleType" or "RuntimeStatus" &&
        value.Direction is ListSortDirection.Ascending or ListSortDirection.Descending;
    void LoadVmSort()
    {
        try {
            if(File.Exists(VmSortPath)) {
                var value=JsonSerializer.Deserialize<VmSortPreference>(File.ReadAllText(VmSortPath));
                if(ValidSort(value))vmSort=value!;
            }
        } catch(IOException) {} catch(JsonException) {} catch(UnauthorizedAccessException) {}
    }
    void InitializeVmSort()
    {
        LoadVmSort();LoadVmColumns();
        VmList.Sorting += VmListSorting;
    }
    void VmListSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled=true;
        var next=new VmSortPreference(e.Column.SortMemberPath,
            e.Column.SortDirection==ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending);
        if(!ValidSort(next))return;
        vmSort=next;ApplyVmSort();
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(VmSortPath)!);
            var temporary=VmSortPath+".tmp";
            File.WriteAllText(temporary,JsonSerializer.Serialize(vmSort));
            File.Move(temporary,VmSortPath,true);
        } catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
            Status("VM sorting is retained for this session but could not be saved.");
        }
    }
    void ApplyVmSort()
    {
        using(VmList.Items.DeferRefresh()) {
            VmList.Items.GroupDescriptions.Clear();
            if(data.Vms.Any(v=>v.Folder.Length>0)) {
                var group=new System.Windows.Data.PropertyGroupDescription(nameof(VmRow.FolderGroup));
                group.SortDescriptions.Add(new("Name",ListSortDirection.Ascending));
                VmList.Items.GroupDescriptions.Add(group);
            }
            VmList.Items.SortDescriptions.Clear();
            VmList.Items.SortDescriptions.Add(new(vmSort.Property,vmSort.Direction));
        }
        foreach(var column in VmList.Columns)
            column.SortDirection=column.SortMemberPath==vmSort.Property ? vmSort.Direction : null;
    }
}

