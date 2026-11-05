// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.Json;
sealed class StageProbe {
 readonly bool enabled=Array.IndexOf(Environment.GetCommandLineArgs(),"--stage-probe")>=0;
 readonly object gate=new(); long start; int id; Rectangle roi; bool damaged;
 Dictionary<string,double> times=new();
 public void Begin(int x,int y){if(!enabled)return;lock(gate){start=Stopwatch.GetTimestamp();id++;roi=new(x+10,y+20,180,110);damaged=false;times=new();}}
 void MarkCore(string name){if(start!=0&&!times.ContainsKey(name))times[name]=(Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency;}
 public void Mark(string name){lock(gate)MarkCore(name);}
 public void Damage(Rectangle area){lock(gate){if(start!=0&&roi.IntersectsWith(area)){damaged=true;MarkCore("damage");}}}
 public void Copied(){lock(gate){if(damaged)MarkCore("decoder_copy_end");}}
 public void Read(Rectangle area,long timestamp){lock(gate){if(damaged&&timestamp>=start&&roi.IntersectsWith(area))MarkCore("ui_copy_end");}}
 public void PaintBegin(){lock(gate){if(times.ContainsKey("ui_copy_end"))MarkCore("paint_begin");}}
 public void PaintEnd(){string? row=null;lock(gate){if(start==0||!times.ContainsKey("paint_begin"))return;MarkCore("paint_end");row=JsonSerializer.Serialize(new{id,roi=new[]{roi.X,roi.Y,roi.Width,roi.Height},times});start=0;}File.AppendAllText(Path.Combine(AppContext.BaseDirectory,"stages.jsonl"),row+"\n");}
}
