using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace vObjectPropertiesPlus.Helpers;

[SupportedOSPlatform("windows")]
internal static class PictureImageProcessor
{
  internal const string UnsharpMask = "Unsharp mask";
  internal const string Laplacian = "Laplacian";
  internal const string HighPass = "High pass";

  internal static readonly string[] Algorithms =
  {
    UnsharpMask,
    Laplacian,
    HighPass
  };

  internal static Bitmap SharpenFile(string path, int level, string algorithm)
  {
    using var loaded = new Bitmap(path);
    var source = new Bitmap(loaded.Width, loaded.Height, PixelFormat.Format32bppArgb);
    using (var graphics = Graphics.FromImage(source))
    {
      graphics.CompositingMode = CompositingMode.SourceCopy;
      graphics.DrawImageUnscaled(loaded, 0, 0);
    }

    int clampedLevel = Math.Clamp(level, 0, 100);
    if (clampedLevel == 0)
      return source;

    byte[] input = ReadPixels(source);
    byte[] output = algorithm switch
    {
      Laplacian => ApplyLaplacian(input, source.Width, source.Height, clampedLevel),
      HighPass => ApplyHighPass(input, source.Width, source.Height, clampedLevel),
      _ => ApplyUnsharpMask(input, source.Width, source.Height, clampedLevel)
    };
    WritePixels(source, output);
    return source;
  }

  private static byte[] ApplyUnsharpMask(byte[] input, int width, int height, int level)
  {
    byte[] blurred = GaussianBlur5(input, width, height);
    byte[] output = new byte[input.Length];
    double amount = level * 0.025;

    Parallel.For(0, height, y =>
    {
      int row = y * width * 4;
      for (int x = 0; x < width; x++)
      {
        int offset = row + x * 4;
        for (int channel = 0; channel < 3; channel++)
        {
          double value = input[offset + channel]
            + amount * (input[offset + channel] - blurred[offset + channel]);
          output[offset + channel] = ClampByte(value);
        }
        output[offset + 3] = input[offset + 3];
      }
    });
    return output;
  }

  private static byte[] ApplyLaplacian(byte[] input, int width, int height, int level)
  {
    byte[] output = new byte[input.Length];
    double amount = level * 0.0125;

    Parallel.For(0, height, y =>
    {
      for (int x = 0; x < width; x++)
      {
        int offset = PixelOffset(x, y, width);
        for (int channel = 0; channel < 3; channel++)
        {
          int center = input[offset + channel];
          int edge = 4 * center
            - Channel(input, x - 1, y, width, height, channel)
            - Channel(input, x + 1, y, width, height, channel)
            - Channel(input, x, y - 1, width, height, channel)
            - Channel(input, x, y + 1, width, height, channel);
          output[offset + channel] = ClampByte(center + amount * edge);
        }
        output[offset + 3] = input[offset + 3];
      }
    });
    return output;
  }

  private static byte[] ApplyHighPass(byte[] input, int width, int height, int level)
  {
    byte[] output = new byte[input.Length];
    double amount = level * 0.02;

    Parallel.For(0, height, y =>
    {
      for (int x = 0; x < width; x++)
      {
        int offset = PixelOffset(x, y, width);
        for (int channel = 0; channel < 3; channel++)
        {
          int sum = 0;
          for (int dy = -1; dy <= 1; dy++)
          {
            for (int dx = -1; dx <= 1; dx++)
              sum += Channel(input, x + dx, y + dy, width, height, channel);
          }

          int center = input[offset + channel];
          output[offset + channel] = ClampByte(center + amount * (center - sum / 9.0));
        }
        output[offset + 3] = input[offset + 3];
      }
    });
    return output;
  }

  private static byte[] GaussianBlur5(byte[] input, int width, int height)
  {
    int[] kernel = { 1, 4, 6, 4, 1 };
    byte[] horizontal = new byte[input.Length];
    byte[] output = new byte[input.Length];

    Parallel.For(0, height, y =>
    {
      for (int x = 0; x < width; x++)
      {
        int offset = PixelOffset(x, y, width);
        for (int channel = 0; channel < 3; channel++)
        {
          int sum = 0;
          for (int k = -2; k <= 2; k++)
            sum += kernel[k + 2] * Channel(input, x + k, y, width, height, channel);
          horizontal[offset + channel] = (byte)(sum / 16);
        }
        horizontal[offset + 3] = input[offset + 3];
      }
    });

    Parallel.For(0, height, y =>
    {
      for (int x = 0; x < width; x++)
      {
        int offset = PixelOffset(x, y, width);
        for (int channel = 0; channel < 3; channel++)
        {
          int sum = 0;
          for (int k = -2; k <= 2; k++)
            sum += kernel[k + 2] * Channel(horizontal, x, y + k, width, height, channel);
          output[offset + channel] = (byte)(sum / 16);
        }
        output[offset + 3] = input[offset + 3];
      }
    });
    return output;
  }

  private static int Channel(byte[] pixels, int x, int y, int width, int height, int channel)
  {
    int clampedX = Math.Clamp(x, 0, width - 1);
    int clampedY = Math.Clamp(y, 0, height - 1);
    return pixels[PixelOffset(clampedX, clampedY, width) + channel];
  }

  private static int PixelOffset(int x, int y, int width) => (y * width + x) * 4;

  private static byte ClampByte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

  private static byte[] ReadPixels(Bitmap bitmap)
  {
    var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
    BitmapData data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    try
    {
      int rowBytes = bitmap.Width * 4;
      var pixels = new byte[rowBytes * bitmap.Height];
      for (int y = 0; y < bitmap.Height; y++)
        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * rowBytes, rowBytes);
      return pixels;
    }
    finally
    {
      bitmap.UnlockBits(data);
    }
  }

  private static void WritePixels(Bitmap bitmap, byte[] pixels)
  {
    var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
    BitmapData data = bitmap.LockBits(rectangle, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
    try
    {
      int rowBytes = bitmap.Width * 4;
      for (int y = 0; y < bitmap.Height; y++)
        Marshal.Copy(pixels, y * rowBytes, IntPtr.Add(data.Scan0, y * data.Stride), rowBytes);
    }
    finally
    {
      bitmap.UnlockBits(data);
    }
  }
}
