using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SLCopilot.Core;

namespace SLCopilot;

/// <summary>
/// 从剪贴板、文件读取图片，缩放到最长边 <see cref="ImageContent.MaxEdgePixels"/> 像素并编码为 PNG（过大时改用 JPEG，再不够则继续缩小）。
/// 只能在 UI 线程调用（剪贴板）。读取或解码失败抛出 <see cref="InvalidOperationException"/>，消息可直接显示。
/// </summary>
internal static class ImageCapture
{
    /// <summary>可作为图片附加的文件扩展名（WPF 自带解码器支持的格式）。</summary>
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff"];
    private const long MaxSourceFileBytes = 50L * 1024 * 1024;

    public static bool IsImageFile(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 剪贴板中是否有可附加的图片（位图、PNG 数据或图片文件）。同时有文字时返回 false：从 Excel、网页等复制的内容常同时带位图，
    /// 此时应按文字粘贴。
    /// </summary>
    public static bool ClipboardHasImage()
    {
        try
        {
            if (Clipboard.ContainsFileDropList()) return Clipboard.GetFileDropList().Cast<string>().Any(IsImageFile);
            return !Clipboard.ContainsText() && (Clipboard.ContainsImage() || Clipboard.ContainsData("PNG"));
        }
        catch (System.Runtime.InteropServices.COMException) { return false; } // 剪贴板被其他程序占用。
    }

    /// <summary>
    /// 读取剪贴板中的图片：优先 PNG 数据（截图工具通常提供，保留透明度与颜色），其次复制的图片文件，最后是位图（去掉 Alpha 通道，
    /// 避免部分截图的 Alpha 全为 0 时显示为透明）。没有图片时返回空列表。
    /// </summary>
    public static IReadOnlyList<(BitmapSource Image, string Name)> FromClipboard()
    {
        try
        {
            if (Clipboard.GetData("PNG") is MemoryStream png)
                return [(Decode(png), "截图.png")];
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().Where(IsImageFile).ToList();
                if (files.Count > 0) return files.Select(f => (FromFile(f), Path.GetFileName(f))).ToList();
            }
            if (Clipboard.GetImage() is { } bitmap)
            {
                var opaque = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0);
                opaque.Freeze();
                return [(opaque, "截图.png")];
            }
            return [];
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or NotSupportedException or ArgumentException or IOException or FileFormatException)
        {
            throw new InvalidOperationException("无法读取剪贴板中的图片：" + ex.Message);
        }
    }

    /// <summary>读取图片文件（动图取第一帧）。</summary>
    public static BitmapSource FromFile(string path)
    {
        try
        {
            if (new FileInfo(path).Length > MaxSourceFileBytes) throw new InvalidOperationException($"{Path.GetFileName(path)} 超过 50 MB。");
            using var stream = File.OpenRead(path);
            return Decode(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FileFormatException)
        {
            throw new InvalidOperationException($"无法读取图片 {Path.GetFileName(path)}：{ex.Message}");
        }
    }

    /// <summary>
    /// 缩放并编码：最长边不超过 1568 像素；PNG 超过上限时改用 JPEG（质量 85，透明处填白色），仍超过则每次缩小到 75% 重试。
    /// </summary>
    public static ImageAttachment Encode(BitmapSource source, string name)
    {
        var scale = Math.Min(1.0, (double)ImageContent.MaxEdgePixels / Math.Max(source.PixelWidth, source.PixelHeight));
        for (var attempt = 0; attempt < 8; attempt++, scale *= 0.75)
        {
            var image = Scale(source, scale);
            var png = Save(new PngBitmapEncoder(), image);
            if (png.Length <= ImageContent.MaxBytes) return new ImageAttachment("image/png", png, image.PixelWidth, image.PixelHeight, Path.ChangeExtension(name, ".png"));
            var jpeg = Save(new JpegBitmapEncoder { QualityLevel = 85 }, Flatten(image));
            if (jpeg.Length <= ImageContent.MaxBytes) return new ImageAttachment("image/jpeg", jpeg, image.PixelWidth, image.PixelHeight, Path.ChangeExtension(name, ".jpg"));
        }
        throw new InvalidOperationException("图片压缩后仍然过大，请裁剪后再附加。");
    }

    /// <summary>界面用的小缩略图（高 72 像素）。</summary>
    public static ImageSource Thumbnail(ImageAttachment image)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelHeight = Math.Min(72, Math.Max(1, image.Height));
        bitmap.StreamSource = new MemoryStream(image.Data);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource Decode(Stream stream)
    {
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) throw new NotSupportedException("没有可用的图像帧。");
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    private static BitmapSource Scale(BitmapSource source, double scale)
    {
        if (scale >= 0.999) return source;
        var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    /// <summary>JPEG 不支持透明：先画在白色背景上。</summary>
    private static BitmapSource Flatten(BitmapSource image)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, image.PixelWidth, image.PixelHeight);
            context.DrawRectangle(Brushes.White, null, rect);
            context.DrawImage(image, rect);
        }
        var target = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static byte[] Save(BitmapEncoder encoder, BitmapSource image)
    {
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
