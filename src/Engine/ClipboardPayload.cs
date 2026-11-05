// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Windows.Forms;

internal sealed record ClipboardPayload(uint Type, byte[] Bytes)
{
    public const int TextLimit=4*1024*1024, ImageLimit=16*1024*1024;
    public static ClipboardPayload Text(string value)=>new(1,Encoding.UTF8.GetBytes(value.Replace("\r\n","\n")));
    public void Validate()
    {
        if(Type is not (1 or 2)||Bytes.Length>(Type==1?TextLimit:ImageLimit))throw new InvalidOperationException("Clipboard exceeds the limit (4 MiB text / 16 MiB PNG), or has an unsupported format.");
        if(Type==1)_=new UTF8Encoding(false,true).GetString(Bytes);
        else {
            if(Bytes.Length<24||!Bytes.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}))throw new InvalidOperationException("Clipboard image is not PNG.");
            uint width=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(Bytes.AsSpan(16,4)),height=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(Bytes.AsSpan(20,4));
            if(width==0||height==0||width>16384||height>16384||(ulong)width*height>32_000_000)throw new InvalidOperationException("Clipboard image dimensions exceed the limit.");
        }
    }
    public static ClipboardPayload? ReadHost(bool text,bool images)
    {
        if(images&&Clipboard.ContainsImage()){
            using var image=Clipboard.GetImage();if(image==null)return null;
            if((long)image.Width*image.Height>32_000_000)throw new InvalidOperationException("Clipboard image is too large.");
            using var stream=new MemoryStream();image.Save(stream,ImageFormat.Png);
            var payload=new ClipboardPayload(2,stream.ToArray());payload.Validate();return payload;
        }
        if(text&&Clipboard.ContainsText(TextDataFormat.UnicodeText)){var payload=Text(Clipboard.GetText(TextDataFormat.UnicodeText));payload.Validate();return payload;}
        return null;
    }
    public void WriteHost()
    {
        Validate();
        if(Type==1){var value=new UTF8Encoding(false,true).GetString(Bytes).TrimEnd('\0').Replace("\r\n","\n").Replace("\n","\r\n");if(value.Length==0)Clipboard.Clear();else Clipboard.SetText(value,TextDataFormat.UnicodeText);return;}
        using var stream=new MemoryStream(Bytes);using var image=Image.FromStream(stream,true,true);using var bitmap=new Bitmap(image);
        var data=new DataObject();data.SetData(DataFormats.Bitmap,true,bitmap);data.SetData("PNG",false,stream);
        Clipboard.SetDataObject(data,true);
    }
}
