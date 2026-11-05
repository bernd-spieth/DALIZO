// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Drawing;
using System.Runtime.InteropServices;

// Owned, viewport-sized image. Never retains a pointer to decoder storage.
// Access is confined to the WinForms/GLib UI thread.
sealed class GdiPresenter : IDisposable
{
    IntPtr dc, bitmap, previous;
    int width, height;
    bool stale = true;
    Rectangle damage;
    public long Uploads;
    [StructLayout(LayoutKind.Sequential)] struct Info {
        public uint Size; public int Width, Height; public ushort Planes, Bits;
        public uint Compression, ImageSize; public int Xppm, Yppm; public uint Used, Important;
    }
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc,int w,int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dc,int x,int y,int w,int h,IntPtr src,int sx,int sy,uint op);
    [DllImport("gdi32.dll")] static extern int StretchDIBits(IntPtr dc,int x,int y,int w,int h,int sx,int sy,int sw,int sh,IntPtr bits,ref Info info,uint use,uint op);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc,int mode);
    [DllImport("gdi32.dll")] static extern int SaveDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool RestoreDC(IntPtr dc,int level);
    [DllImport("gdi32.dll")] static extern int IntersectClipRect(IntPtr dc,int l,int t,int r,int b);

    public void Changed(Rectangle area) { damage=damage.IsEmpty?area:Rectangle.Union(damage,area); }
    public void Reset() { stale=true;damage=Rectangle.Empty; }
    public void Draw(Graphics graphics,Rectangle destination,N.PrimarySurface surface) {
        if(destination.Width<1||destination.Height<1)return;
        IntPtr target=graphics.GetHdc();
        try {
            if(dc==IntPtr.Zero||width!=destination.Width||height!=destination.Height) {
                Dispose();width=destination.Width;height=destination.Height;
                dc=CreateCompatibleDC(target);bitmap=CreateCompatibleBitmap(target,width,height);
                if(dc==IntPtr.Zero||bitmap==IntPtr.Zero){Dispose();throw new InvalidOperationException("Windows could not allocate the console image.");}
                previous=SelectObject(dc,bitmap);SetStretchBltMode(dc,3);stale=true;
            }
            if(stale||!damage.IsEmpty) {
                var info=new Info {Size=40,Width=surface.Stride/4,Height=-surface.Height,Planes=1,Bits=32};
                int saved=SaveDC(dc);
                try {
                    if(!stale) {
                        int l=(int)Math.Floor((double)damage.Left*width/surface.Width)-1;
                        int t=(int)Math.Floor((double)damage.Top*height/surface.Height)-1;
                        int r=(int)Math.Ceiling((double)damage.Right*width/surface.Width)+1;
                        int b=(int)Math.Ceiling((double)damage.Bottom*height/surface.Height)+1;
                        IntersectClipRect(dc,l,t,r,b);
                    }
                    int result=StretchDIBits(dc,0,0,width,height,0,0,surface.Width,surface.Height,surface.Data,ref info,0,0x00cc0020);
                    if(result==-1)throw new InvalidOperationException("Windows could not update the console image.");
                    Uploads++;stale=false;damage=Rectangle.Empty;
                } finally {RestoreDC(dc,saved);}
            }
            if(!BitBlt(target,destination.X,destination.Y,width,height,dc,0,0,0x00cc0020))
                throw new InvalidOperationException("Windows could not present the console image.");
        } finally {graphics.ReleaseHdc(target);}
    }
    public void Dispose() {
        if(dc!=IntPtr.Zero && previous!=IntPtr.Zero)SelectObject(dc,previous);
        if(bitmap!=IntPtr.Zero)DeleteObject(bitmap);
        if(dc!=IntPtr.Zero)DeleteDC(dc);
        dc=bitmap=previous=IntPtr.Zero;width=height=0;Reset();
    }
}
