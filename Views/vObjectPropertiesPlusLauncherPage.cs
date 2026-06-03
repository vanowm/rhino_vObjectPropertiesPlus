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
      vObjectPropertiesPlusPlugIn.DebugLog("LauncherPage: OnActivate(true) - attaching tab monitor and showing panel.");
      EnsureTabMonitorHooked();
      ShowPanel();
    }
    else
    {
      vObjectPropertiesPlusPlugIn.DebugLog("LauncherPage: OnActivate(false)");
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
      {
        vObjectPropertiesPlusPlugIn.DebugLog("LauncherPage: Properties tab handle not found in this idle cycle.");
        return;
      }

      _lastObservedTabTitle = null;
      var allTabs = GetAllTabTitles(_propertiesTabHandle);
      vObjectPropertiesPlusPlugIn.DebugLog($"LauncherPage: hooked native Properties tab handle. Tabs present: {string.Join(", ", allTabs)}");
    }

    string? title = GetSelectedTabTitle(_propertiesTabHandle);
    if (string.Equals(title, _lastObservedTabTitle, StringComparison.Ordinal))
      return;

    _lastObservedTabTitle = title;
    bool isSafe = title != null && SafeTabs.Contains(title);
    vObjectPropertiesPlusPlugIn.DebugLog($"LauncherPage: selected tab changed to '{title ?? "<null>"}'. Safe={isSafe}");

    if (isSafe)
    {
      OpenIfSelected();
      return;
    }

    if (!string.IsNullOrWhiteSpace(title))
    {
      vObjectPropertiesPlusPlugIn.DebugLog($"LauncherPage: hiding panel due to unsafe tab '{title}'.");
      HidePanel();
    }
  }

  private static IntPtr FindPropertiesTabHandleForCurrentProcess()
  {
    IntPtr found = IntPtr.Zero;
    int currentPid = Process.GetCurrentProcess().Id;
    int topWindowsScanned = 0;
    int childWindowsScanned = 0;
    int tabControlsFound = 0;
    
    EnumWindows((topHandle, _) =>
    {
      topWindowsScanned++;
      uint windowPid;
      GetWindowThreadProcessId(topHandle, out windowPid);
      if (windowPid != currentPid)
        return true;

      if (IsTabControlWindow(topHandle))
      {
        tabControlsFound++;
        var allTabs = GetAllTabTitles(topHandle);
        vObjectPropertiesPlusPlugIn.DebugLog($"LauncherPage: found top-level SysTabControl32 #{tabControlsFound} with tabs: [{string.Join(", ", allTabs)}]");
        
        if (TabContainsTitle(topHandle, "Object+"))
        {
          found = topHandle;
          vObjectPropertiesPlusPlugIn.DebugLog($"LauncherPage: tab handle found in top window (scanned {topWindowsScanned} top-level, {childWindowsScanned} child).");
          return false;
        }
      }

      EnumChildWindows(topHandle, (childHandle, _) =>
      {
        childWindowsScanned++;
        if (!IsTabControlWindow(childHandle)) return true;
        
        tabControlsFound++;
        var allTabs = GetAllTabTitles(childHandle);
        vObjectPropertiesPlusPlugIn.DebugLog($"LauncherPage: found child SysTabControl32 #{tabControlsFound} with tabs: [{string.Join(", ", allTabs)}]");
        
        if (!TabContainsTitle(childHandle, "Object+")) return true;
        found = childHandle;
        vObjectPropertiesPlusPlugIn.DebugLog($"LauncherPage: tab handle found in child window (scanned {topWindowsScanned} top-level, {childWindowsScanned} child).");
        return false;
      }, IntPtr.Zero);

      return found == IntPtr.Zero;
    }, IntPtr.Zero);

    if (found == IntPtr.Zero)
      vObjectPropertiesPlusPlugIn.DebugLog($"LauncherPage: no Properties tab found (scanned {topWindowsScanned} top-level, {childWindowsScanned} child windows, found {tabControlsFound} SysTabControl32 controls).");

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

  private static List<string> GetAllTabTitles(IntPtr tabHandle)
  {
    var titles = new List<string>();
    int count = GetTabCount(tabHandle);
    for (int i = 0; i < count; i++)
    {
      var title = GetTabText(tabHandle, i)?.Trim();
      if (title != null)
        titles.Add(title);
    }
    return titles;
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
      bool isFloating = Panels.PanelDockBar(PanelGuid) == Guid.Empty;
      bool isVisible = Panels.IsPanelVisible(PanelGuid);
      vObjectPropertiesPlusPlugIn.DebugLog($"LauncherPage: ShowPanel called. Floating={isFloating}, Visible={isVisible}");

      if (!isVisible && isFloating)
      {
        Panels.FloatPanel(PanelGuid, Panels.FloatPanelMode.Show);
        vObjectPropertiesPlusPlugIn.DebugLog("LauncherPage: opened floating panel.");
        return;
      }

      Panels.OpenPanel(PanelGuid);
      vObjectPropertiesPlusPlugIn.DebugLog("LauncherPage: opened docked panel.");
    }
    catch (Exception ex)
    {
      vObjectPropertiesPlusPlugIn.DebugLog($"LauncherPage: ShowPanel exception: {ex.Message}");
    }
  }

  private static void HidePanel()
  {
    try
    {
      bool isFloating = Panels.PanelDockBar(PanelGuid) == Guid.Empty;
      bool isVisible = Panels.IsPanelVisible(PanelGuid);
      vObjectPropertiesPlusPlugIn.DebugLog($"LauncherPage: HidePanel called. Floating={isFloating}, Visible={isVisible}");

      if (isFloating)
      {
        Panels.FloatPanel(PanelGuid, Panels.FloatPanelMode.Hide);
        vObjectPropertiesPlusPlugIn.DebugLog("LauncherPage: hid floating panel.");
        return;
      }

      Panels.ClosePanel(PanelGuid);
      vObjectPropertiesPlusPlugIn.DebugLog("LauncherPage: closed docked panel.");
    }
    catch (Exception ex)
    {
      vObjectPropertiesPlusPlugIn.DebugLog($"LauncherPage: HidePanel exception: {ex.Message}");
    }
  }

  private static void OpenIfSelected()
  {
    if (!HasSelectedObjects())
    {
      vObjectPropertiesPlusPlugIn.DebugLog("LauncherPage: not opening panel (nothing selected).");
      return;
    }
    vObjectPropertiesPlusPlugIn.DebugLog("LauncherPage: opening panel (objects selected).");
    ShowPanel();
  }

  private void OnSelect(object? sender, RhinoObjectSelectionEventArgs e)
  {
    vObjectPropertiesPlusPlugIn.DebugLog("LauncherPage: OnSelect event.");
    CancelScheduledClose();
    if (_lastObservedTabTitle != null && SafeTabs.Contains(_lastObservedTabTitle))
    {
      vObjectPropertiesPlusPlugIn.DebugLog($"LauncherPage: current tab '{_lastObservedTabTitle}' is safe; opening if selected.");
      OpenIfSelected();
    }
    else
    {
      vObjectPropertiesPlusPlugIn.DebugLog($"LauncherPage: current tab '{_lastObservedTabTitle ?? "<unknown>"}' not safe; not opening.");
    }
  }

  private void OnDeselectAll(object? sender, RhinoDeselectAllObjectsEventArgs e)
  {
    vObjectPropertiesPlusPlugIn.DebugLog("LauncherPage: OnDeselectAll event.");
    ScheduleCloseIfNothingSelected(e.Document);
  }

  private void OnDeselect(object? sender, RhinoObjectSelectionEventArgs e)
  {
    vObjectPropertiesPlusPlugIn.DebugLog("LauncherPage: OnDeselect event.");
    ScheduleCloseIfNothingSelected(e.Document);
  }

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
    if (doc == null)
    {
      vObjectPropertiesPlusPlugIn.DebugLog("LauncherPage: close check skipped (no doc).");
      return;
    }

    var selected = doc.Objects.GetSelectedObjects(false, false).ToArray();
    if (selected.Any())
    {
      vObjectPropertiesPlusPlugIn.DebugLog($"LauncherPage: close check skipped ({selected.Length} objects still selected).");
      return;
    }

    bool isRhinoForeground = IsRhinoForeground();
    vObjectPropertiesPlusPlugIn.DebugLog($"LauncherPage: nothing selected. Rhino foreground={isRhinoForeground}");
    
    if (!isRhinoForeground)
    {
      vObjectPropertiesPlusPlugIn.DebugLog("LauncherPage: not hiding because Rhino is not in foreground.");
      return;
    }

    vObjectPropertiesPlusPlugIn.DebugLog("LauncherPage: hiding panel (nothing selected, Rhino in foreground).");
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

