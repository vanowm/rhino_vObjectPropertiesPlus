using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Rhino;
using Rhino.DocObjects;
using Rhino.UI;

namespace vObjectPropertiesPlus.Views;

[SupportedOSPlatform("windows")]
internal class vObjectPropertiesPlusLauncherPage : ObjectPropertiesPage
{
  private static readonly Guid PanelGuid = typeof(vObjectPropertiesPlusPanel).GUID;

  // O+ panel stays open when any of these tabs are active
  private static readonly HashSet<string> SafeTabs = new(StringComparer.OrdinalIgnoreCase)
    { "object", "properties", "font", "text", "object+", "annotation" };

  private readonly Eto.Forms.Panel _control = new Eto.Forms.Panel
  {
    Content = new Eto.Forms.Label { Text = "↗ Object+ Panel" }
  };

  private IntPtr _propertiesTabHandle;
  private string? _lastObservedTabTitle;
  private bool _tabMonitorAttached;
  private RhinoDoc? _pendingCloseDoc;
  private bool _closeCheckScheduled;

  public vObjectPropertiesPlusLauncherPage()
  {
    RhinoDoc.SelectObjects      += OnSelect;
    RhinoDoc.DeselectAllObjects += OnDeselectAll;
    RhinoDoc.DeselectObjects    += OnDeselect;
  }

  public override string EnglishPageTitle => "Object+";
  public override object PageControl => _control;

  public override System.Drawing.Icon PageIcon(System.Drawing.Size sizeInPixels)
    => vObjectPropertiesPlusPlugIn.LoadPanelIcon();

  public override bool ShouldDisplay(ObjectPropertiesPageEventArgs e) => true;

  public override bool OnActivate(bool active)
  {
    if (active)
    {
      EnsureTabMonitorHooked();
      ShowPanel();
    }
    return base.OnActivate(active);
  }

  // Rhino hosts the Properties pages in native tab controls. Scan the current Rhino
  // process for the tab strip that contains the Object+ tab and monitor it from idle.
  private void EnsureTabMonitorHooked()
  {
    if (_tabMonitorAttached)
      return;

    RhinoApp.Idle += OnIdleMonitorPropertiesTab;
    _tabMonitorAttached = true;
  }

  private void OnIdleMonitorPropertiesTab(object? sender, EventArgs e)
  {
    if (_propertiesTabHandle == IntPtr.Zero || !IsWindow(_propertiesTabHandle))
    {
      _propertiesTabHandle = FindPropertiesTabHandleForCurrentProcess();
      if (_propertiesTabHandle == IntPtr.Zero)
        return;

      _lastObservedTabTitle = null;
      vObjectPropertiesPlusPlugIn.DebugLog("LauncherPage: hooked native Properties tab handle.");
    }

    string? title = GetSelectedTabTitle(_propertiesTabHandle);
    if (string.Equals(title, _lastObservedTabTitle, StringComparison.Ordinal))
      return;

    _lastObservedTabTitle = title;
    vObjectPropertiesPlusPlugIn.DebugLog($"LauncherPage: selected tab changed to '{title ?? "<null>"}'.");

    if (title != null && SafeTabs.Contains(title))
    {
      OpenIfSelected();
      return;
    }

    if (!string.IsNullOrWhiteSpace(title))
      HidePanel();
  }

  private static IntPtr FindPropertiesTabHandleForCurrentProcess()
  {
    IntPtr found = IntPtr.Zero;
    int currentPid = Process.GetCurrentProcess().Id;
    EnumWindows((topHandle, _) =>
    {
      uint windowPid;
      GetWindowThreadProcessId(topHandle, out windowPid);
      if (windowPid != currentPid)
        return true;

      if (IsTabControlWindow(topHandle) && TabContainsTitle(topHandle, "Object+"))
      {
        found = topHandle;
        return false;
      }

      EnumChildWindows(topHandle, (childHandle, _) =>
      {
        if (!IsTabControlWindow(childHandle)) return true;
        if (!TabContainsTitle(childHandle, "Object+")) return true;
        found = childHandle;
        return false;
      }, IntPtr.Zero);

      return found == IntPtr.Zero;
    }, IntPtr.Zero);

    return found;
  }

  private static bool IsTabControlWindow(IntPtr handle)
  {
    var className = new StringBuilder(64);
    return GetClassName(handle, className, className.Capacity) > 0
      && string.Equals(className.ToString(), "SysTabControl32", StringComparison.Ordinal);
  }

  private static bool TabContainsTitle(IntPtr tabHandle, string title)
  {
    int count = GetTabCount(tabHandle);
    for (int index = 0; index < count; index++)
    {
      if (string.Equals(GetTabText(tabHandle, index)?.Trim(), title, StringComparison.OrdinalIgnoreCase))
        return true;
    }

    return false;
  }

  private static int GetTabCount(IntPtr tabHandle)
    => unchecked((int)SendMessage(tabHandle, TCM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt64());

  private static string? GetSelectedTabTitle(IntPtr tabHandle)
  {
    int index = unchecked((int)SendMessage(tabHandle, TCM_GETCURSEL, IntPtr.Zero, IntPtr.Zero).ToInt64());
    if (index < 0)
      return null;

    return GetTabText(tabHandle, index)?.Trim();
  }

  private static string? GetTabText(IntPtr tabHandle, int index)
  {
    const int bufferChars = 256;
    IntPtr buffer = Marshal.AllocHGlobal(bufferChars * sizeof(char));
    try
    {
      var item = new TCITEM
      {
        mask = TCIF_TEXT,
        pszText = buffer,
        cchTextMax = bufferChars
      };

      for (int i = 0; i < bufferChars; i++)
        Marshal.WriteInt16(buffer, i * sizeof(char), 0);

      IntPtr result = SendMessage(tabHandle, TCM_GETITEMW, new IntPtr(index), ref item);
      if (result == IntPtr.Zero)
        return null;

      return Marshal.PtrToStringUni(buffer);
    }
    finally
    {
      Marshal.FreeHGlobal(buffer);
    }
  }

  private static bool HasSelectedObjects()
    => RhinoDoc.ActiveDoc?.Objects.GetSelectedObjects(false, false).Any() == true;

  private static void ShowPanel()
  {
    try
    {
      if (!Panels.IsPanelVisible(PanelGuid) && Panels.PanelDockBar(PanelGuid) == Guid.Empty)
      {
        Panels.FloatPanel(PanelGuid, Panels.FloatPanelMode.Show);
        return;
      }

      Panels.OpenPanel(PanelGuid);
    }
    catch { }
  }

  private static void HidePanel()
  {
    try
    {
      if (Panels.PanelDockBar(PanelGuid) == Guid.Empty)
      {
        Panels.FloatPanel(PanelGuid, Panels.FloatPanelMode.Hide);
        return;
      }

      Panels.ClosePanel(PanelGuid);
    }
    catch { }
  }

  private static void OpenIfSelected()
  {
    if (!HasSelectedObjects()) return;
    ShowPanel();
  }

  private void OnSelect(object? sender, RhinoObjectSelectionEventArgs e)
  {
    CancelScheduledClose();
    if (_lastObservedTabTitle != null && SafeTabs.Contains(_lastObservedTabTitle))
      OpenIfSelected();
  }

  private void OnDeselectAll(object? sender, RhinoDeselectAllObjectsEventArgs e)
    => ScheduleCloseIfNothingSelected(e.Document);

  private void OnDeselect(object? sender, RhinoObjectSelectionEventArgs e)
    => ScheduleCloseIfNothingSelected(e.Document);

  private void ScheduleCloseIfNothingSelected(RhinoDoc? doc)
  {
    _pendingCloseDoc = doc;
    if (_closeCheckScheduled) return;
    _closeCheckScheduled = true;
    RhinoApp.Idle += OnIdleCloseIfNothingSelected;
  }

  private void CancelScheduledClose()
  {
    _pendingCloseDoc = null;
    if (!_closeCheckScheduled) return;
    RhinoApp.Idle -= OnIdleCloseIfNothingSelected;
    _closeCheckScheduled = false;
  }

  private void OnIdleCloseIfNothingSelected(object? sender, EventArgs e)
  {
    RhinoApp.Idle -= OnIdleCloseIfNothingSelected;
    _closeCheckScheduled = false;
    RhinoDoc? doc = _pendingCloseDoc;
    _pendingCloseDoc = null;
    CloseIfNothingSelected(doc);
  }

  private static void CloseIfNothingSelected(RhinoDoc? doc)
  {
    if (doc == null || doc.Objects.GetSelectedObjects(false, false).Any()) return;
    if (!IsRhinoForeground()) return;
    HidePanel();
  }

  private const int TCM_FIRST = 0x1300;
  private const int TCM_GETITEMCOUNT = TCM_FIRST + 4;
  private const int TCM_GETCURSEL = TCM_FIRST + 11;
  private const int TCM_GETITEMW = TCM_FIRST + 60;
  private const uint TCIF_TEXT = 0x0001;

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  private struct TCITEM
  {
    public uint mask;
    public uint dwState;
    public uint dwStateMask;
    public IntPtr pszText;
    public int cchTextMax;
    public int iImage;
    public IntPtr lParam;
  }

  private delegate bool EnumChildProc(IntPtr childHandle, IntPtr lParam);

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool EnumWindows(EnumChildProc callback, IntPtr lParam);

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool EnumChildWindows(IntPtr parentHandle, EnumChildProc callback, IntPtr lParam);

  [DllImport("user32.dll", CharSet = CharSet.Unicode)]
  private static extern int GetClassName(IntPtr handle, StringBuilder className, int maxCount);

  [DllImport("user32.dll")]
  private static extern IntPtr GetForegroundWindow();

  [DllImport("user32.dll")]
  private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool IsWindow(IntPtr handle);

  [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
  private static extern IntPtr SendMessage(IntPtr handle, int msg, IntPtr wParam, IntPtr lParam);

  [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
  private static extern IntPtr SendMessage(IntPtr handle, int msg, IntPtr wParam, ref TCITEM item);

  private static bool IsRhinoForeground()
  {
    IntPtr foreground = GetForegroundWindow();
    if (foreground == IntPtr.Zero)
      return false;

    uint foregroundPid;
    GetWindowThreadProcessId(foreground, out foregroundPid);
    return foregroundPid == Process.GetCurrentProcess().Id;
  }
}

