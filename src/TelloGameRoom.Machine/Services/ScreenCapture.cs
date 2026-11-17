using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using TelloGameRoom.Machine.Interop;

namespace TelloGameRoom.Machine.Services;

/// <summary>Captures the primary screen as a JPEG for the owner's remote screenshot request.</summary>
public static class ScreenCapture
{
    private const int MaxWidth = 1600;

    public static byte[] CaptureJpeg()
    {
        var width = Native.GetSystemMetrics(Native.SM_CXSCREEN);
        var height = Native.GetSystemMetrics(Native.SM_CYSCREEN);
        using var full = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(full))
        {
            g.CopyFromScreen(0, 0, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        }

        // Downscale: enough to see what is on screen, small enough to send quickly.
        var scale = Math.Min(1d, MaxWidth / (double)width);
        using var scaled = new Bitmap((int)(width * scale), (int)(height * scale), PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(full, 0, 0, scaled.Width, scaled.Height);
        }

        using var stream = new MemoryStream();
        var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, 70L);
        scaled.Save(stream, encoder, parameters);
        return stream.ToArray();
    }
}
