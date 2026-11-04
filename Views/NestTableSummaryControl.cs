using System.Globalization;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Environment = System.Environment;

namespace vObjectPropertiesPlus.Views;

internal sealed class NestTableSummaryControl : Panel
{
  private const bool DefaultRulerTicks = true;
  private const bool DefaultRulerSizes = true;
  private const bool DefaultRulerGrid = false;
  private const bool DefaultRulerCursor = true;
  private const bool DefaultRulerInches = true;
  private const bool DefaultRulerFeet = false;
  private const bool DefaultRulerYards = true;
  internal sealed record TableMetric(Guid Id, string Name, string Material, double? Yards, long? Parts);
  internal sealed record DisplayRow(string Table, string Parts, string Yards, bool Summary, Guid Id = default);
  private readonly TreeGridView _tree;
  private readonly Image? _editIcon;
  private readonly GridColumn _editColumn;
  private readonly Button _editAllButton;
  private readonly Label _editError = new() { Visible = false, Wrap = WrapMode.Word, TextColor = Color.FromArgb(175, 40, 35) };
  private Func<RhinoDoc, Guid[], bool>? _editPlacedTables;
  private TreeGridItem _treeRoot = new();
  private readonly HashSet<string> _collapsedMaterials = new(StringComparer.OrdinalIgnoreCase);
  private readonly TableHighlightConduit _tableHighlight = new();
  private readonly Dictionary<Guid, RhinoObject> _tableBorders = new();
  private readonly CheckBox _rulerTicks, _rulerSizes, _rulerGrid, _rulerCursor, _rulerInches, _rulerFeet, _rulerYards;
  private readonly StackLayout _rulerOptions;
  private DisplayConduit? _rulerPreview;
  private Action<DisplayConduit, Guid[], bool, bool, bool, bool, bool, bool?, bool>? _setRulerTables;
  private RhinoDoc? _doc;
  private List<RhinoObject> _selection = new();
  private string _signature = "";
  private bool _dirty = true;
  private bool _started;
  private bool _queued;
  private bool _pending;
  private bool _active = true;
  private bool _updating;
  private readonly HashSet<int> _trackedGroups = new();

  internal NestTableSummaryControl(int height, Image? editIcon = null)
  {
    _editIcon = editIcon;
    _editAllButton = new Button { Text = "Edit All", Image = editIcon, Height = 22,
      ToolTip = "Edit all listed tables in vNest", Visible = false };
    _editAllButton.Click += (_, _) => EditTables(null);
    _rulerTicks = RulerToggle("Ticks", "NestTables.RulerTicks", DefaultRulerTicks);
    _rulerSizes = RulerToggle("Sizes", "NestTables.RulerSizes", DefaultRulerSizes);
    _rulerGrid = RulerGridToggle();
    _rulerCursor = RulerToggle("Cursor", "NestTables.RulerCursor", DefaultRulerCursor);
    _rulerCursor.ToolTip = "Show cursor-position arrows and coordinates around the table";
    _rulerInches = RulerToggle("Inches", "NestTables.RulerInches", DefaultRulerInches);
    _rulerFeet = RulerToggle("Feet", "NestTables.RulerFeet", DefaultRulerFeet);
    _rulerYards = RulerToggle("Yards", "NestTables.RulerYards", DefaultRulerYards);
    _rulerOptions = new StackLayout { Spacing = 2, Visible = false, Items =
    {
      new StackLayout { Orientation = Orientation.Horizontal, Spacing = 8, Items = { _rulerTicks, _rulerGrid } },
      new StackLayout { Orientation = Orientation.Horizontal, Spacing = 8, Items = { _rulerSizes, _rulerCursor } },
      new StackLayout { Orientation = Orientation.Horizontal, Spacing = 8, Items = { _rulerInches, _rulerFeet, _rulerYards } }
    } };
    _tree = new TreeGridView { Height = height, Width = 10, ShowHeader = true,
      AllowMultipleSelection = true, AllowEmptySelection = true, GridLines = GridLines.Horizontal };
    _tree.LoadComplete += (_, _) =>
    {
      if (_tree.ControlObject is not System.Windows.Controls.DataGrid native) return;
      var border = (native.Foreground ?? System.Windows.SystemColors.ControlTextBrush).Clone();
      border.Opacity = 0.14;
      native.HorizontalGridLinesBrush = border;
    };
    _tree.Columns.Add(new GridColumn { HeaderText = "Table", Width = 96, DataCell = new TextBoxCell(0) });
    _tree.Columns.Add(new GridColumn { HeaderText = "Parts", Width = 38, DataCell = new TextBoxCell(1) });
    _tree.Columns.Add(new GridColumn { HeaderText = "Yards", Width = 56, Expand = true, DataCell = new TextBoxCell(2) });
    var editCell = new CustomCell
    {
      CreateCell = _ => NewEditButton(),
      ConfigureCell = (e, control) =>
      {
        if (control is not Button button) return;
        button.Tag = e.Item is TreeGridItem item ? item.Tag : null;
        button.Enabled = _started && _editPlacedTables != null && button.Tag is DisplayRow;
        button.ToolTip = button.Tag is DisplayRow row
          ? row.Summary ? $"Edit all {row.Table} tables in vNest" : $"Edit {row.Table} in vNest"
          : "Edit table in vNest";
      }
    };
    _editColumn = new GridColumn { HeaderText = "", Width = 26, DataCell = editCell, Visible = false };
    _tree.Columns.Add(_editColumn);
    _tree.SelectedItemsChanged += (_, _) => UpdateTableHighlight();
    _tree.Expanded += (_, e) => SetMaterialExpanded(e.Item, true);
    _tree.Collapsed += (_, e) => SetMaterialExpanded(e.Item, false);
    var copy = new ButtonMenuItem { Text = "Copy" };
    copy.Click += (_, _) => Clipboard.Instance.Text = string.Join(Environment.NewLine,
      _tree.SelectedItems.OfType<TreeGridItem>().Select(item => item.Tag).OfType<DisplayRow>()
        .Select(row => row.Table + "\t" + row.Parts + "\t" + row.Yards));
    _tree.ContextMenu = new ContextMenu { Items = { copy } };
    Content = new StackLayout { Padding = new Padding(10, 2, 6, 2), Spacing = 3,
      HorizontalContentAlignment = HorizontalAlignment.Stretch, Items =
      { new StackLayout { Orientation = Orientation.Horizontal, HorizontalContentAlignment = HorizontalAlignment.Right,
          Items = { _editAllButton } }, _rulerOptions, _tree, _editError } };
  }

  private Button NewEditButton()
  {
    var button = new Button { Image = _editIcon, Width = 23, Height = 20, ToolTip = "Edit table in vNest" };
    if (_editIcon == null) button.Text = "...";
    button.Click += (_, _) => { if (button.Tag is DisplayRow row) EditTables(row); };
    return button;
  }

  private void UpdateEditAvailability()
  {
    if (_editPlacedTables == null)
    {
      try
      {
        var type = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == "vTools")
          ?.GetType("vTools.Commands.vNest");
        var method = type?.GetMethod("EditPlacedTables", new[] { typeof(RhinoDoc), typeof(Guid[]) });
        _editPlacedTables = method?.CreateDelegate<Func<RhinoDoc, Guid[], bool>>();
      }
      catch (Exception ex) { Log.Write("NestTables", $"vNest table editor unavailable: {ex.Message}"); }
    }
    bool available = _editPlacedTables != null;
    _editColumn.Visible = _editAllButton.Visible = available;
    _editAllButton.Enabled = available && _started && HasTables && _doc != null && RhinoApp.InCommand == 0;
  }

  internal static IEnumerable<Guid> TableIdsForEdit(TreeGridItem root, DisplayRow? scope)
  {
    var materials = root.Children.OfType<TreeGridItem>();
    if (scope == null) return SelectedTableIds(materials);
    if (scope.Summary)
      return SelectedTableIds(materials.Where(item => item.Tag is DisplayRow row
        && row.Table.Equals(scope.Table, StringComparison.OrdinalIgnoreCase)));
    return SelectedTableIds(materials.SelectMany(item => item.Children.OfType<TreeGridItem>())
      .Where(item => item.Tag is DisplayRow row && row.Id == scope.Id));
  }

  private void EditTables(DisplayRow? scope)
  {
    if (!_started || _updating || _doc == null || RhinoApp.InCommand > 0) return;
    _editError.Visible = false;
    try
    {
      UpdateEditAvailability();
      var borders = TableIdsForEdit(_treeRoot, scope).Where(_tableBorders.ContainsKey)
        .Select(id => _tableBorders[id]).Where(border => !border.IsDeleted).Select(border => border.Id).Distinct().ToArray();
      if (borders.Length == 0) return;
      if (_editPlacedTables == null || !_editPlacedTables(_doc, borders))
        throw new InvalidOperationException("vNest could not open these tables. Finish any active nesting operation and check that the tables contain editable grouped parts.");
    }
    catch (Exception ex)
    {
      Log.Write("NestTables", $"Could not edit tables: {ex.Message}");
      _editError.Text = ex.Message;
      _editError.Visible = true;
    }
  }

  private CheckBox RulerToggle(string label, string key, bool fallback)
  {
    bool value = fallback;
    try { value = vObjectPropertiesPlusPlugIn.Instance.Settings.GetBool(key, fallback); }
    catch (Exception ex) { Log.Write("NestTables", $"Cannot read ruler setting: {ex.Message}"); }
    var toggle = new CheckBox { Text = label, Checked = value };
    toggle.CheckedChanged += (_, _) =>
    {
      try
      {
        var plugIn = vObjectPropertiesPlusPlugIn.Instance;
        plugIn.Settings.SetBool(key, toggle.Checked == true);
        plugIn.SaveSettings();
      }
      catch (Exception ex) { Log.Write("NestTables", $"Cannot save ruler setting: {ex.Message}"); }
      UpdateTableRulers();
    };
    return toggle;
  }

  private CheckBox RulerGridToggle()
  {
    bool? state = DefaultRulerGrid ? null : false;
    try
    {
      var settings = vObjectPropertiesPlusPlugIn.Instance.Settings;
      state = settings.GetString("NestTables.RulerGridMode", "") switch
      {
        "Off" => false,
        "3Feet" => null,
        "4Feet" => true,
        _ => settings.GetBool("NestTables.RulerGrid", DefaultRulerGrid) ? null : false
      };
    }
    catch (Exception ex) { Log.Write("NestTables", $"Cannot read grid setting: {ex.Message}"); }
    string Label() => state == false ? "Grid: Off" : state == null ? "Grid: 3 ft" : "Grid: 4 ft";
    var toggle = new CheckBox { ThreeState = true, Checked = state, Text = Label(),
      ToolTip = "Off; 3 ft; or 40 inches followed by 4 ft intervals. Preview only." };
    bool cycling = false;
    toggle.CheckedChanged += (_, _) =>
    {
      if (cycling) return;
      cycling = true;
      try
      {
        state = state == false ? null : state == null ? true : false;
        toggle.Checked = state;
        toggle.Text = Label();
        var plugIn = vObjectPropertiesPlusPlugIn.Instance;
        plugIn.Settings.SetString("NestTables.RulerGridMode", state == false ? "Off" : state == null ? "3Feet" : "4Feet");
        plugIn.Settings.SetBool("NestTables.RulerGrid", state != false);
        plugIn.SaveSettings();
      }
      catch (Exception ex) { Log.Write("NestTables", $"Cannot save grid setting: {ex.Message}"); }
      finally { cycling = false; }
      UpdateTableRulers();
    };
    return toggle;
  }

  private bool EnsureTableRulerPreview()
  {
    if (_rulerPreview != null) return true;
    if (_doc == null) return false;
    try
    {
      var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(item => item.GetName().Name == "vTools");
      var type = assembly?.GetType("vTools.Commands.vNest");
      var create = type?.GetMethod("CreateTableRulerPreview", new[] { typeof(RhinoDoc) });
      var update = type?.GetMethod("UpdateTableRulerPreview", new[] { typeof(DisplayConduit), typeof(Guid[]),
        typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool?), typeof(bool) });
      if (create == null || update == null) return false;
      _setRulerTables = update.CreateDelegate<Action<DisplayConduit, Guid[], bool, bool, bool, bool, bool, bool?, bool>>();
      _rulerPreview = create.CreateDelegate<Func<RhinoDoc, DisplayConduit>>()(_doc);
      return true;
    }
    catch (Exception ex)
    {
      DisposeTableRulers();
      Log.Write("NestTables", $"vNest table rulers unavailable: {ex.Message}");
      return false;
    }
  }

  private void UpdateTableRulers()
  {
    bool redraw = _rulerPreview?.Enabled == true;
    bool available = _started && HasTables && EnsureTableRulerPreview();
    _rulerOptions.Visible = available;
    if (available)
    {
      try
      {
        _setRulerTables!(_rulerPreview!, _tableBorders.Values.Select(border => border.Id).Distinct().ToArray(),
          _rulerTicks.Checked == true, _rulerSizes.Checked == true, _rulerInches.Checked == true,
          _rulerFeet.Checked == true, _rulerYards.Checked == true, _rulerGrid.Checked, _rulerCursor.Checked == true);
      }
      catch (Exception ex)
      {
        DisposeTableRulers();
        Log.Write("NestTables", $"vNest table rulers could not update: {ex.Message}");
      }
    }
    else if (_rulerPreview != null) DisposeTableRulers();
    _rulerInches.Enabled = _rulerFeet.Enabled = _rulerYards.Enabled = _rulerSizes.Checked == true || _rulerCursor.Checked == true;
    if (redraw || _rulerPreview?.Enabled == true) _doc?.Views.Redraw();
  }

  private void DisposeTableRulers()
  {
    bool redraw = _rulerPreview?.Enabled == true;
    if (_rulerPreview is IDisposable disposable) disposable.Dispose();
    else if (_rulerPreview != null) _rulerPreview.Enabled = false;
    _rulerPreview = null;
    _setRulerTables = null;
    _rulerOptions.Visible = false;
    if (redraw) _doc?.Views.Redraw();
  }

  internal Control ResizeTarget => _tree;
  internal bool HasTables { get; private set; }
  internal event EventHandler? TablesChanged;

  internal static TreeGridItem BuildTree(IReadOnlyList<TableMetric> tables)
  {
    var materials = new List<ITreeGridItem>();
    foreach (var material in tables.GroupBy(table => table.Material, StringComparer.OrdinalIgnoreCase)
      .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase))
    {
      long? parts = material.All(table => table.Parts.HasValue) ? material.Sum(table => table.Parts!.Value) : null;
      double? yards = material.All(table => table.Yards.HasValue) ? material.Sum(table => table.Yards!.Value) : null;
      var children = material.OrderBy(table => table.Name, StringComparer.CurrentCultureIgnoreCase)
        .Select(table => new DisplayRow(table.Name, table.Parts?.ToString(CultureInfo.CurrentCulture) ?? "n/a",
          YardText(table.Yards), false, table.Id))
        .Select(row => (ITreeGridItem)new TreeGridItem(new object[] { row.Table, row.Parts, row.Yards }) { Tag = row });
      var total = new DisplayRow(material.Key, parts?.ToString(CultureInfo.CurrentCulture) ?? "n/a", YardText(yards), true);
      materials.Add(new TreeGridItem(children, new object[] { total.Table, total.Parts, total.Yards })
        { Tag = total, Expanded = true });
    }
    return new TreeGridItem(materials, Array.Empty<object>());
  }

  internal static IEnumerable<Guid> SelectedTableIds(IEnumerable<TreeGridItem> items) =>
    items.SelectMany(item => item.Tag is DisplayRow { Summary: true }
      ? item.Children.OfType<TreeGridItem>() : new[] { item })
      .Select(item => item.Tag).OfType<DisplayRow>().Where(row => !row.Summary)
      .Select(row => row.Id).Distinct();

  internal static IEnumerable<TreeGridItem> VisibleItems(TreeGridItem root) =>
    root.Children.OfType<TreeGridItem>().SelectMany(item => item.Expanded
      ? new[] { item }.Concat(item.Children.OfType<TreeGridItem>()) : new[] { item });

  private void SetMaterialExpanded(ITreeGridItem item, bool expanded)
  {
    if (_updating || item is not TreeGridItem { Tag: DisplayRow { Summary: true } row }) return;
    if (expanded) _collapsedMaterials.Remove(row.Table);
    else _collapsedMaterials.Add(row.Table);
  }

  private static string YardText(double? yards) => yards?.ToString("0.###", CultureInfo.CurrentCulture) ?? "n/a";

  internal void Update(RhinoDoc? doc, IReadOnlyList<RhinoObject> selection)
  {
    bool documentChanged = _doc?.RuntimeSerialNumber != doc?.RuntimeSerialNumber;
    if (documentChanged)
    {
      ClearTableHighlight();
      DisposeTableRulers();
      _collapsedMaterials.Clear();
      _dirty = true;
    }
    _doc = doc; _selection = selection.ToList();
    string signature = (doc?.RuntimeSerialNumber ?? 0) + ":"
      + string.Join(",", selection.Select(obj => obj.Id.ToString("N") + ":" + obj.RuntimeSerialNumber));
    if (!_dirty && signature == _signature) return;
    _signature = signature; _dirty = false; _trackedGroups.Clear();
    _editError.Visible = false;
    var selectedRows = documentChanged ? Array.Empty<DisplayRow>()
      : _tree.SelectedItems.OfType<TreeGridItem>().Select(item => item.Tag).OfType<DisplayRow>().ToArray();
    var selectedIds = selectedRows.Where(row => !row.Summary).Select(row => row.Id).ToHashSet();
    var selectedMaterials = selectedRows.Where(row => row.Summary).Select(row => row.Table)
      .ToHashSet(StringComparer.OrdinalIgnoreCase);
    UpdateEditAvailability();
    _updating = true;
    try
    {
      _tableBorders.Clear();
      var tables = doc == null ? new List<TableMetric>() : ReadTables(doc, selection);
      _treeRoot = BuildTree(tables);
      foreach (var material in _treeRoot.Children.OfType<TreeGridItem>())
        if (material.Tag is DisplayRow row) material.Expanded = !_collapsedMaterials.Contains(row.Table);
      _tree.DataStore = _treeRoot;
      _tree.SelectedRows = VisibleItems(_treeRoot).Select((item, index) => (item, index))
        .Where(entry => entry.item.Tag is DisplayRow row
          && (row.Summary ? selectedMaterials.Contains(row.Table) : selectedIds.Contains(row.Id)))
        .Select(entry => entry.index).ToArray();
      HasTables = tables.Count > 0;
    }
    finally { _updating = false; }
    UpdateEditAvailability();
    TablesChanged?.Invoke(this, EventArgs.Empty);
    UpdateTableHighlight();
    UpdateTableRulers();
  }

  internal void SetActive(bool active)
  {
    _active = active;
    UpdateTableHighlight();
  }

  private void UpdateTableHighlight()
  {
    if (_updating) return;
    bool redraw = _tableHighlight.Enabled;
    var borders = new List<RhinoObject>();
    if (_started && _active && _doc != null)
      foreach (var id in SelectedTableIds(_tree.SelectedItems.OfType<TreeGridItem>()))
        if (_tableBorders.TryGetValue(id, out var border)) borders.Add(border);
    _tableHighlight.SetObjects(_doc, borders);
    _tableHighlight.Enabled = borders.Count > 0;
    if (redraw || _tableHighlight.Enabled) _doc?.Views.Redraw();
  }

  private void ClearTableHighlight()
  {
    bool redraw = _tableHighlight.Enabled;
    _tableHighlight.Enabled = false;
    _tableHighlight.SetObjects(null, Array.Empty<RhinoObject>());
    if (redraw) _doc?.Views.Redraw();
  }

  private List<TableMetric> ReadTables(RhinoDoc doc, IReadOnlyList<RhinoObject> selection)
  {
    var roots = new Dictionary<Guid, (RhinoObject Border, int Group, RhinoObject[] Members)>();
    var memberCache = new Dictionary<int, RhinoObject[]>();
    RhinoObject[] Members(int index)
    {
      if (!memberCache.TryGetValue(index, out var members))
        memberCache[index] = members = doc.Groups.GroupMembers(index) ?? Array.Empty<RhinoObject>();
      return members;
    }
    foreach (var obj in selection)
    {
      string? role = obj.Attributes.GetUserString("vNest.role");
      bool border = role == "table-border" || obj.Attributes.Name == "NestTable";
      if (!border && role is not ("table-label" or "table-label-frame")) continue;
      int[] groups = obj.Attributes.GetGroupList() ?? Array.Empty<int>();
      bool found = false;
      foreach (int index in groups)
      {
        if (index < 0 || index >= doc.Groups.Count || doc.Groups[index].IsDeleted) continue;
        var members = Members(index);
        var outline = members.FirstOrDefault(member => member.Geometry is Curve
          && (member.Attributes.GetUserString("vNest.role") == "table-border" || member.Attributes.Name == "NestTable"));
        if (outline == null) continue;
        if (role == null && !(doc.Groups[index].Name?.StartsWith("vNest_", StringComparison.Ordinal) == true
          && doc.Groups[index].Name.Contains("_table_", StringComparison.Ordinal))) continue;
        roots.TryAdd(doc.Groups[index].Id, (outline, index, members));
        _trackedGroups.Add(index); found = true;
      }
      if (!found && role == "table-border" && obj.Geometry is Curve)
        roots.TryAdd(obj.Id, (obj, -1, new[] { obj }));
    }
    var tables = new List<TableMetric>();
    foreach (var root in roots)
    {
      _tableBorders[root.Key] = root.Value.Border;
      try { tables.Add(ReadTable(doc, root.Key, root.Value.Border, root.Value.Group, root.Value.Members)); }
      catch (Exception ex)
      {
        Log.Write("NestTables", $"Table measurement failed: {ex.Message}");
        tables.Add(new TableMetric(root.Key, root.Value.Border.Attributes.GetUserString("vNest.tableName") ?? "Table",
          root.Value.Border.Attributes.GetUserString("vNest.material") ?? "Default", null, null));
      }
    }
    return tables;
  }

  private static TableMetric ReadTable(RhinoDoc doc, Guid id, RhinoObject border, int group, RhinoObject[] members)
  {
    var attributes = border.Attributes;
    string name = group >= 0 ? doc.Groups[group].Name : attributes.GetUserString("vNest.tableName") ?? "Table";
    string material = attributes.GetUserString("vNest.material") ?? "Default";
    if (string.IsNullOrWhiteSpace(material)) material = "Default";
    var parts = PartInstances(doc, members);
    if (parts.Count == 0)
    {
      string? session = attributes.GetUserString("vNest.session");
      string? table = attributes.GetUserString("vNest.table");
      if (Guid.TryParse(session, out _) && !string.IsNullOrEmpty(table))
        parts = PartInstances(doc, (doc.Objects.FindByUserString("vNest.session", session, true,
          false, true, ObjectType.AnyObject) ?? Array.Empty<RhinoObject>())
            .Where(obj => obj.Attributes.GetUserString("vNest.table") == table));
    }
    long? count = parts.Count;
    bool storedCount = long.TryParse(attributes.GetUserString("vNest.partCount"), NumberStyles.Integer,
      CultureInfo.InvariantCulture, out long originalCount) && originalCount >= 0;
    if (parts.Count == 0 && storedCount) count = originalCount;
    Plane? labels = members.Where(obj => obj.Attributes.GetUserString("vNest.role") == "table-label")
      .Select(obj => obj.Geometry).OfType<TextEntity>().Select(text => (Plane?)text.Plane).FirstOrDefault();
    if (!labels.HasValue)
      labels = members.Where(obj => string.IsNullOrEmpty(obj.Attributes.GetUserString("vNest.part")))
        .Select(obj => obj.Geometry).OfType<TextEntity>().Select(text => (Plane?)text.Plane).FirstOrDefault();
    if (border.Geometry is not Curve curve || !TryBody(curve, labels, doc.ModelAbsoluteTolerance,
      out var frame, out double length, out double originX))
    {
      Log.Write("NestTables", $"Cannot resolve stock outline: table={name}, border={border.Id}, parts={count}, labelPlane={labels.HasValue}");
      return new TableMetric(id, name, material, null, count);
    }

    double? occupied = null;
    if (TryLength(attributes, "vNest.occupiedLength", out double used)
      && TryLength(attributes, "vNest.tableLength", out double originalLength) && originalLength > 0
      && (!count.HasValue || count.Value == originalCount))
      occupied = used * length / originalLength;

    if (!occupied.HasValue || !count.HasValue)
    {
      count ??= parts.Count;
      occupied ??= OccupiedLength(doc, parts.Values, frame, originX);
    }
    double? yards = doc.ModelUnitSystem is UnitSystem.None or UnitSystem.CustomUnits ? null
      : occupied * RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Yards);
    return new TableMetric(id, name, material, yards, count);
  }

  private static bool TryLength(ObjectAttributes attributes, string key, out double length) =>
    double.TryParse(attributes.GetUserString(key), NumberStyles.Float, CultureInfo.InvariantCulture, out length)
      && double.IsFinite(length) && length >= 0;

  private static Dictionary<string, List<RhinoObject>> PartInstances(RhinoDoc doc, IEnumerable<RhinoObject> objects)
  {
    var result = new Dictionary<string, List<RhinoObject>>(StringComparer.Ordinal);
    foreach (var obj in objects)
    {
      if (obj.IsDeleted || obj.Geometry is not Curve || !string.IsNullOrEmpty(obj.Attributes.GetUserString("vNest.role"))
        || IsGrain(doc, obj)) continue;
      string? instance = obj.Attributes.GetUserString("vNest.instance");
      if (string.IsNullOrEmpty(instance))
      {
        string? part = obj.Attributes.GetUserString("vNest.part");
        if (string.IsNullOrEmpty(part)) continue;
        instance = part + ":" + obj.Attributes.GetUserString("vNest.copy");
      }
      if (!result.TryGetValue(instance, out var items)) result[instance] = items = new List<RhinoObject>();
      items.Add(obj);
    }
    return result;
  }

  private static bool IsGrain(RhinoDoc doc, RhinoObject obj) =>
    string.Equals(obj.Attributes.Name, "GRAIN", StringComparison.OrdinalIgnoreCase)
    || string.Equals(LeafLayer(doc, obj), "GRAIN", StringComparison.OrdinalIgnoreCase);

  private static string LeafLayer(RhinoDoc doc, RhinoObject obj) => obj.Attributes.LayerIndex >= 0
    && obj.Attributes.LayerIndex < doc.Layers.Count ? doc.Layers[obj.Attributes.LayerIndex].FullPath.Split("::").Last() : "";

  private static double? OccupiedLength(RhinoDoc doc, IEnumerable<List<RhinoObject>> parts, Plane frame, double originX)
  {
    double maximum = originX;
    foreach (var items in parts)
    {
      var closed = items.Where(obj => obj.Geometry is Curve { IsClosed: true } curve
        && curve.TryGetPlane(out _, doc.ModelAbsoluteTolerance)).ToArray();
      var cutting = closed.Where(obj => !LeafLayer(doc, obj).Equals("Reference", StringComparison.OrdinalIgnoreCase)).ToArray();
      if (cutting.Length > 0) closed = cutting;
      if (closed.Length > 0)
      {
        foreach (var obj in closed) maximum = Math.Max(maximum, obj.Geometry.GetBoundingBox(frame).Max.X);
        continue;
      }
      var curves = items.Select(obj => (Curve)obj.Geometry)
        .Where(curve => curve.IsInPlane(frame, doc.ModelAbsoluteTolerance)).ToArray();
      using var regions = Curve.CreateBooleanRegions(curves, frame, true, doc.ModelAbsoluteTolerance);
      if (regions == null || regions.RegionCount == 0) return null;
      for (int i = 0; i < regions.RegionCount; i++)
        foreach (var outline in regions.RegionCurves(i))
        {
          using (outline) maximum = Math.Max(maximum, outline.GetBoundingBox(frame).Max.X);
        }
    }
    return Math.Max(0, maximum - originX);
  }

  internal static bool TryBody(Curve curve, Plane? labelPlane, double tolerance,
    out Plane frame, out double length, out double originX)
  {
    frame = Plane.Unset; length = 0; originX = 0;
    if (!curve.TryGetPolyline(out var polyline) || !polyline.IsClosed || polyline.Count < 5
      || !curve.TryGetPlane(out var plane, tolerance)) return false;
    if (labelPlane.HasValue && TryBodyInFrame(polyline, labelPlane.Value, tolerance,
      out length, out originX))
    {
      frame = labelPlane.Value;
      return true;
    }
    int count = polyline.Count - 1;
    double bestWidth = 0, bestDistance = double.MaxValue;
    for (int i = 0; i < count; i++)
    {
      var a = polyline[(i - 1 + count) % count]; var b = polyline[i];
      var c = polyline[(i + 1) % count]; var d = polyline[(i + 2) % count];
      var previous = b - a; var middle = c - b; var next = d - c;
      double first = previous.Length, width = middle.Length, last = next.Length;
      if (first <= tolerance || width <= tolerance || Math.Abs(first - last) > Math.Max(tolerance * 2, first * 1e-8)) continue;
      previous.Unitize(); middle.Unitize(); next.Unitize();
      if (previous * next > -0.999999 || Math.Abs(previous * middle) > 1e-6) continue;
      if (labelPlane.HasValue && Math.Abs(middle * labelPlane.Value.YAxis) < 0.999999) continue;
      double distance = a.DistanceTo(polyline[0]) + d.DistanceTo(polyline[0]);
      if (width < bestWidth - tolerance || Math.Abs(width - bestWidth) <= tolerance && distance >= bestDistance) continue;
      bestWidth = width; bestDistance = distance;
      frame = labelPlane ?? new Plane(a, previous, Vector3d.CrossProduct(plane.Normal, previous));
      length = (first + last) / 2;
      originX = Math.Min((a - frame.Origin) * frame.XAxis, (d - frame.Origin) * frame.XAxis);
    }
    return length > tolerance;
  }

  private static bool TryBodyInFrame(Polyline polyline, Plane frame, double tolerance,
    out double length, out double originX)
  {
    length = 0; originX = 0;
    double widest = 0;
    var points = polyline.Select(point =>
    {
      var delta = point - frame.Origin;
      return (X: delta * frame.XAxis, Y: delta * frame.YAxis);
    }).ToArray();
    for (int i = 0; i < points.Length - 1; i++)
    {
      var a = points[i]; var b = points[i + 1];
      double width = Math.Abs(a.Y - b.Y);
      if (Math.Abs(a.X - b.X) > tolerance || width <= widest + tolerance) continue;
      double bottom = Math.Min(a.Y, b.Y);
      var edge = points.Where(point => Math.Abs(point.Y - bottom) <= tolerance).ToArray();
      if (edge.Length < 2) continue;
      double minimum = edge.Min(point => point.X), maximum = edge.Max(point => point.X);
      if (maximum - minimum <= tolerance) continue;
      widest = width; originX = minimum; length = maximum - minimum;
    }
    return length > tolerance;
  }

  internal void Start()
  {
    if (_started) return;
    _started = true;
    RhinoDoc.AddRhinoObject += OnObjectChanged; RhinoDoc.DeleteRhinoObject += OnObjectChanged;
    RhinoDoc.UndeleteRhinoObject += OnObjectChanged; RhinoDoc.ReplaceRhinoObject += OnObjectReplaced;
    RhinoDoc.ModifyObjectAttributes += OnAttributesChanged; RhinoDoc.GroupTableEvent += OnGroupChanged;
    Rhino.Commands.Command.EndCommand += OnCommandEnded;
    RhinoDoc.DocumentPropertiesChanged += OnDocumentPropertiesChanged;
    RhinoDoc.CloseDocument += OnDocumentClosed;
    _dirty = true;
    UpdateEditAvailability();
    UpdateTableHighlight();
    UpdateTableRulers();
  }
  internal void Stop()
  {
    ClearTableHighlight();
    DisposeTableRulers();
    if (!_started) return;
    _started = false;
    _editAllButton.Enabled = false;
    RhinoDoc.AddRhinoObject -= OnObjectChanged; RhinoDoc.DeleteRhinoObject -= OnObjectChanged;
    RhinoDoc.UndeleteRhinoObject -= OnObjectChanged; RhinoDoc.ReplaceRhinoObject -= OnObjectReplaced;
    RhinoDoc.ModifyObjectAttributes -= OnAttributesChanged; RhinoDoc.GroupTableEvent -= OnGroupChanged;
    Rhino.Commands.Command.EndCommand -= OnCommandEnded;
    RhinoDoc.DocumentPropertiesChanged -= OnDocumentPropertiesChanged;
    RhinoDoc.CloseDocument -= OnDocumentClosed;
  }
  private void OnObjectChanged(object? sender, RhinoObjectEventArgs e)
  {
    var doc = e.TheObject?.Document ?? _doc;
    if (doc != null) Invalidate(doc, e.TheObject);
  }
  private void OnObjectReplaced(object? sender, RhinoReplaceObjectEventArgs e) => Invalidate(e.Document, e.NewRhinoObject);
  private void OnAttributesChanged(object? sender, RhinoModifyObjectAttributesEventArgs e) => Invalidate(e.Document, e.RhinoObject);
  private void OnGroupChanged(object? sender, Rhino.DocObjects.Tables.GroupTableEventArgs e)
  { if (_trackedGroups.Contains(e.GroupIndex)) Invalidate(e.Document, null); }
  private void Invalidate(RhinoDoc doc, RhinoObject? obj)
  {
    if (_doc?.RuntimeSerialNumber != doc.RuntimeSerialNumber || !HasTables) return;
    if (obj != null && !_tableBorders.Values.Any(border => border.Id == obj.Id)
      && !(obj.Attributes.GetGroupList() ?? Array.Empty<int>()).Any(_trackedGroups.Contains)) return;
    _dirty = true; QueueRefresh();
  }
  private void OnCommandEnded(object? sender, Rhino.Commands.CommandEventArgs e)
  { UpdateEditAvailability(); if (_pending) QueueRefresh(); else if (HasTables) UpdateTableRulers(); }
  private void OnDocumentPropertiesChanged(object? sender, DocumentEventArgs e)
  { if (_doc?.RuntimeSerialNumber == e.DocumentSerialNumber) { _dirty = true; QueueRefresh(); } }
  private void OnDocumentClosed(object? sender, DocumentEventArgs e)
  { if (_doc?.RuntimeSerialNumber == e.DocumentSerialNumber) Update(null, Array.Empty<RhinoObject>()); }
  private void QueueRefresh()
  {
    if (!_started || _queued) return;
    _queued = true;
    Application.Instance.AsyncInvoke(() =>
    {
      _queued = false;
      if (!_started || _doc == null) return;
      if (RhinoApp.InCommand > 0) { _pending = true; return; }
      _pending = false;
      Update(_doc, _selection.Select(obj => _doc.Objects.FindId(obj.Id)).Where(obj => obj != null).Cast<RhinoObject>().ToList());
    });
  }

  private sealed class TableHighlightConduit : DisplayConduit
  {
    private uint _documentSerial;
    private IReadOnlyList<RhinoObject> _borders = Array.Empty<RhinoObject>();

    internal void SetObjects(RhinoDoc? doc, IReadOnlyList<RhinoObject> borders)
    {
      _documentSerial = doc?.RuntimeSerialNumber ?? 0;
      _borders = borders;
    }

    protected override void DrawOverlay(DrawEventArgs e)
    {
      base.DrawOverlay(e);
      if (_documentSerial == 0 || _documentSerial != e.RhinoDoc?.RuntimeSerialNumber) return;
      foreach (var border in _borders)
        if (!border.IsDeleted && !border.IsHidden && border.Geometry is Curve curve)
          e.Display.DrawCurve(curve, System.Drawing.Color.FromArgb(255, 235, 130, 20), 3);
    }
  }
}
