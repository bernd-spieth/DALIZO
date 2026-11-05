// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

sealed partial class SpiceWorker
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void FileProgress(long current,long total,IntPtr user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void FileFinished(IntPtr source,IntPtr result,IntPtr user);
    [DllImport(SpiceDll,CallingConvention=CallingConvention.Cdecl)] static extern void spice_main_file_copy_async(IntPtr channel,IntPtr files,int flags,IntPtr cancel,FileProgress progress,IntPtr progressUser,FileFinished finished,IntPtr user);
    [DllImport(SpiceDll,CallingConvention=CallingConvention.Cdecl)] static extern int spice_main_file_copy_finish(IntPtr channel,IntPtr result,out IntPtr error);
    [DllImport("libgio-2.0-0.dll",CallingConvention=CallingConvention.Cdecl)] static extern IntPtr g_file_new_for_path([MarshalAs(UnmanagedType.LPUTF8Str)]string path);
    [DllImport("libgio-2.0-0.dll",CallingConvention=CallingConvention.Cdecl)] static extern IntPtr g_cancellable_new();
    [DllImport("libgio-2.0-0.dll",CallingConvention=CallingConvention.Cdecl)] static extern void g_cancellable_cancel(IntPtr value);
    [DllImport("libglib-2.0-0.dll",CallingConvention=CallingConvention.Cdecl)] static extern void g_error_free(IntPtr error);
    [StructLayout(LayoutKind.Sequential)] struct GError {public uint Domain;public int Code;public IntPtr Message;}
    sealed class FileOperation
    {
        public readonly TaskCompletionSource<bool> Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<IntPtr> Files=new();
        public IntPtr Array,Cancel,Channel;public GCHandle Root;
        public CancellationTokenRegistration Registration;
        public IProgress<(long Current,long Total)>? Progress;
        public SpiceWorker Owner=null!;
    }
    readonly HashSet<FileOperation> fileOperations=new();
    static readonly FileProgress progressCallback=(current,total,user)=>{
        try{var op=(FileOperation)GCHandle.FromIntPtr(user).Target!;op.Progress?.Report((Math.Max(0,current),Math.Max(0,total)));}catch{ /* Never unwind through native frames. */ }
    };
    static readonly FileFinished finishedCallback=(source,result,user)=>{
        var op=(FileOperation)GCHandle.FromIntPtr(user).Target!;IntPtr error=IntPtr.Zero;
        try{
            if(spice_main_file_copy_finish(op.Channel,result,out error)==0){
                var message=error==IntPtr.Zero?"File transfer failed.":Marshal.PtrToStringUTF8(Marshal.PtrToStructure<GError>(error).Message)??"File transfer failed.";
                op.Completion.TrySetException(new IOException(message));
            }else op.Completion.TrySetResult(true);
        }catch(Exception ex){op.Completion.TrySetException(ex);}
        finally{if(error!=IntPtr.Zero)g_error_free(error);op.Owner.ReleaseFileOperation(op);}
    };
    void ReleaseFileOperation(FileOperation op)
    {
        op.Registration.Dispose();fileOperations.Remove(op);
        foreach(var file in op.Files)N.g_object_unref(file);
        if(op.Array!=IntPtr.Zero)Marshal.FreeHGlobal(op.Array);
        if(op.Cancel!=IntPtr.Zero)N.g_object_unref(op.Cancel);
        if(op.Channel!=IntPtr.Zero)N.g_object_unref(op.Channel);
        if(op.Root.IsAllocated)op.Root.Free();
    }
    internal static string[] ValidateTransferFiles(string[] paths)
    {
        if(paths.Length==0||paths.Length>100)throw new ArgumentException("Select between 1 and 100 files.");
        var files=paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach(var file in files)if(!File.Exists(file)||(File.GetAttributes(file)&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0)throw new IOException("Select regular files only; folders and links are not supported.");
        return files;
    }
    public Task SendFilesAsync(string[] paths,IProgress<(long Current,long Total)> progress,CancellationToken token)
    {
        var files=ValidateTransferFiles(paths);if(stopping)throw new ObjectDisposedException(nameof(SpiceWorker));
        var op=new FileOperation {Owner=this,Progress=progress};int stamp=generation;
        Post(()=>{
            try{
                token.ThrowIfCancellationRequested();
                if(stamp!=generation||main==IntPtr.Zero||!SessionOpen||!GuestAgentConnected)throw new InvalidOperationException("Connect a SPICE console with a running guest agent first.");
                op.Channel=N.g_object_ref(main);op.Cancel=g_cancellable_new();
                foreach(var file in files){var handle=g_file_new_for_path(file);if(handle==IntPtr.Zero)throw new IOException("Could not open a transfer source.");op.Files.Add(handle);}
                op.Array=Marshal.AllocHGlobal((op.Files.Count+1)*IntPtr.Size);
                for(int i=0;i<op.Files.Count;i++)Marshal.WriteIntPtr(op.Array,i*IntPtr.Size,op.Files[i]);
                Marshal.WriteIntPtr(op.Array,op.Files.Count*IntPtr.Size,IntPtr.Zero);
                op.Root=GCHandle.Alloc(op);fileOperations.Add(op);
                // Cancellation is queued on the GLib owner; completion owns native cleanup.
                op.Registration=token.Register(()=>Post(()=>{if(fileOperations.Contains(op))g_cancellable_cancel(op.Cancel);}));
                var user=GCHandle.ToIntPtr(op.Root);
                spice_main_file_copy_async(op.Channel,op.Array,0,op.Cancel,progressCallback,user,finishedCallback,user);
            }catch(Exception ex){op.Completion.TrySetException(ex);ReleaseFileOperation(op);}
        });
        return op.Completion.Task;
    }
    void CancelFileTransfers(){foreach(var op in fileOperations.ToArray())g_cancellable_cancel(op.Cancel);}
}
