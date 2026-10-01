using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace vObjectPropertiesPlus.Helpers;

[SupportedOSPlatform("windows")]
internal static class PictureImageProcessor
{
  private const long MaxProcessedPixels = 4_000_000;
  private const int MaxProcessedDimension = 4096;
  private static readonly object TemporaryFilesLock = new();
  private static readonly HashSet<string> TemporaryFiles =
    new(StringComparer.OrdinalIgnoreCase);

  internal const string UnsharpMask = "Unsharp mask";
  internal const string Laplacian = "Laplacian";
  internal const string HighPass = "High pass";

  internal static readonly string[] Algorithms =
  {
    UnsharpMask,
    Laplacian,
    HighPass
  };

  internal static Bitmap SharpenFile(string path, int level, string algorithm,
    CancellationToken cancellationToken)
  {
    var totalTimer = Stopwatch.StartNew();
    var stageTimer = Stopwatch.StartNew();
    using var loaded = new Bitmap(path);
    cancellationToken.ThrowIfCancellationRequested();
    double loadMs = stageTimer.Elapsed.TotalMilliseconds;
    stageTimer.Restart();
    Size outputSize = BoundedOutputSize(loaded.Width, loaded.Height);
    var source = new Bitmap(outputSize.Width, outputSize.Height,
      PixelFormat.Format32bppArgb);
    using (var graphics = Graphics.FromImage(source))
    {
      graphics.CompositingMode = CompositingMode.SourceCopy;
      graphics.CompositingQuality = CompositingQuality.HighQuality;
      graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
      graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
      graphics.DrawImage(loaded, new Rectangle(Point.Empty, outputSize));
    }
    cancellationToken.ThrowIfCancellationRequested();
    double resampleMs = stageTimer.Elapsed.TotalMilliseconds;

    if (outputSize.Width != loaded.Width || outputSize.Height != loaded.Height)
      Log.Write($"Picture sharpen bounded '{path}' from {loaded.Width}x{loaded.Height} "
        + $"to {outputSize.Width}x{outputSize.Height}");

    int clampedLevel = Math.Clamp(level, 0, 100);
    if (clampedLevel == 0)
    {
      Log.Write("PictureTiming", $"Sharpness pixels: algorithm={algorithm}, level=0, "
        + $"input={loaded.Width}x{loaded.Height}, output={source.Width}x{source.Height}, "
        + $"total={totalTimer.Elapsed.TotalMilliseconds:0.0}ms, load={loadMs:0.0}ms, "
        + $"resample={resampleMs:0.0}ms");
      return source;
    }

    stageTimer.Restart();
    byte[] input = ReadPixels(source);
    cancellationToken.ThrowIfCancellationRequested();
    double readMs = stageTimer.Elapsed.TotalMilliseconds;
    stageTimer.Restart();
    byte[] output = algorithm switch
    {
      Laplacian => ApplyLaplacian(input, source.Width, source.Height, clampedLevel),
      HighPass => ApplyHighPass(input, source.Width, source.Height, clampedLevel),
      _ => ApplyUnsharpMask(input, source.Width, source.Height, clampedLevel)
    };
    cancellationToken.ThrowIfCancellationRequested();
    double filterMs = stageTimer.Elapsed.TotalMilliseconds;
    stageTimer.Restart();
    WritePixels(source, output);
    double writeMs = stageTimer.Elapsed.TotalMilliseconds;
    Log.Write("PictureTiming", $"Sharpness pixels: algorithm={algorithm}, level={clampedLevel}, "
      + $"input={loaded.Width}x{loaded.Height}, output={source.Width}x{source.Height}, "
      + $"total={totalTimer.Elapsed.TotalMilliseconds:0.0}ms, load={loadMs:0.0}ms, "
      + $"resample={resampleMs:0.0}ms, read={readMs:0.0}ms, "
      + $"filter={filterMs:0.0}ms, write={writeMs:0.0}ms");
    return source;
  }

  internal static string SharpenToTemporaryFile(string path, int level, string algorithm,
    CancellationToken cancellationToken)
  {
    var totalTimer = Stopwatch.StartNew();
    string directory = Path.Combine(Path.GetTempPath(), "vObjectPropertiesPlus");
    Directory.CreateDirectory(directory);
    string outputPath = Path.Combine(directory, $"sharp-{Guid.NewGuid():N}.png");
    try
    {
      var stageTimer = Stopwatch.StartNew();
      using Bitmap bitmap = SharpenFile(path, level, algorithm, cancellationToken);
      double processMs = stageTimer.Elapsed.TotalMilliseconds;
      cancellationToken.ThrowIfCancellationRequested();
      stageTimer.Restart();
      bitmap.Save(outputPath, ImageFormat.Png);
      double encodeMs = stageTimer.Elapsed.TotalMilliseconds;
      cancellationToken.ThrowIfCancellationRequested();
      lock (TemporaryFilesLock)
        TemporaryFiles.Add(outputPath);
      long bytes = new FileInfo(outputPath).Length;
      Log.Write("PictureTiming", $"Sharpness export: algorithm={algorithm}, level={level}, "
        + $"size={bitmap.Width}x{bitmap.Height}, bytes={bytes}, "
        + $"total={totalTimer.Elapsed.TotalMilliseconds:0.0}ms, "
        + $"process={processMs:0.0}ms, encode={encodeMs:0.0}ms");
      return outputPath;
    }
    catch
    {
      TryDeleteTemporaryFile(outputPath);
      throw;
    }
  }

  internal static void ReleaseTemporaryFile(string? path)
  {
    if (string.IsNullOrWhiteSpace(path))
      return;
    bool tracked;
    lock (TemporaryFilesLock)
      tracked = TemporaryFiles.Remove(path);
    if (tracked)
      TryDeleteTemporaryFile(path);
  }

  internal static void CleanupTemporaryFiles()
  {
    string[] paths;
    lock (TemporaryFilesLock)
    {
      paths = TemporaryFiles.ToArray();
      TemporaryFiles.Clear();
    }
    foreach (string path in paths)
      TryDeleteTemporaryFile(path);
  }

  private static void TryDeleteTemporaryFile(string path)
  {
    try
    {
      if (File.Exists(path))
        File.Delete(path);
    }
    catch (Exception ex)
    {
      Log.Write($"Delete temporary picture file failed for '{path}': {ex.Message}");
    }
  }

  private static Size BoundedOutputSize(int width, int height)
  {
    if (width <= 0 || height <= 0)
      throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");

    double pixelScale = Math.Sqrt(MaxProcessedPixels / ((double)width * height));
    double dimensionScale = Math.Min(
      MaxProcessedDimension / (double)width,
      MaxProcessedDimension / (double)height);
    double scale = Math.Min(1.0, Math.Min(pixelScale, dimensionScale));
    return new Size(
      Math.Max(1, (int)Math.Round(width * scale)),
      Math.Max(1, (int)Math.Round(height * scale)));
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
