using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using Rhino;
using Rhino.DocObjects;
using Rhino.UI;

namespace vObjectPropertiesPlus.Views;

[SupportedOSPlatform("windows")]
internal class vObjectPropertiesPlusLauncherPage : ObjectPropertiesPage
{
  private static readonly Guid PanelGuid = typeof(vObjectPropertiesPlusPanel).GUID;

  private readonly Eto.Forms.Panel _control = new Eto.Forms.Panel
  {
    Content = new Eto.Forms.Label { Text = "↗ Object+ Panel" }
  };

  private bool _isObjectPlusTabActive;
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
    _isObjectPlusTabActive = active;
    
    if (active)
    {
      vObjectPropertiesPlusPlugIn.DebugLog("LauncherPage: OnActivate(true) - Object+ tab active, showing panel if objects selected.");
      CancelScheduledClose();
      OpenIfSelected();
    }
    else
    {
      vObjectPropertiesPlusPlugIn.DebugLog("LauncherPage: OnActivate(false) - user left Object+ tab, hiding panel.");
      HidePanel();
    }
    
    return base.OnActivate(active);
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
    vObjectPropertiesPlusPlugIn.DebugLog($"LauncherPage: OnSelect event. Object+ tab active={_isObjectPlusTabActive}");
    CancelScheduledClose();
    
    if (_isObjectPlusTabActive)
    {
      vObjectPropertiesPlusPlugIn.DebugLog("LauncherPage: Object+ tab is active; opening panel.");
      OpenIfSelected();
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

  private static bool IsRhinoForeground()
  {
    IntPtr foreground = GetForegroundWindow();
    if (foreground == IntPtr.Zero)
      return false;

    uint foregroundPid;
    GetWindowThreadProcessId(foreground, out foregroundPid);
    return foregroundPid == Process.GetCurrentProcess().Id;
  }

  [System.Runtime.InteropServices.DllImport("user32.dll")]
  private static extern IntPtr GetForegroundWindow();

  [System.Runtime.InteropServices.DllImport("user32.dll")]
  private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
}

