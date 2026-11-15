using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Text.Json;
using StreamHub.Host.Runtime;

// Draw the production brand painter into bitmaps. Never create an overlay/window.
string root=Path.GetFullPath(args.Single());Directory.CreateDirectory(root);
var type=typeof(ControlDeviceRegistry).Assembly.GetType("StreamHub.Service.PrivacyScreenBrand",true)!;
var rect=type.GetNestedType("NativeRect",BindingFlags.NonPublic)!;
var draw=type.GetMethod("Draw",BindingFlags.NonPublic|BindingFlags.Static)!;
var results=new List<object>();
foreach(var size in new[]{new Size(1280,720),new Size(720,1280),new Size(480,320)})
{
    using var bitmap=new Bitmap(size.Width,size.Height,PixelFormat.Format32bppArgb);
    using(var graphics=Graphics.FromImage(bitmap))
    {
        graphics.Clear(Color.Black);IntPtr dc=graphics.GetHdc();
        try
        {
            var bounds=Activator.CreateInstance(rect)!;
            rect.GetField("Right")!.SetValue(bounds,size.Width);rect.GetField("Bottom")!.SetValue(bounds,size.Height);
            draw.Invoke(null,[dc,bounds]);
        }
        finally{graphics.ReleaseHdc(dc);}
    }
    int painted=0;for(int y=0;y<size.Height;y++)for(int x=0;x<size.Width;x++)
    {var p=bitmap.GetPixel(x,y);if(p.R+p.G+p.B>50)painted++;}
    if(painted<1000)throw new Exception("Brand preview is empty.");
    string file=$"privacy-{size.Width}x{size.Height}.png";bitmap.Save(Path.Combine(root,file),ImageFormat.Png);
    results.Add(new{file,size.Width,size.Height,paintedPixels=painted});
}
File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new{passed=true,results,overlayShown=false,inputSent=false},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine("PASS landscape, portrait and small-display production brand rendering; no overlay shown.");
