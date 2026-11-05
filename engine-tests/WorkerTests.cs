// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

class WorkerTests {
    public static int Run(){
        N.SetDllDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"VirtViewer v11.0-256","bin"));
        using(var worker=new SpiceWorker()){
            for(int i=0;i<100;i++)worker.Probe();
            // Deliberately stop the caller: decoder scheduling must not depend on it.
            Thread.Sleep(300);
            var metrics=worker.Metrics();
            if((int)metrics["worker_dispatch_samples"]!=100)throw new Exception("Worker depends on caller message loop.");
            if((int)metrics["worker_thread_id"]==Thread.CurrentThread.ManagedThreadId)throw new Exception("Wrong thread.");
            Console.WriteLine("Worker dispatch with caller blocked: p95="+metrics["worker_dispatch_p95_ms"]+" ms; max="+metrics["worker_dispatch_max_ms"]+" ms");
        }
        using(var box=new FrameMailbox()) {
            bool done=false;Exception failure=null;
            var producer=new Thread(()=>{try{
                for(int i=1;i<=500;i++){
                    int w=i%2==0?256:160,h=i%2==0?128:90;var pixels=new int[w*h];for(int j=0;j<pixels.Length;j++)pixels[j]=i;
                    IntPtr memory=Marshal.AllocHGlobal(pixels.Length*4);
                    try {Marshal.Copy(pixels,0,memory,pixels.Length);box.Write(new N.PrimarySurface {Format=32,Width=w,Height=h,Stride=w*4,Data=memory},new Rectangle(0,0,w,h));}
                    finally{Marshal.FreeHGlobal(memory);}
                    if(i%11==0)box.Clear();Thread.Sleep(1);
                }
            }catch(Exception ex){failure=ex;}finally{Volatile.Write(ref done,true);}});
            producer.Start();N.PrimarySurface target=new N.PrimarySurface();int reads=0;
            try {while(!Volatile.Read(ref done)){
                Rectangle area;long timestamp;bool replaced;
                if(box.Read(ref target,out area,out timestamp,out replaced)&&target.Data!=IntPtr.Zero){
                    // Producer can replace/free its image while this independent snapshot is read.
                    int expected=Marshal.ReadInt32(target.Data);
                    Thread.Sleep(1);
                    for(int j=0;j<target.Width*target.Height;j++)if(Marshal.ReadInt32(target.Data,j*4)!=expected)throw new Exception("Torn or freed snapshot.");
                    reads++;
                }
            }}finally{producer.Join();FrameMailbox.Free(ref target);}
            if(failure!=null)throw failure;if(reads<20)throw new Exception("Insufficient concurrent snapshots.");
            Console.WriteLine("PASS: "+reads+" concurrent snapshots across 500 surface writes/resizes and repeated clears.");
        }
        Console.WriteLine("PASS: independent worker dispatch, shutdown, owned snapshot lifetime and consistency.");return 0;
    }
}
