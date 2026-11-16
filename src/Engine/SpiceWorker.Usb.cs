// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
sealed partial class SpiceWorker {
    internal sealed record UsbChoice(long Snapshot,int Index,string Name,bool Connected){
        public string Caption=>(Connected?"Release · ":"Redirect · ")+Name;
    }
    const string UsbGlib="libglib-2.0-0.dll";
    [DllImport(SpiceDll,CallingConvention=CallingConvention.Cdecl)] static extern IntPtr spice_usb_device_manager_get(IntPtr session,out IntPtr error);
    [DllImport(SpiceDll,CallingConvention=CallingConvention.Cdecl)] static extern IntPtr spice_usb_device_manager_get_devices(IntPtr manager);
    [DllImport(SpiceDll,CallingConvention=CallingConvention.Cdecl)] static extern IntPtr spice_usb_device_get_description(IntPtr device,IntPtr format);
    [DllImport(SpiceDll,CallingConvention=CallingConvention.Cdecl)] static extern int spice_usb_device_manager_is_device_connected(IntPtr manager,IntPtr device);
    [DllImport(SpiceDll,CallingConvention=CallingConvention.Cdecl)] static extern void spice_usb_device_manager_connect_device_async(IntPtr manager,IntPtr device,IntPtr cancel,FileFinished callback,IntPtr user);
    [DllImport(SpiceDll,CallingConvention=CallingConvention.Cdecl)] static extern int spice_usb_device_manager_connect_device_finish(IntPtr manager,IntPtr result,out IntPtr error);
    [DllImport(SpiceDll,CallingConvention=CallingConvention.Cdecl)] static extern void spice_usb_device_manager_disconnect_device(IntPtr manager,IntPtr device);
    [DllImport(UsbGlib,CallingConvention=CallingConvention.Cdecl)] static extern void g_ptr_array_unref(IntPtr array);
    [DllImport(UsbGlib,CallingConvention=CallingConvention.Cdecl)] static extern void g_free(IntPtr value);
    IntPtr usbManager,usbDevices;
    long usbSnapshot;
    void ReleaseUsb(){
        usbSnapshot++;
        if(usbDevices!=IntPtr.Zero)g_ptr_array_unref(usbDevices);
        usbDevices=usbManager=IntPtr.Zero;
    }
    static Exception UsbError(IntPtr error,string fallback){
        if(error==IntPtr.Zero)return new InvalidOperationException(fallback);
        try{return new InvalidOperationException(Marshal.PtrToStringUTF8(Marshal.PtrToStructure<GError>(error).Message)??fallback);}
        finally{g_error_free(error);}
    }
    internal Task<List<UsbChoice>> ListUsb(){
        var result=new TaskCompletionSource<List<UsbChoice>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(()=>{try{
            ReleaseUsb();
            if(!SessionOpen || session==IntPtr.Zero)throw new InvalidOperationException("Reconnect the SPICE console.");
            usbManager=spice_usb_device_manager_get(session,out var error);
            if(usbManager==IntPtr.Zero)throw UsbError(error,"USB redirection unavailable. Check VirtViewer and UsbDk.");
            usbDevices=spice_usb_device_manager_get_devices(usbManager);
            var devices=new List<UsbChoice>();
            if(usbDevices!=IntPtr.Zero){
                int count=Marshal.ReadInt32(usbDevices,IntPtr.Size);
                for(int i=0;i<count;i++){
                    var device=Marshal.ReadIntPtr(Marshal.ReadIntPtr(usbDevices),i*IntPtr.Size);
                    var label=spice_usb_device_get_description(device,IntPtr.Zero);
                    try{devices.Add(new(usbSnapshot,i,Marshal.PtrToStringUTF8(label)??"USB device",spice_usb_device_manager_is_device_connected(usbManager,device)!=0));}
                    finally{g_free(label);}
                }
            }
            result.SetResult(devices);
        }catch(Exception e){result.SetException(e);}});
        return result.Task;
    }
    static readonly FileFinished usbFinished=(manager,res,user)=>{
        var handle=GCHandle.FromIntPtr(user);
        var completion=(TaskCompletionSource<bool>)handle.Target!;
        try{
            if(spice_usb_device_manager_connect_device_finish(manager,res,out var err)==0)throw UsbError(err,"USB redirection failed.");
            completion.SetResult(true);
        }catch(Exception ex){completion.SetException(ex);}finally{handle.Free();}
    };
    internal Task ToggleUsb(UsbChoice choice){
        var done=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(()=>{try{
            if(!SessionOpen || usbDevices==IntPtr.Zero || choice.Snapshot!=usbSnapshot)throw new InvalidOperationException("Reopen USB devices for the current session.");
            var device=Marshal.ReadIntPtr(Marshal.ReadIntPtr(usbDevices),choice.Index*IntPtr.Size);
            if(choice.Connected){spice_usb_device_manager_disconnect_device(usbManager,device);done.SetResult(true);}
            else{
                var handle=GCHandle.Alloc(done);
                try{spice_usb_device_manager_connect_device_async(usbManager,device,IntPtr.Zero,usbFinished,GCHandle.ToIntPtr(handle));}
                catch{handle.Free();throw;}
            }
        }catch(Exception e){done.SetException(e);}});
        return done.Task;
    }
}
