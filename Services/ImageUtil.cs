using System;
using System.IO;
using System.Windows.Media.Imaging;
using SD = System.Drawing;
using SDI = System.Drawing.Imaging;
using SDD = System.Drawing.Drawing2D;

namespace TodoEdge
{
    internal static class ImageUtil
    {
        private const int MaxSide = 1568; // Claude's recommended maximum edge length

        public static byte[] ToPng(SD.Image image)
        {
            using var ms = new MemoryStream();
            image.Save(ms, SDI.ImageFormat.Png);
            return ms.ToArray();
        }

        public static byte[] FileToPng(string path)
        {
            using var img = SD.Image.FromFile(path);
            return ToPng(img);
        }

        /// <summary>Shrinks large images before upload to save time and cost.</summary>
        public static byte[] PrepareForApi(byte[] png)
        {
            using var ms = new MemoryStream(png);
            using var img = SD.Image.FromStream(ms);
            int longest = Math.Max(img.Width, img.Height);
            if (longest <= MaxSide) return png;

            double scale = (double)MaxSide / longest;
            int w = Math.Max(1, (int)Math.Round(img.Width * scale));
            int h = Math.Max(1, (int)Math.Round(img.Height * scale));
            using var resized = new SD.Bitmap(w, h);
            using (var g = SD.Graphics.FromImage(resized))
            {
                g.InterpolationMode = SDD.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SDD.SmoothingMode.HighQuality;
                g.PixelOffsetMode = SDD.PixelOffsetMode.HighQuality;
                g.DrawImage(img, 0, 0, w, h);
            }
            return ToPng(resized);
        }

        public static BitmapImage ToBitmapImage(byte[] bytes)
        {
            var bmp = new BitmapImage();
            using (var ms = new MemoryStream(bytes))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
            }
            bmp.Freeze();
            return bmp;
        }

        public static bool IsImageFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif" || ext == ".webp";
        }

        public static bool IsTextFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".txt" || ext == ".md" || ext == ".csv";
        }
    }

    internal static class ScreenCapture
    {
        /// <summary>Grabs a region of the screen in physical pixels and returns it as PNG.</summary>
        public static byte[] Grab(SD.Rectangle region)
        {
            using var bmp = new SD.Bitmap(region.Width, region.Height, SDI.PixelFormat.Format32bppArgb);
            using (var g = SD.Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(region.Left, region.Top, 0, 0, region.Size, SD.CopyPixelOperation.SourceCopy);
            }
            return ImageUtil.ToPng(bmp);
        }
    }
}
