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
  private Views.vObjectPropertiesPlusObjectPropertiesPage? _page;
  private static readonly object LogLock = new();

  public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;
  protected override string LocalPlugInName => "vObjectProperties+";

  public vObjectPropertiesPlusPlugIn()
  {
    Instance = this;
  }

  public static vObjectPropertiesPlusPlugIn Instance { get; private set; } = null!;

  protected override void ObjectPropertiesPages(ObjectPropertiesPageCollection collection)
  {
    _page ??= new Views.vObjectPropertiesPlusObjectPropertiesPage();
    collection.Add(_page);
    DebugLog("ObjectPropertiesPages: Object+ page added to collection.");
  }

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
    return LoadReturnCode.Success;
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
