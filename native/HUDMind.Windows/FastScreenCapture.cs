using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using HUDMind.Core.Engine;
using HUDMind.Core.Models;
using HUDMind.Core.Pipeline;
using Microsoft.Win32;

namespace HUDMind.Windows;

/// <summary>
/// Ultra-fast zero-churn Windows capture using Win32 StretchBlt (HALFTONE + SetBrushOrgEx)
/// directly into a reusable target-sized Bitmap, 3x3 cell-averaged 64-bit dHash, solid/black
/// frame rejection, and zero heap waste on unchanged frames.
/// </summary>
public sealed class FastScreenCapture : IScreenCaptureService
{
    private const int Srccopy = 0x00CC0020;
    private const int Halftone = 4;

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr lppt);

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(
        IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
        IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, int rop);

    private static readonly ImageCodecInfo? JpegCodec =
        ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

    private readonly object _sync = new();
    private Bitmap? _reusableBmp;
    private int _bmpW;
    private int _bmpH;
    private readonly MemoryStream _jpegStream = new(16384);
    private EncoderParameters? _cachedEncParams;
    private int _cachedQuality = -1;
    private Rectangle[] _cachedScreens = [];
    private Rectangle _cachedVirtualScreen;
    private volatile bool _screensDirty = true;
    private int _lastMeanLuma = -1;
    private bool _disposed;

    public FastScreenCapture()
    {
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => _screensDirty = true;

    public (CapturedFrameData? Frame, bool SkippedByHash) CaptureIfChanged(
        int monitorIndex,
        (int Left, int Top, int Width, int Height)? regionRect,
        int targetWidth,
        int targetHeight,
        int jpegQuality,
        long frameId,
        ulong? lastAcceptedHash,
        int phashThreshold,
        bool isForced)
    {
        lock (_sync)
        {
            if (_disposed)
                return (null, false);

            try
            {
                var bounds = ResolveBounds(monitorIndex, regionRect);
                if (bounds.Width < 64 || bounds.Height < 64)
                    return (null, false);

                var (outW, outH) = FitAspect(bounds.Width, bounds.Height, targetWidth, targetHeight);
                EnsureReusableBitmap(outW, outH);

                long tCap = Stopwatch.GetTimestamp();
                var screenDc = GetDC(IntPtr.Zero);
                if (screenDc == IntPtr.Zero)
                    return (null, false);
                try
                {
                    using var g = Graphics.FromImage(_reusableBmp!);
                    var destDc = g.GetHdc();
                    try
                    {
                        SetStretchBltMode(destDc, Halftone);
                        SetBrushOrgEx(destDc, 0, 0, IntPtr.Zero);
                        if (!StretchBlt(
                                destDc, 0, 0, outW, outH,
                                screenDc, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                                Srccopy))
                        {
                            return (null, false);
                        }
                    }
                    finally
                    {
                        g.ReleaseHdc(destDc);
                    }
                }
                finally
                {
                    ReleaseDC(IntPtr.Zero, screenDc);
                }
                double capMs = Stopwatch.GetElapsedTime(tCap).TotalMilliseconds;

                long tEnc = Stopwatch.GetTimestamp();
                var (hash, lumaRange, meanLuma) = ComputeCellAveragedDHash(_reusableBmp!);

                // Reject uniform pitch-black / solid loading screens / UAC blank frames automatically
                if (!isForced && lumaRange < 8)
                    return (null, true);

                // Early-exit BEFORE JPEG encoding unless scene or global brightness shifted notably
                bool sameLumaBand = _lastMeanLuma < 0 || Math.Abs(meanLuma - _lastMeanLuma) < 18;
                if (!isForced && lastAcceptedHash.HasValue && sameLumaBand &&
                    PerceptualHasher.HammingDistance(hash, lastAcceptedHash.Value) < phashThreshold)
                {
                    return (null, true);
                }

                _lastMeanLuma = meanLuma;
                byte[] jpegBytes = EncodeJpeg(_reusableBmp!, Math.Clamp(jpegQuality, 35, 92));
                double encMs = Stopwatch.GetElapsedTime(tEnc).TotalMilliseconds;

                return (new CapturedFrameData(
                    jpegBytes,
                    hash,
                    frameId,
                    capMs,
                    encMs,
                    DateTimeOffset.UtcNow,
                    IsForced: isForced
                ), false);
            }
            catch
            {
                return (null, false);
            }
        }
    }

    private void EnsureReusableBitmap(int w, int h)
    {
        if (_reusableBmp is not null && _bmpW == w && _bmpH == h)
            return;
        _reusableBmp?.Dispose();
        _reusableBmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        _bmpW = w;
        _bmpH = h;
    }

    private Rectangle ResolveBounds(int monitorIndex, (int Left, int Top, int Width, int Height)? regionRect)
    {
        if (_screensDirty || _cachedScreens.Length == 0)
        {
            _cachedScreens = Screen.AllScreens.Select(s => s.Bounds).ToArray();
            _cachedVirtualScreen = SystemInformation.VirtualScreen;
            _screensDirty = false;
        }

        int idx = Math.Clamp(monitorIndex - 1, 0, Math.Max(0, _cachedScreens.Length - 1));
        var fallback = _cachedScreens.Length > 0 ? _cachedScreens[idx] : new Rectangle(0, 0, 1920, 1080);

        if (!regionRect.HasValue)
            return fallback;

        var (l, t, w, h) = regionRect.Value;
        var candidate = Rectangle.Intersect(_cachedVirtualScreen, new Rectangle(l, t, w, h));
        return (candidate.Width >= 320 && candidate.Height >= 240) ? candidate : fallback;
    }

    private static (int W, int H) FitAspect(int srcW, int srcH, int maxW, int maxH)
    {
        if (srcW <= maxW && srcH <= maxH)
            return (srcW, srcH);
        double scale = Math.Min((double)maxW / srcW, (double)maxH / srcH);
        return (Math.Max(16, (int)(srcW * scale)), Math.Max(16, (int)(srcH * scale)));
    }

    private static unsafe (ulong Hash, int LumaRange, int MeanLuma) ComputeCellAveragedDHash(Bitmap bmp)
    {
        int w = bmp.Width;
        int h = bmp.Height;
        Span<byte> gray = stackalloc byte[PerceptualHasher.GridWidth * PerceptualHasher.GridHeight];

        int minLuma = 255;
        int maxLuma = 0;
        int sumLuma = 0;

        var rect = new Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            if (data.Scan0 == IntPtr.Zero)
                return (0UL, 0, 0);

            byte* scan0 = (byte*)data.Scan0;
            int stride = data.Stride;
            int stepX = Math.Max(2, w / 36);
            int stepY = Math.Max(2, h / 32);

            // 3x3 sub-sampled cell averaging (~648 pixels) avoids single-pixel noise & tracks corner HUD changes
            int gIdx = 0;
            for (int gy = 0; gy < PerceptualHasher.GridHeight; gy++)
            {
                int cy = Math.Clamp((gy * h + h / 2) / PerceptualHasher.GridHeight, 0, h - 1);
                for (int gx = 0; gx < PerceptualHasher.GridWidth; gx++)
                {
                    int cx = Math.Clamp((gx * w + w / 2) / PerceptualHasher.GridWidth, 0, w - 1);
                    int acc = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int py = Math.Clamp(cy + dy * stepY, 0, h - 1);
                        byte* row = scan0 + (py * stride);
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int px = Math.Clamp(cx + dx * stepX, 0, w - 1);
                            int off = px * 3;
                            int luma = (row[off + 2] * 77 + row[off + 1] * 150 + row[off] * 29) >> 8;
                            acc += luma;
                            if (luma < minLuma) minLuma = luma;
                            if (luma > maxLuma) maxLuma = luma;
                        }
                    }

                    byte avg = (byte)(acc / 9);
                    gray[gIdx++] = avg;
                    sumLuma += avg;
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        ulong hash = PerceptualHasher.ComputeDHash64(gray);
        return (hash, maxLuma - minLuma, sumLuma / gray.Length);
    }

    private byte[] EncodeJpeg(Bitmap bmp, int quality)
    {
        _jpegStream.SetLength(0);
        if (JpegCodec is not null)
        {
            if (_cachedEncParams is null || _cachedQuality != quality)
            {
                _cachedEncParams?.Dispose();
                _cachedEncParams = new EncoderParameters(1);
                _cachedEncParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
                _cachedQuality = quality;
            }
            bmp.Save(_jpegStream, JpegCodec, _cachedEncParams);
        }
        else
        {
            bmp.Save(_jpegStream, ImageFormat.Jpeg);
        }
        return _jpegStream.ToArray();
    }

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        lock (_sync)
        {
            _disposed = true;
            _reusableBmp?.Dispose();
            _reusableBmp = null;
            _cachedEncParams?.Dispose();
            _cachedEncParams = null;
            _jpegStream.Dispose();
        }
    }
}
