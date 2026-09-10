using System;
using SkiaSharp;

namespace Reportman.Drawing.CrossPlatform
{
    /// <summary>
    /// Rasterizes an encoded image (PNG, JPEG, BMP, GIF…) into the 1-bit rows an ESC/POS receipt printer
    /// draws with <c>GS v 0</c>. Registered into <see cref="PrintOutText.ImageRasterizer"/> by the host, so the
    /// text driver (which has no image decoder of its own) can print the logo of a receipt. The image is
    /// fitted inside the requested box keeping its aspect ratio, drawn over white, and dithered with
    /// Floyd–Steinberg (the same finish the raster HTML ticket uses), so logos with grey and colour come out
    /// legible instead of as a black block.
    /// </summary>
    public static class EscPosImagen
    {
        /// <summary>
        /// Fits <paramref name="image"/> inside <paramref name="widthDots"/> × <paramref name="heightDots"/> and
        /// returns it as packed 1-bit rows (MSB first, 1 = black). Null when the image cannot be decoded.
        /// </summary>
        public static PrintOutText.RasterBits Rasterize(byte[] image, int widthDots, int heightDots)
        {
            if (image == null || image.Length == 0 || widthDots < 1 || heightDots < 1)
                return null;
            using (SKBitmap src = SKBitmap.Decode(image))
            {
                if (src == null || src.Width < 1 || src.Height < 1)
                    return null;
                double k = Math.Min((double)widthDots / src.Width, (double)heightDots / src.Height);
                int w = Math.Max(1, (int)Math.Round(src.Width * k));
                int h = Math.Max(1, (int)Math.Round(src.Height * k));
                using (SKBitmap dst = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul))
                using (SKCanvas canvas = new SKCanvas(dst))
                using (SKPaint paint = new SKPaint { IsAntialias = true })
                {
                    canvas.Clear(SKColors.White);
                    canvas.DrawBitmap(src, new SKRect(0, 0, w, h), paint);
                    canvas.Flush();
                    // Luminance with the alpha already composed over white; Floyd–Steinberg error diffusion.
                    float[] lum = new float[w * h];
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                        {
                            SKColor c = dst.GetPixel(x, y);
                            lum[y * w + x] = 0.299f * c.Red + 0.587f * c.Green + 0.114f * c.Blue;
                        }
                    int stride = (w + 7) / 8;
                    byte[] bits = new byte[stride * h];
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                        {
                            int i = y * w + x;
                            float old = lum[i];
                            float nuevo = old < 128f ? 0f : 255f;
                            if (nuevo == 0f)
                                bits[y * stride + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                            float err = old - nuevo;
                            if (x + 1 < w) lum[i + 1] += err * 7f / 16f;
                            if (y + 1 < h)
                            {
                                if (x > 0) lum[i + w - 1] += err * 3f / 16f;
                                lum[i + w] += err * 5f / 16f;
                                if (x + 1 < w) lum[i + w + 1] += err * 1f / 16f;
                            }
                        }
                    return new PrintOutText.RasterBits { Width = w, Height = h, Bits = bits };
                }
            }
        }
    }
}
