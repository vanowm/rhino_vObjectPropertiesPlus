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

    return LoadReturnCode.Success;
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
}
