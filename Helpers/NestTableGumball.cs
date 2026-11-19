using Rhino;
using Rhino.ApplicationSettings;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace vObjectPropertiesPlus.Helpers;

internal static class NestTableGumball
{
  private static readonly HashSet<Guid> _frames = new();
  private static uint _documentSerial;
  private static bool _started, _queued, _pending, _restoreOnExit, _changingGumball;

  internal static void Start()
  {
    if (_started) return;
    _started = true;
    RhinoDoc.SelectObjects += OnSelectionChanged;
    RhinoDoc.DeselectObjects += OnSelectionChanged;
    RhinoDoc.DeselectAllObjects += OnSelectionCleared;
    RhinoDoc.ActiveDocumentChanged += OnDocumentChanged;
    RhinoDoc.CloseDocument += OnDocumentClosed;
    RhinoDoc.ModifyObjectAttributes += OnAttributesChanged;
    RhinoDoc.GroupTableEvent += OnGroupChanged;
    Rhino.Commands.Command.EndCommand += OnCommandEnded;
    RhinoApp.AppSettingsChanged += OnSettingsChanged;
    QueueRefresh();
  }

  internal static void Stop()
  {
    if (!_started) return;
    _started = false;
    _pending = false;
    RhinoDoc.SelectObjects -= OnSelectionChanged;
    RhinoDoc.DeselectObjects -= OnSelectionChanged;
    RhinoDoc.DeselectAllObjects -= OnSelectionCleared;
    RhinoDoc.ActiveDocumentChanged -= OnDocumentChanged;
    RhinoDoc.CloseDocument -= OnDocumentClosed;
    RhinoDoc.ModifyObjectAttributes -= OnAttributesChanged;
    RhinoDoc.GroupTableEvent -= OnGroupChanged;
    Rhino.Commands.Command.EndCommand -= OnCommandEnded;
    RhinoApp.AppSettingsChanged -= OnSettingsChanged;
    Reset();
  }

  internal static Guid[] FrameSelection(RhinoDoc doc, IReadOnlyList<RhinoObject> selection)
  {
    var frames = new List<RhinoObject>();
    var labels = new List<RhinoObject>();
    foreach (var obj in selection)
    {
      if (obj.IsDeleted || obj.IsSelected(false) == 0) return Array.Empty<Guid>();
      string? role = obj.Attributes.GetUserString("vNest.role");
      if (obj.Geometry is Curve && (role == "table-border" || role == null
        && obj.Attributes.Name == "NestTable" && TableGroups(doc, obj).Any())) frames.Add(obj);
      else if (role is "table-label" or "table-label-frame" or "table-label-divider") labels.Add(obj);
      else return Array.Empty<Guid>();
    }
    if (frames.Count == 0) return Array.Empty<Guid>();
    if (labels.Count > 0)
    {
      var groups = frames.SelectMany(obj => TableGroups(doc, obj)).ToHashSet();
      if (labels.Any(obj => !TableGroups(doc, obj).Any(groups.Contains))) return Array.Empty<Guid>();
    }
    return frames.Select(obj => obj.Id).Distinct().ToArray();
  }

  private static IEnumerable<int> TableGroups(RhinoDoc doc, RhinoObject obj)
  {
    bool identified = Guid.TryParse(obj.Attributes.GetUserString("vNest.tableGroupId"), out var id);
    foreach (int index in obj.Attributes.GetGroupList() ?? Array.Empty<int>())
    {
      if (index < 0 || index >= doc.Groups.Count || doc.Groups[index].IsDeleted) continue;
      var group = doc.Groups[index];
      if (identified ? group.Id == id : group.Name?.StartsWith("vNest_", StringComparison.Ordinal) == true
        && group.Name.Contains("_table_", StringComparison.Ordinal)) yield return index;
    }
  }

  internal static void UpdateSelection(RhinoDoc? doc, IReadOnlyList<RhinoObject> selection)
  {
    var frames = doc == null ? Array.Empty<Guid>() : FrameSelection(doc, selection);
    if (_documentSerial == (doc?.RuntimeSerialNumber ?? 0) && _frames.SetEquals(frames)) return;
    Reset();
    if (doc == null || frames.Length == 0) return;
    _documentSerial = doc.RuntimeSerialNumber;
    _frames.UnionWith(frames);
    // Hide once per frame selection; settings changes and label selection must not defeat a hotkey override.
    if (ModelAidSettings.AutoGumballEnabled)
    {
      SetGumball(false);
      _restoreOnExit = true;
      Log.Write("NestTables", "Gumball hidden for table-frame selection; the native gumball toggle remains available.");
    }
  }

  private static void Reset()
  {
    bool restore = _restoreOnExit;
    _restoreOnExit = false;
    _frames.Clear();
    _documentSerial = 0;
    if (restore && !ModelAidSettings.AutoGumballEnabled) SetGumball(true);
  }

  private static void SetGumball(bool enabled)
  {
    _changingGumball = true;
    try
    {
      ModelAidSettings.AutoGumballEnabled = enabled;
      RhinoDoc.ActiveDoc?.Views.Redraw();
    }
    finally { _changingGumball = false; }
  }

  private static void OnSettingsChanged(object? sender, EventArgs e)
  {
    if (!_changingGumball && _restoreOnExit && ModelAidSettings.AutoGumballEnabled)
      _restoreOnExit = false;
  }

  private static void OnSelectionChanged(object? sender, RhinoObjectSelectionEventArgs e) => QueueRefresh();
  private static void OnSelectionCleared(object? sender, RhinoDeselectAllObjectsEventArgs e) => QueueRefresh();
  private static void OnDocumentChanged(object? sender, DocumentEventArgs e) => QueueRefresh();
  private static void OnDocumentClosed(object? sender, DocumentEventArgs e)
  {
    if (_documentSerial == e.DocumentSerialNumber) Reset();
  }
  private static void OnAttributesChanged(object? sender, RhinoModifyObjectAttributesEventArgs e)
  {
    if (e.RhinoObject.IsSelected(true) != 0) QueueRefresh();
  }
  private static void OnGroupChanged(object? sender, Rhino.DocObjects.Tables.GroupTableEventArgs e)
  {
    if (_frames.Count > 0) QueueRefresh();
  }
  private static void OnCommandEnded(object? sender, Rhino.Commands.CommandEventArgs e)
  {
    if (_pending) QueueRefresh();
  }

  private static void QueueRefresh()
  {
    if (!_started) return;
    _pending = true;
    if (_queued) return;
    _queued = true;
    Eto.Forms.Application.Instance.AsyncInvoke(() =>
    {
      _queued = false;
      if (!_started || RhinoApp.InCommand > 0) return;
      _pending = false;
      try
      {
        var doc = RhinoDoc.ActiveDoc;
        UpdateSelection(doc, doc?.Objects.GetSelectedObjects(true, true).ToArray() ?? Array.Empty<RhinoObject>());
      }
      catch (Exception ex) { Log.Write("NestTables", $"Could not update table-frame gumball: {ex.Message}"); }
    });
  }
}
