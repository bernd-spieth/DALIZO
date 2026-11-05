// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Text.Json;
using System.Windows.Controls;
namespace ProxmoxSpice;
public partial class MainWindow
{
    bool restoringColumns;
    string VmColumnsPath => store.FilePath + ".vm-columns.json";
    void LoadVmColumns()
    {
        restoringColumns=true;
        try {
            if(!File.Exists(VmColumnsPath))return;
            var names=JsonSerializer.Deserialize<string[]>(File.ReadAllText(VmColumnsPath));
            if(names==null||names.Any(string.IsNullOrEmpty)||names.Distinct().Count()!=names.Length)return;
            var columns=VmList.Columns.ToDictionary(c=>c.Header.ToString()!);
            if(names.Length!=columns.Count||names.Any(n=>!columns.ContainsKey(n)))return;
            for(int i=0;i<names.Length;i++)columns[names[i]].DisplayIndex=i;
        } catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException) {}
        finally {restoringColumns=false;}
    }
    void VmColumnReordered(object? sender,DataGridColumnEventArgs e)
    {
        if(restoringColumns)return;
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(VmColumnsPath)!);
            var temporary=VmColumnsPath+".tmp";
            File.WriteAllText(temporary,JsonSerializer.Serialize(VmList.Columns.OrderBy(c=>c.DisplayIndex).Select(c=>c.Header.ToString()).ToArray()));
            File.Move(temporary,VmColumnsPath,true);
        } catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
            Status("Column order is retained for this session but could not be saved.");
        }
    }
}
