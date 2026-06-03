using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Rhino;
using Rhino.DocObjects;
using Rhino.UI;

namespace vObjectPropertiesPlus.Views;

[SupportedOSPlatform("windows")]
internal class vObjectPropertiesPlusLauncherPage : ObjectPropertiesPage
{
  [DllImport("user32.dll")]
  private static extern IntPtr GetForegroundWindow();

  private static readonly Guid PanelGuid = typeof(vObjectPropertiesPlusPanel).GUID;

  private readonly Eto.Forms.Panel _control = new Eto.Forms.Panel
  {
    Content = new Eto.Forms.Label { Text = "↗ Object+ Panel" }
  };

  private RhinoDoc? _pendingCloseDoc;
  private bool _closeCheckScheduled;
  private DateTime _lastSelectionTime = DateTime.MinValue;
  private bool _shouldBeVisible = true; // Start true since panel opens on startup

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
      vObjectPropertiesPlusPlugIn.DebugLog("LauncherPage: OnActivate(true) - Object+ tab active, enabling O+ visibility.");
      _shouldBeVisible = true;
      CancelScheduledClose();
      OpenIfSelected();
    }
    else
    {
      bool rhinoHasFocus = RhinoHasFocus();
      
      // Don't hide when Rhino loses focus - only hide on actual tab switches
      if (!rhinoHasFocus)
      {
        vObjectPropertiesPlusPlugIn.DebugLog("LauncherPage: OnActivate(false) but Rhino lost focus - keeping O+ visible.");
        return base.OnActivate(active);
      }
      
      // Distinguish between manual tab clicks and Rhino auto-switching tabs
      var timeSinceSelection = DateTime.Now - _lastSelectionTime;
      bool isLikelyAutoSwitch = timeSinceSelection.TotalMilliseconds < 500;
      
      vObjectPropertiesPlusPlugIn.DebugLog($"LauncherPage: OnActivate(false) - {timeSinceSelection.TotalMilliseconds:F0}ms since selection, likely auto-switch={isLikelyAutoSwitch}");
      
      if (!isLikelyAutoSwitch)
      {
        vObjectPropertiesPlusPlugIn.DebugLog("LauncherPage: manual tab click to unsupported tab - disabling O+ visibility and hiding.");
        _shouldBeVisible = false;
        HidePanel();
      }
      else
      {
        vObjectPropertiesPlusPlugIn.DebugLog("LauncherPage: keeping O+ visible (Rhino auto-switched tab after selection).");
        // Keep _shouldBeVisible as true so O+ stays visible during auto-switches
      }
    }
    
    return base.OnActivate(active);
  }

  private static bool HasSelectedObjects()
    => RhinoDoc.ActiveDoc?.Objects.GetSelectedObjects(false, false).Any() == true;

  private static bool RhinoHasFocus()
  {
    try
    {
      var foreground = GetForegroundWindow();
      var rhinoHandle = Process.GetCurrentProcess().MainWindowHandle;
      return foreground == rhinoHandle;
    }
    catch
    {
      return true; // Default to true if can't determine
    }
  }

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
      
      // Log stack trace to understand what's triggering the hide
      var stackTrace = new System.Diagnostics.StackTrace(1, true);
      var callerFrame = stackTrace.GetFrame(0);
      string caller = callerFrame?.GetMethod()?.Name ?? "unknown";
      vObjectPropertiesPlusPlugIn.DebugLog($"LauncherPage: HidePanel called by {caller}. Floating={isFloating}, Visible={isVisible}");

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
    _lastSelectionTime = DateTime.Now;
    vObjectPropertiesPlusPlugIn.DebugLog($"LauncherPage: OnSelect event - shouldBeVisible={_shouldBeVisible}");
    CancelScheduledClose();
    
    if (_shouldBeVisible)
    {
      vObjectPropertiesPlusPlugIn.DebugLog("LauncherPage: opening panel (objects selected and O+ tab state allows visibility).");
      OpenIfSelected();
    }
    else
    {
      vObjectPropertiesPlusPlugIn.DebugLog("LauncherPage: NOT opening panel (on unsupported tab).");
    }
  }

  private void OnDeselectAll(object? sender, RhinoDeselectAllObjectsEventArgs e)
  {
    vObjectPropertiesPlusPlugIn.DebugLog("LauncherPage: OnDeselectAll event.");
    _lastSelectionTime = DateTime.MinValue;
    // Don't reset _shouldBeVisible here - it should only be controlled by tab state (OnActivate)
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

    vObjectPropertiesPlusPlugIn.DebugLog("LauncherPage: hiding panel (nothing selected).");
    HidePanel();
  }
}

