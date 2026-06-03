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

  public vObjectPropertiesPlusLauncherPage()
  {
    RhinoDoc.DeselectAllObjects += OnDeselectAll;
    RhinoDoc.DeselectObjects    += OnDeselect;
    _control.LoadComplete       += TryHookTabControl;
  }

  public override string EnglishPageTitle => "Object+";
  public override object PageControl => _control;

  public override System.Drawing.Icon PageIcon(System.Drawing.Size sizeInPixels)
    => vObjectPropertiesPlusPlugIn.LoadPanelIcon();

  public override bool ShouldDisplay(ObjectPropertiesPageEventArgs e) => true;

  public override bool OnActivate(bool active)
  {
    if (active)
      try { Panels.OpenPanel(PanelGuid); } catch { }
    return base.OnActivate(active);
  }

  // Walk up the WinForms parent chain to find the Properties panel TabControl
  // (identified by containing a tab titled "Object+"), then subscribe to tab changes.
  private void TryHookTabControl(object? sender, EventArgs e)
  {
    _control.LoadComplete -= TryHookTabControl;
    try
    {
      var handler  = _control.Handler;
      var prop     = handler?.GetType().GetProperty("Control")
                  ?? handler?.GetType().GetProperty("ContainerControl");
      var native   = prop?.GetValue(handler) as System.Windows.Forms.Control;
      for (var p = native?.Parent; p != null; p = p.Parent)
      {
        if (p is TabControl tc &&
            tc.TabPages.Cast<TabPage>().Any(tp =>
              string.Equals(tp.Text?.Trim(), "Object+", StringComparison.OrdinalIgnoreCase)))
        {
          tc.SelectedIndexChanged += OnPropertiesTabChanged;
          return;
        }
      }
    }
    catch { /* silently ignore if hierarchy differs across Rhino versions */ }
  }

  private void OnPropertiesTabChanged(object? sender, EventArgs e)
  {
    if (sender is not TabControl tc) return;
    string? title = tc.SelectedTab?.Text?.Trim();
    if (title != null && SafeTabs.Contains(title)) return;
    // Non-safe tab — close O+ (skip floating panels to preserve their position)
    if (Panels.PanelDockBar(PanelGuid) == Guid.Empty) return;
    try { Panels.ClosePanel(PanelGuid); } catch { }
  }

  private void OnDeselectAll(object? sender, RhinoDeselectAllObjectsEventArgs e)
    => CloseIfNothingSelected(e.Document);

  private void OnDeselect(object? sender, RhinoObjectSelectionEventArgs e)
    => CloseIfNothingSelected(e.Document);

  private static void CloseIfNothingSelected(RhinoDoc? doc)
  {
    if (doc == null || doc.Objects.GetSelectedObjects(false, false).Any()) return;
    if (Panels.PanelDockBar(PanelGuid) == Guid.Empty) return;
    try { Panels.ClosePanel(PanelGuid); } catch { }
  }
}

