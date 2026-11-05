// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Drawing;
using System.Diagnostics;
using System.Runtime.InteropServices;

// Decoder -> owned canonical image -> UI-owned snapshot. Neither consumer
// paints decoder memory, nor can it race a surface destroy or resize.
sealed class FrameMailbox : IDisposable {
    readonly object gate=new object();
    N.PrimarySurface image;
    Rectangle dirty;
    bool reset=true;
    public long Updates;
    public event Action? Changed;
    double decoderCopyMs,uiCopyMs;
    public void ResetMetrics(){lock(gate){decoderCopyMs=uiCopyMs=0;Updates=0;}}
    public double[] CopyMetrics(){lock(gate)return new[]{decoderCopyMs,uiCopyMs};}
    long latest;
    [DllImport("kernel32.dll",EntryPoint="RtlMoveMemory")] static extern void Copy(IntPtr dst,IntPtr src,UIntPtr bytes);
    static void CopyArea(N.PrimarySurface dst,N.PrimarySurface src,Rectangle area){
        if(area.X==0&&area.Width==dst.Width&&dst.Stride==src.Stride){Copy(IntPtr.Add(dst.Data,area.Y*dst.Stride),IntPtr.Add(src.Data,area.Y*src.Stride),(UIntPtr)(area.Height*dst.Stride));return;}
        for(int y=area.Top;y<area.Bottom;y++)Copy(IntPtr.Add(dst.Data,y*dst.Stride+area.X*4),IntPtr.Add(src.Data,y*src.Stride+area.X*4),(UIntPtr)(area.Width*4));
    }
    public static void Free(ref N.PrimarySurface s){if(s.Data!=IntPtr.Zero)Marshal.FreeHGlobal(s.Data);s=new N.PrimarySurface();}
    public void Clear(){lock(gate){Free(ref image);dirty=Rectangle.Empty;reset=true;latest=Stopwatch.GetTimestamp();}Changed?.Invoke();}
    public void Write(N.PrimarySurface source,Rectangle region) {
        if(source.Format!=32||source.Width<1||source.Height<1||source.Width>16384||source.Height>16384||source.Stride<source.Width*4||source.Data==IntPtr.Zero)
            throw new InvalidOperationException("Worker prototype requires a valid 32-bit SPICE surface.");
        if((long)source.Width*source.Height*4>256*1024*1024)throw new InvalidOperationException("SPICE surface exceeds prototype memory limit.");
        lock(gate){
            if(image.Width!=source.Width||image.Height!=source.Height||image.Data==IntPtr.Zero){
                IntPtr allocation=Marshal.AllocHGlobal(source.Width*4*source.Height);
                Free(ref image);image=source;image.Stride=source.Width*4;image.Data=allocation;
                region=new Rectangle(0,0,image.Width,image.Height);reset=true;
            }
            region=Rectangle.Intersect(region,new Rectangle(0,0,image.Width,image.Height));
            if(region.IsEmpty)return;
            var copyWatch=Stopwatch.StartNew();CopyArea(image,source,region);decoderCopyMs+=copyWatch.Elapsed.TotalMilliseconds;
            dirty=dirty.IsEmpty?region:Rectangle.Union(dirty,region);latest=Stopwatch.GetTimestamp();Updates++;
        }
        Changed?.Invoke();
    }
    public bool Read(ref N.PrimarySurface target,out Rectangle region,out long timestamp,out bool replaced){
        lock(gate){
            region=dirty;timestamp=latest;replaced=reset;
            if(!reset&&dirty.IsEmpty)return false;
            if(reset||target.Width!=image.Width||target.Height!=image.Height){
                IntPtr allocation=image.Data==IntPtr.Zero?IntPtr.Zero:Marshal.AllocHGlobal(image.Stride*image.Height);
                Free(ref target);target=image;target.Data=allocation;
                region=new Rectangle(0,0,image.Width,image.Height);replaced=true;
            }
            if(target.Data!=IntPtr.Zero){var copyWatch=Stopwatch.StartNew();CopyArea(target,image,region);uiCopyMs+=copyWatch.Elapsed.TotalMilliseconds;}
            dirty=Rectangle.Empty;reset=false;return true;
        }
    }
    public void Dispose(){Clear();}
}
