// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Windows;
namespace ProxmoxSpice;
public partial class MainWindow
{
    static ClipboardPayload TestImage()
    {
        using var bitmap=new System.Drawing.Bitmap(160,100);
        using(var graphics=System.Drawing.Graphics.FromImage(bitmap)){graphics.Clear(System.Drawing.Color.CornflowerBlue);graphics.FillRectangle(System.Drawing.Brushes.OrangeRed,20,20,60,60);graphics.FillRectangle(System.Drawing.Brushes.LimeGreen,90,20,50,60);}
        using var stream=new MemoryStream();bitmap.Save(stream,System.Drawing.Imaging.ImageFormat.Png);return new ClipboardPayload(2,stream.ToArray());
    }
    void CheckRichClipboard()
    {
        var image=TestImage();image.Validate();
        using(var stream=new MemoryStream(image.Bytes))using(var bitmap=new System.Drawing.Bitmap(stream))if(bitmap.Width!=160||bitmap.GetPixel(25,25).ToArgb()!=System.Drawing.Color.OrangeRed.ToArgb())throw new Exception("PNG roundtrip failed.");
        ClipboardPayload.Text("Grüße\r\nTest").Validate();
        foreach(var invalid in new[]{new ClipboardPayload(2,new byte[24]),new ClipboardPayload(1,new byte[ClipboardPayload.TextLimit+1]),new ClipboardPayload(3,new byte[1])}){
            bool rejected=false;try{invalid.Validate();}catch(InvalidOperationException){rejected=true;}if(!rejected)throw new Exception("Invalid clipboard data accepted.");
        }
        var vm=data.Vms[0] with {ClipboardEnabled=false,ImageClipboardEnabled=true,FileTransferEnabled=true};
        var json=System.Text.Json.JsonSerializer.Serialize(vm);var read=System.Text.Json.JsonSerializer.Deserialize<SavedVm>(json)!;
        if(read.ClipboardEnabled||!read.ImageClipboardEnabled||!read.FileTransferEnabled)throw new Exception("Independent preferences lost.");
        var legacy=System.Text.Json.JsonSerializer.Deserialize<SavedVm>("{\"Id\":\""+vm.Id+"\",\"ServerId\":\""+vm.ServerId+"\",\"VmId\":101,\"Name\":\"Legacy\"}")!;
        if(legacy.ImageClipboardEnabled||legacy.FileTransferEnabled)throw new Exception("Legacy profile unexpectedly enables extra sharing.");
        var file=Path.Combine(AppContext.BaseDirectory,"transfer-fixture.txt");File.WriteAllText(file,"DALIZO synthetic file-transfer fixture");
        if(SpiceWorker.ValidateTransferFiles(new[]{file,file}).Length!=1)throw new Exception("Duplicate files not filtered.");
        foreach(var paths in new[]{Array.Empty<string>(),new[]{AppContext.BaseDirectory},new[]{file+".missing"}}){bool rejected=false;try{SpiceWorker.ValidateTransferFiles(paths);}catch(Exception ex) when(ex is IOException or ArgumentException){rejected=true;}if(!rejected)throw new Exception("Invalid transfer sources accepted.");}
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"rich-clipboard-result.txt"),"PASS: PNG pixel roundtrip, text encoding, invalid/oversized payload rejection, separate sharing preferences, legacy defaults off, file validation/deduplication. No live guest or host clipboard touched by these checks.");
    }
    async Task RunMediaDiagnostic(SavedVm original,SpiceConfig config)
    {
        Title="DALIZO Media Test";IsEnabled=true;
        var vm=original with {ClipboardEnabled=true,ImageClipboardEnabled=true,FileTransferEnabled=true,AutoResolution=false};
        data=data with {Vms=data.Vms.Select(v=>v.Id==vm.Id?vm:v).ToList()};targetVm=vm;
        ConsoleTitle.Text=vm.Name;ConsoleSubtitle.Text="SPICE media diagnostic";
        host=new SpiceHost();var source=host;source.SetClipboardSharing(true);source.SetExtraSharing(true,true);source.SetResizeEnabled(false);
        source.FilesDropped+=files=>{if(host==source)_=ShowFileTransfer(vm,source,files);};
        source.StatusChanged+=_=>UpdateConnectButtons();source.InputAvailabilityChanged+=_=>UpdateConnectButtons();
        ConsoleArea.Child=source;source.Connect(config);RememberConsole(vm);UpdateRuntimeHeader();ShowSidebar(false);UpdateLayout();
        for(int i=0;i<150&&!source.GuestAgentConnected;i++)await Task.Delay(100);
        if(!source.GuestAgentConnected)throw new InvalidOperationException("Guest agent unavailable.");
        var image=TestImage();image.WriteHost();
        var file=Path.Combine(AppContext.BaseDirectory,"DALIZO-transfer-test-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+".txt");File.WriteAllText(file,"DALIZO SPICE file transfer test. Synthetic test data only.");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"media-test-ready.txt"),"Connected; test PNG placed on host clipboard; synthetic file: "+Path.GetFileName(file));
        Status("Media test ready: paste the test image into the empty Paint canvas. File sending is enabled only in this diagnostic session.");
        if(Environment.GetCommandLineArgs().Contains("--media-file-test"))await ShowFileTransfer(vm,source,new[]{file});
        var done=new TaskCompletionSource();Closed+=(_,_)=>done.TrySetResult();await done.Task;
    }
}
