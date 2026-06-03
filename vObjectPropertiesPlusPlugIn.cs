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

    // Open the panel the first time (or whenever it is not already visible)
    RhinoApp.Idle += OpenPanelOnFirstIdle;

    return LoadReturnCode.Success;
  }

  protected override void ObjectPropertiesPages(ObjectPropertiesPageCollection collection)
  {
    collection.Add(new Views.vObjectPropertiesPlusLauncherPage());
  }

  private static void OpenPanelOnFirstIdle(object? sender, EventArgs e)
  {
    RhinoApp.Idle -= OpenPanelOnFirstIdle;
    var doc = RhinoDoc.ActiveDoc;
    var panelGuid = typeof(Views.vObjectPropertiesPlusPanel).GUID;
    DebugLog($"OpenPanelOnFirstIdle: doc={(doc?.Name ?? "null")} panelGuid={panelGuid}");
    try
    {
      if (doc != null)
        Panels.OpenPanel(panelGuid);
      else
        Panels.OpenPanel(panelGuid);
      DebugLog("OpenPanelOnFirstIdle: OpenPanel called.");
    }
    catch (Exception ex)
    {
      DebugLog($"OpenPanelOnFirstIdle: OpenPanel FAILED: {ex}");
    }
  }

  internal static System.Drawing.Icon LoadPanelIcon()
  {
    try
    {
      string dir = Path.GetDirectoryName(typeof(vObjectPropertiesPlusPlugIn).Assembly.Location) ?? AppContext.BaseDirectory;
      string pngPath = Path.Combine(dir, "vObjectPropertiesPlus.png");
      if (File.Exists(pngPath))
      {
        // Do NOT use 'using' here — the HICON from GetHicon() references the
        // bitmap's pixel data; disposing bmp invalidates the handle.
        var bmp = new System.Drawing.Bitmap(pngPath);
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
      }
    }
    catch { }
    return System.Drawing.SystemIcons.Application;
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
        File.AppendAllText(logPath, line);
      }
    }
    catch
    {
    }
  }
}
