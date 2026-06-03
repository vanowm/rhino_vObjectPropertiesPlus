using System;
using System.IO;
using System.Runtime.Versioning;
using Rhino;
using Rhino.PlugIns;
using Rhino.UI;

namespace vObjectPropertiesPlus;

[SupportedOSPlatform("windows")]
[System.Runtime.InteropServices.Guid("2E0E8488-399B-4D87-B845-8A486911F808")]
public class vObjectPropertiesPlusPlugIn : PlugIn
{
  private static readonly object LogLock = new();
  private static bool _isFirstLog = true;
  private static System.Drawing.Icon? _cachedPanelIcon;

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
    DebugLog($"OnLoad: asmLocation='{asmLocation}' versionText='{versionText}'");

    RhinoApp.WriteLine($"{LocalPlugInName} v{versionText}");
    DebugLog("OnLoad: plugin loaded.");

    var panelGuid = typeof(Views.vObjectPropertiesPlusPanel).GUID;
    DebugLog($"OnLoad: registering panel GUID={panelGuid}");
    try
    {
      Panels.RegisterPanel(this, typeof(Views.vObjectPropertiesPlusPanel), "Object+", LoadPanelIcon());
      DebugLog("OnLoad: RegisterPanel succeeded.");
    }
    catch (Exception ex)
    {
      DebugLog($"OnLoad: RegisterPanel FAILED: {ex}");
    }

    return LoadReturnCode.Success;
  }

  protected override void ObjectPropertiesPages(ObjectPropertiesPageCollection collection)
  {
    collection.Add(new Views.vObjectPropertiesPlusLauncherPage());
  }

  internal static System.Drawing.Icon LoadPanelIcon()
  {
    if (_cachedPanelIcon != null)
      return _cachedPanelIcon;

    try
    {
      string dir = Path.GetDirectoryName(typeof(vObjectPropertiesPlusPlugIn).Assembly.Location) ?? AppContext.BaseDirectory;
      string pngPath = Path.Combine(dir, "vObjectPropertiesPlus.png");
      DebugLog($"LoadPanelIcon: looking for icon at '{pngPath}'");
      
      if (File.Exists(pngPath))
      {
        // Keep bitmap alive in memory - create persistent icon
        var bmp = new System.Drawing.Bitmap(pngPath);
        DebugLog($"LoadPanelIcon: loaded bitmap {bmp.Width}x{bmp.Height}");
        _cachedPanelIcon = System.Drawing.Icon.FromHandle(bmp.GetHicon());
        DebugLog("LoadPanelIcon: created icon from bitmap");
        return _cachedPanelIcon;
      }
      
      DebugLog("LoadPanelIcon: PNG file not found, using system icon");
    }
    catch (Exception ex)
    {
      DebugLog($"LoadPanelIcon: exception: {ex.Message}");
    }
    
    _cachedPanelIcon = System.Drawing.SystemIcons.Application;
    return _cachedPanelIcon;
  }

  internal static void DebugLog(string message)
  {
    try
    {
      string assemblyDir = Path.GetDirectoryName(typeof(vObjectPropertiesPlusPlugIn).Assembly.Location) ?? AppContext.BaseDirectory;
      string logsDir = Path.Combine(assemblyDir, "logs");
      try
      {
        Directory.CreateDirectory(logsDir);
      }
      catch
      {
        logsDir = assemblyDir;
      }

      string logPath = Path.Combine(logsDir, "vObjectPropertiesPlus_debug.log");
      string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " | " + message + System.Environment.NewLine;
      lock (LogLock)
      {
        if (_isFirstLog)
        {
          File.WriteAllText(logPath, line); // Clear log on first write
          _isFirstLog = false;
        }
        else
        {
          File.AppendAllText(logPath, line);
        }
      }
    }
    catch
    {
    }
  }
}
