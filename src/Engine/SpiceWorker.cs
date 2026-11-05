// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

sealed partial class SpiceWorker : IDisposable {
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Channel(IntPtr s,IntPtr c,IntPtr u);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Event(IntPtr c,int e,IntPtr u);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Damage(IntPtr c,int x,int y,int w,int h,IntPtr u);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Notify(IntPtr c,IntPtr u);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Create(IntPtr c,int f,int w,int h,int stride,int shm,IntPtr p,IntPtr u);
    [DllImport("libglib-2.0-0.dll",CallingConvention=CallingConvention.Cdecl)] static extern void g_main_context_wakeup(IntPtr context);
    static readonly ConcurrentQueue<Action> commands=new ConcurrentQueue<Action>();
    static readonly Thread dispatcher=StartDispatcher();
    readonly List<Delegate> callbacks=new List<Delegate>();
    readonly List<Tuple<IntPtr,ulong>> bindings=new List<Tuple<IntPtr,ulong>>();
    
    readonly object metricGate=new object();
    readonly List<double> delays=new List<double>();
    public readonly StageProbe Trace=new();
    public readonly FrameMailbox Frames=new FrameMailbox();
    public volatile string Status="Disconnected";
    public volatile bool InputReady;
    volatile bool stopping;
    int generation;
    IntPtr session,display,inputs,main;
    public volatile bool SessionOpen;
    readonly HashSet<uint> pressed=new HashSet<uint>();
    int mouseButtons;
    public int ThreadId;
    long inputDispatched;
    public SpiceWorker(){ThreadId=dispatcher.ManagedThreadId;}
    static Thread StartDispatcher(){var t=new Thread(Run){IsBackground=true,Name="DALIZO SPICE decoder"};t.Start();return t;}
    public void Probe(){Post(()=>{},true);}
    public void RequestFrame(){int stamp=generation;Post(()=>{if(stamp==generation)Capture(new Rectangle(0,0,int.MaxValue,int.MaxValue));},true);}
    void Post(Action action,bool measure=false){
        if(stopping)return;long queued=Stopwatch.GetTimestamp();
        commands.Enqueue(()=>{if(measure){lock(metricGate){if(delays.Count<4096)delays.Add((Stopwatch.GetTimestamp()-queued)*1000.0/Stopwatch.Frequency);}}try{action();}catch(Exception ex){Status=ex.Message;}});
        g_main_context_wakeup(IntPtr.Zero);
    }
    static void Run(){
        while(true){Action action;int count=0;
            while(count++<256&&commands.TryDequeue(out action))action();
            N.g_main_context_iteration(IntPtr.Zero,commands.IsEmpty?1:0);
        }
    }
    public Dictionary<string,object> Metrics(){lock(metricGate){var a=delays.OrderBy(x=>x).ToArray();return new Dictionary<string,object>{{"input_commands_dispatched",Interlocked.Read(ref inputDispatched)},{"worker_dispatch_samples",a.Length},{"worker_dispatch_p95_ms",a.Length==0?0:a[(int)((a.Length-1)*.95)]},{"worker_dispatch_max_ms",a.Length==0?0:a[a.Length-1]},{"worker_thread_id",ThreadId}};}}
    public void ResetMetrics(){lock(metricGate)delays.Clear();}
    void Input(Action<IntPtr> action){int stamp=generation;Post(()=>{if(stamp==generation&&InputReady&&inputs!=IntPtr.Zero){Interlocked.Increment(ref inputDispatched);action(inputs);}},true);}
    public void Key(uint scan,bool down){Input(ch=>{if(down){pressed.Add(scan);N.spice_inputs_key_press(ch,scan);}else{pressed.Remove(scan);N.spice_inputs_key_release(ch,scan);}});}
    public void SendChord(uint[] keys){
        if(keys.Length<1||keys.Length>8)throw new ArgumentException("Invalid chord.");
        var copy=(uint[])keys.Clone();
        Input(ch=>SendChordCore(copy,(key,down)=>{if(down)N.spice_inputs_key_press(ch,key);else N.spice_inputs_key_release(ch,key);}));
    }
    internal static void SendChordCore(uint[] keys,Action<uint,bool> send){
        int sent=0;
        try{foreach(var key in keys){send(key,true);sent++;}}
        finally{for(int i=sent-1;i>=0;i--)send(keys[i],false);}
    }
    public void Position(int x,int y,int buttons){Input(ch=>N.spice_inputs_position(ch,x,y,0,buttons));}
    public void Button(int b,int mask,bool down){Input(ch=>{Trace.Mark(down?"press_dispatch":"release_dispatch");mouseButtons=mask;if(down)N.spice_inputs_button_press(ch,b,mask);else N.spice_inputs_button_release(ch,b,mask);});}
    public void Disconnect(){SessionOpen=false;Interlocked.Increment(ref generation);Post(DisconnectCore);}
    public void Connect(Dictionary<string,string> config){SessionOpen=true;int stamp=generation;Post(()=>{if(stamp!=generation){config.Clear();return;}try{ConnectCore(config);}catch{SessionOpen=false;DisconnectCore();throw;}finally{config.Clear();}});}
    int Get(IntPtr obj,string key){int value;N.GetInt(obj,key,out value,IntPtr.Zero);return value;}
    void Bind(IntPtr obj,string name,Delegate callback){callbacks.Add(callback);N.g_object_ref(obj);bindings.Add(Tuple.Create(obj,N.g_signal_connect_data(obj,name,Marshal.GetFunctionPointerForDelegate(callback),IntPtr.Zero,IntPtr.Zero,0)));}
    void Guard(Action action){try{action();}catch(Exception ex){Status=ex.Message;}}
    void ConnectCore(Dictionary<string,string> cfg){
        DisconnectCore();SessionOpen=true;Status="Connecting...";session=N.spice_session_new();
        foreach(string key in new[]{"host","tls-port","password","proxy"})N.SetString(session,key,cfg[key],IntPtr.Zero);
        N.SetString(session,"cert-subject",cfg["host-subject"],IntPtr.Zero);N.SetInt(session,"verify",4,IntPtr.Zero);
        N.SetInt(session,"enable-usbredir",0,IntPtr.Zero);N.SetInt(session,"enable-audio",0,IntPtr.Zero);
        IntPtr ca=N.g_byte_array_new();try{byte[] b=Encoding.UTF8.GetBytes(cfg["ca"]);N.g_byte_array_append(ca,b,(uint)b.Length);N.SetPtr(session,"ca",ca,IntPtr.Zero);}finally{N.g_byte_array_unref(ca);}
        Bind(session,"channel-new",new Channel((s,c,u)=>Guard(()=>NewChannel(c))));
        Bind(session,"channel-destroy",new Channel((s,c,u)=>{if(c==display){display=IntPtr.Zero;Frames.Clear();}if(c==inputs){InputReady=false;inputs=IntPtr.Zero;}if(c==main){main=IntPtr.Zero;UpdateAgent();}}));
        if(N.spice_session_connect(session)==0)throw new InvalidOperationException("SPICE connection could not start.");
    }
    void NewChannel(IntPtr channel){int type=Get(channel,"channel-type");int channelGeneration=generation;
        Bind(channel,"channel-event",new Event((c,e,u)=>Guard(()=>{
            if(channelGeneration!=generation)return;
            if(type==3)InputReady=e==10;
            if(type==1&&e==10){N.spice_main_request_mouse_mode(c,2);UpdateAgent();}
            if(e>=20&&e<=24||type==1&&e==12){SessionOpen=false;InputReady=false;Frames.Clear();Status="SPICE connection error (channel event "+e+"). Reconnect for a fresh ticket.";}
        })));
        if(type==2&&Get(channel,"channel-id")==0){display=channel;
            Bind(channel,"display-primary-create",new Create((c,f,w,h,st,sh,p,u)=>Guard(()=>Capture(new Rectangle(0,0,w,h)))));
            Bind(channel,"display-primary-destroy",new Notify((c,u)=>Frames.Clear()));
            Bind(channel,"display-invalidate",new Damage((c,x,y,w,h,u)=>Guard(()=>Capture(new Rectangle(x,y,w,h)))));
            Bind(channel,"display-mark",new Event((c,m,u)=>{if(m!=0)Guard(()=>Capture(new Rectangle(0,0,int.MaxValue,int.MaxValue)));}));
        }
        if(type==1){main=channel;BindClipboard(channel);UpdateAgent();}
        if(type==3)inputs=channel;
        if(type==2||type==3)N.spice_channel_connect(channel);
    }
    void Capture(Rectangle area){N.PrimarySurface surface;if(display==IntPtr.Zero||N.spice_display_get_primary(display,0,out surface)==0)return;Trace.Damage(area);Frames.Write(surface,area);Trace.Copied();Status="Connected - "+surface.Width+" x "+surface.Height;}
    void DisconnectCore(){if(InputReady&&inputs!=IntPtr.Zero){foreach(uint key in pressed)N.spice_inputs_key_release(inputs,key);for(int b=1;b<=3;b++)if((mouseButtons&(1<<(b-1)))!=0)N.spice_inputs_button_release(inputs,b,0);}pressed.Clear();mouseButtons=0;
        InputReady=false;CancelFileTransfers();ResetClipboard();if(session!=IntPtr.Zero)N.spice_session_disconnect(session);Frames.Clear();
        foreach(var b in bindings)N.g_signal_handler_disconnect(b.Item1,b.Item2);
        foreach(var b in bindings)N.g_object_unref(b.Item1);
        bindings.Clear();callbacks.Clear();if(session!=IntPtr.Zero)N.g_object_unref(session);session=display=inputs=main=IntPtr.Zero;Status="Disconnected";
    }
    public void Dispose(){
        if(stopping)return;
        SessionOpen=false;Interlocked.Increment(ref generation);
        using(var done=new ManualResetEventSlim()){
            Post(()=>{try{DisconnectCore();Frames.Dispose();stopping=true;}finally{done.Set();}});
            if(!done.Wait(5000))throw new TimeoutException("SPICE session did not close.");
        }
    }
}
