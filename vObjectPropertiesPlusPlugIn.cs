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
    DebugLog($"OnLoad: asmLocation='{asmLocation}' versionText='{versionText}'");

    RhinoApp.WriteLine($"{LocalPlugInName} v{versionText}");
    DebugLog("OnLoad: plugin loaded.");

    var panelGuid = typeof(Views.vObjectPropertiesPlusPanel).GUID;
    DebugLog($"OnLoad: registering panel GUID={panelGuid}");
    try
    {
      Panels.RegisterPanel(this, typeof(Views.vObjectPropertiesPlusPanel), "Object+", LoadPanelIcon());
      DebugLog("OnLoad: RegisterPanel succeeded.");
      
      // Open panel on first idle to make it visible and show up in Windows menu
      RhinoApp.Idle += OnFirstIdle;
    }
    catch (Exception ex)
    {
      DebugLog($"OnLoad: RegisterPanel FAILED: {ex}");
    }

    return LoadReturnCode.Success;
  }

  private static void OnFirstIdle(object? sender, EventArgs e)
  {
    RhinoApp.Idle -= OnFirstIdle;
    var panelGuid = typeof(Views.vObjectPropertiesPlusPanel).GUID;
    DebugLog($"OnFirstIdle: opening panel GUID={panelGuid}");
    try
    {
      Panels.OpenPanel(panelGuid);
      DebugLog("OnFirstIdle: OpenPanel succeeded.");
    }
    catch (Exception ex)
    {
      DebugLog($"OnFirstIdle: OpenPanel FAILED: {ex}");
    }
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
          DebugLog("LoadPanelIcon: loading from embedded .ico resource");
          _cachedPanelIcon = new System.Drawing.Icon(stream);
          DebugLog($"LoadPanelIcon: loaded icon {_cachedPanelIcon.Width}x{_cachedPanelIcon.Height}");
          return _cachedPanelIcon;
        }
      }
      
      // Fallback to embedded PNG resource
      using (var stream = asm.GetManifestResourceStream("vObjectPropertiesPlus.png"))
      {
        if (stream != null)
        {
          DebugLog("LoadPanelIcon: loading from embedded .png resource");
          _iconBitmap = new System.Drawing.Bitmap(stream);
          DebugLog($"LoadPanelIcon: loaded bitmap {_iconBitmap.Width}x{_iconBitmap.Height}");
          _cachedPanelIcon = System.Drawing.Icon.FromHandle(_iconBitmap.GetHicon());
          return _cachedPanelIcon;
        }
      }
      
      // Fallback to file system
      string dir = Path.GetDirectoryName(asm.Location) ?? AppContext.BaseDirectory;
      string icoPath = Path.Combine(dir, "vObjectPropertiesPlus.ico");
      if (File.Exists(icoPath))
      {
        DebugLog($"LoadPanelIcon: loading from file '{icoPath}'");
        _cachedPanelIcon = new System.Drawing.Icon(icoPath);
        return _cachedPanelIcon;
      }
      
      string pngPath = Path.Combine(dir, "vObjectPropertiesPlus.png");
      if (File.Exists(pngPath))
      {
        DebugLog($"LoadPanelIcon: loading from file '{pngPath}'");
        _iconBitmap = new System.Drawing.Bitmap(pngPath);
        _cachedPanelIcon = System.Drawing.Icon.FromHandle(_iconBitmap.GetHicon());
        return _cachedPanelIcon;
      }
      
      DebugLog("LoadPanelIcon: no icon resource found, using system icon");
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
