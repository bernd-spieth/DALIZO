// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

sealed partial class SpiceWorker
{
    const string SpiceDll="libspice-client-glib-2.0-8.dll";
    
    [DllImport(SpiceDll,CallingConvention=CallingConvention.Cdecl)] static extern void spice_main_clipboard_selection_grab(IntPtr c,uint selection,uint[] types,int count);
    [DllImport(SpiceDll,CallingConvention=CallingConvention.Cdecl)] static extern void spice_main_clipboard_selection_request(IntPtr c,uint selection,uint type);
    [DllImport(SpiceDll,CallingConvention=CallingConvention.Cdecl)] static extern void spice_main_clipboard_selection_notify(IntPtr c,uint selection,uint type,byte[] data,nuint count);
    [DllImport(SpiceDll,CallingConvention=CallingConvention.Cdecl)] static extern void spice_main_clipboard_selection_release(IntPtr c,uint selection);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ClipboardGrab(IntPtr c,uint selection,IntPtr types,uint count,IntPtr u);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ClipboardRequest(IntPtr c,uint selection,uint type,IntPtr u);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void ClipboardData(IntPtr c,uint selection,uint type,IntPtr data,uint size,IntPtr u);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void ClipboardRelease(IntPtr c,uint selection,IntPtr u);
    public volatile bool GuestAgentConnected,ClipboardCapability,MonitorCapability;
    volatile bool sharing, imageSharing;
    bool Allowed(uint type)=>type==1?sharing:type==2&&imageSharing;
    ClipboardPayload? localClipboard;
    bool awaitingClipboard; uint requestedType;
    readonly ConcurrentQueue<Tuple<int,ClipboardPayload>> receivedClipboard=new();
    public int ResizeRequests;
    public void SetClipboardSharing(bool enabled,bool images=false){
        sharing=enabled;imageSharing=images;while(receivedClipboard.TryDequeue(out _)){} int stamp=generation;Post(()=>{
            if(stamp!=generation)return;
            if(main!=IntPtr.Zero&&localClipboard!=null)spice_main_clipboard_selection_release(main,0);
            localClipboard=null;awaitingClipboard=false;
        });
    }
    public bool TryClipboard(out ClipboardPayload? payload){
        payload=null;while(receivedClipboard.TryDequeue(out var item))if(Allowed(item.Item2.Type)&&item.Item1==generation)payload=item.Item2;
        return payload!=null;
    }
    void UpdateAgent(){
        GuestAgentConnected=main!=IntPtr.Zero&&Get(main,"agent-connected")!=0;
        ClipboardCapability=GuestAgentConnected&&N.spice_main_agent_test_capability(main,5)!=0;
        MonitorCapability=GuestAgentConnected&&N.spice_main_agent_test_capability(main,1)!=0;
        if(!GuestAgentConnected){localClipboard=null;awaitingClipboard=false;}
    }
    void ResetClipboard(){GuestAgentConnected=ClipboardCapability=MonitorCapability=false;localClipboard=null;awaitingClipboard=false;while(receivedClipboard.TryDequeue(out _)){} }
    void BindClipboard(IntPtr channel){
        Bind(channel,"main-agent-update",new Notify((c,u)=>Guard(UpdateAgent)));
        Bind(channel,"main-clipboard-selection-grab",new ClipboardGrab((c,sel,types,count,u)=>{
            try{
                if(c!=main||sel!=0||(!sharing&&!imageSharing)||count>256)return 0;
                localClipboard=null;awaitingClipboard=false;
                foreach(uint wanted in new uint[]{2,1})if(Allowed(wanted))for(int i=0;i<count;i++)if(Marshal.ReadInt32(types,i*4)==wanted){awaitingClipboard=true;requestedType=wanted;spice_main_clipboard_selection_request(c,0,wanted);return 1;}
            }catch(Exception ex){Status="Clipboard: "+ex.Message;}
            return 0;
        }));
        Bind(channel,"main-clipboard-selection",new ClipboardData((c,sel,type,data,size,u)=>Guard(()=>{
            if(c!=main||sel!=0||!Allowed(type)||type!=requestedType||!awaitingClipboard)return;
            awaitingClipboard=false;
            if(size>(type==1?ClipboardPayload.TextLimit:ClipboardPayload.ImageLimit)){Status="Clipboard data exceeds the size limit.";return;}
            var bytes=new byte[(int)size];if(size>0)Marshal.Copy(data,bytes,0,(int)size);
            var payload=new ClipboardPayload(type,bytes);payload.Validate();while(receivedClipboard.TryDequeue(out _)){} receivedClipboard.Enqueue(Tuple.Create(generation,payload));
        })));
        Bind(channel,"main-clipboard-selection-request",new ClipboardRequest((c,sel,type,u)=>{
            try{if(c!=main||sel!=0||!Allowed(type))return 0;
                var bytes=localClipboard?.Type==type?localClipboard.Bytes:null;
                spice_main_clipboard_selection_notify(c,0,bytes==null?0u:type,bytes??Array.Empty<byte>(),(nuint)(bytes?.Length??0));return 1;
            }catch(Exception ex){Status="Clipboard: "+ex.Message;return 0;}
        }));
        Bind(channel,"main-clipboard-selection-release",new ClipboardRelease((c,sel,u)=>{if(c==main&&sel==0)awaitingClipboard=false;}));
    }
    public void OfferClipboard(string? text,bool paste=false)=>OfferPayload(text==null?null:ClipboardPayload.Text(text),paste);
    public void OfferPayload(ClipboardPayload? payload,bool paste=false){
        payload?.Validate();int stamp=generation;
        Post(()=>{
            if(stamp!=generation||!ClipboardCapability||main==IntPtr.Zero)return;
            if(payload!=null&&!Allowed(payload.Type))return;
            localClipboard=payload;awaitingClipboard=false;
            if(payload==null){spice_main_clipboard_selection_release(main,0);return;}
            spice_main_clipboard_selection_grab(main,0,new uint[]{payload.Type},1);
            if(paste&&InputReady)System.Threading.Tasks.Task.Delay(100).ContinueWith(_=>Post(()=>{
                if(stamp!=generation||!Allowed(payload.Type)||!InputReady||localClipboard!=payload)return;
                foreach(uint key in pressed)N.spice_inputs_key_release(inputs,key);pressed.Clear();
                N.spice_inputs_key_press(inputs,0x1d);N.spice_inputs_key_press(inputs,0x2f);
                N.spice_inputs_key_release(inputs,0x2f);N.spice_inputs_key_release(inputs,0x1d);
            }));
        });
    }
    public void ResizeGuest(int width,int height){int stamp=generation;Post(()=>{
        if(stamp!=generation||!MonitorCapability||main==IntPtr.Zero)return;
        N.spice_main_update_display_enabled(main,0,1,0);N.spice_main_update_display(main,0,0,0,width,height,0);
        if(N.spice_main_send_monitor_config(main)!=0)ResizeRequests++;
    });}
}
