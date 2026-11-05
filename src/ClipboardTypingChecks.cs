// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
namespace ProxmoxSpice;
public partial class MainWindow
{
    void CheckClipboardKeystrokes()
    {
        var us=ClipboardKeystrokes.Build("Az@!",false,false);var de=ClipboardKeystrokes.Build("Yz@ä€",true,false);
        if(us.Count!=4||us[0][0].Scan!=0x2a||us[0][1].Scan!=0x1e||de[0][1].Scan!=0x2c||de[1][0].Scan!=0x15||de[2][0].Scan!=0x138||de[2][1].Scan!=0x10||de[3][0].Scan!=0x28)throw new Exception("Keyboard layout mapping failed.");
        foreach(var chord in us.Concat(de)){
            var events=new List<(uint,bool)>();var keys=chord.Select(k=>k.Scan).ToArray();
            SpiceWorker.SendChordCore(keys,(key,down)=>events.Add((key,down)));
            if(!events.SequenceEqual(keys.Select(k=>(k,true)).Concat(keys.Reverse().Select(k=>(k,false)))))throw new Exception("Typing modifier release failed.");
        }
        foreach(var value in new[]{"bad\ninput","bad\tinput","emoji🙂"}){bool rejected=false;try{ClipboardKeystrokes.Build(value,false,false);}catch(InvalidOperationException){rejected=true;}if(!rejected)throw new Exception("Typing preflight allowed unsupported input.");}
        var lines=ClipboardKeystrokes.Build("a\r\nb\t",false,true);if(lines.Count!=4||lines[1][0].Scan!=0x1c||lines[^1][0].Scan!=0xf)throw new Exception("Enter/Tab normalization failed.");
        if(ClipboardKeystrokes.Build("^",true,false).Count!=2)throw new Exception("Dead-key neutralization failed.");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"typing-result.txt"),"PASS: DE/US scan mapping including Y/Z, shift, AltGr, umlaut, euro; SPICE chord press/reverse release; unsupported input preflight; optional CRLF/Tab normalization; dead-key space; no appended Enter. Synthetic input only; no guest login tested.");
    }
}
