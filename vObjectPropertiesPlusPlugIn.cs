using System;
using System.Runtime.Versioning;
using Rhino;
using Rhino.PlugIns;
using Rhino.UI;

namespace vObjectPropertiesPlus;

[SupportedOSPlatform("windows")]
[System.Runtime.InteropServices.Guid("2E0E8488-399B-4D87-B845-8A486911F808")]
public class vObjectPropertiesPlusPlugIn : PlugIn
{
  private static System.Drawing.Icon? _cachedPanelIcon;
  private static System.Drawing.Bitmap? _iconBitmap; // Keep bitmap alive for icon handle
  private static System.Drawing.Icon? _cachedPicturesPanelIcon;
  private static bool _picturesPanelOpenPending;

  public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;
  protected override string LocalPlugInName => "vObjectProperties+";

  public vObjectPropertiesPlusPlugIn()
  {
    Instance = this;
  }

  public static vObjectPropertiesPlusPlugIn Instance { get; private set; } = null!;

  protected override LoadReturnCode OnLoad(ref string errorMessage)
  {
    var asm = typeof(vObjectPropertiesPlusPlugIn).Assembly;
    string asmLocation = asm.Location;
    string versionText = (!string.IsNullOrEmpty(asmLocation)
                            ? System.Diagnostics.FileVersionInfo.GetVersionInfo(asmLocation).FileVersion
                            : null)
                         ?? asm.GetName().Version?.ToString()
                         ?? "unknown";
    Log.Initialize();
    Log.Write($"startup  rhino={RhinoApp.Version}  version={versionText}  dll={asmLocation}");

    RhinoApp.WriteLine($"{LocalPlugInName} v{versionText}");
    Log.Write("OnLoad: plugin loaded.");
    RhinoApp.Closing += OnRhinoClosing;

    var panelGuid = typeof(Views.vObjectPropertiesPlusPanel).GUID;
    Log.Write($"OnLoad: registering panel GUID={panelGuid}");
    try
    {
      Panels.RegisterPanel(this, typeof(Views.vObjectPropertiesPlusPanel), "Properties+", LoadPanelIcon(), PanelType.PerDoc);
      Log.Write("OnLoad: RegisterPanel succeeded with PanelType.PerDoc");
    }
    catch (Exception ex)
    {
      Log.Write($"OnLoad: RegisterPanel FAILED: {ex}");
    }

    try
    {
      Panels.RegisterPanel(this, typeof(Views.vPicturesPanel), "Background photos", LoadPicturesPanelIcon(), PanelType.PerDoc);
      Log.Write("OnLoad: Background photos panel registration succeeded with PanelType.PerDoc");
      Panels.Show += OnPanelShown;
      RhinoDoc.EndOpenDocumentInitialViewUpdate += OnDocumentInitialViewUpdate;
      Eto.Forms.Application.Instance.AsyncInvoke(
        () => RefreshVisiblePicturesPanel(RhinoDoc.ActiveDoc));
    }
    catch (Exception ex)
    {
      Log.Write($"OnLoad: Background photos panel registration FAILED: {ex}");
    }

    return LoadReturnCode.Success;
  }

  private static void OnRhinoClosing(object? sender, EventArgs e)
  {
    Helpers.PictureImageProcessor.CleanupTemporaryFiles();
  }

  private static void OnPanelShown(object? sender, ShowPanelEventArgs e)
  {
    if (e.Show && e.PanelId == typeof(Views.vPicturesPanel).GUID)
      RefreshVisiblePicturesPanel(e.Document ?? RhinoDoc.ActiveDoc, true);
  }

  private static void OnDocumentInitialViewUpdate(object? sender, DocumentOpenEventArgs e)
    => RefreshVisiblePicturesPanel(e.Document);

  private static void RefreshVisiblePicturesPanel(RhinoDoc? doc,
    bool panelIsShowing = false)
  {
    if (doc == null || _picturesPanelOpenPending
      || (!panelIsShowing && Array.IndexOf(Panels.GetOpenPanelIds(),
        typeof(Views.vPicturesPanel).GUID) < 0))
      return;

    bool makeSelected = panelIsShowing
      || Panels.IsPanelVisible(typeof(Views.vPicturesPanel), true);
    _picturesPanelOpenPending = true;
    Eto.Forms.Application.Instance.AsyncInvoke(() =>
    {
      try
      {
        Log.Write($"Ensuring restored Background photos panel for document {doc.RuntimeSerialNumber}");
        Panels.OpenPanel(typeof(Views.vPicturesPanel), makeSelected);
      }
      catch (Exception ex)
      {
        Log.Write($"Refresh visible Background photos panel failed: {ex}");
      }
      finally
      {
        _picturesPanelOpenPending = false;
      }
    });
  }

  internal static System.Drawing.Icon LoadPanelIcon()
  {
    if (_cachedPanelIcon != null)
      return _cachedPanelIcon;

    try
    {
      var asm = typeof(vObjectPropertiesPlusPlugIn).Assembly;

      // Try loading from embedded .ico resource first
      using (var stream = asm.GetManifestResourceStream("vObjectPropertiesPlus.ico"))
      {
        if (stream != null)
        {
          Log.Write("LoadPanelIcon: loading from embedded .ico resource");
          _cachedPanelIcon = new System.Drawing.Icon(stream);
          Log.Write($"LoadPanelIcon: loaded icon {_cachedPanelIcon.Width}x{_cachedPanelIcon.Height}");
          return _cachedPanelIcon;
        }
      }

      // Fallback to embedded PNG resource
      using (var stream = asm.GetManifestResourceStream("vObjectPropertiesPlus.png"))
      {
        if (stream != null)
        {
          Log.Write("LoadPanelIcon: loading from embedded .png resource");
          _iconBitmap = new System.Drawing.Bitmap(stream);
          Log.Write($"LoadPanelIcon: loaded bitmap {_iconBitmap.Width}x{_iconBitmap.Height}");
          _cachedPanelIcon = System.Drawing.Icon.FromHandle(_iconBitmap.GetHicon());
          return _cachedPanelIcon;
        }
      }

      Log.Write("LoadPanelIcon: no embedded icon resource found, using system icon");
    }
    catch (Exception ex)
    {
      Log.Write($"LoadPanelIcon: exception: {ex.Message}");
    }

    _cachedPanelIcon = System.Drawing.SystemIcons.Application;
    return _cachedPanelIcon;
  }

  internal static System.Drawing.Icon LoadPicturesPanelIcon()
  {
    if (_cachedPicturesPanelIcon != null)
      return _cachedPicturesPanelIcon;

    try
    {
      using var bitmap = new System.Drawing.Bitmap(32, 32,
        System.Drawing.Imaging.PixelFormat.Format32bppArgb);
      using var graphics = System.Drawing.Graphics.FromImage(bitmap);
      graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
      graphics.Clear(System.Drawing.Color.Transparent);

      using var frameBrush = new System.Drawing.SolidBrush(
        System.Drawing.Color.FromArgb(255, 48, 61, 71));
      using var imageBrush = new System.Drawing.SolidBrush(
        System.Drawing.Color.FromArgb(255, 232, 247, 250));
      using var mountainBrush = new System.Drawing.SolidBrush(
        System.Drawing.Color.FromArgb(255, 41, 151, 117));
      using var foregroundBrush = new System.Drawing.SolidBrush(
        System.Drawing.Color.FromArgb(255, 22, 115, 154));
      using var sunBrush = new System.Drawing.SolidBrush(
        System.Drawing.Color.FromArgb(255, 255, 190, 46));

      graphics.FillRectangle(frameBrush, 2, 4, 28, 24);
      graphics.FillRectangle(imageBrush, 5, 7, 22, 18);
      graphics.FillEllipse(sunBrush, 19, 9, 5, 5);
      graphics.FillPolygon(mountainBrush, new[]
      {
        new System.Drawing.Point(5, 22),
        new System.Drawing.Point(12, 14),
        new System.Drawing.Point(18, 20),
        new System.Drawing.Point(22, 16),
        new System.Drawing.Point(27, 22),
        new System.Drawing.Point(27, 25),
        new System.Drawing.Point(5, 25)
      });
      graphics.FillPolygon(foregroundBrush, new[]
      {
        new System.Drawing.Point(5, 23),
        new System.Drawing.Point(15, 18),
        new System.Drawing.Point(22, 25),
        new System.Drawing.Point(5, 25)
      });

      using var pngStream = new System.IO.MemoryStream();
      bitmap.Save(pngStream, System.Drawing.Imaging.ImageFormat.Png);
      byte[] pngBytes = pngStream.ToArray();
      using var iconStream = new System.IO.MemoryStream();
      using (var writer = new System.IO.BinaryWriter(iconStream,
        System.Text.Encoding.UTF8, leaveOpen: true))
      {
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write((byte)32);
        writer.Write((byte)32);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write((uint)pngBytes.Length);
        writer.Write((uint)22);
        writer.Write(pngBytes);
      }
      iconStream.Position = 0;
      using var icon = new System.Drawing.Icon(iconStream);
      _cachedPicturesPanelIcon = (System.Drawing.Icon)icon.Clone();
      Log.Write("LoadPicturesPanelIcon: generated self-contained picture icon");
      return _cachedPicturesPanelIcon;
    }
    catch (Exception ex)
    {
      Log.Write($"LoadPicturesPanelIcon: exception: {ex.Message}");
      return LoadPanelIcon();
    }
  }
}
