using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using TextDataFormat = System.Windows.TextDataFormat;

namespace StreamHub.Host.Runtime;

/// <summary>Runs on the logged-in desktop's STA thread, never in the service session.</summary>
public static class DesktopClipboard
{
    public static ClipboardPayload Read()
    {
        var png = System.Windows.Clipboard.GetData("PNG");
        if (png is MemoryStream pngStream)
            return ClipboardPolicy.Encode("image/png", pngStream.ToArray());
        if (System.Windows.Clipboard.ContainsImage())
        {
            var image = System.Windows.Clipboard.GetImage() ?? throw new InvalidOperationException("电脑剪贴板图片不可用。");
            if ((long)image.PixelWidth * image.PixelHeight > 40000000) throw new InvalidOperationException("剪贴板图片尺寸过大。");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var output = new MemoryStream(); encoder.Save(output);
            return ClipboardPolicy.Encode("image/png", output.ToArray());
        }
        if (System.Windows.Clipboard.ContainsText(TextDataFormat.Html))
        {
            var html = ExtractHtml(System.Windows.Clipboard.GetText(TextDataFormat.Html));
            return ClipboardPolicy.Encode("text/html", Encoding.UTF8.GetBytes(html));
        }
        if (System.Windows.Clipboard.ContainsText(TextDataFormat.UnicodeText))
            return ClipboardPolicy.Encode("text/plain", Encoding.UTF8.GetBytes(System.Windows.Clipboard.GetText(TextDataFormat.UnicodeText)));
        throw new InvalidOperationException("电脑剪贴板没有可用的文字、HTML 或 PNG 图片。");
    }

    public static void Write(ClipboardPayload payload)
    {
        byte[] bytes = ClipboardPolicy.Decode(payload);
        if (payload.Format == "text/plain")
        {
            System.Windows.Clipboard.SetText(Encoding.UTF8.GetString(bytes), TextDataFormat.UnicodeText);
            return;
        }
        var data = new DataObject();
        if (payload.Format == "text/html") data.SetData(DataFormats.Html, CreateWindowsHtml(Encoding.UTF8.GetString(bytes)));
        else
        {
            using var input = new MemoryStream(bytes);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = input; bitmap.EndInit(); bitmap.Freeze();
            data.SetData("PNG", new MemoryStream(bytes));
            data.SetImage(bitmap);
        }
        System.Windows.Clipboard.SetDataObject(data, true);
    }

    public static string CreateWindowsHtml(string fragment)
    {
        const string header = "Version:1.0\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string prefix = "<html><body><!--StartFragment-->";
        const string suffix = "<!--EndFragment--></body></html>";
        int startHtml = Encoding.ASCII.GetByteCount(string.Format(System.Globalization.CultureInfo.InvariantCulture, header, 0, 0, 0, 0));
        int startFragment = startHtml + Encoding.UTF8.GetByteCount(prefix);
        int endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        int endHtml = endFragment + Encoding.UTF8.GetByteCount(suffix);
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, header, startHtml, endHtml, startFragment, endFragment) + prefix + fragment + suffix;
    }

    public static string ExtractHtml(string html)
    {
        const string startMarker = "<!--StartFragment-->", endMarker = "<!--EndFragment-->";
        int start = html.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
        int end = html.IndexOf(endMarker, StringComparison.OrdinalIgnoreCase);
        if (start >= 0 && end >= start + startMarker.Length)
            return html.Substring(start + startMarker.Length, end - start - startMarker.Length);
        return html;
    }
}
