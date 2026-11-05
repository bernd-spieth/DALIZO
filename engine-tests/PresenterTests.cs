// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices;
class PresenterTests {
    [DllImport("user32.dll")] static extern uint GetGuiResources(IntPtr process,uint flag);
    static Bitmap Render(GdiPresenter p,N.PrimarySurface s,int w,int h) {
        var image=new Bitmap(w,h,PixelFormat.Format32bppRgb);
        using(var g=Graphics.FromImage(image))p.Draw(g,new Rectangle(0,0,w,h),s);
        return image;
    }
    static void Equal(Bitmap a,Bitmap b) {
        for(int y=0;y<a.Height;y++)for(int x=0;x<a.Width;x++)
            if(a.GetPixel(x,y)!=b.GetPixel(x,y))throw new Exception("Stale or incorrectly scaled pixel at "+x+","+y);
    }
    public static int Run(){
        const int w=256,h=160;var pixels=new int[w*h];
        for(int i=0;i<pixels.Length;i++)pixels[i]=((i*7919)&0xffffff);
        IntPtr memory=Marshal.AllocHGlobal(pixels.Length*4);
        var s=new N.PrimarySurface {Format=32,Width=w,Height=h,Stride=w*4,Data=memory};
        try {
            Marshal.Copy(pixels,0,memory,pixels.Length);
            using(var p=new GdiPresenter()) {
                using(var first=Render(p,s,173,107))using(var exposed=Render(p,s,173,107))Equal(first,exposed);
                if(p.Uploads!=1)throw new Exception("Exposure uploaded unchanged pixels.");
                for(int y=31;y<73;y++)for(int x=17;x<96;x++)pixels[y*w+x]=0x00f02a54;
                Marshal.Copy(pixels,0,memory,pixels.Length);p.Changed(new Rectangle(17,31,79,42));
                using(var reference=new GdiPresenter())using(var partial=Render(p,s,173,107))using(var full=Render(reference,s,173,107))Equal(partial,full);
                using(var reference=new GdiPresenter())using(var resized=Render(p,s,319,213))using(var full=Render(reference,s,319,213))Equal(resized,full);
                p.Dispose();using(var reopened=Render(p,s,173,107))using(var reference=new GdiPresenter())using(var full=Render(reference,s,173,107))Equal(reopened,full);
            }
            uint before=GetGuiResources(Process.GetCurrentProcess().Handle,0);
            for(int i=0;i<100;i++)using(var p=new GdiPresenter())using(var b=Render(p,s,173,107)){}
            uint after=GetGuiResources(Process.GetCurrentProcess().Handle,0);
            if(after>before+2)throw new Exception("GDI resource growth: "+(after-before));
            Console.WriteLine("PASS: exposure cache, partial-update pixels, resize, dispose/recreate, 100 GDI lifecycles.");return 0;
        } finally {Marshal.FreeHGlobal(memory);}
    }
}
