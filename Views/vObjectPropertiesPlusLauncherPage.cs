using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Windows.Forms;
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

  private TabControl? _propertiesTabControl;
  private RhinoDoc? _pendingCloseDoc;
  private bool _closeCheckScheduled;

  public vObjectPropertiesPlusLauncherPage()
  {
    RhinoDoc.DeselectAllObjects += OnDeselectAll;
    RhinoDoc.DeselectObjects    += OnDeselect;
    _control.LoadComplete       += OnControlLoadComplete;
  }

  public override string EnglishPageTitle => "Object+";
  public override object PageControl => _control;

  public override System.Drawing.Icon PageIcon(System.Drawing.Size sizeInPixels)
    => vObjectPropertiesPlusPlugIn.LoadPanelIcon();

  public override bool ShouldDisplay(ObjectPropertiesPageEventArgs e) => true;

  public override void OnCreateParent(IntPtr hwndParent)
  {
    base.OnCreateParent(hwndParent);
    EnsureTabControlHooked(hwndParent);
  }

  public override bool OnActivate(bool active)
  {
    if (active)
    {
      EnsureTabControlHooked(IntPtr.Zero);
      ShowPanel();
    }
    return base.OnActivate(active);
  }

  private void OnControlLoadComplete(object? sender, EventArgs e)
  {
    _control.LoadComplete -= OnControlLoadComplete;
    EnsureTabControlHooked(IntPtr.Zero);
  }

  // Resolve the Properties TabControl once the page is actually parented.
  // LoadComplete is sometimes too early, so we also retry from OnActivate.
  private void EnsureTabControlHooked(IntPtr hwndParent)
  {
    if (_propertiesTabControl != null) return;

    try
    {
      var native = GetNativeControl(hwndParent) ?? GetNativeControl(_control);
      if (native == null)
      {
        vObjectPropertiesPlusPlugIn.DebugLog("LauncherPage: native control not available for tab hook.");
        return;
      }

      var tabControl = FindPropertiesTabControl(native);
      if (tabControl == null)
      {
        vObjectPropertiesPlusPlugIn.DebugLog("LauncherPage: Properties TabControl not found.");
        return;
      }

      _propertiesTabControl = tabControl;
      _propertiesTabControl.SelectedIndexChanged += OnPropertiesTabChanged;
      vObjectPropertiesPlusPlugIn.DebugLog($"LauncherPage: hooked TabControl, selected tab='{_propertiesTabControl.SelectedTab?.Text?.Trim() ?? "<null>"}'.");
    }
    catch (Exception ex)
    {
      vObjectPropertiesPlusPlugIn.DebugLog($"LauncherPage: failed to hook TabControl: {ex.Message}");
    }
  }

  private static System.Windows.Forms.Control? GetNativeControl(IntPtr hwndParent)
  {
    if (hwndParent == IntPtr.Zero) return null;
    try { return System.Windows.Forms.Control.FromHandle(hwndParent); }
    catch { return null; }
  }

  private static System.Windows.Forms.Control? GetNativeControl(Eto.Forms.Control control)
  {
    var handler = control.Handler;
    var type = handler?.GetType();
    var prop = type?.GetProperty("Control")
            ?? type?.GetProperty("ContainerControl")
            ?? type?.GetProperty("Widget");
    return prop?.GetValue(handler) as System.Windows.Forms.Control;
  }

  private static TabControl? FindPropertiesTabControl(System.Windows.Forms.Control native)
  {
    for (var parent = native.Parent; parent != null; parent = parent.Parent)
    {
      if (parent is TabControl tabControl && HasObjectPlusTab(tabControl))
        return tabControl;
    }

    var root = native.FindForm() ?? native.TopLevelControl ?? native;
    foreach (var child in EnumerateControls(root))
    {
      if (child is TabControl tabControl && HasObjectPlusTab(tabControl))
        return tabControl;
    }

    return null;
  }

  private static IEnumerable<System.Windows.Forms.Control> EnumerateControls(System.Windows.Forms.Control root)
  {
    yield return root;
    foreach (System.Windows.Forms.Control child in root.Controls)
    {
      foreach (var descendant in EnumerateControls(child))
        yield return descendant;
    }
  }

  private static bool HasObjectPlusTab(TabControl tabControl)
    => tabControl.TabPages.Cast<TabPage>().Any(tp =>
      string.Equals(tp.Text?.Trim(), "Object+", StringComparison.OrdinalIgnoreCase));

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

  private void OnPropertiesTabChanged(object? sender, EventArgs e)
  {
    if (sender is not TabControl tc) return;
    string? title = tc.SelectedTab?.Text?.Trim();
    vObjectPropertiesPlusPlugIn.DebugLog($"LauncherPage: selected tab changed to '{title ?? "<null>"}'.");
    if (title != null && SafeTabs.Contains(title))
    {
      OpenIfSelected();
      return;
    }

    HidePanel();
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

  private void OnIdleCloseIfNothingSelected(object? sender, EventArgs e)
  {
    RhinoApp.Idle -= OnIdleCloseIfNothingSelected;
    _closeCheckScheduled = false;
    CloseIfNothingSelected(_pendingCloseDoc);
    _pendingCloseDoc = null;
  }

  private static void CloseIfNothingSelected(RhinoDoc? doc)
  {
    if (doc == null || doc.Objects.GetSelectedObjects(false, false).Any()) return;
    HidePanel();
  }
}

