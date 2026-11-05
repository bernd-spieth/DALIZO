// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Windows.Forms;
sealed partial class NativeConsole
{
    public bool FilesAllowed;
    public event Action<string[]>? FilesDropped;
    void FileDragEnter(object? sender,DragEventArgs e)
    {
        e.Effect=FilesAllowed&&CanInput&&Backend.GuestAgentConnected&&e.Data?.GetDataPresent(DataFormats.FileDrop)==true&&(e.AllowedEffect&DragDropEffects.Copy)!=0?DragDropEffects.Copy:DragDropEffects.None;
    }
    void FileDragDrop(object? sender,DragEventArgs e)
    {
        if(!FilesAllowed||!CanInput||!Backend.GuestAgentConnected||e.Data?.GetData(DataFormats.FileDrop) is not string[] files)return;
        ReleaseInput();FilesDropped?.Invoke(files);
    }
}
