using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using HUDMind.Core.Engine;
using HUDMind.Core.Models;
using HUDMind.Core.Pipeline;

namespace HUDMind.Windows;

/// <summary>
/// Zero-dependency native Windows capture + unsafe LockBits 64-bit dHash + fast JPEG encoder (&lt;8ms total).
/// </summary>
public sealed class FastScreenCapture : IScreenCaptureService
{
    private static readonly ImageCodecInfo? JpegCodec =
        ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public CapturedFrameData? CaptureCompressedFrame(
        int monitorIndex,
        (int Left, int Top, int Width, int Height)? regionRect,
        int targetWidth,
        int targetHeight,
        int jpegQuality,
        long frameId)
    {
        try
        {
            var bounds = ResolveBounds(monitorIndex, regionRect);
            if (bounds.Width < 64 || bounds.Height < 64)
                return null;

            var swCap = Stopwatch.StartNew();
            using var fullBmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(fullBmp))
            {
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            }
            swCap.Stop();

            var swEnc = Stopwatch.StartNew();
            var (outW, outH) = FitAspect(bounds.Width, bounds.Height, targetWidth, targetHeight);
            using var resized = new Bitmap(outW, outH, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.CompositingQuality = CompositingQuality.HighSpeed;
                g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                g.DrawImage(fullBmp, 0, 0, outW, outH);
            }

            ulong hash = ComputeDHashFromBitmap(resized);
            byte[] jpegBytes = EncodeJpeg(resized, Math.Clamp(jpegQuality, 35, 92));
            swEnc.Stop();

            return new CapturedFrameData(
                jpegBytes,
                hash,
                frameId,
                swCap.Elapsed.TotalMilliseconds,
                swEnc.Elapsed.TotalMilliseconds,
                DateTimeOffset.UtcNow
            );
        }
        catch
        {
            return null;
        }
    }

    private static Rectangle ResolveBounds(int monitorIndex, (int Left, int Top, int Width, int Height)? regionRect)
    {
        var screens = Screen.AllScreens;
        int idx = Math.Clamp(monitorIndex - 1, 0, Math.Max(0, screens.Length - 1));
        var fallback = screens.Length > 0 ? screens[idx].Bounds : new Rectangle(0, 0, 1920, 1080);

        if (!regionRect.HasValue)
            return fallback;

        var virt = SystemInformation.VirtualScreen;
        var (l, t, w, h) = regionRect.Value;
        var candidate = Rectangle.Intersect(virt, new Rectangle(l, t, w, h));
        return (candidate.Width >= 320 && candidate.Height >= 240) ? candidate : fallback;
    }

    private static (int W, int H) FitAspect(int srcW, int srcH, int maxW, int maxH)
    {
        if (srcW <= maxW && srcH <= maxH)
            return (srcW, srcH);
        double scale = Math.Min((double)maxW / srcW, (double)maxH / srcH);
        return (Math.Max(16, (int)(srcW * scale)), Math.Max(16, (int)(srcH * scale)));
    }

    private static unsafe ulong ComputeDHashFromBitmap(Bitmap bmp)
    {
        using var tiny = new Bitmap(PerceptualHasher.GridWidth, PerceptualHasher.GridHeight, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(tiny))
        {
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.DrawImage(bmp, 0, 0, PerceptualHasher.GridWidth, PerceptualHasher.GridHeight);
        }

        Span<byte> gray = stackalloc byte[PerceptualHasher.GridWidth * PerceptualHasher.GridHeight];
        var rect = new Rectangle(0, 0, tiny.Width, tiny.Height);
        var data = tiny.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            byte* scan0 = (byte*)data.Scan0;
            int stride = data.Stride;
            int idx = 0;
            for (int y = 0; y < tiny.Height; y++)
            {
                byte* row = scan0 + (y * stride);
                for (int x = 0; x < tiny.Width; x++)
                {
                    int b = row[x * 3];
                    int gr = row[x * 3 + 1];
                    int r = row[x * 3 + 2];
                    gray[idx++] = (byte)((r * 77 + gr * 150 + b * 29) >> 8);
                }
            }
        }
        finally
        {
            tiny.UnlockBits(data);
        }

        return PerceptualHasher.ComputeDHash64(gray);
    }

    private static byte[] EncodeJpeg(Bitmap bmp, int quality)
    {
        using var ms = new MemoryStream(8192);
        if (JpegCodec is not null)
        {
            using var encParams = new EncoderParameters(1);
            encParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
            bmp.Save(ms, JpegCodec, encParams);
        }
        else
        {
            bmp.Save(ms, ImageFormat.Jpeg);
        }
        return ms.ToArray();
    }

    public void Dispose() { }
}
