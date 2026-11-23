using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using HUDMind.Core.Engine;

namespace HUDMind.Windows;

/// <summary>
/// Pure CPU GDI+ (0% GPU, zero NuGet) temporal storyboard composer that packs chronological frames
/// into a sharp timestamp-labeled motion strip (2x1) or 4-frame sequence (2x2) so ANY Vision model
/// perceives velocity, intent, and rapid mechanics like flip resets across time in one image.
/// </summary>
public sealed class Win32TemporalStoryboardComposer : ITemporalStoryboardComposer
{
    private static readonly ImageCodecInfo? JpegCodec =
        ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public byte[]? ComposeSequenceJpeg(
        IReadOnlyList<TemporalFrameSample> frames,
        int targetWidth,
        int targetHeight,
        int jpegQuality)
    {
        if (frames is null || frames.Count < 2)
            return null;

        int take = Math.Min(6, frames.Count);
        var slice = frames.Skip(frames.Count - take).ToList();
        var latestUtc = slice[^1].CapturedAtUtc;

        int cols = slice.Count >= 5 ? 3 : 2;
        int rows = slice.Count <= 2 ? 1 : 2;
        int canvasW = Math.Clamp(targetWidth, 960, 1680);
        int canvasH = rows == 1
            ? Math.Clamp(targetHeight, 360, 540)
            : Math.Clamp(targetHeight, 640, 960);

        const int dividerPx = 4;
        int tileW = (canvasW - (cols - 1) * dividerPx) / cols;
        int tileH = rows == 1 ? canvasH : (canvasH - dividerPx) / rows;

        var streams = new List<MemoryStream>(slice.Count);
        var decoded = new List<(Image Img, DateTimeOffset Ts)>(slice.Count);
        try
        {
            foreach (var sample in slice)
            {
                if (sample.JpegBytes is not { Length: > 16 })
                    return null;
                var ms = new MemoryStream(sample.JpegBytes, writable: false);
                streams.Add(ms);
                try
                {
                    var img = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: false);
                    decoded.Add((img, sample.CapturedAtUtc));
                }
                catch
                {
                    return null;
                }
            }

            using var canvas = new Bitmap(canvasW, canvasH, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(canvas);
            g.Clear(Color.FromArgb(7, 12, 19));
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            using var dividerBrush = new SolidBrush(Color.FromArgb(34, 211, 238));
            using var badgeBg = new SolidBrush(Color.FromArgb(215, 7, 15, 24));
            using var badgeBorder = new Pen(Color.FromArgb(34, 211, 238), 1f);
            using var badgeFont = new Font("Segoe UI", Math.Max(10f, tileH / 34f), FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.FromArgb(240, 253, 255));
            using var nowTextBrush = new SolidBrush(Color.FromArgb(110, 231, 183));

            for (int i = 0; i < decoded.Count; i++)
            {
                int col = i % cols;
                int row = i / cols;
                int x = col * (tileW + dividerPx);
                int y = row * (tileH + dividerPx);

                g.DrawImage(decoded[i].Img, x, y, tileW, tileH);

                bool isLast = i == decoded.Count - 1;
                double secAgo = Math.Max(0.1, (latestUtc - decoded[i].Ts).TotalSeconds);
                string label = isLast
                    ? $"{i + 1} · AHORA"
                    : string.Create(CultureInfo.InvariantCulture, $"{i + 1} · -{secAgo:0.0}s");

                var size = g.MeasureString(label, badgeFont);
                int pillW = (int)Math.Ceiling(size.Width) + 12;
                int pillH = (int)Math.Ceiling(size.Height) + 6;
                int bx = x + 6;
                int by = y + 6;
                g.FillRectangle(badgeBg, bx, by, pillW, pillH);
                g.DrawRectangle(badgeBorder, bx, by, pillW, pillH);
                g.DrawString(label, badgeFont, isLast ? nowTextBrush : textBrush, bx + 6, by + 3);
            }

            // Crisp dividers so Vision models see distinct tile boundaries unmistakably
            for (int c = 1; c < cols; c++)
                g.FillRectangle(dividerBrush, c * tileW + (c - 1) * dividerPx, 0, 2, canvasH);
            if (rows == 2)
                g.FillRectangle(dividerBrush, 0, tileH, canvasW, 2);

            using var outMs = new MemoryStream(96 * 1024);
            if (JpegCodec is not null)
            {
                using var encParams = new EncoderParameters(1);
                encParams.Param[0] = new EncoderParameter(Encoder.Quality, Math.Clamp((long)jpegQuality, 60L, 92L));
                canvas.Save(outMs, JpegCodec, encParams);
            }
            else
            {
                canvas.Save(outMs, ImageFormat.Jpeg);
            }
            return outMs.ToArray();
        }
        catch
        {
            return null;
        }
        finally
        {
            foreach (var item in decoded)
                item.Img.Dispose();
            foreach (var ms in streams)
                ms.Dispose();
        }
    }
}
