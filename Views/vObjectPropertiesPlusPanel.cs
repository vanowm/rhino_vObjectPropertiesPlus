using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.UI;

namespace vObjectPropertiesPlus.Views;

[SupportedOSPlatform("windows")]
[System.Runtime.InteropServices.Guid("B827CFBD-288D-473A-B31F-E0D36D57F982")]
public sealed class vObjectPropertiesPlusPanel : Panel
{
  private const int LabelWidth = 122;
  private const int ValueWidth = 102;
  private const int NumericValueWidth = 84;
  private const int InfoNumericValueWidth = 64;
  private const int InfoUnitDropWidth = 42;
  private const int InfoFormatDropWidth = 82;
  private const int InfoPrecisionDropWidth = 64;
  private const int RowHeight = 20;
  private const string DocUnitPrefsSection = "vObjectPropertiesPlus.InfoUnits";

  private readonly DropDown _typeDrop;
  private readonly TextBox _nameBox;
  private readonly DropDown _layerDrop;
  private readonly DropDown _displayColorDrop;
  private readonly Button _displayColorButton;
  private readonly DropDown _displayModeDrop;
  private readonly DropDown _linetypeDrop;
  private readonly NumericStepper _linetypeScaleStepper;
  private readonly DropDown _printColorDrop;
  private readonly Button _printColorButton;
  private readonly DropDown _printWidthDrop;
  private readonly DropDown _sectionStyleDrop;
  private readonly Label _hyperlinkLabel;
  private readonly Button _hyperlinkButton;
  private readonly Button _customMeshAdjustButton;

  private readonly CheckBox _customMeshCheck;
  private readonly CheckBox _castsShadowsCheck;
  private readonly CheckBox _receivesShadowsCheck;
  private readonly NumericStepper _densityStepper;
  private readonly CheckBox _showIsocurveCheck;

  private readonly TextBox _totalLengthBox;
  private readonly Label _totalLengthNameLabel;
  private readonly Label _curveMetricLabel;
  private readonly TextBox _curveMetricBox;
  private readonly TextBox _radiusBox;
  private readonly TextBox _diameterBox;
  private readonly DropDown _curveMetricUnitDrop;
  private readonly DropDown _radiusUnitDrop;
  private readonly DropDown _diameterUnitDrop;
  private readonly DropDown _totalLengthUnitDrop;
  private readonly DropDown _infoFormatDrop;
  private readonly DropDown _infoPrecisionDrop;
  private readonly Label _radiusNameLabel;
  private readonly Label _diameterNameLabel;
  private readonly Label _polygonSidesLabel;
  private readonly NumericStepper _polygonSidesStepper;

  // Text section
  private readonly ToggleButton _textAlignLeftBtn, _textAlignCenterBtn, _textAlignRightBtn, _textAlignAutoBtn;
  private readonly ToggleButton _textVAlignTopBtn, _textVAlignMiddleBtn, _textVAlignBottomBtn;
  private readonly ToggleButton _textBoldBtn, _textItalicBtn, _textUnderlineBtn;
  private readonly DropDown _textFontDrop;
  private readonly NumericStepper _textHeightStepper;
  private readonly DropDown _textHeightUnitDrop;
  private readonly TextArea _textContentArea;
  private readonly UITimer _textContentTimer = new UITimer { Interval = 0.4 };
  private readonly StackLayout _infoPlusSection;
  private readonly TableLayout _textSection;

  private readonly Dictionary<string, Image?> _uiIconCache = new(StringComparer.OrdinalIgnoreCase);
  private readonly Dictionary<Guid, bool> _layerExpandedState = new();
  private string _currentLayerFullPath = "-";

  private enum RectangleHighlightKind
  {
    None,
    Width,
    Height
  }

  private readonly RectangleSideHighlightConduit _rectangleSideHighlightConduit = new();
  private RectangleHighlightKind _rectangleHighlightKind = RectangleHighlightKind.None;
  private readonly FocusHighlightConduit _focusHighlightConduit = new();

  private RhinoDoc? _doc;
  private readonly List<Guid> _selectedObjectIds = new();
  private List<RhinoObject> _allSelectedObjects = new();
  private List<RhinoObject?> _typeDropMap = new();
  private List<List<RhinoObject>> _dropdownClusters = new();
  private readonly List<string> _clusterKeyOrder = new();
  private Guid _focusedObjectId = Guid.Empty;
  private bool _isUpdatingUi;
  private long _lastUserEditMs;
  private uint _unitPrefsLoadedDocSerial;

  public vObjectPropertiesPlusPanel()
  {
    _typeDrop = new DropDown { Width = ValueWidth, Height = RowHeight };
    _typeDrop.SelectedIndexChanged += OnTypeDropSelectedIndexChanged;
    _nameBox = NewValueBox();
    _layerDrop = NewReadOnlyDropDown();
    _displayColorDrop = NewReadOnlyDropDown();
    _displayColorButton = NewActionButton("...");
    _displayModeDrop = NewReadOnlyDropDown();
    _linetypeDrop = NewReadOnlyDropDown();
    _linetypeScaleStepper = NewNumericStepper(0.001, 1000000.0, 0.1, 3);
    _printColorDrop = NewReadOnlyDropDown();
    _printColorButton = NewActionButton("...");
    _printWidthDrop = NewReadOnlyDropDown();
    _sectionStyleDrop = NewReadOnlyDropDown();
    _hyperlinkLabel = NewValueLabel();
    _hyperlinkButton = new Button { Text = "...", Width = 28 };
    _customMeshAdjustButton = new Button { Text = "Adjust" };

    _customMeshCheck = NewCheckBox();
    _castsShadowsCheck = NewCheckBox();
    _receivesShadowsCheck = NewCheckBox();
    _densityStepper = NewNumericStepper(-1, 999, 1, 0);
    _showIsocurveCheck = NewCheckBox();

    SetButtonIcon(_displayColorButton, "Layer");
    SetButtonIcon(_printColorButton, "Display_Display_Panel");

    _totalLengthBox = NewSelectableLabelBox();
    _totalLengthNameLabel = new Label { Text = "Total length", Width = LabelWidth };
    _curveMetricLabel = new Label { Text = "Length", Width = LabelWidth };
    _curveMetricBox = NewValueBox();
    _radiusBox = NewValueBox();
    _diameterBox = NewValueBox();
    _curveMetricUnitDrop = NewUnitDropDown();
    _radiusUnitDrop = NewUnitDropDown();
    _diameterUnitDrop = NewUnitDropDown();
    _totalLengthUnitDrop = NewUnitDropDown();
    _infoFormatDrop = NewFormatDropDown();
    _infoPrecisionDrop = NewPrecisionDropDown();

    _radiusNameLabel = new Label { Text = "Radius", Width = LabelWidth };
    _diameterNameLabel = new Label { Text = "Diameter", Width = LabelWidth };
    _polygonSidesLabel = new Label { Text = "Sides", Width = LabelWidth };
    _polygonSidesStepper = NewNumericStepper(3, 360, 1, 0);

    _textFontDrop = new DropDown { Height = RowHeight, Width = ValueWidth };
    foreach (var ff in System.Drawing.FontFamily.Families.OrderBy(f => f.Name))
      _textFontDrop.Items.Add(new ListItem { Text = ff.Name, Key = ff.Name });
    _textHeightStepper = NewNumericStepper(0.0001, 100000, 0.1, 4);
    _textHeightStepper.Width = InfoNumericValueWidth;
    _textHeightUnitDrop = NewUnitDropDown();
    SetUnitDropOptions(_textHeightUnitDrop);
    _textAlignLeftBtn = MakeToggleButton("L");
    _textAlignCenterBtn = MakeToggleButton("C");
    _textAlignRightBtn = MakeToggleButton("R");
    _textAlignAutoBtn = MakeToggleButton("A", 22);
    _textVAlignTopBtn = MakeToggleButton("T");
    _textVAlignMiddleBtn = MakeToggleButton("M");
    _textVAlignBottomBtn = MakeToggleButton("B");
    _textBoldBtn = MakeToggleButton("B");
    _textItalicBtn = MakeToggleButton("I");
    _textUnderlineBtn = MakeToggleButton("U");
    _textContentArea = new TextArea { AcceptsReturn = true, Height = 70 };

    SetUnitDropOptions(_curveMetricUnitDrop);
    SetUnitDropOptions(_radiusUnitDrop);
    SetUnitDropOptions(_diameterUnitDrop);
    SetUnitDropOptions(_totalLengthUnitDrop);
    SetFormatDropOptions(_infoFormatDrop);
    SetPrecisionDropOptions(_infoPrecisionDrop, "decimal", 3);

    _totalLengthBox.Width = InfoNumericValueWidth;
    _curveMetricBox.Width = InfoNumericValueWidth;
    _radiusBox.Width = InfoNumericValueWidth;
    _diameterBox.Width = InfoNumericValueWidth;

    _nameBox.ReadOnly = false;
    _curveMetricBox.ReadOnly = false;
    _radiusBox.ReadOnly = false;
    _diameterBox.ReadOnly = false;

    _nameBox.LostFocus += (_, _) => ApplyName();
    _nameBox.TextChanged += (_, _) => { if (!_isUpdatingUi) _lastUserEditMs = System.Environment.TickCount64; };
    _curveMetricBox.TextChanged += (_, _) => { if (!_isUpdatingUi) _lastUserEditMs = System.Environment.TickCount64; };
    _radiusBox.TextChanged += (_, _) => { if (!_isUpdatingUi) _lastUserEditMs = System.Environment.TickCount64; };
    _diameterBox.TextChanged += (_, _) => { if (!_isUpdatingUi) _lastUserEditMs = System.Environment.TickCount64; };
    _hyperlinkButton.Click += (_, _) => EditHyperlink();
    _customMeshAdjustButton.Click += (_, _) => RhinoApp.RunScript("_CustomMesh", false);
    _displayColorButton.Click += (_, _) => PickDisplayColor();
    _printColorButton.Click += (_, _) => PickPrintColor();
    _linetypeScaleStepper.ValueChanged += (_, _) => ApplyLinetypeScale();
    _densityStepper.ValueChanged += (_, _) => ApplyWireDensity();
    _curveMetricBox.LostFocus += (_, _) => ApplyEditedCurveMetric();
    _radiusBox.LostFocus += (_, _) => ApplyEditedRadius();
    _diameterBox.LostFocus += (_, _) => ApplyEditedDiameter();
    _radiusBox.GotFocus += (_, _) => OnRectangleDimensionFocusChanged(RectangleHighlightKind.Width);
    _diameterBox.GotFocus += (_, _) => OnRectangleDimensionFocusChanged(RectangleHighlightKind.Height);
    _radiusBox.LostFocus += (_, _) => OnRectangleDimensionFocusChanged(RectangleHighlightKind.None);
    _diameterBox.LostFocus += (_, _) => OnRectangleDimensionFocusChanged(RectangleHighlightKind.None);
    _curveMetricUnitDrop.SelectedIndexChanged += (_, _) => OnUnitDropChanged(_curveMetricUnitDrop, "CurveMetric");
    _radiusUnitDrop.SelectedIndexChanged += (_, _) => OnUnitDropChanged(_radiusUnitDrop, "Radius");
    _diameterUnitDrop.SelectedIndexChanged += (_, _) => OnUnitDropChanged(_diameterUnitDrop, "Diameter");
    _totalLengthUnitDrop.SelectedIndexChanged += (_, _) => OnUnitDropChanged(_totalLengthUnitDrop, "TotalLength");
    _infoFormatDrop.SelectedIndexChanged += (_, _) => OnInfoFormatChanged();
    _infoPrecisionDrop.SelectedIndexChanged += (_, _) => OnInfoPrecisionChanged();
    WireSubmitOnEnter(_curveMetricBox, ApplyEditedCurveMetric);
    WireSubmitOnEnter(_radiusBox, ApplyEditedRadius);
    WireSubmitOnEnter(_diameterBox, ApplyEditedDiameter);
    _polygonSidesStepper.ValueChanged += (_, _) => { if (!_isUpdatingUi) ApplyPolygonSides(); };

    _textFontDrop.SelectedIndexChanged += (_, _) => { if (!_isUpdatingUi) ApplyTextFont(); };
    _textHeightStepper.ValueChanged += (_, _) => { if (!_isUpdatingUi) ApplyTextHeight(); };
    _textHeightUnitDrop.SelectedIndexChanged += (_, _) => OnUnitDropChanged(_textHeightUnitDrop, "TextHeight");
    _textAlignLeftBtn.Click += (_, _) => { if (!_isUpdatingUi) ApplyTextHAlignment(TextHorizontalAlignment.Left); };
    _textAlignCenterBtn.Click += (_, _) => { if (!_isUpdatingUi) ApplyTextHAlignment(TextHorizontalAlignment.Center); };
    _textAlignRightBtn.Click += (_, _) => { if (!_isUpdatingUi) ApplyTextHAlignment(TextHorizontalAlignment.Right); };
    _textAlignAutoBtn.Click += (_, _) => { if (!_isUpdatingUi) ApplyTextHAlignment(TextHorizontalAlignment.Auto); };
    _textVAlignTopBtn.Click += (_, _) => { if (!_isUpdatingUi) ApplyTextVAlignment(TextVerticalAlignment.Top); };
    _textVAlignMiddleBtn.Click += (_, _) => { if (!_isUpdatingUi) ApplyTextVAlignment(TextVerticalAlignment.Middle); };
    _textVAlignBottomBtn.Click += (_, _) => { if (!_isUpdatingUi) ApplyTextVAlignment(TextVerticalAlignment.Bottom); };
    _textBoldBtn.Click += (_, _) => { if (!_isUpdatingUi) ApplyTextBold(); };
    _textItalicBtn.Click += (_, _) => { if (!_isUpdatingUi) ApplyTextItalic(); };
    _textUnderlineBtn.Click += (_, _) => { if (!_isUpdatingUi) ApplyTextUnderline(); };
    _textContentTimer.Elapsed += (_, _) => { _textContentTimer.Stop(); ApplyTextContent(); };
    _textContentArea.TextChanged += (_, _) => { if (!_isUpdatingUi) { _textContentTimer.Stop(); _textContentTimer.Start(); } };

    _layerDrop.SelectedIndexChanged += (_, _) => ApplyLayer();
    _displayColorDrop.SelectedIndexChanged += (_, _) => ApplyDisplayColorSource();
    _displayModeDrop.SelectedIndexChanged += (_, _) => ApplyDisplayModeSelection();
    _linetypeDrop.SelectedIndexChanged += (_, _) => ApplyLinetypeSource();
    _printColorDrop.SelectedIndexChanged += (_, _) => ApplyPrintColorSource();
    _printWidthDrop.SelectedIndexChanged += (_, _) => ApplyPlotWeightSource();
    _sectionStyleDrop.SelectedIndexChanged += (_, _) => ApplySectionSource();

    _customMeshCheck.CheckedChanged += (_, _) => ApplyCustomMesh();
    _castsShadowsCheck.CheckedChanged += (_, _) => ApplyCastsShadows();
    _receivesShadowsCheck.CheckedChanged += (_, _) => ApplyReceivesShadows();
    _showIsocurveCheck.CheckedChanged += (_, _) => ApplyShowIsocurve();

    var matchButton = new Button { Text = "Match" };
    matchButton.Click += (_, _) => RhinoApp.RunScript("_MatchProperties", false);

    var detailsButton = new Button { Text = "Details" };
    detailsButton.Click += (_, _) => RhinoApp.RunScript("_What", false);

    _layerDrop.ItemTextBinding = Binding.Property<LayerDropItem, string>(i => i.DisplayText);
    _layerDrop.ItemImageBinding = Binding.Property<LayerDropItem, Image>(i => i.Swatch);

    var objectTable = new TableLayout
    {
      Spacing = new Eto.Drawing.Size(4, 1),
      Padding = new Eto.Drawing.Padding(10, 2, 6, 2),
      Rows =
      {
        NewValueRow("Type", _typeDrop),
        NewValueRow("Name", _nameBox),
        NewValueRow("Layer", _layerDrop),
        NewControlWithButtonRow("Display Color", _displayColorDrop, _displayColorButton),
        NewValueRow("Display Mode", _displayModeDrop),
        NewValueRow("Linetype", _linetypeDrop),
        NewValueRow("Linetype Scale", _linetypeScaleStepper),
        NewControlWithButtonRow("Print Color", _printColorDrop, _printColorButton),
        NewValueRow("Print Width", _printWidthDrop),
        NewValueRow("Section Style", _sectionStyleDrop),
        NewLabelWithButtonRow("Hyperlink", _hyperlinkLabel, _hyperlinkButton),
      }
    };

    var meshTable = new TableLayout
    {
      Spacing = new Eto.Drawing.Size(4, 1),
      Padding = new Eto.Drawing.Padding(10, 2, 6, 2),
      Rows =
      {
        NewCheckRow("Custom Mesh", _customMeshCheck),
        NewControlRow("Settings", _customMeshAdjustButton),
      }
    };

    var renderingTable = new TableLayout
    {
      Spacing = new Eto.Drawing.Size(4, 1),
      Padding = new Eto.Drawing.Padding(10, 2, 6, 2),
      Rows =
      {
        NewCheckRow("Casts shadows", _castsShadowsCheck),
        NewCheckRow("Receives shadows", _receivesShadowsCheck),
      }
    };

    var isocurveTable = new TableLayout
    {
      Spacing = new Eto.Drawing.Size(4, 1),
      Padding = new Eto.Drawing.Padding(10, 2, 6, 2),
      Rows =
      {
        NewValueRow("Density", _densityStepper),
        NewCheckRow("Show surface isocurve", _showIsocurveCheck),
      }
    };

    var plusTable = new TableLayout
    {
      Spacing = new Eto.Drawing.Size(4, 1),
      Padding = new Eto.Drawing.Padding(10, 2, 6, 2),
      Rows =
      {
        NewValueWithUnitDropRow("Unit type", _infoFormatDrop, _infoPrecisionDrop),
        NewDynamicValueWithUnitDropRow(_curveMetricLabel, _curveMetricBox, _curveMetricUnitDrop),
        NewDynamicValueWithUnitDropRow(_radiusNameLabel, _radiusBox, _radiusUnitDrop),
        NewDynamicValueWithUnitDropRow(_diameterNameLabel, _diameterBox, _diameterUnitDrop),
        NewDynamicValueRow(_polygonSidesLabel, _polygonSidesStepper),
        NewDynamicValueWithUnitDropRow(_totalLengthNameLabel, _totalLengthBox, _totalLengthUnitDrop),
      }
    };

    _infoPlusSection = new StackLayout
    {
      Items = { NewSectionLabel("Info+"), plusTable, NewRule() }
    };

    var textTable = new TableLayout
    {
      Spacing = new Eto.Drawing.Size(4, 1),
      Padding = new Eto.Drawing.Padding(10, 2, 6, 2),
      Rows =
      {
        NewValueRow("Font", _textFontDrop),
        NewValueWithUnitDropRow("Height", _textHeightStepper, _textHeightUnitDrop),
      }
    };

    var alignStylePanel = new Panel
    {
      Padding = new Eto.Drawing.Padding(10, 1, 6, 1),
      Content = new StackLayout
      {
        Orientation = Orientation.Horizontal,
        Spacing = 2,
        Items =
        {
          new StackLayoutItem(_textAlignLeftBtn, false),
          new StackLayoutItem(_textAlignCenterBtn, false),
          new StackLayoutItem(_textAlignRightBtn, false),
          new StackLayoutItem(_textAlignAutoBtn, false),
          new StackLayoutItem(new Panel { Width = 6 }, false),
          new StackLayoutItem(_textVAlignTopBtn, false),
          new StackLayoutItem(_textVAlignMiddleBtn, false),
          new StackLayoutItem(_textVAlignBottomBtn, false),
          new StackLayoutItem(new Panel { Width = 12 }, false),
          new StackLayoutItem(_textBoldBtn, false),
          new StackLayoutItem(_textItalicBtn, false),
          new StackLayoutItem(_textUnderlineBtn, false),
        }
      }
    };

    _textSection = new TableLayout
    {
      Visible = false,
      Spacing = new Eto.Drawing.Size(0, 0),
      Rows =
      {
        new TableRow(new TableCell(NewSectionLabel("Text"), true)),
        new TableRow(new TableCell(textTable, true)),
        new TableRow(new TableCell(alignStylePanel, true)),
        new TableRow(new TableCell(new Panel { Content = _textContentArea, Padding = new Eto.Drawing.Padding(10, 2, 6, 2) }, true)),
      }
    };

    Content = new StackLayout
    {
      Spacing = 2,
      Padding = new Eto.Drawing.Padding(0, 2, 0, 2),
      HorizontalContentAlignment = HorizontalAlignment.Stretch,
      Items =
      {
        objectTable,
        _infoPlusSection,
        _textSection,
        NewSectionLabel("Render Mesh Settings"),
        meshTable,
        NewSectionLabel("Rendering"),
        renderingTable,
        NewSectionLabel("Isocurve Density"),
        isocurveTable,
        new StackLayout
        {
          Orientation = Orientation.Horizontal,
          Spacing = 6,
          Padding = new Eto.Drawing.Padding(6, 2, 6, 4),
          Items =
          {
            new StackLayoutItem(matchButton, true),
            new StackLayoutItem(detailsButton, true)
          }
        }
      }
    };

    // Min width: the align+style button row controls (padding 10+6, 4×22btn + 3×2sp + 6 + 3×22btn + 2×2sp + 12 + 3×22btn + 2×2sp)
    MinimumSize = new Size(
      Math.Max(
        10 + 4 * 22 + 3 * 2 + 6 + 3 * 22 + 2 * 2 + 12 + 3 * 22 + 2 * 2 + 6,
        10 + LabelWidth + 4 + ValueWidth + 6),
      0);

    Load += (_, _) => { var d = RhinoDoc.ActiveDoc; if (d != null) RefreshFromDoc(d); };
    RhinoDoc.SelectObjects      += (_, e) => RefreshFromDoc(e.Document);
    RhinoDoc.DeselectObjects    += (_, e) => RefreshFromDoc(e.Document);
    RhinoDoc.DeselectAllObjects += (_, e) => RefreshFromDoc(e.Document);
    RhinoDoc.ModifyObjectAttributes += (_, e) => { if (e.RhinoObject?.IsSelected(false) == 1) RefreshFromDoc(e.Document); };

    SetEmptyState();
  }

  private void RefreshFromDoc(RhinoDoc doc)
  {
    var selected = doc.Objects.GetSelectedObjects(false, false).ToList();
    Application.Instance.AsyncInvoke(() => UpdateFromSelection(doc, selected));
  }

  public void UpdateFromSelection(RhinoDoc? doc, IEnumerable<RhinoObject> objects)
  {
    _doc = doc;
    EnsureDocUnitPrefsLoaded(doc);

    var objectList = objects?.ToList() ?? new List<RhinoObject>();

    // When called with the full selection (not a focused-object drill-down), save it.
    // A full-selection call always comes in with all _selectedObjectIds matching the new list,
    // or when we explicitly pass the full set. We detect a "restore" by checking whether
    // the new list contains all the same IDs as _allSelectedObjects (order-independent).
    bool isFocusDrillDown = _focusedObjectId != Guid.Empty
      && objectList.Count == 1
      && objectList[0].Id == _focusedObjectId;

    // If the incoming selection set matches the recorded full selection, this is a
    // Rhino-internal refresh (e.g. after ModifyAttributes / geometry replace), not a
    // user-initiated selection change.  Preserve focus by redirecting to the focused path.
    if (!isFocusDrillDown && _focusedObjectId != Guid.Empty)
    {
      bool isSameSelectionSet = objectList.Count == _allSelectedObjects.Count
        && objectList.All(o => _allSelectedObjects.Any(a => a.Id == o.Id));
      if (isSameSelectionSet)
      {
        var freshFocused = objectList.FirstOrDefault(o => o.Id == _focusedObjectId);
        if (freshFocused != null)
        {
          UpdateFromSelection(doc, new[] { freshFocused });
          return;
        }
      }
    }

    if (!isFocusDrillDown)
    {
      bool wasHighlighting = _focusHighlightConduit.Enabled;
      _dropdownClusters = BuildDropdownClusters(objectList);
      _allSelectedObjects = _dropdownClusters.SelectMany(c => c).ToList();
      _focusedObjectId = Guid.Empty;
      _selectedObjectIds.Clear();
      foreach (var o in objectList)
        _selectedObjectIds.Add(o.Id);
      _focusHighlightConduit.Clear();
      _focusHighlightConduit.Enabled = false;
      if (wasHighlighting)
        doc?.Views.Redraw();
    }
    else
    {
      // Keep _allSelectedObjects intact; scope edits to focused object only.
      _selectedObjectIds.Clear();
      _selectedObjectIds.Add(_focusedObjectId);
      // Replace stale reference in _allSelectedObjects with the fresh one.
      if (objectList.Count == 1)
      {
        int fi = _allSelectedObjects.FindIndex(o => o.Id == _focusedObjectId);
        if (fi >= 0)
          _allSelectedObjects[fi] = objectList[0];
        // Also refresh stale reference in _dropdownClusters.
        foreach (var cluster in _dropdownClusters)
        {
          int ci = cluster.FindIndex(o => o.Id == _focusedObjectId);
          if (ci >= 0) { cluster[ci] = objectList[0]; break; }
        }
        // Keep conduit pointing at the fresh object so geometry/layer stays current.
        _focusHighlightConduit.SetObject(objectList[0]);
      }
    }

    _isUpdatingUi = true;
    try
    {
      if (objectList.Count == 0)
      {
        SetEmptyState();
        return;
      }

      // Re-enable all controls that were disabled in SetEmptyState
      _typeDrop.Enabled = true;
      _typeDrop.BackgroundColor = SystemColors.Control;
      _typeDrop.TextColor = SystemColors.ControlText;
      _nameBox.ReadOnly = false;
      _nameBox.Enabled = true;
      _nameBox.BackgroundColor = SystemColors.Control;
      _nameBox.TextColor = SystemColors.ControlText;
      _layerDrop.Enabled = true;
      _layerDrop.BackgroundColor = SystemColors.Control;
      _layerDrop.TextColor = SystemColors.ControlText;
      _displayColorDrop.Enabled = true;
      _displayColorDrop.BackgroundColor = SystemColors.Control;
      _displayColorDrop.TextColor = SystemColors.ControlText;
      _displayModeDrop.Enabled = true;
      _displayModeDrop.BackgroundColor = SystemColors.Control;
      _displayModeDrop.TextColor = SystemColors.ControlText;
      _linetypeDrop.Enabled = true;
      _linetypeDrop.BackgroundColor = SystemColors.Control;
      _linetypeDrop.TextColor = SystemColors.ControlText;
      _linetypeScaleStepper.BackgroundColor = SystemColors.Control;
      _linetypeScaleStepper.TextColor = SystemColors.ControlText;
      _printColorDrop.Enabled = true;
      _printColorDrop.BackgroundColor = SystemColors.Control;
      _printColorDrop.TextColor = SystemColors.ControlText;
      _printWidthDrop.Enabled = true;
      _printWidthDrop.BackgroundColor = SystemColors.Control;
      _printWidthDrop.TextColor = SystemColors.ControlText;
      _sectionStyleDrop.Enabled = true;
      _sectionStyleDrop.BackgroundColor = SystemColors.Control;
      _sectionStyleDrop.TextColor = SystemColors.ControlText;
      _hyperlinkButton.Enabled = true;
      _totalLengthBox.Enabled = true;
      _curveMetricUnitDrop.Enabled = true;
      _curveMetricUnitDrop.BackgroundColor = SystemColors.Control;
      _curveMetricUnitDrop.TextColor = SystemColors.ControlText;
      _totalLengthUnitDrop.Enabled = true;
      _totalLengthUnitDrop.BackgroundColor = SystemColors.Control;
      _totalLengthUnitDrop.TextColor = SystemColors.ControlText;
      _infoFormatDrop.Enabled = true;
      _infoFormatDrop.BackgroundColor = SystemColors.Control;
      _infoFormatDrop.TextColor = SystemColors.ControlText;
      _infoPrecisionDrop.Enabled = true;
      _infoPrecisionDrop.BackgroundColor = SystemColors.Control;
      _infoPrecisionDrop.TextColor = SystemColors.ControlText;
      _curveMetricBox.BackgroundColor = SystemColors.Control;
      _curveMetricBox.TextColor = SystemColors.ControlText;
      _radiusBox.BackgroundColor = SystemColors.Control;
      _radiusBox.TextColor = SystemColors.ControlText;
      _diameterBox.BackgroundColor = SystemColors.Control;
      _diameterBox.TextColor = SystemColors.ControlText;
      _densityStepper.BackgroundColor = SystemColors.Control;
      _densityStepper.TextColor = SystemColors.ControlText;
      _textFontDrop.Enabled = true;
      _textFontDrop.BackgroundColor = SystemColors.Control;
      _textFontDrop.TextColor = SystemColors.ControlText;
      _textHeightStepper.BackgroundColor = SystemColors.Control;
      _textHeightStepper.TextColor = SystemColors.ControlText;
      _textHeightUnitDrop.Enabled = true;
      _textHeightUnitDrop.BackgroundColor = SystemColors.Control;
      _textHeightUnitDrop.TextColor = SystemColors.ControlText;
      _textContentArea.Enabled = true;
      _textContentArea.BackgroundColor = SystemColors.Control;
      _textContentArea.TextColor = SystemColors.ControlText;
      _textAlignLeftBtn.Enabled = true;
      _textAlignCenterBtn.Enabled = true;
      _textAlignRightBtn.Enabled = true;
      _textAlignAutoBtn.Enabled = true;
      _textVAlignTopBtn.Enabled = true;
      _textVAlignMiddleBtn.Enabled = true;
      _textVAlignBottomBtn.Enabled = true;
      _textBoldBtn.Enabled = true;
      _textItalicBtn.Enabled = true;
      _textUnderlineBtn.Enabled = true;

      // Populate type dropdown: item 0 = summary, then per-object items with separators between groups.
      var typeItems = new List<string>();
      _typeDropMap = new List<RhinoObject?>();
      typeItems.Add(BuildTypeText(_allSelectedObjects));
      _typeDropMap.Add(null); // index 0 = summary
      if (_allSelectedObjects.Count > 1)
      {
        var dropClusters = _dropdownClusters;
        bool firstCluster = true;
        foreach (var cluster in dropClusters)
        {
          if (!firstCluster)
          {
            typeItems.Add("─────────────");
            _typeDropMap.Add(null);
          }
          firstCluster = false;
          foreach (var o in cluster)
          {
            typeItems.Add(BuildObjectDropLabel(o, doc));
            _typeDropMap.Add(o);
          }
        }
      }
      _typeDrop.DataStore = typeItems;
      int focusedMapIdx = isFocusDrillDown
        ? _typeDropMap.FindIndex(o => o?.Id == _focusedObjectId)
        : -1;
      _typeDrop.SelectedIndex = focusedMapIdx > 0 ? focusedMapIdx : 0;

    _nameBox.Text = CommonOrVaries(objectList, o => SafeString(o.Attributes.Name));
    string layerText = CommonOrVaries(objectList, o => LayerName(doc, o.Attributes.LayerIndex));
    _currentLayerFullPath = layerText;
    SetLayerDropValue(_layerDrop, doc, layerText, _layerExpandedState);
    SetDropValue(_displayColorDrop, NormalizeDisplayColorText(CommonOrVaries(objectList, o => SafeString(o.Attributes.ColorSource.ToString()))),
      "By Layer", "By Parent", "By Material", "Custom...");
    _displayColorButton.Enabled = true;
    UpdateDisplayColorButtonIcon();
    SetDisplayModeDropValue(_displayModeDrop, doc, NormalizeDisplayModeText(CommonOrVaries(objectList, o => (GetPropertyText(o.Attributes, "DisplayModeSource") ?? "By View"))));
    SetLinetypeDropValue(_linetypeDrop, doc, NormalizeLinetypeText(CommonOrVaries(objectList, o => LinetypeText(doc, o.Attributes))));
    string linetypeScaleText = CommonOrVaries(objectList, o => FormatNumber(o.Attributes.LinetypePatternScale));
    _linetypeScaleStepper.Enabled = linetypeScaleText != VariesText;
    if (TryParseDouble(linetypeScaleText, out double lts))
      _linetypeScaleStepper.Value = lts;
    SetDropValue(_printColorDrop, NormalizePrintColorText(CommonOrVaries(objectList, o => SafeString(o.Attributes.PlotColorSource.ToString()))),
      "By Display", "By Layer", "By Parent", "Custom...");
    _printColorButton.Enabled = true;
    UpdatePrintColorButtonIcon();
    SetPrintWidthDropValue(_printWidthDrop, NormalizePrintWidthText(CommonOrVaries(objectList, o => PlotWidthText(o.Attributes))));
    SetDropValue(_sectionStyleDrop, NormalizeSectionStyleText(CommonOrVaries(objectList, o => (GetPropertyText(o.Attributes, "SectionAttributesSource") ?? "By Layer"))),
      "By Layer", "By Parent", "By Clipping Plane", "Custom...");
    _hyperlinkLabel.Text = CommonOrVaries(objectList, o =>
      GetPropertyText(o.Attributes, "Url")
      ?? GetPropertyText(o.Attributes, "m_url")
      ?? string.Empty);

    SetCheckState(_customMeshCheck, CommonBoolOrVaries(objectList, o => HasCustomMesh(o.Attributes)));
    SetCheckState(_castsShadowsCheck, CommonBoolOrVaries(objectList, o => ReadBool(o.Attributes, "CastsShadows") ?? false));
    SetCheckState(_receivesShadowsCheck, CommonBoolOrVaries(objectList, o => ReadBool(o.Attributes, "ReceivesShadows") ?? false));

    bool customMeshApplicable = objectList.All(o => SupportsCustomMesh(o));
    _customMeshCheck.Enabled = customMeshApplicable;
    _customMeshAdjustButton.Enabled = customMeshApplicable;

    bool isoApplicable = objectList.All(SupportsIsocurve);
    string densityText = CommonOrVaries(objectList, o => WireDensityText(o.Attributes));
    bool? showIso = isoApplicable ? CommonBoolOrVaries(objectList, o => ShowIsoFromDensity(o.Attributes)) : false;
    SetCheckState(_showIsocurveCheck, showIso);
    _showIsocurveCheck.Enabled = isoApplicable;
    _densityStepper.Enabled = isoApplicable && showIso == true;
    if (TryParseDouble(densityText, out double densityValue))
      _densityStepper.Value = densityValue;
    else
      _densityStepper.Value = -1;

    List<Curve> infoCurves = GetInfoCurvesForSelection(objectList, out bool hasSegmentSelection);
    UpdateTextSection(objectList, doc);
    _curveMetricBox.ReadOnly = hasSegmentSelection;
    _radiusBox.ReadOnly = hasSegmentSelection;
    _diameterBox.ReadOnly = hasSegmentSelection;

    int curveCount = 0;
    double totalCurveLength = 0.0;

    var circularRadii = new List<double>();
    int circleCount = 0;
    int arcCount = 0;
    var ellipseAxes = new List<(double a, double b)>();
    int ellipseCount = 0;
    var polygonInfoData = new List<(int sides, double circumRadius)>();
    int polygonCount = 0;
    var rectangleSizes = new List<(double width, double height)>();
    double rectangleDistanceTolerance = Math.Max((doc?.ModelAbsoluteTolerance ?? RhinoMath.SqrtEpsilon) * 2.0, 1e-8);
    double rectangleAngleToleranceRadians = RhinoMath.ToRadians(2.0);
    Vector3d horizontalReference = GetHorizontalReference(doc);

    foreach (Curve curve in infoCurves)
    {
      curveCount++;

      totalCurveLength += curve.GetLength();

      if (curve.TryGetCircle(out Circle circle))
      {
        circularRadii.Add(circle.Radius);
        circleCount++;
      }
      else if (curve.TryGetArc(out Arc arc))
      {
        circularRadii.Add(arc.Radius);
        arcCount++;
      }
      else if (curve.TryGetEllipse(out Ellipse ellipse))
      {
        double a = Math.Max(ellipse.Radius1, ellipse.Radius2);
        double b = Math.Min(ellipse.Radius1, ellipse.Radius2);
        ellipseAxes.Add((a, b));
        ellipseCount++;
      }

      if (TryGetRectangleDimensions(curve, rectangleDistanceTolerance, rectangleAngleToleranceRadians, horizontalReference, out double rectWidth, out double rectHeight))
        rectangleSizes.Add((rectWidth, rectHeight));
      else if (curve.TryGetPolyline(out Polyline polyPl) && polyPl.IsValid
        && TryGetRegularPolygon(polyPl, rectangleDistanceTolerance,
          out int polySides, out _, out _, out _, out double polyCircumR, out _))
      {
        polygonInfoData.Add((polySides, polyCircumR));
        polygonCount++;
      }
    }

    UnitSystem modelUnits = doc?.ModelUnitSystem ?? UnitSystem.None;
    UnitSystem totalLengthUnits = GetSelectedUnitSystem(_totalLengthUnitDrop, doc);
    UnitSystem curveMetricUnits = GetSelectedUnitSystem(_curveMetricUnitDrop, doc);
    UnitSystem radiusUnits = GetSelectedUnitSystem(_radiusUnitDrop, doc);
    UnitSystem diameterUnits = GetSelectedUnitSystem(_diameterUnitDrop, doc);

    _totalLengthBox.Text = curveCount == 0
      ? "-"
      : FormatInfoNumber(ConvertLength(totalCurveLength, modelUnits, totalLengthUnits), _totalLengthUnitDrop);

    bool hasCircle = circleCount > 0;
    bool hasArc = arcCount > 0;
    bool hasCircular = hasCircle || hasArc;
    bool hasEllipse = ellipseCount > 0 && !hasCircular;

    bool hasRectangleOnly = !hasSegmentSelection
      && !hasCircular
      && !hasEllipse
      && curveCount > 0
      && rectangleSizes.Count == curveCount
      && objectList.All(o => o.Geometry is Curve);

    bool hasPolygonOnly = !hasSegmentSelection
      && !hasCircular
      && !hasEllipse
      && !hasRectangleOnly
      && polygonCount > 0
      && polygonCount == curveCount
      && objectList.All(o => o.Geometry is Curve);

    _totalLengthNameLabel.Text = (hasRectangleOnly || hasPolygonOnly) ? "Perimeter" : "Total length";

    if (hasRectangleOnly)
    {
      _radiusNameLabel.Text = "Width";
      _diameterNameLabel.Text = "Height";
    }
    else if (hasEllipse)
    {
      _radiusNameLabel.Text = "A";
      _diameterNameLabel.Text = "B";
    }
    else if (hasPolygonOnly)
    {
      _radiusNameLabel.Text = "Outer R";
      _diameterNameLabel.Text = "Inner R";
    }
    else
    {
      _radiusNameLabel.Text = "Radius";
      _diameterNameLabel.Text = "Diameter";
    }

    bool showSecondaryMetrics = hasCircular || hasRectangleOnly || hasEllipse || hasPolygonOnly;
    _radiusNameLabel.Visible = showSecondaryMetrics;
    _radiusBox.Visible = showSecondaryMetrics;
    _radiusUnitDrop.Visible = showSecondaryMetrics;
    _diameterNameLabel.Visible = showSecondaryMetrics;
    _diameterBox.Visible = showSecondaryMetrics;
    _diameterUnitDrop.Visible = showSecondaryMetrics;
    _polygonSidesLabel.Visible = hasPolygonOnly;
    _polygonSidesStepper.Visible = hasPolygonOnly;

    bool showCurveMetric = !hasRectangleOnly && !hasPolygonOnly;
    _curveMetricLabel.Visible = showCurveMetric;
    _curveMetricBox.Visible = showCurveMetric;
    _curveMetricUnitDrop.Visible = showCurveMetric;

    bool hasNonCircularCurve = infoCurves.Any(c => !c.TryGetCircle(out _) && !c.TryGetArc(out _));
    _curveMetricLabel.Text = hasCircle && !hasArc && !hasNonCircularCurve
      ? "Circumference"
      : hasArc && !hasCircle && !hasNonCircularCurve
        ? "Arc length"
        : hasCircular
          ? "Len/Circum"
          : "Length";

    var curveLengths = infoCurves
      .Select(c => ConvertLength(c.GetLength(), modelUnits, curveMetricUnits))
      .ToList();

    _curveMetricBox.Enabled = showCurveMetric && curveLengths.Count > 0;
    if (!showCurveMetric)
    {
      SetEditableTextValue(_curveMetricBox, "-");
    }
    else if (curveLengths.Count == 0)
    {
      SetEditableTextValue(_curveMetricBox, "-");
    }
    else
    {
      bool lengthsSame = curveLengths.All(l => RhinoMath.EpsilonEquals(l, curveLengths[0], RhinoMath.SqrtEpsilon));
      SetEditableTextValue(_curveMetricBox, lengthsSame ? FormatInfoNumber(curveLengths[0], _curveMetricUnitDrop) : VariesText);
    }

    if (!showSecondaryMetrics)
    {
      SetEditableTextValue(_radiusBox, "-");
      SetEditableTextValue(_diameterBox, "-");
      _radiusBox.Enabled = false;
      _diameterBox.Enabled = false;
      EnsureSegmentReadOnlyInfoBoxesAreSelectable(hasSegmentSelection);
      RefreshRectangleSideHighlight();
      return;
    }

    if (hasRectangleOnly)
    {
      _radiusBox.Enabled = true;
      _diameterBox.Enabled = true;

      var displayWidths = rectangleSizes.Select(s => ConvertLength(s.width, modelUnits, radiusUnits)).ToList();
      var displayHeights = rectangleSizes.Select(s => ConvertLength(s.height, modelUnits, diameterUnits)).ToList();

      bool widthsSame = displayWidths.All(w => RhinoMath.EpsilonEquals(w, displayWidths[0], RhinoMath.SqrtEpsilon));
      bool heightsSame = displayHeights.All(h => RhinoMath.EpsilonEquals(h, displayHeights[0], RhinoMath.SqrtEpsilon));

      SetEditableTextValue(_radiusBox, widthsSame ? FormatInfoNumber(displayWidths[0], _radiusUnitDrop) : VariesText);
      SetEditableTextValue(_diameterBox, heightsSame ? FormatInfoNumber(displayHeights[0], _diameterUnitDrop) : VariesText);
      EnsureSegmentReadOnlyInfoBoxesAreSelectable(hasSegmentSelection);
      RefreshRectangleSideHighlight();
      return;
    }

    if (hasEllipse)
    {
      _radiusBox.Enabled = true;
      _diameterBox.Enabled = true;

      var displayA = ellipseAxes.Select(e => ConvertLength(e.a, modelUnits, radiusUnits)).ToList();
      var displayB = ellipseAxes.Select(e => ConvertLength(e.b, modelUnits, diameterUnits)).ToList();

      bool aSame = displayA.All(v => RhinoMath.EpsilonEquals(v, displayA[0], RhinoMath.SqrtEpsilon));
      bool bSame = displayB.All(v => RhinoMath.EpsilonEquals(v, displayB[0], RhinoMath.SqrtEpsilon));

      SetEditableTextValue(_radiusBox, aSame ? FormatInfoNumber(displayA[0], _radiusUnitDrop) : VariesText);
      SetEditableTextValue(_diameterBox, bSame ? FormatInfoNumber(displayB[0], _diameterUnitDrop) : VariesText);
      EnsureSegmentReadOnlyInfoBoxesAreSelectable(hasSegmentSelection);
      RefreshRectangleSideHighlight();
      return;
    }

    if (hasPolygonOnly)
    {
      _polygonSidesStepper.Value = polygonInfoData[0].sides;
      _polygonSidesStepper.Enabled = true;
      _radiusBox.Enabled = true;
      _diameterBox.Enabled = true;

      var displayOuterR = polygonInfoData
        .Select(p => ConvertLength(p.circumRadius, modelUnits, radiusUnits))
        .ToList();
      var displayInnerR = polygonInfoData
        .Select(p => ConvertLength(p.circumRadius * Math.Cos(Math.PI / p.sides), modelUnits, diameterUnits))
        .ToList();

      bool outerSame = displayOuterR.All(r => RhinoMath.EpsilonEquals(r, displayOuterR[0], RhinoMath.SqrtEpsilon));
      bool innerSame = displayInnerR.All(r => RhinoMath.EpsilonEquals(r, displayInnerR[0], RhinoMath.SqrtEpsilon));

      SetEditableTextValue(_radiusBox, outerSame ? FormatInfoNumber(displayOuterR[0], _radiusUnitDrop) : VariesText);
      SetEditableTextValue(_diameterBox, innerSame ? FormatInfoNumber(displayInnerR[0], _diameterUnitDrop) : VariesText);
      EnsureSegmentReadOnlyInfoBoxesAreSelectable(hasSegmentSelection);
      RefreshRectangleSideHighlight();
      return;
    }

    _radiusBox.Enabled = true;
    _diameterBox.Enabled = true;
    var displayRadii = circularRadii.Select(r => ConvertLength(r, modelUnits, radiusUnits)).ToList();
    var displayDiameters = circularRadii.Select(r => ConvertLength(r * 2.0, modelUnits, diameterUnits)).ToList();
    bool radiiSame = displayRadii.All(r => RhinoMath.EpsilonEquals(r, displayRadii[0], RhinoMath.SqrtEpsilon));
    bool diametersSame = displayDiameters.All(d => RhinoMath.EpsilonEquals(d, displayDiameters[0], RhinoMath.SqrtEpsilon));
    if (!radiiSame)
    {
      SetEditableTextValue(_radiusBox, VariesText);
    }
    else
    {
      double radius = displayRadii[0];
      SetEditableTextValue(_radiusBox, FormatInfoNumber(radius, _radiusUnitDrop));
    }

    if (!diametersSame)
    {
      SetEditableTextValue(_diameterBox, VariesText);
      EnsureSegmentReadOnlyInfoBoxesAreSelectable(hasSegmentSelection);
      return;
    }

    SetEditableTextValue(_diameterBox, FormatInfoNumber(displayDiameters[0], _diameterUnitDrop));
    EnsureSegmentReadOnlyInfoBoxesAreSelectable(hasSegmentSelection);
    RefreshRectangleSideHighlight();
    }
    finally
    {
      _isUpdatingUi = false;
    }
  }

  private void SetEmptyState()
  {
    _typeDrop.DataStore = new[] { "No selection" };
    _typeDrop.SelectedIndex = 0;
    _typeDrop.Enabled = false;
    _typeDrop.BackgroundColor = SystemColors.ControlBackground;
    _typeDrop.TextColor = SystemColors.DisabledText;
    _allSelectedObjects.Clear();
    _focusedObjectId = Guid.Empty;
    _selectedObjectIds.Clear();
    _nameBox.Text = "";
    _nameBox.ReadOnly = true;
    _nameBox.Enabled = false;
    _nameBox.BackgroundColor = SystemColors.ControlBackground;
    _nameBox.TextColor = SystemColors.DisabledText;
    _currentLayerFullPath = "-";
    SetLayerDropValue(_layerDrop, null, "-", _layerExpandedState);
    _layerDrop.Enabled = false;
    _layerDrop.BackgroundColor = SystemColors.ControlBackground;
    _layerDrop.TextColor = SystemColors.DisabledText;
    SetDropValue(_displayColorDrop, "-", "By Layer", "By Parent", "By Material", "Custom...");
    _displayColorDrop.Enabled = false;
    _displayColorDrop.BackgroundColor = SystemColors.ControlBackground;
    _displayColorDrop.TextColor = SystemColors.DisabledText;
    SetDisplayModeDropValue(_displayModeDrop, _doc, "-");
    _displayModeDrop.Enabled = false;
    _displayModeDrop.BackgroundColor = SystemColors.ControlBackground;
    _displayModeDrop.TextColor = SystemColors.DisabledText;
    SetLinetypeDropValue(_linetypeDrop, _doc, "-");
    _linetypeDrop.Enabled = false;
    _linetypeDrop.BackgroundColor = SystemColors.ControlBackground;
    _linetypeDrop.TextColor = SystemColors.DisabledText;
    _linetypeScaleStepper.Value = 1;
    _linetypeScaleStepper.Enabled = false;
    _linetypeScaleStepper.BackgroundColor = SystemColors.ControlBackground;
    _linetypeScaleStepper.TextColor = SystemColors.DisabledText;
    SetDropValue(_printColorDrop, "-", "By Display", "By Layer", "By Parent", "Custom...");
    _printColorDrop.Enabled = false;
    _printColorDrop.BackgroundColor = SystemColors.ControlBackground;
    _printColorDrop.TextColor = SystemColors.DisabledText;
    SetPrintWidthDropValue(_printWidthDrop, "-");
    _printWidthDrop.Enabled = false;
    _printWidthDrop.BackgroundColor = SystemColors.ControlBackground;
    _printWidthDrop.TextColor = SystemColors.DisabledText;
    SetDropValue(_sectionStyleDrop, "-", "By Layer", "By Parent", "By Clipping Plane", "Custom...");
    _sectionStyleDrop.Enabled = false;
    _sectionStyleDrop.BackgroundColor = SystemColors.ControlBackground;
    _sectionStyleDrop.TextColor = SystemColors.DisabledText;
    _hyperlinkLabel.Text = "";
    _hyperlinkButton.Enabled = false;
    _displayColorButton.Enabled = false;
    SetButtonIcon(_displayColorButton, "Layer");
    _printColorButton.Enabled = false;
    SetButtonIcon(_printColorButton, "Display_Display_Panel");

    SetCheckState(_customMeshCheck, null);
    _customMeshCheck.Enabled = false;
    _customMeshAdjustButton.Enabled = false;
    SetCheckState(_castsShadowsCheck, null);
    _castsShadowsCheck.Enabled = false;
    SetCheckState(_receivesShadowsCheck, null);
    _receivesShadowsCheck.Enabled = false;
    _densityStepper.Value = -1;
    _densityStepper.Enabled = false;
    _densityStepper.BackgroundColor = SystemColors.ControlBackground;
    _densityStepper.TextColor = SystemColors.DisabledText;
    SetCheckState(_showIsocurveCheck, null);
    _showIsocurveCheck.Enabled = false;

    _totalLengthBox.Text = "-";
    _totalLengthBox.Enabled = false;
    _totalLengthNameLabel.Text = "Total length";
    _curveMetricLabel.Text = "Length";
    _curveMetricLabel.Visible = true;
    _curveMetricBox.Visible = true;
    _curveMetricUnitDrop.Visible = true;
    _curveMetricUnitDrop.Enabled = false;
    _curveMetricUnitDrop.BackgroundColor = SystemColors.ControlBackground;
    _curveMetricUnitDrop.TextColor = SystemColors.DisabledText;
    _totalLengthUnitDrop.Enabled = false;
    _totalLengthUnitDrop.BackgroundColor = SystemColors.ControlBackground;
    _totalLengthUnitDrop.TextColor = SystemColors.DisabledText;
    _infoFormatDrop.Enabled = false;
    _infoFormatDrop.BackgroundColor = SystemColors.ControlBackground;
    _infoFormatDrop.TextColor = SystemColors.DisabledText;
    _infoPrecisionDrop.Enabled = false;
    _infoPrecisionDrop.BackgroundColor = SystemColors.ControlBackground;
    _infoPrecisionDrop.TextColor = SystemColors.DisabledText;
    _curveMetricBox.ReadOnly = true;
    _radiusNameLabel.Text = "Radius";
    _diameterNameLabel.Text = "Diameter";
    SetEditableTextValue(_curveMetricBox, "-");
    _curveMetricBox.Enabled = false;
    _curveMetricBox.BackgroundColor = SystemColors.ControlBackground;
    _curveMetricBox.TextColor = SystemColors.DisabledText;
    SetEditableTextValue(_radiusBox, "-");
    _radiusBox.Enabled = false;
    _radiusBox.BackgroundColor = SystemColors.ControlBackground;
    _radiusBox.TextColor = SystemColors.DisabledText;
    SetEditableTextValue(_diameterBox, "-");
    _diameterBox.Enabled = false;
    _diameterBox.BackgroundColor = SystemColors.ControlBackground;
    _diameterBox.TextColor = SystemColors.DisabledText;
    _radiusNameLabel.Visible = false;
    _radiusBox.Visible = false;
    _radiusBox.ReadOnly = true;
    _radiusUnitDrop.Visible = false;
    _diameterNameLabel.Visible = false;
    _diameterBox.Visible = false;
    _diameterBox.ReadOnly = true;
    _diameterUnitDrop.Visible = false;
    _polygonSidesLabel.Visible = false;
    _polygonSidesStepper.Visible = false;
    DisableRectangleSideHighlight();

    _infoPlusSection.Visible = true;
    _textSection.Visible = false;
    _textFontDrop.SelectedIndex = -1;
    _textFontDrop.Enabled = false;
    _textFontDrop.BackgroundColor = SystemColors.ControlBackground;
    _textFontDrop.TextColor = SystemColors.DisabledText;
    _textHeightStepper.Enabled = false;
    _textHeightStepper.BackgroundColor = SystemColors.ControlBackground;
    _textHeightStepper.TextColor = SystemColors.DisabledText;
    _textHeightUnitDrop.Enabled = false;
    _textHeightUnitDrop.BackgroundColor = SystemColors.ControlBackground;
    _textHeightUnitDrop.TextColor = SystemColors.DisabledText;
    _textContentArea.Text = "";
    _textContentArea.Enabled = false;
    _textContentArea.BackgroundColor = SystemColors.ControlBackground;
    _textContentArea.TextColor = SystemColors.DisabledText;
    _textContentArea.Font = new Eto.Drawing.Font(SystemFont.Default, 9f);
    _textContentArea.TextAlignment = TextAlignment.Left;
    _textAlignLeftBtn.Checked = false;
    _textAlignLeftBtn.Enabled = false;
    _textAlignCenterBtn.Checked = false;
    _textAlignCenterBtn.Enabled = false;
    _textAlignRightBtn.Checked = false;
    _textAlignRightBtn.Enabled = false;
    _textAlignAutoBtn.Checked = false;
    _textAlignAutoBtn.Enabled = false;
    _textVAlignTopBtn.Checked = false;
    _textVAlignTopBtn.Enabled = false;
    _textVAlignMiddleBtn.Checked = false;
    _textVAlignMiddleBtn.Enabled = false;
    _textVAlignBottomBtn.Checked = false;
    _textVAlignBottomBtn.Enabled = false;
    _textBoldBtn.Checked = false;
    _textBoldBtn.Enabled = false;
    _textItalicBtn.Checked = false;
    _textItalicBtn.Enabled = false;
    _textUnderlineBtn.Checked = false;
    _textUnderlineBtn.Enabled = false;
  }

  private void EnsureSegmentReadOnlyInfoBoxesAreSelectable(bool hasSegmentSelection)
  {
    if (!hasSegmentSelection)
      return;

    if (_curveMetricBox.Visible)
      _curveMetricBox.Enabled = true;
    if (_radiusBox.Visible)
      _radiusBox.Enabled = true;
    if (_diameterBox.Visible)
      _diameterBox.Enabled = true;
  }

  private const string VariesText = "(varies)";

  private static string BuildTypeText(IReadOnlyList<RhinoObject> objects)
  {
    if (objects.Count == 0)
      return "-";

    if (objects.Count == 1)
      return TypeName(objects[0]);

    string firstName = TypeName(objects[0]);
    bool allSameType = objects.All(o => TypeName(o) == firstName);
    if (allSameType)
      return $"{objects.Count} {Pluralize(firstName, objects.Count)}";

    return "varies";
  }

  private static string TypeName(RhinoObject obj)
  {
    if (obj.ObjectType == ObjectType.Curve)
      return obj.Geometry is Curve c ? CurveTypeName(c) : "curve";

    return obj.Geometry switch
    {
      TextEntity    => "text",
      TextDot       => "text dot",
      AngularDimension => "angle dim",
      RadialDimension  => "radial dim",
      Centermark    => "centermark",
      Dimension     => "dimension",
      Leader        => "leader",
      _             => obj.ObjectType.ToString().Replace("_", " ").ToLowerInvariant()
    };
  }

  private static string CurveTypeName(Curve c)
  {
    // Circle (check before arc — circles satisfy TryGetArc too).
    if (c.TryGetCircle(out _))
      return "circle";

    // Arc.
    if (c.TryGetArc(out _))
      return "arc";

    // Full ellipse.
    if (c.IsClosed && c.TryGetEllipse(out _))
      return "ellipse";

    // Polyline / line / rectangle / square.
    if (c.TryGetPolyline(out Polyline pl) && pl.IsValid)
    {
      if (pl.SegmentCount == 1)
        return "line";

      if (pl.IsClosed && pl.SegmentCount == 4 && IsPolylineRectangle(pl))
        return IsPolylineSquare(pl) ? "square" : "rectangle";

      if (pl.IsClosed && pl.SegmentCount >= 3
        && TryGetRegularPolygon(pl, RhinoMath.SqrtEpsilon * 100,
          out _, out _, out _, out _, out _, out _))
        return "polygon";

      return "polyline";
    }

    // LineCurve fallback.
    if (c is LineCurve)
      return "line";

    return c.IsClosed ? "closed curve" : "curve";
  }

  private static bool IsPolylineRectangle(Polyline pl)
  {
    // pl is closed with 4 segments; check all interior angles are 90 degrees.
    for (int i = 0; i < 4; i++)
    {
      Vector3d v1 = pl[(i + 1) % 4] - pl[i];
      Vector3d v2 = pl[(i + 2) % 4] - pl[(i + 1) % 4];
      double len1 = v1.Length;
      double len2 = v2.Length;
      if (len1 < RhinoMath.SqrtEpsilon || len2 < RhinoMath.SqrtEpsilon)
        return false;
      double cosAngle = (v1 * v2) / (len1 * len2);
      if (Math.Abs(cosAngle) > 1e-6)
        return false;
    }
    return true;
  }

  private static bool IsPolylineSquare(Polyline pl)
  {
    double l0 = pl[0].DistanceTo(pl[1]);
    double l1 = pl[1].DistanceTo(pl[2]);
    double l2 = pl[2].DistanceTo(pl[3]);
    double l3 = pl[3].DistanceTo(pl[0]);
    double avg = (l0 + l1 + l2 + l3) / 4.0;
    if (avg < RhinoMath.SqrtEpsilon)
      return false;
    double tol = avg * 1e-6;
    return Math.Abs(l0 - l1) < tol && Math.Abs(l1 - l2) < tol && Math.Abs(l2 - l3) < tol;
  }

  private static string Pluralize(string value, int count)
  {
    if (count == 1)
      return value;
    if (value.EndsWith("s", StringComparison.OrdinalIgnoreCase))
      return value;
    return value + "s";
  }

  // Build clusters of objects for the type dropdown.
  // Objects sharing a Rhino group index are kept together; cluster order is maintained
  // across selection changes: known clusters keep their previous relative order,
  // newly-seen clusters are appended at the end in document order.
  // Within each cluster, objects are sorted alphabetically by type name.
  private List<List<RhinoObject>> BuildDropdownClusters(List<RhinoObject> objects)
  {
    if (objects.Count == 0) return new();

    var clusters = new List<List<RhinoObject>>();
    var groupMap = new Dictionary<int, List<RhinoObject>>();

    foreach (var obj in objects)
    {
      int[]? groups = obj.Attributes.GetGroupList();
      if (groups != null && groups.Length > 0)
      {
        int gIdx = groups[0];
        if (!groupMap.TryGetValue(gIdx, out var cluster))
        {
          cluster = new List<RhinoObject>();
          groupMap[gIdx] = cluster;
          clusters.Add(cluster);
        }
        cluster.Add(obj);
      }
      else
      {
        clusters.Add(new List<RhinoObject> { obj });
      }
    }

    // Sort each cluster internally by type name.
    foreach (var cluster in clusters)
      cluster.Sort((a, b) => string.Compare(TypeName(a), TypeName(b), StringComparison.Ordinal));

    // Order clusters: known clusters (seen in a previous selection) keep their
    // previous relative order; new clusters are appended at the end in document order.
    var ordered = clusters
      .Select((c, docIdx) => (cluster: c, docIdx, keyIdx: _clusterKeyOrder.IndexOf(ClusterKey(c))))
      .OrderBy(t => t.keyIdx >= 0 ? t.keyIdx : int.MaxValue)
      .ThenBy(t => t.docIdx)
      .Select(t => t.cluster)
      .ToList();

    // Persist order for the next call.
    _clusterKeyOrder.Clear();
    _clusterKeyOrder.AddRange(ordered.Select(ClusterKey));

    return ordered;
  }

  private static string ClusterKey(List<RhinoObject> cluster)
  {
    int[]? groups = cluster[0].Attributes.GetGroupList();
    return groups != null && groups.Length > 0 ? $"g:{groups[0]}" : $"o:{cluster[0].Id}";
  }

  private static string BuildObjectDropLabel(RhinoObject obj, RhinoDoc? doc)
  {
    string type = TypeName(obj);
    string name = obj.Attributes.Name;
    string info = ObjectExtraInfo(obj, doc);
    string label = string.IsNullOrWhiteSpace(name) ? type : $"{type} \"{name}\"";
    if (info.Length > 0)
      label += $" ({info})";
    return label;
  }

  private static string ObjectExtraInfo(RhinoObject obj, RhinoDoc? doc)
  {
    UnitSystem units = doc?.ModelUnitSystem ?? UnitSystem.None;
    string ua = ModelUnitAbbrev(units);

    // Text annotations: show text content.
    if (obj.Geometry is AnnotationBase ann)
    {
      if (ann is TextEntity || ann is Leader)
      {
        string plain = ann.PlainText?.Trim() ?? "";
        if (plain.Length > 0) return TruncateDropLabel(plain, 30);
      }
      return "";
    }
    if (obj.Geometry is TextDot dot)
      return TruncateDropLabel(dot.Text?.Trim() ?? "", 30);

    // Curves: show geometry metrics.
    if (obj.Geometry is Curve crv)
    {
      // Circle / Arc → diameter.
      if (crv.TryGetCircle(out Circle circle))
        return FormatDiamDropLabel(circle.Radius * 2, ua);
      if (crv.TryGetArc(out Arc arc))
        return FormatDiamDropLabel(arc.Radius * 2, ua);

      // Rectangle / Square → W × H.
      if (crv.TryGetPolyline(out Polyline pl) && pl.IsValid && pl.IsClosed
          && pl.SegmentCount == 4 && IsPolylineRectangle(pl))
      {
        double w = pl[0].DistanceTo(pl[1]);
        double h = pl[1].DistanceTo(pl[2]);
        if (h > w) { var tmp = w; w = h; h = tmp; }
        string suffix = ua.Length > 0 ? " " + ua : "";
        return $"{FormatNumber(w)} × {FormatNumber(h)}{suffix}";
      }

      // Length for everything else (line, polyline, ellipse, generic curve).
      double len = crv.GetLength();
      if (len > 0 && !double.IsNaN(len))
      {
        string suffix = ua.Length > 0 ? " " + ua : "";
        return $"{FormatNumber(len)}{suffix}";
      }
    }

    return "";
  }

  private static string FormatDiamDropLabel(double diameter, string unitAbbr)
  {
    string num = FormatNumber(diameter);
    return unitAbbr.Length > 0 ? $"⌀{num} {unitAbbr}" : $"⌀{num}";
  }

  private static string TruncateDropLabel(string s, int maxLen)
  {
    if (string.IsNullOrEmpty(s)) return "";
    return s.Length <= maxLen ? s : s.Substring(0, maxLen - 1) + "\u2026";
  }

  private static string ModelUnitAbbrev(UnitSystem us) => us switch
  {
    UnitSystem.Millimeters => "mm",
    UnitSystem.Centimeters => "cm",
    UnitSystem.Meters      => "m",
    UnitSystem.Kilometers  => "km",
    UnitSystem.Inches      => "in",
    UnitSystem.Feet        => "ft",
    UnitSystem.Miles       => "mi",
    UnitSystem.Yards       => "yd",
    UnitSystem.Microns     => "\u00b5m",
    UnitSystem.Nanometers  => "nm",
    _                      => ""
  };

  private void OnTypeDropSelectedIndexChanged(object? sender, EventArgs e)
  {
    if (_isUpdatingUi)
      return;

    int idx = _typeDrop.SelectedIndex;
    if (idx <= 0 || _doc == null || _allSelectedObjects.Count < 2)
    {
      // Restore full selection view.
      if (_focusedObjectId != Guid.Empty)
      {
        _focusedObjectId = Guid.Empty;
        _selectedObjectIds.Clear();
        foreach (var o in _allSelectedObjects)
          _selectedObjectIds.Add(o.Id);
        _focusHighlightConduit.Clear();
        _focusHighlightConduit.Enabled = false;
        _doc?.Views.Redraw();
        UpdateFromSelection(_doc, _allSelectedObjects);
      }
      return;
    }

    // Separator or out-of-range: snap back to summary, which triggers the restore path.
    if (idx >= _typeDropMap.Count || _typeDropMap[idx] == null)
    {
      _typeDrop.SelectedIndex = 0;
      return;
    }

    RhinoObject focused = _typeDropMap[idx]!;
    _focusedObjectId = focused.Id;

    // Scope edits to focused object only.
    _selectedObjectIds.Clear();
    _selectedObjectIds.Add(focused.Id);

    // Show orange highlight on focused object via conduit (selection unchanged).
    _focusHighlightConduit.SetObject(focused);
    _focusHighlightConduit.Enabled = true;
    _doc.Views.Redraw();

    // Refresh panel for just this one object.
    UpdateFromSelection(_doc, new[] { focused });
  }

  private static string LayerName(RhinoDoc? doc, int layerIndex)
  {
    if (doc == null)
      return "-";

    try
    {
      var layer = doc.Layers[layerIndex];
      return layer?.FullPath ?? "-";
    }
    catch
    {
      return "-";
    }
  }

  private static string LinetypeText(RhinoDoc? doc, ObjectAttributes attributes)
  {
    if (attributes.LinetypeSource != ObjectLinetypeSource.LinetypeFromObject)
      return attributes.LinetypeSource.ToString().Replace("LinetypeFrom", "By ");

    return "By Object";
  }

  private static string PlotWidthText(ObjectAttributes attributes)
  {
    if (attributes.PlotWeightSource != ObjectPlotWeightSource.PlotWeightFromObject)
      return attributes.PlotWeightSource.ToString().Replace("PlotWeightFrom", "By ");

    double width = attributes.PlotWeight;
    if (RhinoMath.EpsilonEquals(width, -1.0, RhinoMath.ZeroTolerance))
      return "No Print";
    if (RhinoMath.EpsilonEquals(width, 0.0, RhinoMath.ZeroTolerance))
      return "Default";
    return FormatNumber(width);
  }

  private static string WireDensityText(ObjectAttributes attributes)
  {
    int? wireDensity = ReadInt(attributes, "WireDensity");
    return wireDensity.HasValue ? wireDensity.Value.ToString(CultureInfo.InvariantCulture) : "-1";
  }

  private static bool ShowIsoFromDensity(ObjectAttributes attributes)
  {
    int? wireDensity = ReadInt(attributes, "WireDensity");
    return wireDensity.HasValue && wireDensity.Value > 0;
  }

  private static bool HasCustomMesh(ObjectAttributes attributes)
  {
    var value = ReadProperty(attributes, "CustomMeshingParameters");
    return value != null;
  }

  private static object? ReadProperty(object source, string propertyName)
  {
    try
    {
      return source.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(source, null);
    }
    catch
    {
      return null;
    }
  }

  private static string? GetPropertyText(object source, string propertyName)
  {
    object? value = ReadProperty(source, propertyName);
    if (value == null)
      return null;
    return SafeString(value.ToString());
  }

  private static bool? ReadBool(object source, string propertyName)
  {
    object? value = ReadProperty(source, propertyName);
    if (value is bool b)
      return b;
    return null;
  }

  private static int? ReadInt(object source, string propertyName)
  {
    object? value = ReadProperty(source, propertyName);
    if (value is int i)
      return i;
    return null;
  }

  private static string CommonOrVaries(IReadOnlyList<RhinoObject> objects, Func<RhinoObject, string> selector)
  {
    if (objects.Count == 0)
      return "-";

    string first = selector(objects[0]);
    for (int i = 1; i < objects.Count; i++)
    {
      if (!string.Equals(first, selector(objects[i]), StringComparison.Ordinal))
        return VariesText;
    }
    return string.IsNullOrEmpty(first) ? "" : first;
  }

  private static bool? CommonBoolOrVaries(IReadOnlyList<RhinoObject> objects, Func<RhinoObject, bool> selector)
  {
    if (objects.Count == 0)
      return null;

    bool first = selector(objects[0]);
    for (int i = 1; i < objects.Count; i++)
    {
      if (first != selector(objects[i]))
        return null;
    }
    return first;
  }

  private IEnumerable<RhinoObject> SelectedRhinoObjects()
  {
    if (_doc == null)
      yield break;

    if (_focusedObjectId != Guid.Empty)
    {
      var obj = _doc.Objects.FindId(_focusedObjectId);
      if (obj != null)
        yield return obj;
      yield break;
    }

    foreach (var id in _selectedObjectIds)
    {
      var obj = _doc.Objects.FindId(id);
      if (obj != null)
        yield return obj;
    }
  }

  private static List<Curve> GetInfoCurvesForSelection(IReadOnlyList<RhinoObject> objects, out bool hasSegmentSelection)
  {
    hasSegmentSelection = false;
    var curves = new List<Curve>();

    foreach (var obj in objects)
    {
      if (obj.Geometry is not Curve curve)
        continue;

      curves.Add(curve);
    }

    return curves;
  }

  private void RefreshFromCurrentSelection()
  {
    if (_doc == null)
      return;

    var objs = SelectedRhinoObjects().ToList();
    UpdateFromSelection(_doc, objs);
  }

  private void ApplyAttributes(Action<ObjectAttributes> update)
  {
    if (_isUpdatingUi || _doc == null)
      return;

    _lastUserEditMs = System.Environment.TickCount64;
    uint undoRecord = _doc.BeginUndoRecord("Object+ Attributes");
    bool changed = false;
    try
    {
      foreach (var obj in SelectedRhinoObjects())
      {
        var attrs = obj.Attributes.Duplicate();
        update(attrs);
        changed |= _doc.Objects.ModifyAttributes(obj, attrs, true);
      }
    }
    finally
    {
      if (undoRecord != 0)
        _doc.EndUndoRecord(undoRecord);
    }

    if (changed)
    {
      _doc.Views.Redraw();
      RefreshFromCurrentSelection();
    }
  }

  private static bool TryParseDouble(string text, out double value)
  {
    string t = (text ?? string.Empty).Trim();
    string normalizedFraction = NormalizeWholeFractionSeparators(t);
    if (!string.Equals(normalizedFraction, t, StringComparison.Ordinal)
      && RhinoMath.TryParseNumber(normalizedFraction, out value))
      return true;

    if (RhinoMath.TryParseNumber(t, out value))
      return true;

    return double.TryParse(normalizedFraction, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
      || double.TryParse(normalizedFraction, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
      || double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
      || double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
  }

  private static Vector3d GetHorizontalReference(RhinoDoc? doc)
  {
    try
    {
      var view = doc?.Views?.ActiveView;
      if (view != null)
      {
        Vector3d axis = view.ActiveViewport.ConstructionPlane().XAxis;
        if (axis.IsValid && axis.Unitize())
          return axis;
      }
    }
    catch
    {
    }

    return Vector3d.XAxis;
  }

  private static bool IsPerpendicular(Vector3d a, Vector3d b, double angleToleranceRadians)
  {
    double angle = Vector3d.VectorAngle(a, b);
    return Math.Abs(angle - (Math.PI * 0.5)) <= angleToleranceRadians;
  }

  private static bool IsParallel(Vector3d a, Vector3d b, double angleToleranceRadians)
  {
    double angle = Vector3d.VectorAngle(a, b);
    return angle <= angleToleranceRadians || Math.Abs(angle - Math.PI) <= angleToleranceRadians;
  }

  private static bool TryGetRectangleFrame(
    Curve curve,
    double distanceTolerance,
    double angleToleranceRadians,
    Vector3d horizontalReference,
    out Point3d center,
    out Vector3d widthAxis,
    out Vector3d heightAxis,
    out double width,
    out double height)
  {
    center = Point3d.Unset;
    widthAxis = Vector3d.Unset;
    heightAxis = Vector3d.Unset;
    width = 0.0;
    height = 0.0;

    if (curve == null || !curve.IsClosed)
      return false;

    if (!curve.TryGetPolyline(out Polyline polyline) || !polyline.IsValid || polyline.Count < 4)
      return false;

    var points = polyline.ToList();
    if (points.Count < 4)
      return false;

    if (points[0].DistanceTo(points[^1]) <= distanceTolerance)
      points.RemoveAt(points.Count - 1);

    if (points.Count != 4)
      return false;

    var edges = new Vector3d[4];
    var lengths = new double[4];
    for (int i = 0; i < 4; i++)
    {
      Vector3d edge = points[(i + 1) % 4] - points[i];
      double length = edge.Length;
      if (length <= distanceTolerance)
        return false;

      edge.Unitize();
      edges[i] = edge;
      lengths[i] = length;
    }

    for (int i = 0; i < 4; i++)
    {
      if (!IsPerpendicular(edges[i], edges[(i + 1) % 4], angleToleranceRadians))
        return false;
    }

    if (!IsParallel(edges[0], edges[2], angleToleranceRadians) || !IsParallel(edges[1], edges[3], angleToleranceRadians))
      return false;

    double lengthTolerance = Math.Max(distanceTolerance * 10.0, 1e-6);
    if (Math.Abs(lengths[0] - lengths[2]) > lengthTolerance || Math.Abs(lengths[1] - lengths[3]) > lengthTolerance)
      return false;

    Vector3d planeNormal = Vector3d.CrossProduct(edges[0], edges[1]);
    if (!planeNormal.IsValid || !planeNormal.Unitize())
      return false;

    Vector3d horizontalInPlane = horizontalReference.IsValid ? horizontalReference : Vector3d.XAxis;
    horizontalInPlane -= planeNormal * Vector3d.Multiply(horizontalInPlane, planeNormal);
    if (!horizontalInPlane.IsValid || horizontalInPlane.Length <= RhinoMath.SqrtEpsilon || !horizontalInPlane.Unitize())
      horizontalInPlane = edges[0];

    double scoreA = Math.Abs(Vector3d.Multiply(edges[0], horizontalInPlane));
    double scoreB = Math.Abs(Vector3d.Multiply(edges[1], horizontalInPlane));

    double sideA = (lengths[0] + lengths[2]) * 0.5;
    double sideB = (lengths[1] + lengths[3]) * 0.5;

    if (scoreA >= scoreB)
    {
      width = sideA;
      height = sideB;
      widthAxis = edges[0];
      heightAxis = edges[1];
    }
    else
    {
      width = sideB;
      height = sideA;
      widthAxis = edges[1];
      heightAxis = edges[0];
    }

    center = new Point3d(
      (points[0].X + points[1].X + points[2].X + points[3].X) * 0.25,
      (points[0].Y + points[1].Y + points[2].Y + points[3].Y) * 0.25,
      (points[0].Z + points[1].Z + points[2].Z + points[3].Z) * 0.25);

    return true;
  }

  private static bool TryGetRectangleDimensions(
    Curve curve,
    double distanceTolerance,
    double angleToleranceRadians,
    Vector3d horizontalReference,
    out double width,
    out double height)
  {
    return TryGetRectangleFrame(
      curve,
      distanceTolerance,
      angleToleranceRadians,
      horizontalReference,
      out _,
      out _,
      out _,
      out width,
      out height);
  }

  private static bool TryParseLengthDisplay(string text, DropDown unitDrop, out double value)
  {
    string token = NormalizeUnitToken(unitDrop.SelectedValue?.ToString());
    if (IsFeetAndInchesUnitToken(token) && TryParseFeetAndInches(text, out value))
      return true;

    return TryParseDouble(text, out value);
  }

  private static bool TryParseFeetAndInches(string text, out double valueFeet)
  {
    valueFeet = 0;
    string t = (text ?? string.Empty).Trim();
    if (string.IsNullOrEmpty(t))
      return false;

    if (!t.Contains("'", StringComparison.Ordinal) && !t.Contains('"', StringComparison.Ordinal))
      return false;

    bool negative = false;
    if (t.StartsWith("-", StringComparison.Ordinal))
    {
      negative = true;
      t = t.Substring(1).TrimStart();
    }
    else if (t.StartsWith("+", StringComparison.Ordinal))
    {
      t = t.Substring(1).TrimStart();
    }

    double feetPart = 0.0;
    double inchesPart = 0.0;

    int apostrophe = t.IndexOf('\'');
    if (apostrophe >= 0)
    {
      string feetText = t.Substring(0, apostrophe).Trim();
      if (!string.IsNullOrEmpty(feetText) && !TryParseDouble(feetText, out feetPart))
        return false;
      t = t.Substring(apostrophe + 1).Trim();
    }

    string inchesText = NormalizeWholeFractionSeparators(t.Replace("\"", string.Empty).Trim());
    if (!string.IsNullOrEmpty(inchesText) && !TryParseDouble(inchesText, out inchesPart))
      return false;

    valueFeet = feetPart + (inchesPart / 12.0);
    if (negative)
      valueFeet = -valueFeet;

    return true;
  }

  private static string DropText(DropDown d)
  {
    string? selected = d.SelectedValue?.ToString();
    if (!string.IsNullOrWhiteSpace(selected))
      return selected;
    if (d.DataStore is IEnumerable<string> strings)
    {
      var list = strings.ToList();
      if (d.SelectedIndex >= 0 && d.SelectedIndex < list.Count)
        return list[d.SelectedIndex] ?? string.Empty;
    }
    return string.Empty;
  }

  private void ApplyName()
  {
    if (_nameBox.Text == VariesText)
      return;
    ApplyAttributes(a => a.Name = _nameBox.Text ?? string.Empty);
  }

  private void EditHyperlink()
  {
    if (_isUpdatingUi || _doc == null)
      return;

    string[] schemes = { "https", "http", "file", "mailto" };
    string current = _hyperlinkLabel.Text == VariesText ? string.Empty : (_hyperlinkLabel.Text ?? string.Empty);
    string scheme = "https";
    string urlValue = current;

    int colon = current.IndexOf(':');
    if (colon > 0)
    {
      string prefix = current.Substring(0, colon).ToLowerInvariant();
      if (schemes.Contains(prefix))
      {
        scheme = prefix;
        if (prefix == "mailto")
          urlValue = current.Substring(colon + 1);
        else if (current.StartsWith(prefix + "://", StringComparison.OrdinalIgnoreCase))
          urlValue = current.Substring((prefix + "://").Length);
      }
    }

    var typeDrop = new DropDown { DataStore = schemes.ToList() };
    typeDrop.SelectedIndex = Math.Max(0, schemes.ToList().IndexOf(scheme));
    var urlBox = new TextBox { Text = urlValue, Width = 260 };
    var okButton = new Button { Text = "OK" };
    var cancelButton = new Button { Text = "Cancel" };

    var layout = new DynamicLayout { Spacing = new Size(6, 6) };
    layout.AddRow(new Label { Text = "Hyperlink Information" });
    layout.AddRow(new Label { Text = "Type:", Width = 56 }, typeDrop);
    layout.AddRow(new Label { Text = "Address:", Width = 56 }, urlBox);
    layout.AddSeparateRow(null, okButton, cancelButton);

    var dialog = new Dialog<bool>
    {
      Title = "Hyperlink",
      Resizable = false,
      Padding = new Padding(10),
      Content = layout
    };

    okButton.Click += (_, _) => dialog.Close(true);
    cancelButton.Click += (_, _) => dialog.Close(false);

    bool accepted = dialog.ShowModal();
    if (!accepted)
      return;

    string selectedScheme = typeDrop.SelectedValue?.ToString() ?? "https";
    string typed = (urlBox.Text ?? string.Empty).Trim();
    string final = string.Empty;
    if (!string.IsNullOrEmpty(typed))
    {
      if (Uri.TryCreate(typed, UriKind.Absolute, out _))
      {
        final = typed;
      }
      else if (selectedScheme == "mailto")
      {
        final = $"mailto:{typed.TrimStart('/')}";
      }
      else
      {
        final = $"{selectedScheme}://{typed.TrimStart('/')}";
      }
    }

    ApplyAttributes(a => a.Url = final);
  }

  private void ShowLayerDropMenu()
  {
    if (_isUpdatingUi || _doc == null)
      return;

    var all = _doc.Layers.Cast<Layer>()
      .Where(l => l != null && !l.IsDeleted && !string.IsNullOrWhiteSpace(l.FullPath))
      .OrderBy(l => l.SortIndex)
      .ToList();

    if (all.Count == 0)
      return;

    var byParent = new Dictionary<Guid, List<Layer>>();
    foreach (var layer in all)
    {
      Guid pid = layer.ParentLayerId;
      if (!byParent.TryGetValue(pid, out var children))
      {
        children = new List<Layer>();
        byParent[pid] = children;
      }
      children.Add(layer);
    }

    var menu = new ContextMenu();

    void AddChildrenFlat(MenuItemCollection items, Guid parentId, int depth)
    {
      if (!byParent.TryGetValue(parentId, out var children))
        return;

      foreach (var child in children.OrderBy(c => c.SortIndex))
      {
        string indent = depth <= 0 ? string.Empty : new string(' ', depth * 2);
        int currentIndex = _doc.Layers.FindByFullPath(_currentLayerFullPath, -1);
        bool isCurrent = currentIndex >= 0 && child.Index == currentIndex;
        var item = new ButtonMenuItem
        {
          Text = (isCurrent ? "✓ " : string.Empty) + indent + child.Name,
          Image = CreateLayerMenuSwatch(ToEtoColor(ResolveLayerDisplayColor(_doc, child)))
        };
        Layer picked = child;
        item.Click += (_, _) => ApplyLayerSelection(picked);
        items.Add(item);
        AddChildrenFlat(items, child.Id, depth + 1);
      }
    }

    AddChildrenFlat(menu.Items, Guid.Empty, 0);
    if (menu.Items.Count == 0)
      return;

    menu.Show(_layerDrop, new Eto.Drawing.Point(0, _layerDrop.Height));
  }

  private static Bitmap CreateLayerMenuSwatch(Color color)
  {
    var bitmap = new Bitmap(18, 18, PixelFormat.Format32bppRgba);
    using (var g = new Graphics(bitmap))
    {
      g.FillRectangle(Color.FromArgb(242, 242, 242), 0, 0, 9, 9);
      g.FillRectangle(Color.FromArgb(191, 191, 191), 9, 0, 9, 9);
      g.FillRectangle(Color.FromArgb(191, 191, 191), 0, 9, 9, 9);
      g.FillRectangle(Color.FromArgb(242, 242, 242), 9, 9, 9, 9);
      g.FillRectangle(color, 0, 0, 18, 18);
      g.DrawRectangle(Colors.Black, 0, 0, 17, 17);
    }
    return bitmap;
  }

  private void ApplyLayerSelection(Layer layer)
  {
    if (_doc == null || layer == null || layer.IsDeleted)
      return;

    _currentLayerFullPath = layer.FullPath;
    ApplyAttributes(a => a.LayerIndex = layer.Index);
  }

  private void ApplyLayer()
  {
    if (_isUpdatingUi || _doc == null)
      return;

    var selected = _layerDrop.SelectedValue as LayerDropItem;
    if (selected?.IsToggle == true)
      return;

    string name = selected?.Name ?? DropText(_layerDrop);
    name = name.Trim();
    if (string.IsNullOrEmpty(name) || name == "-" || name == VariesText)
      return;

    int layerIndex = selected?.LayerIndex ?? _doc.Layers.FindByFullPath(name, -1);
    if (layerIndex < 0)
      layerIndex = _doc.Layers.FindName(name)?.Index ?? -1;
    if (layerIndex < 0)
      return;

    _currentLayerFullPath = name;
    ApplyAttributes(a => a.LayerIndex = layerIndex);
  }

  private static T ParseByEnumText<T>(string text, T fallback) where T : struct, Enum
  {
    string t = text.Trim().ToLowerInvariant();
    if (t.Contains("layer"))
      t = "layer";
    else if (t.Contains("object") || t.Contains("custom"))
      t = "object";
    else if (t.Contains("parent"))
      t = "parent";
    else if (t.Contains("display"))
      t = "display";
    else if (t.Contains("section"))
      t = "sectioner";

    foreach (var value in Enum.GetValues(typeof(T)).Cast<T>())
    {
      string n = value.ToString().ToLowerInvariant();
      if (n.Contains(t))
        return value;
    }
    return fallback;
  }

  private void ApplyDisplayColorSource()
  {
    var value = ParseDisplayColorSource(DropText(_displayColorDrop));
    _displayColorButton.Enabled = true;
    UpdateDisplayColorButtonIcon();
    ApplyAttributes(a => a.ColorSource = value);
  }

  private void ApplyDisplayModeSelection()
  {
    if (_isUpdatingUi || _doc == null)
      return;

    string text = DropText(_displayModeDrop);
    if (string.IsNullOrWhiteSpace(text) || text == "-" || text == VariesText || text == "By View")
      return;

    if (text == "Multiple Viewports...")
    {
      RhinoApp.RunScript("_SetObjectDisplayMode", false);
      return;
    }

    if (text == "Custom...")
    {
      RhinoApp.RunScript("_SetObjectDisplayMode", false);
      return;
    }

    RhinoApp.RunScript("_SetObjectDisplayMode", false);
  }

  private void ApplyLinetypeSource()
  {
    if (_isUpdatingUi || _doc == null)
      return;

    string text = DropText(_linetypeDrop);
    if (string.IsNullOrWhiteSpace(text) || text == "-" || text == VariesText)
      return;

    if (text.Equals("By Layer", StringComparison.OrdinalIgnoreCase))
    {
      ApplyAttributes(a => a.LinetypeSource = ObjectLinetypeSource.LinetypeFromLayer);
      return;
    }

    if (text.Equals("By Parent", StringComparison.OrdinalIgnoreCase))
    {
      ApplyAttributes(a => a.LinetypeSource = ObjectLinetypeSource.LinetypeFromParent);
      return;
    }

    if (text.Equals("Custom...", StringComparison.OrdinalIgnoreCase))
    {
      int idx = 0;
      if (!Rhino.UI.Dialogs.ShowSelectLinetypeDialog(ref idx, false))
        return;
      ApplyAttributes(a =>
      {
        a.LinetypeSource = ObjectLinetypeSource.LinetypeFromObject;
        a.LinetypeIndex = idx;
      });
      return;
    }

    int match = _doc.Linetypes.Find(text);
    if (match < 0)
      return;

    ApplyAttributes(a =>
    {
      a.LinetypeSource = ObjectLinetypeSource.LinetypeFromObject;
      a.LinetypeIndex = match;
    });
  }

  private void ApplyPrintColorSource()
  {
    var value = ParsePrintColorSource(DropText(_printColorDrop));
    _printColorButton.Enabled = true;
    UpdatePrintColorButtonIcon();
    ApplyAttributes(a => a.PlotColorSource = value);
  }

  private void PickDisplayColor()
  {
    if (_isUpdatingUi || _doc == null)
      return;

    var selected = SelectedRhinoObjects().ToList();
    if (selected.Count == 0)
      return;

    var color4f = new Color4f(selected[0].Attributes.ObjectColor);
    if (!Rhino.UI.Dialogs.ShowColorDialog(ref color4f, true))
      return;

    var picked = color4f.AsSystemColor();
    ApplyAttributes(a =>
    {
      a.ColorSource = ObjectColorSource.ColorFromObject;
      a.ObjectColor = picked;
    });
  }

  private void PickPrintColor()
  {
    if (_isUpdatingUi || _doc == null)
      return;

    var selected = SelectedRhinoObjects().ToList();
    if (selected.Count == 0)
      return;

    var color4f = new Color4f(selected[0].Attributes.PlotColor);
    if (!Rhino.UI.Dialogs.ShowColorDialog(ref color4f, true))
      return;

    var picked = color4f.AsSystemColor();
    ApplyAttributes(a =>
    {
      a.PlotColorSource = ObjectPlotColorSource.PlotColorFromObject;
      a.PlotColor = picked;
    });
  }

  private void ApplyPlotWeightSource()
  {
    string text = DropText(_printWidthDrop);
    if (string.IsNullOrWhiteSpace(text) || text == "-" || text == VariesText)
      return;

    if (text.Equals("By Layer", StringComparison.OrdinalIgnoreCase))
    {
      ApplyAttributes(a => a.PlotWeightSource = ObjectPlotWeightSource.PlotWeightFromLayer);
      return;
    }

    if (text.Equals("By Parent", StringComparison.OrdinalIgnoreCase))
    {
      ApplyAttributes(a => a.PlotWeightSource = ObjectPlotWeightSource.PlotWeightFromParent);
      return;
    }

    if (text.Equals("Custom", StringComparison.OrdinalIgnoreCase))
    {
      double width = Rhino.UI.Dialogs.ShowPrintWidths("Print Width", "Choose print width");
      if (width == RhinoMath.UnsetValue)
        return;
      ApplyAttributes(a =>
      {
        a.PlotWeightSource = ObjectPlotWeightSource.PlotWeightFromObject;
        a.PlotWeight = width;
      });
      return;
    }

    if (text.Equals("Default", StringComparison.OrdinalIgnoreCase))
    {
      ApplyAttributes(a =>
      {
        a.PlotWeightSource = ObjectPlotWeightSource.PlotWeightFromObject;
        a.PlotWeight = 0.0;
      });
      return;
    }

    if (text.Equals("Hairline", StringComparison.OrdinalIgnoreCase))
    {
      ApplyAttributes(a =>
      {
        a.PlotWeightSource = ObjectPlotWeightSource.PlotWeightFromObject;
        a.PlotWeight = 0.0;
      });
      return;
    }

    if (text.Equals("No Print", StringComparison.OrdinalIgnoreCase))
    {
      ApplyAttributes(a =>
      {
        a.PlotWeightSource = ObjectPlotWeightSource.PlotWeightFromObject;
        a.PlotWeight = -1.0;
      });
      return;
    }

    if (!TryParseDouble(text, out double value))
      return;

    ApplyAttributes(a =>
    {
      a.PlotWeightSource = ObjectPlotWeightSource.PlotWeightFromObject;
      a.PlotWeight = value;
    });
  }

  private void ApplySectionSource()
  {
    string text = DropText(_sectionStyleDrop);
    if (string.IsNullOrWhiteSpace(text) || text == "-" || text == VariesText)
      return;

    ObjectSectionAttributesSource value = text switch
    {
      "By Layer" => ObjectSectionAttributesSource.FromLayer,
      "By Parent" => ObjectSectionAttributesSource.FromParent,
      "By Clipping Plane" => ObjectSectionAttributesSource.FromSectioner,
      "Custom..." => ObjectSectionAttributesSource.FromObject,
      _ => ObjectSectionAttributesSource.FromLayer
    };
    ApplyAttributes(a => a.SectionAttributesSource = value);
  }

  private void ApplyLinetypeScale()
  {
    double value = _linetypeScaleStepper.Value;
    if (value <= RhinoMath.ZeroTolerance)
      return;
    ApplyAttributes(a => a.LinetypePatternScale = value);
  }

  private void ApplyWireDensity()
  {
    int value = (int)Math.Round(_densityStepper.Value);

    ApplyAttributes(a => a.WireDensity = value);
  }

  private void ApplyCustomMesh()
  {
    if (_customMeshCheck.Checked == null)
      return;
    bool on = _customMeshCheck.Checked.Value;
    ApplyAttributes(a => a.CustomMeshingParameters = on ? MeshingParameters.Default : null);
  }

  private void ApplyCastsShadows()
  {
    if (_castsShadowsCheck.Checked == null)
      return;
    bool value = _castsShadowsCheck.Checked.Value;
    ApplyAttributes(a => a.CastsShadows = value);
  }

  private void ApplyReceivesShadows()
  {
    if (_receivesShadowsCheck.Checked == null)
      return;
    bool value = _receivesShadowsCheck.Checked.Value;
    ApplyAttributes(a => a.ReceivesShadows = value);
  }

  private void ApplyShowIsocurve()
  {
    if (_showIsocurveCheck.Checked == null)
      return;
    bool show = _showIsocurveCheck.Checked.Value;
    _densityStepper.Enabled = show;
    ApplyAttributes(a =>
    {
      if (show)
      {
        if (a.WireDensity <= 0)
          a.WireDensity = 1;
      }
      else
      {
        a.WireDensity = -1;
      }
    });
  }

  private List<(Guid id, Circle circle)> SelectedCircles()
  {
    var list = new List<(Guid id, Circle circle)>();
    foreach (var obj in SelectedRhinoObjects())
    {
      if (obj.Geometry is Curve c && c.TryGetCircle(out Circle circle))
        list.Add((obj.Id, circle));
    }
    return list;
  }

  private List<(Guid id, Point3d center, Vector3d widthAxis, Vector3d heightAxis, double width, double height)> SelectedRectangles()
  {
    var list = new List<(Guid id, Point3d center, Vector3d widthAxis, Vector3d heightAxis, double width, double height)>();
    double distanceTolerance = Math.Max((_doc?.ModelAbsoluteTolerance ?? RhinoMath.SqrtEpsilon) * 2.0, 1e-8);
    double angleToleranceRadians = RhinoMath.ToRadians(2.0);
    Vector3d horizontalReference = GetHorizontalReference(_doc);

    foreach (var obj in SelectedRhinoObjects())
    {
      if (obj.Geometry is not Curve curve)
        continue;

      if (!TryGetRectangleFrame(
            curve,
            distanceTolerance,
            angleToleranceRadians,
        horizontalReference,
            out Point3d center,
            out Vector3d widthAxis,
            out Vector3d heightAxis,
            out double width,
            out double height))
        continue;

      list.Add((obj.Id, center, widthAxis, heightAxis, width, height));
    }

    return list;
  }

  private bool IsRectangleOnlySelection(out List<(Guid id, Point3d center, Vector3d widthAxis, Vector3d heightAxis, double width, double height)> rectangles)
  {
    rectangles = SelectedRectangles();
    var selected = SelectedRhinoObjects().ToList();
    if (selected.Count == 0)
      return false;
    if (!selected.All(o => o.Geometry is Curve))
      return false;
    if (selected.Any(o => o.Geometry is Curve c && (c.TryGetCircle(out _) || c.TryGetArc(out _))))
      return false;

    return rectangles.Count == selected.Count;
  }

  private static Curve BuildRectangleCurve(Point3d center, Vector3d widthAxis, Vector3d heightAxis, double width, double height)
  {
    double halfW = width * 0.5;
    double halfH = height * 0.5;

    Point3d p0 = center - (widthAxis * halfW) - (heightAxis * halfH);
    Point3d p1 = center + (widthAxis * halfW) - (heightAxis * halfH);
    Point3d p2 = center + (widthAxis * halfW) + (heightAxis * halfH);
    Point3d p3 = center - (widthAxis * halfW) + (heightAxis * halfH);
    var polyline = new Polyline(new[] { p0, p1, p2, p3, p0 });
    return new PolylineCurve(polyline);
  }

  private bool ApplyRectangleDimensionsToSelection(double? targetWidth, double? targetHeight)
  {
    if (_isUpdatingUi || _doc == null)
      return false;
    if (!IsRectangleOnlySelection(out var rectangles))
      return false;

    double minLen = Math.Max(_doc.ModelAbsoluteTolerance, RhinoMath.SqrtEpsilon);
    if (targetWidth.HasValue && targetWidth.Value <= minLen)
      return true;
    if (targetHeight.HasValue && targetHeight.Value <= minLen)
      return true;

    uint undoRecord = _doc.BeginUndoRecord("Object+ Rectangle Width/Height");
    bool changed = false;
    try
    {
      foreach (var item in rectangles)
      {
        double width = targetWidth ?? item.width;
        double height = targetHeight ?? item.height;
        if (width <= minLen || height <= minLen)
          continue;

        Curve updated = BuildRectangleCurve(item.center, item.widthAxis, item.heightAxis, width, height);
        changed |= _doc.Objects.Replace(item.id, updated);
      }
    }
    finally
    {
      if (undoRecord != 0)
        _doc.EndUndoRecord(undoRecord);
    }

    if (changed)
    {
      _doc.Views.Redraw();
      RefreshFromCurrentSelection();
    }

    return true;
  }

  private void OnRectangleDimensionFocusChanged(RectangleHighlightKind kind)
  {
    _rectangleHighlightKind = kind;
    RefreshRectangleSideHighlight();
  }

  private void DisableRectangleSideHighlight()
  {
    bool wasEnabled = _rectangleSideHighlightConduit.Enabled;
    _rectangleSideHighlightConduit.SetSegments(Array.Empty<Line>());
    _rectangleSideHighlightConduit.Enabled = false;
    if (wasEnabled)
      _doc?.Views.Redraw();
  }

  private void RefreshRectangleSideHighlight()
  {
    if (_doc == null || _rectangleHighlightKind == RectangleHighlightKind.None)
    {
      DisableRectangleSideHighlight();
      return;
    }

    if (!IsRectangleOnlySelection(out var rectangles) || rectangles.Count == 0)
    {
      DisableRectangleSideHighlight();
      return;
    }

    var segments = new List<Line>(rectangles.Count * 2);
    foreach (var item in rectangles)
    {
      double halfW = item.width * 0.5;
      double halfH = item.height * 0.5;

      Point3d p0 = item.center - (item.widthAxis * halfW) - (item.heightAxis * halfH);
      Point3d p1 = item.center + (item.widthAxis * halfW) - (item.heightAxis * halfH);
      Point3d p2 = item.center + (item.widthAxis * halfW) + (item.heightAxis * halfH);
      Point3d p3 = item.center - (item.widthAxis * halfW) + (item.heightAxis * halfH);

      if (_rectangleHighlightKind == RectangleHighlightKind.Width)
      {
        segments.Add(new Line(p0, p1));
        segments.Add(new Line(p3, p2));
      }
      else if (_rectangleHighlightKind == RectangleHighlightKind.Height)
      {
        segments.Add(new Line(p0, p3));
        segments.Add(new Line(p1, p2));
      }
    }

    if (segments.Count == 0)
    {
      DisableRectangleSideHighlight();
      return;
    }

    _rectangleSideHighlightConduit.SetSegments(segments);
    _rectangleSideHighlightConduit.Enabled = true;
    _doc.Views.Redraw();
  }

  private static bool TryCreateArcWithRadius(Arc arc, double radius, out Arc resizedArc)
  {
    resizedArc = Arc.Unset;
    if (!arc.IsValid || radius <= RhinoMath.ZeroTolerance)
      return false;

    // Arc is a value type — copy it and update only the radius.
    // This preserves the plane (and its start-angle offset), angle domain, and sweep direction.
    var candidate = arc;
    candidate.Radius = radius;
    if (!candidate.IsValid)
      return false;

    resizedArc = candidate;
    return true;
  }

  private void ApplyCircularRadiusToSelection(double radius)
  {
    if (_isUpdatingUi || _doc == null)
      return;
    if (radius <= RhinoMath.ZeroTolerance)
      return;

    uint undoRecord = _doc.BeginUndoRecord("Object+ Radius/Diameter");
    bool changed = false;
    try
    {
      foreach (var obj in SelectedRhinoObjects())
      {
        if (obj.Geometry is not Curve curve)
          continue;

        if (!curve.TryGetArc(out Arc arc))
          continue;

        if (arc.IsCircle)
        {
          // Full circle — keep it a circle at the new radius.
          var c = new Circle(arc.Plane, radius);
          changed |= _doc.Objects.Replace(obj.Id, new ArcCurve(c));
        }
        else
        {
          // Partial arc — scale around center, preserve start/end angles.
          if (!TryCreateArcWithRadius(arc, radius, out Arc resizedArc))
            continue;
          changed |= _doc.Objects.Replace(obj.Id, new ArcCurve(resizedArc));
        }
      }
    }
    finally
    {
      if (undoRecord != 0)
        _doc.EndUndoRecord(undoRecord);
    }

    if (changed)
    {
      _doc.Views.Redraw();
      RefreshFromCurrentSelection();
    }
  }

  private List<(Guid id, int sides, Point3d center, Vector3d firstVertexDir, Vector3d normal, double circumRadius)> SelectedPolygons()
  {
    var list = new List<(Guid, int, Point3d, Vector3d, Vector3d, double)>();
    double distTol = Math.Max((_doc?.ModelAbsoluteTolerance ?? RhinoMath.SqrtEpsilon) * 2.0, 1e-8);
    foreach (var obj in SelectedRhinoObjects())
    {
      if (obj.Geometry is not Curve c)
        continue;
      if (!c.TryGetPolyline(out Polyline pl) || !pl.IsValid)
        continue;
      if (!TryGetRegularPolygon(pl, distTol, out int sides, out Point3d center,
            out Vector3d firstVertexDir, out Vector3d polyNormal, out double circumR, out _))
        continue;
      list.Add((obj.Id, sides, center, firstVertexDir, polyNormal, circumR));
    }
    return list;
  }

  private bool IsEllipseOnlySelection(out List<(Guid id, Ellipse ellipse)> ellipses)
  {
    ellipses = new List<(Guid id, Ellipse ellipse)>();
    foreach (var obj in SelectedRhinoObjects())
    {
      if (obj.Geometry is not Curve curve)
        return false;
      // Full circles and circular arcs satisfy TryGetEllipse too; exclude both.
      if (curve.TryGetCircle(out _))
        return false;
      if (curve.TryGetArc(out _))
        return false;
      if (!curve.TryGetEllipse(out Ellipse e))
        return false;
      ellipses.Add((obj.Id, e));
    }
    return ellipses.Count > 0;
  }

  private void ApplyEllipseAxisToSelection(double newValue, bool isMajorAxis)
  {
    if (_isUpdatingUi || _doc == null)
      return;
    if (newValue <= RhinoMath.ZeroTolerance)
      return;
    if (!IsEllipseOnlySelection(out var ellipses))
      return;

    uint undoRecord = _doc.BeginUndoRecord("Object+ Ellipse Axis");
    bool changed = false;
    try
    {
      foreach (var (id, ellipse) in ellipses)
      {
        double newRadius1, newRadius2;
        if (isMajorAxis)
        {
          // Major axis = max(Radius1, Radius2)
          newRadius1 = (ellipse.Radius1 >= ellipse.Radius2) ? newValue : ellipse.Radius1;
          newRadius2 = (ellipse.Radius1 < ellipse.Radius2) ? newValue : ellipse.Radius2;
        }
        else
        {
          // Minor axis = min(Radius1, Radius2)
          newRadius1 = (ellipse.Radius1 < ellipse.Radius2) ? newValue : ellipse.Radius1;
          newRadius2 = (ellipse.Radius1 >= ellipse.Radius2) ? newValue : ellipse.Radius2;
        }
        var newEllipse = new Ellipse(ellipse.Plane, newRadius1, newRadius2);
        var newCurve = newEllipse.ToNurbsCurve();
        if (newCurve != null)
          changed |= _doc.Objects.Replace(id, newCurve);
      }
    }
    finally
    {
      if (undoRecord != 0)
        _doc.EndUndoRecord(undoRecord);
    }

    if (changed)
    {
      _doc.Views.Redraw();
      RefreshFromCurrentSelection();
    }
  }

  private bool IsPolygonOnlySelection(out List<(Guid id, int sides, Point3d center, Vector3d firstVertexDir, Vector3d normal, double circumRadius)> polygons)
  {
    polygons = SelectedPolygons();
    var selected = SelectedRhinoObjects().ToList();
    if (selected.Count == 0)
      return false;
    if (!selected.All(o => o.Geometry is Curve))
      return false;
    return polygons.Count == selected.Count;
  }

  private void ApplyPolygonSides()
  {
    if (_isUpdatingUi || _doc == null)
      return;
    int newSides = (int)Math.Round(_polygonSidesStepper.Value);
    if (newSides < 3)
      return;
    if (!IsPolygonOnlySelection(out var polygons))
      return;

    uint undoRecord = _doc.BeginUndoRecord("Object+ Polygon Sides");
    bool changed = false;
    try
    {
      foreach (var p in polygons)
      {
        var curve = BuildRegularPolygonCurve(p.center, p.firstVertexDir, p.normal, p.circumRadius, newSides);
        changed |= _doc.Objects.Replace(p.id, curve);
      }
    }
    finally
    {
      if (undoRecord != 0)
        _doc.EndUndoRecord(undoRecord);
    }

    if (changed)
    {
      _doc.Views.Redraw();
      RefreshFromCurrentSelection();
    }
  }

  private void ApplyPolygonCircumRadiusToSelection(double newCircumR)
  {
    if (_isUpdatingUi || _doc == null)
      return;
    if (newCircumR <= RhinoMath.ZeroTolerance)
      return;
    if (!IsPolygonOnlySelection(out var polygons))
      return;

    uint undoRecord = _doc.BeginUndoRecord("Object+ Polygon Radius");
    bool changed = false;
    try
    {
      foreach (var p in polygons)
      {
        var curve = BuildRegularPolygonCurve(p.center, p.firstVertexDir, p.normal, newCircumR, p.sides);
        changed |= _doc.Objects.Replace(p.id, curve);
      }
    }
    finally
    {
      if (undoRecord != 0)
        _doc.EndUndoRecord(undoRecord);
    }

    if (changed)
    {
      _doc.Views.Redraw();
      RefreshFromCurrentSelection();
    }
  }

  private static Curve BuildRegularPolygonCurve(Point3d center, Vector3d firstVertexDir, Vector3d normal, double circumRadius, int sides)
  {
    var pts = new Point3d[sides + 1];
    double angleStep = 2.0 * Math.PI / sides;
    for (int i = 0; i < sides; i++)
    {
      var v = new Vector3d(firstVertexDir);
      v.Rotate(i * angleStep, normal);
      pts[i] = center + v * circumRadius;
    }
    pts[sides] = pts[0];
    return new PolylineCurve(pts);
  }

  private static bool TryGetRegularPolygon(
    Polyline pl,
    double distTol,
    out int sides,
    out Point3d center,
    out Vector3d firstVertexDir,
    out Vector3d normal,
    out double circumRadius,
    out double apothem)
  {
    sides = 0;
    center = Point3d.Unset;
    firstVertexDir = Vector3d.Zero;
    normal = Vector3d.Zero;
    circumRadius = 0;
    apothem = 0;

    if (!pl.IsValid || !pl.IsClosed)
      return false;

    int n = pl.SegmentCount;
    if (n < 3)
      return false;

    // All segments must have equal length.
    double len0 = pl[0].DistanceTo(pl[1]);
    if (len0 < distTol)
      return false;
    double lenTol = Math.Max(distTol, len0 * 1e-4);
    for (int i = 1; i < n; i++)
    {
      double li = pl[i].DistanceTo(pl[(i + 1) % n]);
      if (Math.Abs(li - len0) > lenTol)
        return false;
    }

    // All interior angles must equal (n-2)*180/n degrees.
    double expectedInteriorDeg = (n - 2) * 180.0 / n;
    double angTolDeg = 1.5;
    for (int i = 0; i < n; i++)
    {
      var incoming = pl[(i + 1) % n] - pl[i];
      var outgoing = pl[(i + 2) % n] - pl[(i + 1) % n];
      if (!incoming.Unitize() || !outgoing.Unitize())
        return false;
      double cosInterior = (-incoming) * outgoing;
      double interiorDeg = RhinoMath.ToDegrees(Math.Acos(Math.Clamp(cosInterior, -1.0, 1.0)));
      if (Math.Abs(interiorDeg - expectedInteriorDeg) > angTolDeg)
        return false;
    }

    // Center = mean of vertices (excluding duplicate closing vertex).
    double cx = 0, cy = 0, cz = 0;
    for (int i = 0; i < n; i++) { cx += pl[i].X; cy += pl[i].Y; cz += pl[i].Z; }
    center = new Point3d(cx / n, cy / n, cz / n);

    // All vertices must be equidistant from center.
    circumRadius = center.DistanceTo(pl[0]);
    if (circumRadius < distTol)
      return false;
    double radTol = Math.Max(distTol, circumRadius * 1e-4);
    for (int i = 1; i < n; i++)
    {
      if (Math.Abs(center.DistanceTo(pl[i]) - circumRadius) > radTol)
        return false;
    }

    // Normal from first two edge vectors.
    var edge0 = pl[1] - pl[0];
    var edge1 = pl[2] - pl[1];
    normal = Vector3d.CrossProduct(edge0, edge1);
    if (!normal.Unitize())
      return false;

    firstVertexDir = pl[0] - center;
    if (!firstVertexDir.Unitize())
      return false;

    Point3d midEdge = pl[0] + (pl[1] - pl[0]) * 0.5;
    apothem = center.DistanceTo(midEdge);
    sides = n;
    return true;
  }

  private void ApplyEditedRadius()
  {
    if (_radiusBox.ReadOnly)
      return;

    if (!TryParseLengthDisplay(_radiusBox.Text ?? string.Empty, _radiusUnitDrop, out double radiusDisplay))
      return;
    if (_doc == null)
      return;
    double radius = ConvertLength(radiusDisplay, GetSelectedUnitSystem(_radiusUnitDrop, _doc), _doc.ModelUnitSystem);

    if (ApplyRectangleDimensionsToSelection(radius, null))
      return;

    if (IsPolygonOnlySelection(out _))
    {
      ApplyPolygonCircumRadiusToSelection(radius);
      return;
    }

    if (IsEllipseOnlySelection(out _))
    {
      ApplyEllipseAxisToSelection(radius, isMajorAxis: true);
      return;
    }

    ApplyCircularRadiusToSelection(radius);
  }

  private void ApplyEditedDiameter()
  {
    if (_diameterBox.ReadOnly)
      return;

    if (!TryParseLengthDisplay(_diameterBox.Text ?? string.Empty, _diameterUnitDrop, out double diameterDisplay))
      return;
    if (_doc == null)
      return;
    double diameter = ConvertLength(diameterDisplay, GetSelectedUnitSystem(_diameterUnitDrop, _doc), _doc.ModelUnitSystem);

    if (ApplyRectangleDimensionsToSelection(null, diameter))
      return;

    if (IsPolygonOnlySelection(out var polygons) && polygons.Count > 0)
    {
      // Inner R (apothem) → circumradius: outerR = innerR / cos(π/N)
      double apothem = diameter;
      double circumR = apothem / Math.Cos(Math.PI / polygons[0].sides);
      ApplyPolygonCircumRadiusToSelection(circumR);
      return;
    }

    if (IsEllipseOnlySelection(out _))
    {
      ApplyEllipseAxisToSelection(diameter, isMajorAxis: false);
      return;
    }

    ApplyCircularRadiusToSelection(diameter * 0.5);
  }

  private void ApplyEditedCurveMetric()
  {
    if (_curveMetricBox.ReadOnly)
      return;

    if (!TryParseLengthDisplay(_curveMetricBox.Text ?? string.Empty, _curveMetricUnitDrop, out double targetLengthDisplay))
      return;
    if (_doc == null || _isUpdatingUi)
      return;

    double targetLength = ConvertLength(targetLengthDisplay, GetSelectedUnitSystem(_curveMetricUnitDrop, _doc), _doc.ModelUnitSystem);
    if (targetLength <= RhinoMath.ZeroTolerance)
      return;

    uint undoRecord = _doc.BeginUndoRecord("Object+ Length");
    bool changed = false;
    try
    {
      foreach (var obj in SelectedRhinoObjects())
      {
        if (obj.Geometry is not Curve curve)
          continue;

        if (curve.TryGetCircle(out Circle circle))
        {
          double newRadius = targetLength / (2.0 * Math.PI);
          if (newRadius <= RhinoMath.ZeroTolerance)
            continue;
          changed |= _doc.Objects.Replace(obj.Id, new ArcCurve(new Circle(circle.Plane, circle.Center, newRadius)));
          continue;
        }

        double currentLength = curve.GetLength();
        if (currentLength <= RhinoMath.ZeroTolerance)
          continue;

        double scale = targetLength / currentLength;
        var bbox = curve.GetBoundingBox(true);
        var xform = Transform.Scale(bbox.Center, scale);
        var duplicate = curve.DuplicateCurve();
        if (!duplicate.Transform(xform))
          continue;

        changed |= _doc.Objects.Replace(obj.Id, duplicate);
      }
    }
    finally
    {
      if (undoRecord != 0)
        _doc.EndUndoRecord(undoRecord);
    }

    if (changed)
    {
      _doc.Views.Redraw();
      RefreshFromCurrentSelection();
    }
  }

  private static string SafeString(string? value)
  {
    return value ?? string.Empty;
  }

  private void UpdateDisplayColorButtonIcon()
  {
    string mode = DropText(_displayColorDrop);
    if (mode == "Custom..." || mode == "By Object")
    {
      _displayColorButton.Image = CreateButtonColorSwatch(GetSelectedDisplayColorOrDefault());
      _displayColorButton.Text = string.Empty;
      _displayColorButton.BackgroundColor = Colors.White;
      return;
    }

    _displayColorButton.BackgroundColor = Colors.White;
    string icon = mode switch
    {
      "By Layer" => "Layer",
      "By Parent" => "Block",
      "By Material" => "Render_Tools_Materials_Panel",
      _ => "Layer"
    };
    SetButtonIcon(_displayColorButton, icon);
  }

  private void UpdatePrintColorButtonIcon()
  {
    string mode = DropText(_printColorDrop);
    if (mode == "Custom..." || mode == "By Object")
    {
      _printColorButton.Image = CreateButtonColorSwatch(GetSelectedPrintColorOrDefault());
      _printColorButton.Text = string.Empty;
      _printColorButton.BackgroundColor = Colors.White;
      return;
    }

    _printColorButton.BackgroundColor = Colors.White;
    string icon = mode switch
    {
      "By Layer" => "Layer",
      "By Parent" => "Block",
      "By Display" => "Display_Display_Panel",
      _ => "Display_Display_Panel"
    };
    SetButtonIcon(_printColorButton, icon);
  }

  private Color GetSelectedDisplayColorOrDefault()
  {
    var colors = SelectedRhinoObjects().Select(o => ToEtoColor(o.Attributes.ObjectColor)).ToList();
    if (colors.Count == 0)
      return Colors.Gray;

    var first = colors[0];
    bool allSame = colors.All(c => c.ToArgb() == first.ToArgb());
    return allSame ? first : Colors.Gray;
  }

  private Color GetSelectedPrintColorOrDefault()
  {
    var colors = SelectedRhinoObjects().Select(o => ToEtoColor(o.Attributes.PlotColor)).ToList();
    if (colors.Count == 0)
      return Colors.Gray;

    var first = colors[0];
    bool allSame = colors.All(c => c.ToArgb() == first.ToArgb());
    return allSame ? first : Colors.Gray;
  }

  private void SetButtonIcon(Button button, string iconName)
  {
    var image = LoadUiIcon(iconName);
    if (image != null)
    {
      button.Image = image;
      button.Text = string.Empty;
      return;
    }

    button.Image = null;
    button.Text = string.Empty;
  }

  private Image? LoadUiIcon(string iconName)
  {
    if (_uiIconCache.TryGetValue(iconName, out var cached))
      return cached;

    string assemblyDir = Path.GetDirectoryName(typeof(vObjectPropertiesPlusPanel).Assembly.Location) ?? AppContext.BaseDirectory;
    string[] candidates =
    {
      Path.Combine(assemblyDir, "rhino_svgs", iconName + ".svg"),
      Path.Combine(assemblyDir, iconName + ".svg"),
      Path.Combine(assemblyDir, "..", "icons", "rhino_svgs", iconName + ".svg"),
      Path.Combine(AppContext.BaseDirectory, "rhino_svgs", iconName + ".svg")
    };

    foreach (string path in candidates)
    {
      try
      {
        if (!File.Exists(path))
          continue;

        string svg = File.ReadAllText(path);
        using var sysBitmap = Rhino.UI.DrawingUtilities.BitmapFromSvg(svg, 16, 16, true);
        string tmpPng = Path.Combine(Path.GetTempPath(), "vObjectPropertiesPlus_" + iconName + "_" + Guid.NewGuid().ToString("N") + ".png");
        sysBitmap.Save(tmpPng);
        var etoBitmap = new Bitmap(tmpPng);
        try { File.Delete(tmpPng); } catch { }
        _uiIconCache[iconName] = etoBitmap;
        return etoBitmap;
      }
      catch
      {
      }
    }

    _uiIconCache[iconName] = null;
    return null;
  }

  private static void SetCheckState(CheckBox checkBox, bool? value)
  {
    checkBox.ThreeState = false;
    checkBox.Checked = value ?? false;
  }

  private static void SetEditableTextValue(TextBox textBox, string value)
  {
    if (value == VariesText)
    {
      textBox.Text = string.Empty;
      textBox.PlaceholderText = VariesText;
      return;
    }

    textBox.PlaceholderText = string.Empty;
    textBox.Text = value;
  }

  private static string FormatNumber(double value)
  {
    return value.ToString("0.###", CultureInfo.InvariantCulture);
  }

  private string FormatInfoNumber(double value, DropDown unitDrop)
  {
    if (_doc == null)
      return FormatNumber(value);

    string unitToken = NormalizeUnitToken(unitDrop.SelectedValue?.ToString());
    bool isFeetAndInchesUnit = IsFeetAndInchesUnitToken(unitToken);
    string formatToken = GetSelectedFormatToken();
    bool useFractional = formatToken.Equals("fractional", StringComparison.OrdinalIgnoreCase);

    if (isFeetAndInchesUnit)
    {
      if (formatToken.Equals("decimal", StringComparison.OrdinalIgnoreCase))
        return FormatFeetAndInchesDecimal(value, GetDecimalPrecision());

      int denominator = 1 << GetFractionalPrecisionBits();
      return FormatFeetAndInchesFractional(value, denominator);
    }

    if (useFractional)
    {
      int denominator = 1 << GetFractionalPrecisionBits();
      return FormatFractionalWithPlus(value, denominator);
    }

    int precision = GetDecimalPrecision();
    string fmt = precision == 0 ? "0" : "0." + new string('#', precision);
    return value.ToString(fmt, CultureInfo.CurrentCulture);
  }

  private int GetDecimalPrecision()
  {
    int fallback = _doc?.ModelDistanceDisplayPrecision ?? 3;
    return GetSelectedPrecision(fallback, 8);
  }

  private int GetFractionalPrecisionBits()
  {
    int fallback = _doc?.ModelDistanceDisplayPrecision ?? 4;
    return GetSelectedPrecision(fallback, 8);
  }

  private int GetSelectedPrecision(int fallback, int max)
  {
    int precision = _infoPrecisionDrop.SelectedIndex;
    if (precision < 0)
      precision = fallback;

    return Math.Max(0, Math.Min(max, precision));
  }

  private static string NormalizeWholeFractionSeparators(string value)
  {
    if (string.IsNullOrWhiteSpace(value))
      return value;

    // Convert whole-fraction separator 1-1/2 to 1+1/2 so text can be reused directly as Rhino input.
    return Regex.Replace(value, @"(?<=\d)\s*-\s*(?=\d+\s*/\s*\d+)", "+");
  }

  private static string FormatFractionalWithPlus(double value, int denominator)
  {
    if (denominator <= 0)
      denominator = 1;

    bool negative = value < 0.0;
    double abs = Math.Abs(value);
    long whole = (long)Math.Floor(abs);
    double frac = abs - whole;
    int num = (int)Math.Round(frac * denominator, MidpointRounding.AwayFromZero);

    if (num >= denominator)
    {
      whole += 1;
      num = 0;
    }

    string core;
    if (num == 0)
    {
      core = whole.ToString(CultureInfo.CurrentCulture);
    }
    else
    {
      int g = Gcd(num, denominator);
      int n = num / g;
      int d = denominator / g;
      core = whole > 0
        ? whole.ToString(CultureInfo.CurrentCulture) + "+" + n.ToString(CultureInfo.CurrentCulture) + "/" + d.ToString(CultureInfo.CurrentCulture)
        : n.ToString(CultureInfo.CurrentCulture) + "/" + d.ToString(CultureInfo.CurrentCulture);
    }

    return negative ? "-" + core : core;
  }

  private static string FormatFeetAndInchesDecimal(double valueFeet, int inchPrecision)
  {
    bool negative = valueFeet < 0.0;
    double absFeet = Math.Abs(valueFeet);
    long wholeFeet = (long)Math.Floor(absFeet);
    double inches = (absFeet - wholeFeet) * 12.0;

    double roundedInches = Math.Round(inches, Math.Max(0, inchPrecision), MidpointRounding.AwayFromZero);
    if (roundedInches >= 12.0 - 1e-12)
    {
      wholeFeet += 1;
      roundedInches = 0.0;
    }

    string fmt = inchPrecision <= 0 ? "0" : "0." + new string('#', inchPrecision);
    string inchesText = roundedInches.ToString(fmt, CultureInfo.CurrentCulture);

    string core = wholeFeet > 0
      ? wholeFeet.ToString(CultureInfo.CurrentCulture) + "'" + inchesText + "\""
      : inchesText + "\"";

    return negative ? "-" + core : core;
  }

  private static string FormatFeetAndInchesFractional(double valueFeet, int denominator)
  {
    if (denominator <= 0)
      denominator = 1;

    bool negative = valueFeet < 0.0;
    double absFeet = Math.Abs(valueFeet);
    double totalInches = absFeet * 12.0;
    long wholeInches = (long)Math.Floor(totalInches);
    double frac = totalInches - wholeInches;
    int num = (int)Math.Round(frac * denominator, MidpointRounding.AwayFromZero);

    if (num >= denominator)
    {
      wholeInches += 1;
      num = 0;
    }

    long wholeFeet = wholeInches / 12;
    int inchWhole = (int)(wholeInches % 12);

    string inchText;
    if (num == 0)
    {
      inchText = inchWhole.ToString(CultureInfo.CurrentCulture);
    }
    else
    {
      int g = Gcd(num, denominator);
      int n = num / g;
      int d = denominator / g;
      inchText = inchWhole > 0
        ? inchWhole.ToString(CultureInfo.CurrentCulture) + "+" + n.ToString(CultureInfo.CurrentCulture) + "/" + d.ToString(CultureInfo.CurrentCulture)
        : n.ToString(CultureInfo.CurrentCulture) + "/" + d.ToString(CultureInfo.CurrentCulture);
    }

    string core;
    if (wholeFeet > 0)
    {
      core = (inchWhole == 0 && num == 0)
        ? wholeFeet.ToString(CultureInfo.CurrentCulture) + "'"
        : wholeFeet.ToString(CultureInfo.CurrentCulture) + "'" + inchText + "\"";
    }
    else
    {
      core = inchText + "\"";
    }

    return negative ? "-" + core : core;
  }

  private static bool IsFeetAndInchesUnitToken(string token)
  {
    return token.Equals("ft+in", StringComparison.OrdinalIgnoreCase)
      || token.Equals("feet & inches", StringComparison.OrdinalIgnoreCase)
      || token.Equals("feet and inches", StringComparison.OrdinalIgnoreCase)
      || token.Equals("ft&in", StringComparison.OrdinalIgnoreCase)
      || token.Equals("ft/in", StringComparison.OrdinalIgnoreCase);
  }

  private static string NormalizeUnitToken(string? token)
  {
    return (token ?? string.Empty).Trim().ToLowerInvariant();
  }

  private static int Gcd(int a, int b)
  {
    a = Math.Abs(a);
    b = Math.Abs(b);
    while (b != 0)
    {
      int t = a % b;
      a = b;
      b = t;
    }

    return a == 0 ? 1 : a;
  }

  private static Color ToEtoColor(System.Drawing.Color c)
  {
    return Color.FromArgb(c.ToArgb());
  }

  private static System.Drawing.Color ToDrawingColor(Color c)
  {
    return System.Drawing.Color.FromArgb(255, c.Rb, c.Gb, c.Bb);
  }

  private static void SetUnitDropOptions(DropDown drop)
  {
    drop.DataStore = new List<string> { "in", "ft", "ft+in", "yds", "mm", "cm", "m" };
    drop.SelectedIndex = 0;
  }

  private static void SetFormatDropOptions(DropDown drop)
  {
    drop.DataStore = new List<string> { "Decimal", "Fractional" };
    drop.SelectedIndex = 0;
  }

  private static void SetPrecisionDropOptions(DropDown drop, string kindToken, int selectedPrecision)
  {
    List<string> options;
    if (kindToken.Equals("feet", StringComparison.OrdinalIgnoreCase))
    {
      options = new List<string>
      {
        "0'-1\"",
        "0'-1/2\"",
        "0'-1/4\"",
        "0'-1/8\"",
        "0'-1/16\"",
        "0'-1/32\"",
        "0'-1/64\"",
        "0'-1/128\"",
        "0'-1/256\"",
      };
    }
    else if (kindToken.Equals("fractional", StringComparison.OrdinalIgnoreCase))
    {
      options = new List<string> { "1", "1/2", "1/4", "1/8", "1/16", "1/32", "1/64", "1/128", "1/256" };
    }
    else
    {
      options = new List<string>
      {
        "1",
        "0.1",
        "0.01",
        "0.001",
        "0.0001",
        "0.00001",
        "0.000001",
        "0.0000001",
        "0.00000001",
      };
    }

    drop.DataStore = options;
    drop.SelectedIndex = Math.Max(0, Math.Min(8, selectedPrecision));
  }

  private string GetSelectedFormatToken()
  {
    string selected = (_infoFormatDrop.SelectedValue?.ToString() ?? "Decimal").Trim().ToLowerInvariant();
    if (selected.Equals("feet & inches", StringComparison.OrdinalIgnoreCase)
      || selected.Equals("feet and inches", StringComparison.OrdinalIgnoreCase))
      return "fractional";

    return selected.Equals("fractional", StringComparison.OrdinalIgnoreCase)
      ? "fractional"
      : "decimal";
  }

  private string GetWorkspaceFormatToken(RhinoDoc doc)
  {
    try
    {
      string sample = doc.FormatNumber(1.5) ?? string.Empty;
      if (sample.Contains("'", StringComparison.Ordinal) || sample.Contains('"', StringComparison.Ordinal))
        return "fractional";
      if (sample.Contains("/", StringComparison.Ordinal))
        return "fractional";
    }
    catch
    {
    }

    return "decimal";
  }

  private int GetInfoPrecisionBitsFromDocOrModel(RhinoDoc doc)
  {
    return Math.Max(0, Math.Min(8, doc.ModelDistanceDisplayPrecision));
  }

  private bool AnyDisplayedUnitDropIsFeetAndInches()
  {
    if (IsFeetAndInchesUnitToken(NormalizeUnitToken(_curveMetricUnitDrop.SelectedValue?.ToString())))
      return true;
    if (IsFeetAndInchesUnitToken(NormalizeUnitToken(_totalLengthUnitDrop.SelectedValue?.ToString())))
      return true;
    if (_radiusUnitDrop.Visible && IsFeetAndInchesUnitToken(NormalizeUnitToken(_radiusUnitDrop.SelectedValue?.ToString())))
      return true;
    if (_diameterUnitDrop.Visible && IsFeetAndInchesUnitToken(NormalizeUnitToken(_diameterUnitDrop.SelectedValue?.ToString())))
      return true;

    return false;
  }

  private string GetSelectedPrecisionKindToken()
  {
    if (AnyDisplayedUnitDropIsFeetAndInches())
      return "feet";

    return GetSelectedFormatToken();
  }

  private void RefreshPrecisionDropOptions()
  {
    int selectedBits = GetSelectedPrecision(_doc?.ModelDistanceDisplayPrecision ?? 3, 8);
    bool wasUpdating = _isUpdatingUi;
    _isUpdatingUi = true;
    try
    {
      SetPrecisionDropOptions(_infoPrecisionDrop, GetSelectedPrecisionKindToken(), selectedBits);
    }
    finally
    {
      _isUpdatingUi = wasUpdating;
    }
  }

  private void EnsureDocUnitPrefsLoaded(RhinoDoc? doc)
  {
    if (doc == null)
      return;

    if (_unitPrefsLoadedDocSerial == doc.RuntimeSerialNumber)
      return;

    _unitPrefsLoadedDocSerial = doc.RuntimeSerialNumber;

    bool wasUpdating = _isUpdatingUi;
    _isUpdatingUi = true;
    try
    {
      SetDropFromDocOrModel(doc, _curveMetricUnitDrop, "CurveMetric");
      SetDropFromDocOrModel(doc, _radiusUnitDrop, "Radius");
      SetDropFromDocOrModel(doc, _diameterUnitDrop, "Diameter");
      SetDropFromDocOrModel(doc, _totalLengthUnitDrop, "TotalLength");
      SelectDropByText(_infoFormatDrop, GetWorkspaceFormatToken(doc));
      SetPrecisionDropOptions(_infoPrecisionDrop, GetSelectedPrecisionKindToken(), GetInfoPrecisionBitsFromDocOrModel(doc));
    }
    finally
    {
      _isUpdatingUi = wasUpdating;
    }
  }

  private static void SetDropFromDocValue(RhinoDoc doc, DropDown drop, string key, string fallback)
  {
    string? stored = GetDocUnitPref(doc, key);
    string preferred = !string.IsNullOrWhiteSpace(stored)
      ? stored.Trim().ToLowerInvariant()
      : fallback;
    SelectDropByText(drop, preferred);
  }

  private void SetDropFromDocOrModel(RhinoDoc doc, DropDown drop, string key)
  {
    string? stored = GetDocUnitPref(doc, key);
    string preferred = !string.IsNullOrWhiteSpace(stored)
      ? stored.Trim().ToLowerInvariant()
      : UnitTokenFromModel(doc.ModelUnitSystem);

    SelectDropByText(drop, preferred);
  }

  private static string UnitTokenFromModel(UnitSystem model)
  {
    return model switch
    {
      UnitSystem.Inches => "in",
      UnitSystem.Feet => "ft",
      UnitSystem.Yards => "yds",
      UnitSystem.Millimeters => "mm",
      UnitSystem.Centimeters => "cm",
      UnitSystem.Meters => "m",
      _ => "in"
    };
  }

  private static void SelectDropByText(DropDown drop, string token)
  {
    string normalized = token.Trim().ToLowerInvariant();
    if (IsFeetAndInchesUnitToken(normalized))
      normalized = "ft+in";

    if (drop.DataStore is IEnumerable<string> items)
    {
      var list = items.ToList();
      int idx = list.FindIndex(s => string.Equals(s?.Trim(), normalized, StringComparison.OrdinalIgnoreCase));
      if (idx >= 0)
      {
        drop.SelectedIndex = idx;
        return;
      }
    }

    drop.SelectedIndex = 0;
  }

  private void OnUnitDropChanged(DropDown drop, string key)
  {
    if (_isUpdatingUi)
      return;

    if (_doc != null)
      SetDocUnitPref(_doc, key, (drop.SelectedValue?.ToString() ?? "").Trim().ToLowerInvariant());

    RefreshPrecisionDropOptions();
    if (_doc != null)
      SetDocUnitPref(_doc, "Precision", GetSelectedPrecision(_doc.ModelDistanceDisplayPrecision, 8).ToString(CultureInfo.InvariantCulture));

    RefreshFromCurrentSelection();
  }

  private void OnInfoFormatChanged()
  {
    if (_isUpdatingUi)
      return;

    if (_doc != null)
      SetDocUnitPref(_doc, "Format", GetSelectedFormatToken());

    RefreshPrecisionDropOptions();
    if (_doc != null)
      SetDocUnitPref(_doc, "Precision", GetSelectedPrecision(_doc.ModelDistanceDisplayPrecision, 8).ToString(CultureInfo.InvariantCulture));

    RefreshFromCurrentSelection();
  }

  private void OnInfoPrecisionChanged()
  {
    if (_isUpdatingUi)
      return;

    if (_doc != null)
      SetDocUnitPref(_doc, "Precision", GetSelectedPrecision(_doc.ModelDistanceDisplayPrecision, 8).ToString(CultureInfo.InvariantCulture));

    RefreshFromCurrentSelection();
  }

  private static string? GetDocUnitPref(RhinoDoc doc, string key)
  {
    try
    {
      object? strings = doc.GetType().GetProperty("Strings", BindingFlags.Public | BindingFlags.Instance)?.GetValue(doc, null);
      if (strings == null)
        return null;

      Type t = strings.GetType();

      MethodInfo? getBySection = t.GetMethod("GetValue", new[] { typeof(string), typeof(string) });
      if (getBySection != null)
      {
        object? val = getBySection.Invoke(strings, new object[] { DocUnitPrefsSection, key });
        string? s = val?.ToString();
        if (!string.IsNullOrWhiteSpace(s))
          return s;
      }

      MethodInfo? getFlat = t.GetMethod("GetValue", new[] { typeof(string) });
      if (getFlat != null)
      {
        object? val = getFlat.Invoke(strings, new object[] { DocUnitPrefsSection + "." + key });
        string? s = val?.ToString();
        if (!string.IsNullOrWhiteSpace(s))
          return s;
      }
    }
    catch
    {
    }

    return null;
  }

  private static void SetDocUnitPref(RhinoDoc doc, string key, string value)
  {
    try
    {
      object? strings = doc.GetType().GetProperty("Strings", BindingFlags.Public | BindingFlags.Instance)?.GetValue(doc, null);
      if (strings == null)
        return;

      Type t = strings.GetType();

      MethodInfo? setBySection = t.GetMethod("SetString", new[] { typeof(string), typeof(string), typeof(string) });
      if (setBySection != null)
      {
        setBySection.Invoke(strings, new object[] { DocUnitPrefsSection, key, value });
        return;
      }

      MethodInfo? setFlat = t.GetMethod("SetString", new[] { typeof(string), typeof(string) });
      if (setFlat != null)
      {
        setFlat.Invoke(strings, new object[] { DocUnitPrefsSection + "." + key, value });
      }
    }
    catch
    {
    }
  }

  private static UnitSystem GetSelectedUnitSystem(DropDown drop, RhinoDoc? doc)
  {
    if (doc == null)
      return UnitSystem.None;

    string selected = NormalizeUnitToken(drop.SelectedValue?.ToString());
    return selected switch
    {
      "in" => UnitSystem.Inches,
      "ft" => UnitSystem.Feet,
      "ft+in" => UnitSystem.Feet,
      "feet & inches" => UnitSystem.Feet,
      "feet and inches" => UnitSystem.Feet,
      "yds" => UnitSystem.Yards,
      "mm" => UnitSystem.Millimeters,
      "cm" => UnitSystem.Centimeters,
      "m" => UnitSystem.Meters,
      _ => doc.ModelUnitSystem
    };
  }

  private static double ConvertLength(double value, UnitSystem from, UnitSystem to)
  {
    if (from == to)
      return value;
    return value * RhinoMath.UnitScale(from, to);
  }

  private static string NormalizeByText(string value)
  {
    string v = SafeString(value).Trim();
    if (string.IsNullOrEmpty(v) || v == "-")
      return "-";

    if (v.Contains("Layer", StringComparison.OrdinalIgnoreCase))
      return "By Layer";
    if (v.Contains("Parent", StringComparison.OrdinalIgnoreCase))
      return "By Parent";
    if (v.Contains("Object", StringComparison.OrdinalIgnoreCase))
      return "By Object";
    if (v.Contains("View", StringComparison.OrdinalIgnoreCase))
      return "By View";

    return v;
  }

  private static string NormalizeDisplayModeText(string value)
  {
    string v = SafeString(value).Trim();
    if (string.IsNullOrEmpty(v) || v == "-")
      return "-";
    if (v.Contains("View", StringComparison.OrdinalIgnoreCase))
      return "By View";
    return v;
  }

  private static string NormalizeLinetypeText(string value)
  {
    string v = SafeString(value).Trim();
    if (string.IsNullOrEmpty(v) || v == "-")
      return "-";
    if (v.Contains("Object", StringComparison.OrdinalIgnoreCase) || v.Contains("Custom", StringComparison.OrdinalIgnoreCase))
      return "Custom...";
    return v;
  }

  private static string NormalizePrintWidthText(string value)
  {
    string v = SafeString(value).Trim();
    if (string.IsNullOrEmpty(v) || v == "-")
      return "-";
    if (v.Contains("Object", StringComparison.OrdinalIgnoreCase) || v.Contains("Custom", StringComparison.OrdinalIgnoreCase))
      return "Default";
    return v;
  }

  private static string NormalizeSectionStyleText(string value)
  {
    string v = SafeString(value).Trim();
    if (string.IsNullOrEmpty(v) || v == "-")
      return "-";
    if (v.Contains("Layer", StringComparison.OrdinalIgnoreCase))
      return "By Layer";
    if (v.Contains("Parent", StringComparison.OrdinalIgnoreCase))
      return "By Parent";
    if (v.Contains("Section", StringComparison.OrdinalIgnoreCase) || v.Contains("Clipping", StringComparison.OrdinalIgnoreCase))
      return "By Clipping Plane";
    if (v.Contains("Object", StringComparison.OrdinalIgnoreCase) || v.Contains("Custom", StringComparison.OrdinalIgnoreCase))
      return "Custom...";
    return v;
  }

  private static void SetDisplayModeDropValue(DropDown dropDown, RhinoDoc? doc, string value)
  {
    var items = new List<string> { "By View" };
    try
    {
      var allModes = Rhino.Display.DisplayModeDescription.GetDisplayModes()
        .Where(m => m != null)
        .ToList();

      var baseIds = new HashSet<Guid>
      {
        Rhino.Display.DisplayModeDescription.WireframeId,
        Rhino.Display.DisplayModeDescription.ShadedId,
        Rhino.Display.DisplayModeDescription.RenderedId,
        Rhino.Display.DisplayModeDescription.GhostedId,
        Rhino.Display.DisplayModeDescription.XRayId,
        Rhino.Display.DisplayModeDescription.ArtisticId
      };

      var baseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var baseMode in allModes.Where(m => baseIds.Contains(m.Id)))
      {
        string en = baseMode.EnglishName ?? string.Empty;
        string local = baseMode.LocalName ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(en))
          baseNames.Add(en);
        if (!string.IsNullOrWhiteSpace(local))
          baseNames.Add(local);
      }

      foreach (var mode in allModes)
      {
        if (!mode.InMenu)
          continue;

        string name = mode.EnglishName;
        if (string.IsNullOrWhiteSpace(name))
          name = mode.LocalName;
        if (string.IsNullOrWhiteSpace(name) || items.Contains(name))
          continue;

        bool isBase = baseIds.Contains(mode.Id);
        bool hasBaseName = baseNames.Any(baseName =>
          !string.IsNullOrWhiteSpace(baseName)
          && name.IndexOf(baseName, StringComparison.OrdinalIgnoreCase) >= 0);

        if (!isBase && !hasBaseName)
          continue;

        items.Add(name);
      }
    }
    catch
    {
    }

    if (!items.Contains("Multiple Viewports..."))
      items.Add("Multiple Viewports...");

    string normalized = NormalizeDisplayModeText(value);
    if (!items.Contains(normalized) && normalized != "-")
      items.Insert(1, normalized);
    if (normalized == "-")
      items.Insert(0, "-");

    dropDown.DataStore = items;
    dropDown.SelectedIndex = Math.Max(0, items.IndexOf(normalized == "-" ? "-" : normalized));
  }

  private static void SetLinetypeDropValue(DropDown dropDown, RhinoDoc? doc, string value)
  {
    var items = new List<string>
    {
      "By Layer",
      "By Parent",
      "Continuous",
      "Border",
      "Center",
      "DashDot",
      "Dashed",
      "Dots",
      "Hidden",
      "Custom..."
    };
    try
    {
      // Keep screenshot-defined ordering and labels deterministic.
    }
    catch
    {
    }

    string normalized = NormalizeLinetypeText(value);
    if (!items.Contains(normalized) && normalized != "-")
      items.Insert(items.Count - 1, normalized);
    if (normalized == "-")
      items.Insert(0, "-");

    dropDown.DataStore = items;
    dropDown.SelectedIndex = Math.Max(0, items.IndexOf(normalized == "-" ? "-" : normalized));
  }

  private static void SetPrintWidthDropValue(DropDown dropDown, string value)
  {
    var items = new List<string>
    {
      "By Layer", "By Parent", "Default", "Hairline",
      "0.13", "0.18", "0.25", "0.3", "0.35", "0.5", "0.6", "0.7", "0.8", "0.9", "1", "1.2", "1.4", "2",
      "No Print"
    };

    string normalized = NormalizePrintWidthText(value);
    if (!items.Contains(normalized) && normalized != "-")
      items.Insert(items.Count, normalized);
    if (normalized == "-")
      items.Insert(0, "-");

    dropDown.DataStore = items;
    dropDown.SelectedIndex = Math.Max(0, items.IndexOf(normalized == "-" ? "-" : normalized));
  }

  private static string NormalizeDisplayColorText(string value)
  {
    string v = SafeString(value).Trim();
    if (string.IsNullOrEmpty(v) || v == "-")
      return "-";
    if (v.Contains("Layer", StringComparison.OrdinalIgnoreCase))
      return "By Layer";
    if (v.Contains("Parent", StringComparison.OrdinalIgnoreCase))
      return "By Parent";
    if (v.Contains("Material", StringComparison.OrdinalIgnoreCase))
      return "By Material";
    if (v.Contains("Object", StringComparison.OrdinalIgnoreCase) || v.Contains("Custom", StringComparison.OrdinalIgnoreCase))
      return "Custom...";
    return v;
  }

  private static string NormalizePrintColorText(string value)
  {
    string v = SafeString(value).Trim();
    if (string.IsNullOrEmpty(v) || v == "-")
      return "-";
    if (v.Contains("Display", StringComparison.OrdinalIgnoreCase))
      return "By Display";
    if (v.Contains("Object", StringComparison.OrdinalIgnoreCase) || v.Contains("Custom", StringComparison.OrdinalIgnoreCase))
      return "Custom...";
    if (v.Contains("Layer", StringComparison.OrdinalIgnoreCase))
      return "By Layer";
    if (v.Contains("Parent", StringComparison.OrdinalIgnoreCase))
      return "By Parent";
    return v;
  }

  private static ObjectColorSource ParseDisplayColorSource(string text)
  {
    string t = SafeString(text).Trim();
    if (t.Equals("By Object", StringComparison.OrdinalIgnoreCase) || t.Contains("Object", StringComparison.OrdinalIgnoreCase) || t.Contains("Custom", StringComparison.OrdinalIgnoreCase))
      return ObjectColorSource.ColorFromObject;
    if (t.Contains("Material", StringComparison.OrdinalIgnoreCase))
      return ObjectColorSource.ColorFromMaterial;
    if (t.Contains("Parent", StringComparison.OrdinalIgnoreCase))
      return ObjectColorSource.ColorFromParent;
    return ObjectColorSource.ColorFromLayer;
  }

  private static ObjectPlotColorSource ParsePrintColorSource(string text)
  {
    string t = SafeString(text).Trim();
    if (t.Contains("Display", StringComparison.OrdinalIgnoreCase))
      return ObjectPlotColorSource.PlotColorFromDisplay;
    if (t.Equals("By Object", StringComparison.OrdinalIgnoreCase) || t.Contains("Object", StringComparison.OrdinalIgnoreCase) || t.Contains("Custom", StringComparison.OrdinalIgnoreCase))
      return ObjectPlotColorSource.PlotColorFromObject;
    if (t.Contains("Parent", StringComparison.OrdinalIgnoreCase))
      return ObjectPlotColorSource.PlotColorFromParent;
    return ObjectPlotColorSource.PlotColorFromLayer;
  }

  private sealed class LayerDropItem
  {
    public LayerDropItem(string name, int layerIndex, Guid layerId, string displayText, Color color, bool isToggle, bool expanded = true)
    {
      Name = name;
      LayerIndex = layerIndex;
      LayerId = layerId;
      DisplayText = displayText;
      IsToggle = isToggle;
      Swatch = isToggle ? CreateToggleSwatch(expanded) : CreateColorSwatch(color);
    }

    public string Name { get; }
    public int LayerIndex { get; }
    public Guid LayerId { get; }
    public string DisplayText { get; }
    public bool IsToggle { get; }
    public Image Swatch { get; }

    public override string ToString() => Name;

    private static Bitmap CreateSpacerSwatch()
    {
      var bitmap = new Bitmap(18, 18, PixelFormat.Format32bppRgba);
      using (var g = new Graphics(bitmap))
      {
        g.Clear(Colors.Transparent);
      }
      return bitmap;
    }

    private static Bitmap CreateToggleSwatch(bool expanded)
    {
      var bitmap = new Bitmap(18, 18, PixelFormat.Format32bppRgba);
      using (var g = new Graphics(bitmap))
      {
        g.Clear(Colors.Transparent);
        var c = Color.FromArgb(80, 80, 80);
        if (expanded)
        {
          g.FillPolygon(c, new[]
          {
            new PointF(4, 6),
            new PointF(14, 6),
            new PointF(9, 12)
          });
        }
        else
        {
          g.FillPolygon(c, new[]
          {
            new PointF(6, 4),
            new PointF(12, 9),
            new PointF(6, 14)
          });
        }
      }
      return bitmap;
    }

    private static Bitmap CreateColorSwatch(Color color)
    {
      var bitmap = new Bitmap(18, 18, PixelFormat.Format32bppRgba);
      using (var g = new Graphics(bitmap))
      {
        // Checkerboard underlay for alpha-aware swatches.
        g.FillRectangle(Color.FromArgb(242, 242, 242), 0, 0, 9, 9);
        g.FillRectangle(Color.FromArgb(191, 191, 191), 9, 0, 9, 9);
        g.FillRectangle(Color.FromArgb(191, 191, 191), 0, 9, 9, 9);
        g.FillRectangle(Color.FromArgb(242, 242, 242), 9, 9, 9, 9);
        g.FillRectangle(color, 0, 0, 18, 18);
        g.DrawRectangle(Colors.Black, 0, 0, 17, 17);
      }
      return bitmap;
    }
  }

  private sealed class FocusHighlightConduit : DisplayConduit
  {
    private RhinoObject? _obj;

    public void SetObject(RhinoObject obj) => _obj = obj;
    public void Clear() => _obj = null;

    protected override void DrawOverlay(DrawEventArgs e)
    {
      base.DrawOverlay(e);
      if (_obj?.Geometry == null)
        return;

      var color = System.Drawing.Color.FromArgb(255, 235, 130, 20);
      switch (_obj.Geometry)
      {
        case Curve crv:
          e.Display.DrawCurve(crv, color, 3);
          break;
        case Rhino.Geometry.Brep brep:
          e.Display.DrawBrepWires(brep, color, 1);
          break;
        case Rhino.Geometry.Extrusion ext:
          var extBrep = ext.ToBrep(true);
          if (extBrep != null) e.Display.DrawBrepWires(extBrep, color, 1);
          break;
        case Rhino.Geometry.Mesh mesh:
          e.Display.DrawMeshWires(mesh, color, 2);
          break;
        default:
          var bb = _obj.Geometry.GetBoundingBox(false);
          if (bb.IsValid)
            e.Display.DrawBox(new Rhino.Geometry.Box(bb), color, 2);
          break;
      }
    }
  }

  private sealed class RectangleSideHighlightConduit : DisplayConduit
  {
    private readonly List<Line> _segments = new();

    public void SetSegments(IEnumerable<Line> segments)
    {
      _segments.Clear();
      if (segments != null)
        _segments.AddRange(segments);
    }

    protected override void DrawOverlay(DrawEventArgs e)
    {
      base.DrawOverlay(e);

      if (_segments.Count == 0)
        return;

      var color = System.Drawing.Color.FromArgb(255, 230, 140, 20);
      foreach (var segment in _segments)
        e.Display.DrawLine(segment.From, segment.To, color, 3);
    }
  }

  private static void SetLayerDropValue(DropDown dropDown, RhinoDoc? doc, string current, Dictionary<Guid, bool> expandedState)
  {
    var list = new List<LayerDropItem>();
    string normalized = SafeString(current).Trim();
    if (string.IsNullOrEmpty(normalized))
      normalized = "-";

    if (doc != null)
    {
      try
      {
        var all = doc.Layers.Cast<Layer>()
          .Where(l => l != null && !l.IsDeleted && !string.IsNullOrWhiteSpace(l.FullPath))
          .OrderBy(l => l.SortIndex)
          .ToList();

        var byParent = new Dictionary<Guid, List<Layer>>();
        foreach (var layer in all)
        {
          Guid parentId = layer.ParentLayerId;
          if (!byParent.TryGetValue(parentId, out var children))
          {
            children = new List<Layer>();
            byParent[parentId] = children;
          }
          children.Add(layer);
        }

        void AddChildrenFlat(Guid parentId, int depth)
        {
          if (!byParent.TryGetValue(parentId, out var children))
            return;

          foreach (var child in children.OrderBy(c => c.SortIndex))
          {
            string indent = depth <= 0 ? string.Empty : new string(' ', depth * 2);
            var swatchColor = ResolveLayerDisplayColor(doc, child);
            list.Add(new LayerDropItem(child.FullPath, child.Index, child.Id, indent + child.Name, ToEtoColor(swatchColor), false));
            AddChildrenFlat(child.Id, depth + 1);
          }
        }

        AddChildrenFlat(Guid.Empty, 0);
      }
      catch
      {
      }
    }

    if (!list.Any(i => i.Name == normalized))
      list.Insert(0, new LayerDropItem(normalized, -1, Guid.Empty, normalized, Colors.White, false));

    int newSelectedIndex = Math.Max(0, list.FindIndex(i => i.Name == normalized));

    bool sameData = false;
    if (dropDown.DataStore is IEnumerable<LayerDropItem> existing)
    {
      var existingList = existing.ToList();
      if (existingList.Count == list.Count)
      {
        sameData = true;
        for (int i = 0; i < list.Count; i++)
        {
          var a = existingList[i];
          var b = list[i];
          if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal)
            || !string.Equals(a.DisplayText, b.DisplayText, StringComparison.Ordinal)
            || a.LayerIndex != b.LayerIndex)
          {
            sameData = false;
            break;
          }
        }
      }
    }

    if (sameData)
    {
      if (dropDown.SelectedIndex != newSelectedIndex)
        dropDown.SelectedIndex = newSelectedIndex;
      return;
    }

    dropDown.DataStore = list;
    dropDown.ItemTextBinding = Binding.Property<LayerDropItem, string>(i => i.DisplayText);
    dropDown.ItemImageBinding = Binding.Property<LayerDropItem, Image>(i => i.Swatch);
    dropDown.SelectedIndex = newSelectedIndex;
  }

  private static System.Drawing.Color ResolveLayerDisplayColor(RhinoDoc doc, Layer layer)
  {
    try
    {
      var activeView = doc.Views.ActiveView;
      if (activeView != null)
      {
        var perViewport = layer.PerViewportColor(activeView.ActiveViewportID);
        if (perViewport != System.Drawing.Color.Empty)
          return perViewport;
      }
    }
    catch
    {
    }

    return layer.Color;
  }

  private static void SetDropValue(DropDown dropDown, string value, params string[] options)
  {
    string normalized = SafeString(value).Trim();
    if (string.IsNullOrEmpty(normalized))
      normalized = "-";

    var items = new List<string>();
    if (options != null)
    {
      foreach (string option in options)
      {
        if (!string.IsNullOrWhiteSpace(option) && !items.Contains(option))
          items.Add(option);
      }
    }

    if (!items.Contains(normalized))
      items.Insert(0, normalized);

    dropDown.DataStore = items;
    dropDown.SelectedIndex = Math.Max(0, items.IndexOf(normalized));
  }

  private static TableRow NewValueRow(string name, Control value)
  {
    value.Height = RowHeight;
    if (value.Width <= 0)
      value.Width = ValueWidth;
    return NewBorderedRow(new Label { Text = name, Width = LabelWidth }, value);
  }

  private static TableRow NewControlWithButtonRow(string name, Control control, Button button)
  {
    control.Height = RowHeight;
    if (control.Width <= 0)
      control.Width = ValueWidth - 34;
    button.Height = RowHeight;
    if (button.Width <= 0)
      button.Width = 28;

    var right = new StackLayout
    {
      Orientation = Orientation.Horizontal,
      Spacing = 3,
      Items =
      {
        new StackLayoutItem(button, false),
        new StackLayoutItem(control, true)
      }
    };

    return NewBorderedRow(new Label { Text = name, Width = LabelWidth }, right);
  }

  private static TableRow NewLabelWithButtonRow(string name, Label valueLabel, Button button)
  {
    valueLabel.Height = RowHeight;
    if (valueLabel.Width <= 0)
      valueLabel.Width = ValueWidth - 34;
    button.Height = RowHeight;
    if (button.Width <= 0)
      button.Width = 28;

    var right = new StackLayout
    {
      Orientation = Orientation.Horizontal,
      Spacing = 3,
      Items =
      {
        new StackLayoutItem(valueLabel, true),
        new StackLayoutItem(button, false)
      }
    };

    return NewBorderedRow(new Label { Text = name, Width = LabelWidth }, right);
  }

  private static TableRow NewValueWithUnitDropRow(string name, Control value, DropDown unitDrop)
  {
    value.Height = RowHeight;
    unitDrop.Height = RowHeight;
    var right = new StackLayout
    {
      Orientation = Orientation.Horizontal,
      Spacing = 3,
      Items =
      {
        new StackLayoutItem(new Panel(), true),
        new StackLayoutItem(value, false),
        new StackLayoutItem(unitDrop, false)
      }
    };

    return NewBorderedRow(new Label { Text = name, Width = LabelWidth }, right);
  }

  private static TableRow NewDynamicValueRow(Label label, Control value)
  {
    value.Height = RowHeight;
    if (value.Width <= 0)
      value.Width = ValueWidth;
    return NewBorderedRow(label, value);
  }

  private static TableRow NewDynamicValueWithUnitDropRow(Label label, Control value, DropDown unitDrop)
  {
    value.Height = RowHeight;
    unitDrop.Height = RowHeight;
    var right = new StackLayout
    {
      Orientation = Orientation.Horizontal,
      Spacing = 3,
      Items =
      {
        new StackLayoutItem(new Panel(), true),
        new StackLayoutItem(value, false),
        new StackLayoutItem(unitDrop, false)
      }
    };

    return NewBorderedRow(label, right);
  }

  private static TableRow NewCheckRow(string name, CheckBox checkBox)
  {
    checkBox.Height = RowHeight;
    return NewBorderedRow(new Label { Text = name, Width = LabelWidth }, checkBox);
  }

  private static TableRow NewControlRow(string name, Control control)
  {
    control.Height = RowHeight;
    if (control.Width <= 0)
      control.Width = ValueWidth;
    return NewBorderedRow(new Label { Text = name, Width = LabelWidth }, control);
  }

  private static TableRow NewBorderedRow(Control left, Control right)
  {
    return new TableRow(
      new TableCell(left, false),
      new TableCell(right, true)
    );
  }

  private static TextBox NewValueBox()
  {
    return new TextBox
    {
      ReadOnly = true
    };
  }

  private static Label NewValueLabel()
  {
    return new Label
    {
      VerticalAlignment = VerticalAlignment.Center
    };
  }

  private static NumericStepper NewNumericStepper(double min, double max, double increment, int decimalPlaces)
  {
    return new NumericStepper
    {
      MinValue = min,
      MaxValue = max,
      Increment = increment,
      DecimalPlaces = decimalPlaces,
      Width = NumericValueWidth
    };
  }

  private static DropDown NewReadOnlyDropDown()
  {
    return new DropDown
    {
      Enabled = true
    };
  }

  private static DropDown NewUnitDropDown()
  {
    return new DropDown
    {
      Enabled = true,
      Width = InfoUnitDropWidth
    };
  }

  private static DropDown NewFormatDropDown()
  {
    return new DropDown
    {
      Enabled = true,
      Width = InfoFormatDropWidth
    };
  }

  private static DropDown NewPrecisionDropDown()
  {
    return new DropDown
    {
      Enabled = true,
      Width = InfoPrecisionDropWidth
    };
  }

  private static Control NewSectionLabel(string text, params Control[] rightControls)
  {
    var rightGroup = new StackLayout
    {
      Orientation = Orientation.Horizontal,
      Spacing = 4,
      Items = { }
    };

    foreach (Control rightControl in rightControls)
    {
      if (rightControl != null)
        rightGroup.Items.Add(new StackLayoutItem(rightControl, false));
    }

    return new TableLayout
    {
      Padding = new Padding(0, 0, 0, 0),
      Spacing = new Size(4, 0),
      Rows =
      {
        new TableRow(
          new TableCell(new Label { Text = text }, false),
          new TableCell(new Panel(), true),
          new TableCell(rightGroup, false)
        )
      }
    };
  }

  private static Control NewRule()
  {
    return new Panel
    {
      Height = 1,
      BackgroundColor = Colors.Gray
    };
  }

  private static CheckBox NewCheckBox() => new CheckBox();

  private static Bitmap CreateButtonColorSwatch(Color color)
  {
    const int size = 16;
    var bitmap = new Bitmap(size, size, PixelFormat.Format32bppRgba);
    using (var g = new Graphics(bitmap))
    {
      var light = Color.FromArgb(242, 242, 242);
      var dark = Color.FromArgb(191, 191, 191);
      g.FillRectangle(light, 0, 0, 8, 8);
      g.FillRectangle(dark, 8, 0, 8, 8);
      g.FillRectangle(dark, 0, 8, 8, 8);
      g.FillRectangle(light, 8, 8, 8, 8);
      g.FillRectangle(color, 0, 0, size, size);
      g.DrawRectangle(Colors.Black, 0, 0, size - 1, size - 1);
    }
    return bitmap;
  }

  private static Button NewActionButton(string text)
  {
    return new Button
    {
      Text = text,
      Width = 28
    };
  }

  private static TextBox NewSelectableLabelBox()
  {
    return new TextBox
    {
      ReadOnly = true,
      BackgroundColor = Colors.Transparent,
      TabIndex = -1
    };
  }

  private static Button NewColorSwatchButton()
  {
    return new Button
    {
      Text = string.Empty,
      Width = 28,
      Enabled = false,
      BackgroundColor = Colors.Gray
    };
  }

  private static bool SupportsCustomMesh(RhinoObject obj)
  {
    return obj.Geometry is Brep
      || obj.Geometry is Extrusion
      || obj.Geometry is Mesh
      || obj.Geometry is SubD
      || obj.Geometry is Surface;
  }

  private static bool SupportsIsocurve(RhinoObject obj)
  {
    return obj.Geometry is Brep
      || obj.Geometry is Extrusion
      || obj.Geometry is Surface
      || obj.Geometry is SubD;
  }

  private static void WireSubmitOnEnter(TextBox textBox, Action apply)
  {
    textBox.KeyDown += (_, e) =>
    {
      if (e.KeyData != Keys.Enter)
        return;
      apply();
      e.Handled = true;
    };
  }

  // ── Text section helpers ─────────────────────────────────────────────────

  private static ToggleButton MakeToggleButton(string text, int width = 22, int height = 18)
    => new ToggleButton { Text = text, Width = width, Height = height };

  private TableRow NewTextAlignRow()
  {
    var row = new StackLayout
    {
      Orientation = Orientation.Horizontal,
      Spacing = 2,
      Items =
      {
        new StackLayoutItem(_textAlignLeftBtn, false),
        new StackLayoutItem(_textAlignCenterBtn, false),
        new StackLayoutItem(_textAlignRightBtn, false),
        new StackLayoutItem(_textAlignAutoBtn, false),
        new StackLayoutItem(new Panel { Width = 6 }, false),
        new StackLayoutItem(_textVAlignTopBtn, false),
        new StackLayoutItem(_textVAlignMiddleBtn, false),
        new StackLayoutItem(_textVAlignBottomBtn, false),
      }
    };
    return NewBorderedRow(new Label { Text = "Alignment", Width = LabelWidth }, row);
  }

  private TableRow NewTextStyleRow()
  {
    var row = new StackLayout
    {
      Orientation = Orientation.Horizontal,
      Spacing = 2,
      Items =
      {
        new StackLayoutItem(_textBoldBtn, false),
        new StackLayoutItem(_textItalicBtn, false),
        new StackLayoutItem(_textUnderlineBtn, false),
      }
    };
    return NewBorderedRow(new Label { Text = "Style", Width = LabelWidth }, row);
  }

  private static DimensionStyle GetEffectiveTextDimStyle(TextEntity te, RhinoDoc? doc)
  {
    if (doc == null) return new DimensionStyle();
    var baseId = te.DimensionStyleId != Guid.Empty ? te.DimensionStyleId : doc.DimStyles.Current.Id;
    var parent = doc.DimStyles.FindId(baseId) ?? doc.DimStyles.Current;
    return te.GetDimensionStyle(parent);
  }


  private void ApplyToTextObjects(Action<TextEntity> apply)
  {
    if (_doc == null) return;
    uint undoRecord = _doc.BeginUndoRecord("Object+ Text");
    bool changed = false;
    try
    {
      foreach (var obj in SelectedRhinoObjects())
      {
        if (obj.Geometry is not TextEntity te) continue;
        var dup = te.Duplicate() as TextEntity;
        if (dup == null) continue;
        apply(dup);
        if (_doc.Objects.Replace(obj.Id, dup)) changed = true;
      }
    }
    finally
    {
      if (undoRecord != 0) _doc.EndUndoRecord(undoRecord);
    }
    if (changed)
    {
      _doc.Views.Redraw();
      RefreshFromCurrentSelection();
    }
  }

  private void UpdateTextSection(List<RhinoObject> objectList, RhinoDoc? doc)
  {
    bool isTextOnly = objectList.Count > 0 && objectList.All(o => o.Geometry is TextEntity);
    _infoPlusSection.Visible = !isTextOnly;
    _textSection.Visible = isTextOnly;
    if (!isTextOnly) return;

    _isUpdatingUi = true;
    try
    {
      var texts = objectList
        .Select(o => o.Geometry as TextEntity)
        .Where(t => t != null).Cast<TextEntity>().ToList();
      if (texts.Count == 0) return;

      var fontNames = texts.Select(t =>
        t.FirstCharFont?.EnglishFamilyName ?? t.Font?.EnglishFamilyName ?? ""
      ).Distinct().ToList();
      string fontKey = fontNames.Count == 1 ? fontNames[0] : "";
      _textFontDrop.SelectedKey = fontKey;

      var modelUnits = doc?.ModelUnitSystem ?? UnitSystem.None;
      var units = GetSelectedUnitSystem(_textHeightUnitDrop, doc);
      var heights = texts.Select(t => ConvertLength(GetEffectiveTextDimStyle(t, doc).TextHeight, modelUnits, units)).ToList();
      bool heightsSame = heights.Count > 0 && heights.All(h => RhinoMath.EpsilonEquals(h, heights[0], RhinoMath.SqrtEpsilon));
      _textHeightStepper.Value = heightsSame ? heights[0] : 0;
      _textHeightStepper.Enabled = true;

      var hAligns = texts.Select(t => t.TextHorizontalAlignment).Distinct().ToList();
      _textAlignLeftBtn.Checked = hAligns.Count == 1 && hAligns[0] == TextHorizontalAlignment.Left;
      _textAlignCenterBtn.Checked = hAligns.Count == 1 && hAligns[0] == TextHorizontalAlignment.Center;
      _textAlignRightBtn.Checked = hAligns.Count == 1 && hAligns[0] == TextHorizontalAlignment.Right;
      _textAlignAutoBtn.Checked = hAligns.Count == 1 && hAligns[0] == TextHorizontalAlignment.Auto;
      _textContentArea.TextAlignment = (hAligns.Count == 1 && hAligns[0] == TextHorizontalAlignment.Center) ? TextAlignment.Center
                                     : (hAligns.Count == 1 && hAligns[0] == TextHorizontalAlignment.Right)  ? TextAlignment.Right
                                     : TextAlignment.Left;

      var vAligns = texts.Select(t => t.TextVerticalAlignment).Distinct().ToList();
      _textVAlignTopBtn.Checked = vAligns.Count == 1 && vAligns[0] == TextVerticalAlignment.Top;
      _textVAlignMiddleBtn.Checked = vAligns.Count == 1 && vAligns[0] == TextVerticalAlignment.Middle;
      _textVAlignBottomBtn.Checked = vAligns.Count == 1 && vAligns[0] == TextVerticalAlignment.Bottom;

      var bolds = texts.Select(t => t.IsAllBold()).Distinct().ToList();
      var italics = texts.Select(t => t.IsAllItalic()).Distinct().ToList();
      var underlines = texts.Select(t => t.IsAllUnderlined()).Distinct().ToList();
      _textBoldBtn.Checked = bolds.Count == 1 && bolds[0];
      _textItalicBtn.Checked = italics.Count == 1 && italics[0];
      _textUnderlineBtn.Checked = underlines.Count == 1 && underlines[0];

      {
        bool tb = bolds.Count == 1 && bolds[0];
        bool ti = italics.Count == 1 && italics[0];
        bool tu = underlines.Count == 1 && underlines[0];
        string tf = fontKey.Length > 0 ? fontKey : new Eto.Drawing.Font(SystemFont.Default).FamilyName;
        var ts = (tb ? FontStyle.Bold : FontStyle.None) | (ti ? FontStyle.Italic : FontStyle.None);
        _textContentArea.Font = new Eto.Drawing.Font(tf, 9f, ts, tu ? FontDecoration.Underline : FontDecoration.None);
      }

      var contents = texts.Select(t => t.PlainText ?? "").Distinct().ToList();
      _textContentArea.Text = contents.Count == 1 ? contents[0] : "";
    }
    finally
    {
      _isUpdatingUi = false;
    }
  }

  private void ApplyTextFont()
  {
    if (_isUpdatingUi || _doc == null) return;
    string face = _textFontDrop.SelectedKey ?? "";
    if (string.IsNullOrEmpty(face)) return;
    ApplyToTextObjects(te => te.SetFacename(true, face));
  }

  private void ApplyTextHeight()
  {
    if (_isUpdatingUi || _doc == null) return;
    double h = _textHeightStepper.Value;
    if (h <= 0) return;
    var modelUnits = _doc.ModelUnitSystem;
    var units = GetSelectedUnitSystem(_textHeightUnitDrop, _doc);
    double hModel = ConvertLength(h, units, modelUnits);
    ApplyToTextObjects(te => { te.TextHeight = Math.Max(hModel, RhinoMath.ZeroTolerance); });
  }

  private void ApplyTextHAlignment(TextHorizontalAlignment alignment)
  {
    if (_isUpdatingUi || _doc == null) return;
    ApplyToTextObjects(te => { te.TextHorizontalAlignment = alignment; });
  }

  private void ApplyTextVAlignment(TextVerticalAlignment alignment)
  {
    if (_isUpdatingUi || _doc == null) return;
    ApplyToTextObjects(te => { te.TextVerticalAlignment = alignment; });
  }

  private void ApplyTextBold()
  {
    if (_isUpdatingUi || _doc == null) return;
    bool allBold = SelectedRhinoObjects().Where(o => o.Geometry is TextEntity).All(o => ((TextEntity)o.Geometry!).IsAllBold());
    ApplyToTextObjects(te => te.SetBold(!allBold));
  }

  private void ApplyTextItalic()
  {
    if (_isUpdatingUi || _doc == null) return;
    bool allItalic = SelectedRhinoObjects().Where(o => o.Geometry is TextEntity).All(o => ((TextEntity)o.Geometry!).IsAllItalic());
    ApplyToTextObjects(te => te.SetItalic(!allItalic));
  }

  private void ApplyTextUnderline()
  {
    if (_isUpdatingUi || _doc == null) return;
    bool allUnderlined = SelectedRhinoObjects().Where(o => o.Geometry is TextEntity).All(o => ((TextEntity)o.Geometry!).IsAllUnderlined());
    ApplyToTextObjects(te => te.SetUnderline(!allUnderlined));
  }

  private void ApplyTextContent()
  {
    if (_isUpdatingUi || _doc == null) return;
    string content = _textContentArea.Text ?? "";
    ApplyToTextObjects(te => { te.PlainText = content; });
  }
}
