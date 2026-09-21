using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Render;
using vObjectPropertiesPlus.Helpers;

namespace vObjectPropertiesPlus.Views;

[SupportedOSPlatform("windows")]
internal sealed class PictureEditorControl : Panel
{
  private const int LabelWidth = 122;
  private const int ValueWidth = 102;
  private const int RowHeight = 20;
  private const string VariesText = "(varies)";
  private const string SharpnessSourceKey = "vObjectPropertiesPlus.Picture.Source";
  private const string SharpnessLevelKey = "vObjectPropertiesPlus.Picture.Sharpness";
  private const string SharpnessAlgorithmKey = "vObjectPropertiesPlus.Picture.SharpenAlgorithm";
  private const string ScaleContentsSettingKey = "Panel.PictureScaleContents";

  private static readonly string[] CopiedTextureParameters =
  {
    "rdk-texture-adjust-clamp",
    "rdk-texture-adjust-clamp-max",
    "rdk-texture-adjust-clamp-min",
    "rdk-texture-adjust-gain",
    "rdk-texture-adjust-gamma",
    "rdk-texture-adjust-grayscale",
    "rdk-texture-adjust-hue-shift",
    "rdk-texture-adjust-invert",
    "rdk-texture-adjust-multiplier",
    "rdk-texture-adjust-saturation",
    "rdk-texture-adjust-scale-to-clamp",
    "has-transparent-color",
    "transparent-color",
    "transparent-color-sensitivity",
    "use-alpha-channel"
  };

  private readonly Func<RhinoDoc?> _documentProvider;
  private readonly Func<IReadOnlyList<RhinoObject>> _targetProvider;
  private readonly TextBox _fileBox;
  private readonly Button _fileButton;
  private readonly NumericStepper _scaleFactorStepper;
  private readonly Button _calibrateScaleButton;
  private readonly CheckBox _scaleContentsCheck;
  private readonly Slider _brightnessSlider;
  private readonly Label _brightnessValueLabel;
  private readonly Slider _contrastSlider;
  private readonly Label _contrastValueLabel;
  private readonly Slider _saturationSlider;
  private readonly Label _saturationValueLabel;
  private readonly Slider _sharpnessSlider;
  private readonly Label _sharpnessValueLabel;
  private readonly DropDown _sharpnessAlgorithmDrop;
  private readonly CheckBox _selfIlluminationCheck;
  private readonly CheckBox _alphaChannelCheck;
  private readonly CheckBox _colorMaskCheck;
  private readonly Button _colorMaskButton;
  private readonly Slider _toleranceSlider;
  private readonly Label _toleranceValueLabel;
  private readonly Slider _transparencySlider;
  private readonly Label _transparencyValueLabel;
  private readonly UITimer _brightnessTimer = new() { Interval = 0.15 };
  private readonly UITimer _contrastTimer = new() { Interval = 0.15 };
  private readonly UITimer _saturationTimer = new() { Interval = 0.15 };
  private readonly UITimer _sharpnessTimer = new() { Interval = 0.3 };
  private readonly UITimer _toleranceTimer = new() { Interval = 0.15 };
  private readonly UITimer _transparencyTimer = new() { Interval = 0.15 };
  private readonly UITimer _scaleTimer = new() { Interval = 0.3 };
  private readonly Dictionary<UITimer, Guid[]> _sliderTargetIds = new();
  private readonly List<Guid> _targetIds = new();

  private RhinoDoc? _doc;
  private bool _isUpdatingUi;
  private bool _isApplying;
  private bool _renderRefreshPending;
  private bool _stopped;
  private int _sharpnessRequestVersion;

  internal PictureEditorControl(Func<RhinoDoc?> documentProvider,
    Func<IReadOnlyList<RhinoObject>> targetProvider)
  {
    _documentProvider = documentProvider;
    _targetProvider = targetProvider;

    _fileBox = new TextBox { ReadOnly = true };
    _fileButton = new Button { Text = "...", Width = 28 };
    _scaleFactorStepper = new NumericStepper
    {
      MinValue = 0.001,
      MaxValue = 1000.0,
      Increment = 0.1,
      DecimalPlaces = 4,
      Value = 1.0
    };
    _calibrateScaleButton = new Button
    {
      Width = 28,
      Image = CreateCalibrationIcon(),
      ToolTip = "Calibrate scale from two points"
    };
    _scaleContentsCheck = new CheckBox
    {
      Checked = LoadScaleContentsSetting()
    };
    _brightnessSlider = NewAdjustmentSlider();
    _brightnessValueLabel = NewPercentageLabel();
    _contrastSlider = NewAdjustmentSlider();
    _contrastValueLabel = NewPercentageLabel();
    _saturationSlider = NewAdjustmentSlider();
    _saturationValueLabel = NewPercentageLabel();
    _sharpnessSlider = NewPercentageSlider();
    _sharpnessValueLabel = NewPercentageLabel();
    _sharpnessAlgorithmDrop = new DropDown { DataStore = PictureImageProcessor.Algorithms };
    _selfIlluminationCheck = new CheckBox();
    _alphaChannelCheck = new CheckBox();
    _colorMaskCheck = new CheckBox();
    _colorMaskButton = new Button { Width = ValueWidth };
    _toleranceSlider = NewPercentageSlider();
    _toleranceValueLabel = NewPercentageLabel();
    _transparencySlider = NewPercentageSlider();
    _transparencyValueLabel = NewPercentageLabel();

    _fileButton.Click += (_, _) => ChoosePictureFile();
    _scaleFactorStepper.ValueChanged += (_, _) => QueueScale();
    _calibrateScaleButton.Click += (_, _) => CalibrateScale();
    _scaleContentsCheck.CheckedChanged += (_, _) => SaveScaleContentsSetting();
    _selfIlluminationCheck.CheckedChanged += (_, _) => ApplyPictureBoolean(
      _selfIlluminationCheck, "self-illuminated", false, "Picture Self Illumination");
    _alphaChannelCheck.CheckedChanged += (_, _) => ApplyPictureBoolean(
      _alphaChannelCheck, "use-alpha-channel", true, "Picture Alpha Channel");
    _colorMaskCheck.CheckedChanged += (_, _) => ApplyPictureColorMask();
    _colorMaskButton.Click += (_, _) => PickPictureColorMask();

    _brightnessSlider.ValueChanged += (_, _) => QueueSlider(
      _brightnessTimer, _brightnessSlider, _brightnessValueLabel, true);
    _contrastSlider.ValueChanged += (_, _) => QueueSlider(
      _contrastTimer, _contrastSlider, _contrastValueLabel, true);
    _saturationSlider.ValueChanged += (_, _) => QueueSlider(
      _saturationTimer, _saturationSlider, _saturationValueLabel, true);
    _sharpnessSlider.ValueChanged += (_, _) =>
    {
      _sharpnessRequestVersion++;
      QueueSlider(_sharpnessTimer, _sharpnessSlider, _sharpnessValueLabel);
    };
    _sharpnessAlgorithmDrop.SelectedIndexChanged += (_, _) =>
    {
      if (_isUpdatingUi)
        return;
      _sharpnessRequestVersion++;
      QueueSlider(_sharpnessTimer, _sharpnessSlider, _sharpnessValueLabel);
    };
    _toleranceSlider.ValueChanged += (_, _) => QueueSlider(
      _toleranceTimer, _toleranceSlider, _toleranceValueLabel);
    _transparencySlider.ValueChanged += (_, _) => QueueSlider(
      _transparencyTimer, _transparencySlider, _transparencyValueLabel);

    _brightnessTimer.Elapsed += (_, _) =>
    {
      _brightnessTimer.Stop();
      ApplyPercentage(_brightnessSlider, "rdk-texture-adjust-multiplier", true,
        "Picture Brightness", 0.01, 1.0, TakeSliderTargets(_brightnessTimer));
    };
    _contrastTimer.Elapsed += (_, _) =>
    {
      _contrastTimer.Stop();
      ApplyPercentage(_contrastSlider, "rdk-texture-adjust-gain", true,
        "Picture Contrast", 0.005, 0.5, TakeSliderTargets(_contrastTimer));
    };
    _saturationTimer.Elapsed += (_, _) =>
    {
      _saturationTimer.Stop();
      ApplySaturation(TakeSliderTargets(_saturationTimer));
    };
    _sharpnessTimer.Elapsed += (_, _) =>
    {
      _sharpnessTimer.Stop();
      int requestVersion = _sharpnessRequestVersion;
      string algorithm = _sharpnessAlgorithmDrop.SelectedValue?.ToString()
        ?? PictureImageProcessor.UnsharpMask;
      ApplySharpnessAsync(TakeSliderTargets(_sharpnessTimer),
        _sharpnessSlider.Value, algorithm, requestVersion);
    };
    _toleranceTimer.Elapsed += (_, _) =>
    {
      _toleranceTimer.Stop();
      ApplyPercentage(_toleranceSlider, "transparent-color-sensitivity", true,
        "Picture Color Mask Tolerance", 1.0, 0.0, TakeSliderTargets(_toleranceTimer));
    };
    _transparencyTimer.Elapsed += (_, _) =>
    {
      _transparencyTimer.Stop();
      ApplyPercentage(_transparencySlider, "transparency", false,
        "Picture Transparency", 0.01, 0.0, TakeSliderTargets(_transparencyTimer));
    };
    _scaleTimer.Elapsed += (_, _) =>
    {
      _scaleTimer.Stop();
      double factor = _scaleFactorStepper.Value;
      Guid[] targetIds = TakeSliderTargets(_scaleTimer).ToArray();
      _isUpdatingUi = true;
      try
      {
        _scaleFactorStepper.Value = 1.0;
      }
      finally
      {
        _isUpdatingUi = false;
      }
      ScalePictures(targetIds, factor, _scaleContentsCheck.Checked == true);
    };

    Content = new TableLayout
    {
      Spacing = new Size(4, 1),
      Padding = new Padding(10, 2, 6, 2),
      Rows =
      {
        NewControlWithButtonRow("Image", _fileBox, _fileButton),
        NewControlWithButtonRow("Scale factor", _scaleFactorStepper, _calibrateScaleButton),
        NewCheckRow("Scale contents", _scaleContentsCheck),
        NewSliderRow("Brightness", _brightnessSlider, _brightnessValueLabel),
        NewSliderRow("Contrast", _contrastSlider, _contrastValueLabel),
        NewSliderRow("Saturation", _saturationSlider, _saturationValueLabel),
        NewSliderRow("Sharpness", _sharpnessSlider, _sharpnessValueLabel),
        NewControlRow("Sharpen method", _sharpnessAlgorithmDrop),
        NewCheckRow("Self illumination", _selfIlluminationCheck),
        NewCheckRow("Use alpha channel", _alphaChannelCheck),
        NewCheckRow("Use color mask", _colorMaskCheck),
        NewControlRow("Mask color", _colorMaskButton),
        NewSliderRow("Mask tolerance", _toleranceSlider, _toleranceValueLabel),
        NewSliderRow("Transparency", _transparencySlider, _transparencyValueLabel)
      }
    };

    RenderContent.ContentChanged += OnRenderContentChanged;
    SetEmptyState();
  }

  internal bool IsApplying => _isApplying;

  internal void RefreshTargets()
  {
    if (_stopped)
      return;
    RhinoDoc? doc = _documentProvider();
    Update(doc, doc == null ? Array.Empty<RhinoObject>() : _targetProvider());
  }

  internal void Update(RhinoDoc? doc, IReadOnlyList<RhinoObject> objects)
  {
    _doc = doc;
    var pictures = objects
      .Where(obj => obj.IsPictureFrame && obj.RenderMaterial != null)
      .GroupBy(obj => obj.Id)
      .Select(group => group.First())
      .ToList();

    _targetIds.Clear();
    _targetIds.AddRange(pictures.Select(obj => obj.Id));

    _isUpdatingUi = true;
    try
    {
      if (doc == null || pictures.Count == 0)
      {
        SetEmptyState();
        return;
      }

      string fileName = CommonOrVaries(pictures, PictureFileName);
      _fileBox.Text = fileName;
      _fileBox.ToolTip = fileName == VariesText ? string.Empty : fileName;

      bool? selfIllumination = CommonBoolOrVaries(pictures,
        obj => ReadPictureBool(obj, "self-illuminated", false) ?? false);
      bool? alphaChannel = CommonBoolOrVaries(pictures,
        obj => ReadPictureBool(obj, "use-alpha-channel", true) ?? false);
      bool? colorMask = CommonBoolOrVaries(pictures,
        obj => ReadPictureBool(obj, "has-transparent-color", true) ?? false);
      SetCheckState(_selfIlluminationCheck, selfIllumination);
      SetCheckState(_alphaChannelCheck, alphaChannel);
      SetCheckState(_colorMaskCheck, colorMask);

      bool alphaSupported = pictures.Any(obj =>
      {
        string path = PictureFileName(obj);
        return !string.IsNullOrWhiteSpace(path)
          && File.Exists(path)
          && Rhino.FileIO.ImageFile.SupportsAlphaChannel(path);
      });
      _alphaChannelCheck.Enabled = alphaSupported;

      var maskColors = pictures
        .Select(ReadPictureMaskColor)
        .Where(color => color.HasValue)
        .Select(color => color!.Value)
        .ToList();
      bool maskColorVaries = maskColors.Count != pictures.Count
        || (maskColors.Count > 1
          && maskColors.Skip(1).Any(color => color.ToArgb() != maskColors[0].ToArgb()));
      _colorMaskButton.Text = maskColorVaries ? "..." : string.Empty;
      _colorMaskButton.Image = maskColorVaries || maskColors.Count == 0
        ? null
        : CreateColorSwatch(ToEtoColor(maskColors[0]));
      _colorMaskButton.BackgroundColor = Colors.White;

      double? tolerance = CommonDouble(pictures,
        obj => ReadPictureDouble(obj, "transparent-color-sensitivity", true) ?? 0.0);
      double? transparency = CommonDouble(pictures,
        obj => 100.0 * (ReadPictureDouble(obj, "transparency", false) ?? 0.0));
      double? brightness = CommonDouble(pictures,
        obj => 100.0 * ((ReadPictureDouble(obj, "rdk-texture-adjust-multiplier", true) ?? 1.0) - 1.0));
      double? contrast = CommonDouble(pictures,
        obj => 200.0 * ((ReadPictureDouble(obj, "rdk-texture-adjust-gain", true) ?? 0.5) - 0.5));
      double? saturation = CommonDouble(pictures, obj =>
      {
        bool grayscale = ReadPictureBool(obj, "rdk-texture-adjust-grayscale", true) ?? false;
        return grayscale
          ? -100.0
          : 100.0 * ((ReadPictureDouble(obj, "rdk-texture-adjust-saturation", true) ?? 1.0) - 1.0);
      });
      double? sharpness = CommonDouble(pictures, PictureSharpnessLevel);
      string sharpnessAlgorithm = CommonOrVaries(pictures, PictureSharpnessAlgorithm);

      SetPercentageSliderValue(_toleranceSlider, _toleranceValueLabel, tolerance);
      SetPercentageSliderValue(_transparencySlider, _transparencyValueLabel, transparency);
      SetAdjustmentSliderValue(_brightnessSlider, _brightnessValueLabel, brightness);
      SetAdjustmentSliderValue(_contrastSlider, _contrastValueLabel, contrast);
      SetAdjustmentSliderValue(_saturationSlider, _saturationValueLabel, saturation);
      SetPercentageSliderValue(_sharpnessSlider, _sharpnessValueLabel, sharpness);
      SetDropValue(_sharpnessAlgorithmDrop, sharpnessAlgorithm, PictureImageProcessor.Algorithms);

      SetEnabled(true);
      bool colorMaskControlsEnabled = colorMask != false;
      _colorMaskButton.Enabled = colorMaskControlsEnabled;
      _toleranceSlider.Enabled = colorMaskControlsEnabled;
      _alphaChannelCheck.Enabled = alphaSupported;
    }
    finally
    {
      _isUpdatingUi = false;
    }
  }

  internal void Stop()
  {
    if (_stopped)
      return;
    _stopped = true;
    _sharpnessRequestVersion++;
    foreach (UITimer timer in AllTimers())
      timer.Stop();
    _sliderTargetIds.Clear();
    RenderContent.ContentChanged -= OnRenderContentChanged;
  }

  private IEnumerable<UITimer> AllTimers()
  {
    yield return _brightnessTimer;
    yield return _contrastTimer;
    yield return _saturationTimer;
    yield return _sharpnessTimer;
    yield return _toleranceTimer;
    yield return _transparencyTimer;
    yield return _scaleTimer;
  }

  private void OnRenderContentChanged(object? sender, RenderContentChangedEventArgs e)
  {
    RhinoDoc? doc = e.Document ?? _doc;
    if (_stopped || doc == null || !ReferenceEquals(doc, _doc) || _targetIds.Count == 0)
      return;
    if (_isApplying || _renderRefreshPending)
      return;

    _renderRefreshPending = true;
    Application.Instance.AsyncInvoke(() =>
    {
      _renderRefreshPending = false;
      RefreshTargets();
    });
  }

  private void SetEmptyState()
  {
    _fileBox.Text = string.Empty;
    _fileBox.ToolTip = string.Empty;
    SetCheckState(_selfIlluminationCheck, null);
    SetCheckState(_alphaChannelCheck, null);
    SetCheckState(_colorMaskCheck, null);
    _colorMaskButton.Text = string.Empty;
    _colorMaskButton.Image = null;
    SetPercentageSliderValue(_toleranceSlider, _toleranceValueLabel, null);
    SetPercentageSliderValue(_transparencySlider, _transparencyValueLabel, null);
    SetAdjustmentSliderValue(_brightnessSlider, _brightnessValueLabel, null);
    SetAdjustmentSliderValue(_contrastSlider, _contrastValueLabel, null);
    SetAdjustmentSliderValue(_saturationSlider, _saturationValueLabel, null);
    SetPercentageSliderValue(_sharpnessSlider, _sharpnessValueLabel, null);
    SetDropValue(_sharpnessAlgorithmDrop, PictureImageProcessor.UnsharpMask,
      PictureImageProcessor.Algorithms);
    SetEnabled(false);
  }

  private void SetEnabled(bool enabled)
  {
    _fileBox.Enabled = enabled;
    _fileButton.Enabled = enabled;
    _scaleFactorStepper.Enabled = enabled;
    _calibrateScaleButton.Enabled = enabled;
    _scaleContentsCheck.Enabled = enabled;
    _brightnessSlider.Enabled = enabled;
    _contrastSlider.Enabled = enabled;
    _saturationSlider.Enabled = enabled;
    _sharpnessSlider.Enabled = enabled;
    _sharpnessAlgorithmDrop.Enabled = enabled;
    _selfIlluminationCheck.Enabled = enabled;
    _alphaChannelCheck.Enabled = enabled;
    _colorMaskCheck.Enabled = enabled;
    _colorMaskButton.Enabled = enabled;
    _toleranceSlider.Enabled = enabled;
    _transparencySlider.Enabled = enabled;
  }

  private static bool LoadScaleContentsSetting()
  {
    try
    {
      return vObjectPropertiesPlusPlugIn.Instance.Settings.GetBool(
        ScaleContentsSettingKey, true);
    }
    catch
    {
      return true;
    }
  }

  private void SaveScaleContentsSetting()
  {
    if (_isUpdatingUi || !_scaleContentsCheck.Checked.HasValue)
      return;
    try
    {
      vObjectPropertiesPlusPlugIn.Instance.Settings.SetBool(
        ScaleContentsSettingKey, _scaleContentsCheck.Checked.Value);
    }
    catch (Exception ex)
    {
      Log.Write($"Save picture scale-contents setting failed: {ex.Message}");
    }
  }

  private void QueueScale()
  {
    if (_isUpdatingUi)
      return;

    _scaleTimer.Stop();
    if (RhinoMath.EpsilonEquals(_scaleFactorStepper.Value, 1.0, 1e-9))
    {
      _sliderTargetIds.Remove(_scaleTimer);
      return;
    }

    _sliderTargetIds[_scaleTimer] = CurrentTargetIds();
    _scaleTimer.Start();
  }

  private void CalibrateScale()
  {
    if (_isUpdatingUi || _doc == null || _targetIds.Count == 0)
      return;

    _scaleTimer.Stop();
    _sliderTargetIds.Remove(_scaleTimer);
    _isUpdatingUi = true;
    try
    {
      _scaleFactorStepper.Value = 1.0;
    }
    finally
    {
      _isUpdatingUi = false;
    }

    Guid[] targetIds = CurrentTargetIds();
    if (RhinoGet.GetPoint("First picture reference point", false, out Point3d first)
      != Result.Success)
      return;

    var secondPoint = new Rhino.Input.Custom.GetPoint();
    secondPoint.SetCommandPrompt("Second picture reference point");
    secondPoint.SetBasePoint(first, true);
    secondPoint.DrawLineFromPoint(first, true);
    secondPoint.Get();
    if (secondPoint.CommandResult() != Result.Success)
      return;

    double measuredDistance = first.DistanceTo(secondPoint.Point());
    if (!RhinoMath.IsValidDouble(measuredDistance)
      || measuredDistance <= RhinoMath.ZeroTolerance)
      return;

    double desiredDistance = measuredDistance;
    if (RhinoGet.GetNumber("Reference distance", false, ref desiredDistance,
      RhinoMath.ZeroTolerance, double.MaxValue) != Result.Success)
      return;

    ScalePictures(targetIds, desiredDistance / measuredDistance,
      _scaleContentsCheck.Checked == true);
  }

  private void ScalePictures(IReadOnlyCollection<Guid> targetObjectIds,
    double factor, bool includeContents)
  {
    if (_doc == null || !RhinoMath.IsValidDouble(factor)
      || factor <= RhinoMath.ZeroTolerance
      || RhinoMath.EpsilonEquals(factor, 1.0, 1e-9))
      return;

    List<RhinoObject> targetPictures = ResolveTargets(targetObjectIds);
    double tolerance = Math.Max(_doc.ModelAbsoluteTolerance, RhinoMath.SqrtEpsilon);
    var scopes = targetPictures
      .Select(obj => TryCreateScaleScope(obj, tolerance, out PictureScaleScope? scope)
        ? scope
        : null)
      .Where(scope => scope != null)
      .Cast<PictureScaleScope>()
      .OrderBy(scope => scope.Area)
      .ToList();
    if (scopes.Count == 0)
      return;

    var targetIds = targetPictures.Select(obj => obj.Id).ToHashSet();
    var assignments = new Dictionary<Guid, PictureScaleScope>();
    if (includeContents)
    {
      var candidates = _doc.Objects.GetObjectList(ObjectType.AnyObject)
        .Where(obj => obj != null
          && !targetIds.Contains(obj.Id)
          && obj.Visible
          && !obj.IsLocked)
        .ToList();

      foreach (PictureScaleScope scope in scopes)
      {
        foreach (RhinoObject candidate in candidates)
        {
          if (!assignments.ContainsKey(candidate.Id)
            && IsInsidePictureBoundary(candidate, scope, tolerance))
            assignments[candidate.Id] = scope;
        }
      }
    }

    uint undoRecord = _doc.BeginUndoRecord("Properties+ Scale Picture");
    bool changed = false;
    _isApplying = true;
    try
    {
      foreach (PictureScaleScope scope in scopes)
      {
        Transform transform = Transform.Scale(scope.Center, factor);
        changed |= TransformPreservingSelection(scope.PictureId, transform);
      }

      foreach ((Guid objectId, PictureScaleScope scope) in assignments)
      {
        Transform transform = Transform.Scale(scope.Center, factor);
        changed |= TransformPreservingSelection(objectId, transform);
      }
    }
    catch (Exception ex)
    {
      Log.Write($"Scale picture failed: {ex}");
    }
    finally
    {
      try
      {
        if (undoRecord != 0)
          _doc.EndUndoRecord(undoRecord);
      }
      finally
      {
        _isApplying = false;
      }
    }

    if (!changed)
      return;
    _doc.Views.Redraw();
    Application.Instance.AsyncInvoke(RefreshTargets);
  }

  private bool TransformPreservingSelection(Guid objectId, Transform transform)
  {
    if (_doc == null)
      return false;

    RhinoObject? original = _doc.Objects.FindId(objectId);
    if (original == null)
      return false;
    bool wasSelected = original.IsSelected(false) > 0;
    Guid transformedId = _doc.Objects.Transform(original, transform, true);
    if (transformedId == Guid.Empty)
      return false;
    if (wasSelected)
      _doc.Objects.FindId(transformedId)?.Select(true);
    return true;
  }

  private static bool TryCreateScaleScope(RhinoObject picture, double tolerance,
    out PictureScaleScope? scope)
  {
    scope = null;
    Plane plane;
    var points = new List<Point3d>();

    switch (picture.Geometry)
    {
      case Brep brep when brep.Faces.Count > 0
        && brep.Faces[0].TryGetPlane(out plane, tolerance):
        points.AddRange(brep.Vertices.Select(vertex => vertex.Location));
        if (points.Count < 3)
          AddSurfaceCorners(brep.Faces[0], points);
        break;
      case Surface surface when surface.TryGetPlane(out plane, tolerance):
        AddSurfaceCorners(surface, points);
        break;
      default:
        return false;
    }

    if (points.Count < 3)
      return false;

    double minU = double.PositiveInfinity;
    double maxU = double.NegativeInfinity;
    double minV = double.PositiveInfinity;
    double maxV = double.NegativeInfinity;
    foreach (Point3d point in points)
    {
      if (!plane.ClosestParameter(point, out double u, out double v))
        continue;
      minU = Math.Min(minU, u);
      maxU = Math.Max(maxU, u);
      minV = Math.Min(minV, v);
      maxV = Math.Max(maxV, v);
    }

    if (!RhinoMath.IsValidDouble(minU) || maxU - minU <= tolerance
      || maxV - minV <= tolerance)
      return false;

    scope = new PictureScaleScope(
      picture.Id,
      plane,
      minU,
      maxU,
      minV,
      maxV,
      plane.PointAt((minU + maxU) * 0.5, (minV + maxV) * 0.5));
    return true;
  }

  private static void AddSurfaceCorners(Surface surface, ICollection<Point3d> points)
  {
    Interval u = surface.Domain(0);
    Interval v = surface.Domain(1);
    points.Add(surface.PointAt(u.T0, v.T0));
    points.Add(surface.PointAt(u.T1, v.T0));
    points.Add(surface.PointAt(u.T1, v.T1));
    points.Add(surface.PointAt(u.T0, v.T1));
  }

  private static bool IsInsidePictureBoundary(RhinoObject obj,
    PictureScaleScope scope, double tolerance)
  {
    BoundingBox bounds;
    try
    {
      bounds = obj.Geometry.GetBoundingBox(scope.Plane);
    }
    catch
    {
      return false;
    }

    return bounds.IsValid
      && bounds.Min.X >= scope.MinU - tolerance
      && bounds.Max.X <= scope.MaxU + tolerance
      && bounds.Min.Y >= scope.MinV - tolerance
      && bounds.Max.Y <= scope.MaxV + tolerance;
  }

  private void ChoosePictureFile()
  {
    if (_isUpdatingUi || _doc == null)
      return;

    var dialog = new OpenFileDialog
    {
      Title = "Select picture image",
      CheckFileExists = true,
      MultiSelect = false
    };
    dialog.Filters.Add(new FileFilter("Image files",
      ".bmp", ".dds", ".exr", ".gif", ".hdr", ".jpeg", ".jpg", ".pcx",
      ".png", ".tga", ".tif", ".tiff", ".webp"));
    dialog.Filters.Add(new FileFilter("All files", ".*"));

    string currentFile = _fileBox.Text ?? string.Empty;
    if (currentFile != VariesText && File.Exists(currentFile))
    {
      dialog.FileName = currentFile;
      string? directory = Path.GetDirectoryName(currentFile);
      if (!string.IsNullOrWhiteSpace(directory))
        dialog.Directory = new Uri(directory);
    }

    if (dialog.ShowDialog(this) != DialogResult.Ok || string.IsNullOrWhiteSpace(dialog.FileName))
      return;

    _sharpnessRequestVersion++;
    ApplyParameters("Picture Image", new[] { ("filename", (object)dialog.FileName) }, true,
      true, CurrentTargetIds(), ClearSharpnessMetadata);
  }

  private void ApplyPictureBoolean(CheckBox checkBox, string parameterName,
    bool texture, string undoName)
  {
    if (_isUpdatingUi || !checkBox.Checked.HasValue)
      return;
    ApplyParameters(undoName,
      new[] { (parameterName, (object)checkBox.Checked.Value) }, texture,
      false, CurrentTargetIds());
  }

  private void ApplyPictureColorMask()
  {
    if (_isUpdatingUi || !_colorMaskCheck.Checked.HasValue)
      return;

    bool enabled = _colorMaskCheck.Checked.Value;
    _colorMaskButton.Enabled = enabled;
    _toleranceSlider.Enabled = enabled;
    ApplyParameters("Picture Color Mask",
      new[] { ("has-transparent-color", (object)enabled) }, true,
      false, CurrentTargetIds());
  }

  private void QueueSlider(UITimer timer, Slider slider, Label valueLabel,
    bool signed = false)
  {
    if (_isUpdatingUi)
      return;

    valueLabel.Text = signed ? FormatSignedPercentage(slider.Value) : $"{slider.Value}%";
    _sliderTargetIds[timer] = CurrentTargetIds();
    timer.Stop();
    timer.Start();
  }

  private void ApplyPercentage(Slider slider, string parameterName,
    bool texture, string undoName, double scale, double offset,
    IReadOnlyCollection<Guid> targetObjectIds)
  {
    ApplyParameters(undoName,
      new[] { (parameterName, (object)(offset + slider.Value * scale)) }, texture,
      false, targetObjectIds);
  }

  private void ApplySaturation(IReadOnlyCollection<Guid> targetObjectIds)
  {
    ApplyParameters("Picture Saturation", new[]
    {
      ("rdk-texture-adjust-grayscale", (object)false),
      ("rdk-texture-adjust-saturation", (object)(1.0 + _saturationSlider.Value * 0.01))
    }, true, false, targetObjectIds);
  }

  private IReadOnlyCollection<Guid> TakeSliderTargets(UITimer timer)
  {
    if (!_sliderTargetIds.Remove(timer, out Guid[]? targetIds))
      return Array.Empty<Guid>();
    return targetIds;
  }

  private Guid[] CurrentTargetIds() => _targetIds.Distinct().ToArray();

  private void PickPictureColorMask()
  {
    if (_isUpdatingUi || _doc == null)
      return;

    RhinoObject? selectedPicture = ResolveTargets(CurrentTargetIds()).FirstOrDefault();
    if (selectedPicture == null)
      return;

    var color4f = new Color4f(ReadPictureMaskColor(selectedPicture)
      ?? System.Drawing.Color.Black);
    if (!Rhino.UI.Dialogs.ShowColorDialog(ref color4f, true))
      return;

    ApplyParameters("Picture Color Mask",
      new[] { ("transparent-color", (object)color4f) }, true,
      true, CurrentTargetIds());
  }

  private void ApplyParameters(string undoName,
    IReadOnlyList<(string Name, object Value)> parameters, bool texture,
    bool refreshControls, IReadOnlyCollection<Guid> targetObjectIds,
    Action<ObjectAttributes>? updateAttributes = null)
  {
    if (_isUpdatingUi || _doc == null)
      return;

    List<RhinoObject> targetObjects = ResolveTargets(targetObjectIds);
    var contents = targetObjects
      .Select(obj => PictureContent(obj, texture))
      .Where(content => content != null)
      .Cast<RenderContent>()
      .GroupBy(content => content.Id)
      .Select(group => group.First())
      .ToList();
    if (contents.Count == 0)
      return;

    uint undoRecord = _doc.BeginUndoRecord($"Properties+ {undoName}");
    bool changed = false;
    _isApplying = true;
    try
    {
      foreach (RenderContent content in contents)
      {
        content.BeginChange(RenderContent.ChangeContexts.UI);
        try
        {
          foreach ((string name, object value) in parameters)
          {
            try
            {
              changed |= content.SetParameter(name, value);
            }
            catch (Exception ex)
            {
              Log.Write($"Apply picture parameter failed for {name}: {ex}");
            }
          }
        }
        finally
        {
          content.EndChange();
        }
      }

      if (updateAttributes != null)
      {
        foreach (RhinoObject obj in targetObjects)
        {
          ObjectAttributes attributes = obj.Attributes.Duplicate();
          updateAttributes(attributes);
          changed |= _doc.Objects.ModifyAttributes(obj, attributes, true);
        }
      }
    }
    finally
    {
      try
      {
        if (undoRecord != 0)
          _doc.EndUndoRecord(undoRecord);
      }
      finally
      {
        _isApplying = false;
      }
    }

    if (!changed)
      return;
    _doc.Views.Redraw();
    if (refreshControls)
      RefreshTargets();
  }

  private async void ApplySharpnessAsync(IReadOnlyCollection<Guid> targetObjectIds,
    int level, string algorithm, int requestVersion)
  {
    if (_doc == null || requestVersion != _sharpnessRequestVersion)
      return;

    var jobs = ResolveTargets(targetObjectIds)
      .Where(obj => obj.RenderMaterial != null)
      .GroupBy(obj => obj.RenderMaterial!.Id)
      .Select(group => new SharpnessJob(
        group.Key,
        PictureSourceFile(group.First()),
        group.Select(obj => obj.Id).Distinct().ToArray()))
      .Where(job => !string.IsNullOrWhiteSpace(job.SourceFile))
      .ToList();
    if (jobs.Count == 0)
      return;

    List<SharpnessResult> results;
    try
    {
      results = await Task.Run(() =>
      {
        var processed = new List<SharpnessResult>();
        foreach (SharpnessJob job in jobs)
        {
          try
          {
            if (!File.Exists(job.SourceFile))
            {
              Log.Write($"Picture sharpen source was not found: {job.SourceFile}");
              continue;
            }
            processed.Add(new SharpnessResult(job,
              PictureImageProcessor.SharpenFile(job.SourceFile, level, algorithm)));
          }
          catch (Exception ex)
          {
            Log.Write($"Picture sharpen failed for '{job.SourceFile}': {ex}");
          }
        }
        return processed;
      });
    }
    catch (Exception ex)
    {
      Log.Write($"Picture sharpen task failed: {ex}");
      return;
    }

    Application.Instance.AsyncInvoke(() =>
    {
      if (_stopped || requestVersion != _sharpnessRequestVersion)
      {
        DisposeResults(results);
        return;
      }
      CommitSharpness(results, level, algorithm);
    });
  }

  private void CommitSharpness(IReadOnlyList<SharpnessResult> results,
    int level, string algorithm)
  {
    if (_doc == null)
    {
      DisposeResults(results);
      return;
    }

    uint undoRecord = _doc.BeginUndoRecord("Properties+ Picture Sharpness");
    bool changed = false;
    var allPictures = _doc.Objects.GetObjectList(ObjectType.AnyObject)
      .Where(obj => obj?.IsPictureFrame == true && obj.RenderMaterial != null)
      .Cast<RhinoObject>()
      .ToList();
    _isApplying = true;
    try
    {
      foreach (SharpnessResult result in results)
      {
        RhinoObject? firstTarget = result.Job.TargetObjectIds
          .Select(id => _doc.Objects.FindId(id))
          .FirstOrDefault(obj => obj?.IsPictureFrame == true
            && obj.RenderMaterial?.Id == result.Job.MaterialId);
        RenderMaterial? material = firstTarget?.RenderMaterial;
        RenderTexture? oldTexture = firstTarget == null
          ? null
          : PictureContent(firstTarget, true) as RenderTexture;
        if (material == null || oldTexture == null)
          continue;

        RenderTexture newTexture = RenderTexture.NewBitmapTexture(result.Bitmap, _doc);
        CopyTextureSettings(oldTexture, newTexture);
        bool childChanged;
        material.BeginChange(RenderContent.ChangeContexts.UI);
        try
        {
          childChanged = material.SetChild(newTexture, "bitmap-texture");
        }
        finally
        {
          material.EndChange();
        }
        if (!childChanged)
        {
          newTexture.Dispose();
          continue;
        }
        changed = true;

        foreach (RhinoObject obj in allPictures.Where(obj =>
          obj.RenderMaterial?.Id == result.Job.MaterialId))
        {
          ObjectAttributes attributes = obj.Attributes.Duplicate();
          attributes.UserDictionary.Set(SharpnessSourceKey, result.Job.SourceFile);
          attributes.UserDictionary.Set(SharpnessLevelKey, level);
          attributes.UserDictionary.Set(SharpnessAlgorithmKey, algorithm);
          changed |= _doc.Objects.ModifyAttributes(obj, attributes, true);
        }
      }
    }
    catch (Exception ex)
    {
      Log.Write($"Commit picture sharpness failed: {ex}");
    }
    finally
    {
      try
      {
        if (undoRecord != 0)
          _doc.EndUndoRecord(undoRecord);
      }
      finally
      {
        _isApplying = false;
        DisposeResults(results);
      }
    }

    if (!changed)
      return;
    _doc.Views.Redraw();
    RefreshTargets();
  }

  private static void CopyTextureSettings(RenderTexture source, RenderTexture target)
  {
    target.BeginChange(RenderContent.ChangeContexts.Program);
    try
    {
      foreach (string parameterName in CopiedTextureParameters)
      {
        object? value = source.GetParameter(parameterName);
        if (value != null)
          target.SetParameter(parameterName, value);
      }

      target.Name = source.Name;
      target.Notes = source.Notes;
      target.SetProjectionMode(source.GetProjectionMode(), RenderContent.ChangeContexts.Program);
      target.SetWrapType(source.GetWrapType(), RenderContent.ChangeContexts.Program);
      target.SetMappingChannel(source.GetMappingChannel(), RenderContent.ChangeContexts.Program);
      target.SetRepeatLocked(source.GetRepeatLocked(), RenderContent.ChangeContexts.Program);
      target.SetOffsetLocked(source.GetOffsetLocked(), RenderContent.ChangeContexts.Program);
      target.SetRepeat(source.GetRepeat(), RenderContent.ChangeContexts.Program);
      target.SetOffset(source.GetOffset(), RenderContent.ChangeContexts.Program);
      target.SetRotation(source.GetRotation(), RenderContent.ChangeContexts.Program);
    }
    catch (Exception ex)
    {
      Log.Write($"Copy picture texture settings failed: {ex}");
    }
    finally
    {
      target.EndChange();
    }
  }

  private List<RhinoObject> ResolveTargets(IEnumerable<Guid> targetObjectIds)
  {
    if (_doc == null)
      return new List<RhinoObject>();
    return targetObjectIds
      .Select(id => _doc.Objects.FindId(id))
      .Where(obj => obj?.IsPictureFrame == true && obj.RenderMaterial != null)
      .Cast<RhinoObject>()
      .GroupBy(obj => obj.Id)
      .Select(group => group.First())
      .ToList();
  }

  private static RenderContent? PictureContent(RhinoObject obj, bool texture)
  {
    if (!obj.IsPictureFrame)
      return null;
    RenderMaterial? material = obj.RenderMaterial;
    return texture ? material?.FindChild("bitmap-texture") : material;
  }

  private static object? ReadPictureParameter(RhinoObject obj,
    string parameterName, bool texture)
  {
    try
    {
      return PictureContent(obj, texture)?.GetParameter(parameterName);
    }
    catch (Exception ex)
    {
      Log.Write($"Read picture parameter failed for {parameterName}: {ex.Message}");
      return null;
    }
  }

  private static bool? ReadPictureBool(RhinoObject obj, string parameterName, bool texture)
  {
    object? value = ReadPictureParameter(obj, parameterName, texture);
    if (value == null)
      return null;
    try
    {
      return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
    }
    catch
    {
      return null;
    }
  }

  private static double? ReadPictureDouble(RhinoObject obj, string parameterName, bool texture)
  {
    object? value = ReadPictureParameter(obj, parameterName, texture);
    if (value == null)
      return null;
    try
    {
      return Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }
    catch
    {
      return null;
    }
  }

  private static string PictureFileName(RhinoObject obj)
  {
    if (obj.Attributes.UserDictionary.TryGetString(SharpnessSourceKey, out string source)
      && !string.IsNullOrWhiteSpace(source))
      return source;
    return NativePictureFileName(obj);
  }

  private static string PictureSourceFile(RhinoObject obj)
  {
    if (obj.Attributes.UserDictionary.TryGetString(SharpnessSourceKey, out string source)
      && !string.IsNullOrWhiteSpace(source))
      return source;
    return NativePictureFileName(obj);
  }

  private static string NativePictureFileName(RhinoObject obj)
  {
    object? value = ReadPictureParameter(obj, "filename", true);
    if (value != null)
      return value.ToString()?.Trim() ?? string.Empty;
    return PictureContent(obj, true) is RenderTexture texture
      ? texture.Filename?.Trim() ?? string.Empty
      : string.Empty;
  }

  private static double PictureSharpnessLevel(RhinoObject obj)
  {
    return obj.Attributes.UserDictionary.TryGetInteger(SharpnessLevelKey, out int value)
      ? Math.Clamp(value, 0, 100)
      : 0;
  }

  private static string PictureSharpnessAlgorithm(RhinoObject obj)
  {
    if (obj.Attributes.UserDictionary.TryGetString(SharpnessAlgorithmKey, out string value)
      && PictureImageProcessor.Algorithms.Contains(value))
      return value;
    return PictureImageProcessor.UnsharpMask;
  }

  private static void ClearSharpnessMetadata(ObjectAttributes attributes)
  {
    attributes.UserDictionary.Remove(SharpnessSourceKey);
    attributes.UserDictionary.Remove(SharpnessLevelKey);
    attributes.UserDictionary.Remove(SharpnessAlgorithmKey);
  }

  private static System.Drawing.Color? ReadPictureMaskColor(RhinoObject obj)
  {
    object? value = ReadPictureParameter(obj, "transparent-color", true);
    return value switch
    {
      Color4f color => color.AsSystemColor(),
      System.Drawing.Color color => color,
      _ => null
    };
  }

  private static bool? CommonBoolOrVaries(IReadOnlyList<RhinoObject> objects,
    Func<RhinoObject, bool> selector)
  {
    if (objects.Count == 0)
      return null;
    bool first = selector(objects[0]);
    return objects.Skip(1).Any(obj => selector(obj) != first) ? null : first;
  }

  private static double? CommonDouble(IReadOnlyList<RhinoObject> objects,
    Func<RhinoObject, double> selector)
  {
    if (objects.Count == 0)
      return null;
    double first = selector(objects[0]);
    return objects.Skip(1).Any(obj => !RhinoMath.EpsilonEquals(selector(obj), first, 1e-6))
      ? null
      : first;
  }

  private static string CommonOrVaries(IReadOnlyList<RhinoObject> objects,
    Func<RhinoObject, string> selector)
  {
    if (objects.Count == 0)
      return string.Empty;
    string first = selector(objects[0]);
    return objects.Skip(1).Any(obj => !string.Equals(selector(obj), first, StringComparison.Ordinal))
      ? VariesText
      : first;
  }

  private static void SetCheckState(CheckBox checkBox, bool? value)
  {
    checkBox.ThreeState = !value.HasValue;
    checkBox.Checked = value;
  }

  private static void SetPercentageSliderValue(Slider slider, Label valueLabel, double? value)
  {
    if (!value.HasValue)
    {
      slider.Value = 0;
      valueLabel.Text = VariesText;
      return;
    }
    int percentage = Math.Clamp((int)Math.Round(value.Value), 0, 100);
    slider.Value = percentage;
    valueLabel.Text = $"{percentage}%";
  }

  private static void SetAdjustmentSliderValue(Slider slider, Label valueLabel, double? value)
  {
    if (!value.HasValue)
    {
      slider.Value = 0;
      valueLabel.Text = VariesText;
      return;
    }
    int percentage = Math.Clamp((int)Math.Round(value.Value), -100, 100);
    slider.Value = percentage;
    valueLabel.Text = FormatSignedPercentage(percentage);
  }

  private static string FormatSignedPercentage(int percentage)
    => percentage > 0 ? $"+{percentage}%" : $"{percentage}%";

  private static void SetDropValue(DropDown dropDown, string value, params string[] options)
  {
    var items = options.Distinct().ToList();
    if (!items.Contains(value))
      items.Insert(0, value);
    dropDown.DataStore = items;
    dropDown.SelectedIndex = Math.Max(0, items.IndexOf(value));
  }

  private static Slider NewPercentageSlider() => new()
  {
    MinValue = 0,
    MaxValue = 100,
    TickFrequency = 1,
    SnapToTick = false
  };

  private static Slider NewAdjustmentSlider() => new()
  {
    MinValue = -100,
    MaxValue = 100,
    TickFrequency = 1,
    SnapToTick = false
  };

  private static Label NewPercentageLabel() => new()
  {
    Text = "0%",
    TextAlignment = TextAlignment.Right,
    VerticalAlignment = VerticalAlignment.Center
  };

  private static TableRow NewControlWithButtonRow(string name, Control control, Button button)
  {
    control.Height = RowHeight;
    control.Width = ValueWidth - 34;
    button.Height = RowHeight;
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
    return NewRow(name, right);
  }

  private static TableRow NewSliderRow(string name, Slider slider, Label valueLabel)
  {
    slider.Height = RowHeight;
    valueLabel.Width = 48;
    valueLabel.Height = RowHeight;
    var right = new StackLayout
    {
      Orientation = Orientation.Horizontal,
      Spacing = 4,
      Items =
      {
        new StackLayoutItem(slider, true),
        new StackLayoutItem(valueLabel, false)
      }
    };
    return NewRow(name, right);
  }

  private static TableRow NewCheckRow(string name, CheckBox checkBox)
  {
    checkBox.Height = RowHeight;
    return NewRow(name, checkBox);
  }

  private static TableRow NewControlRow(string name, Control control)
  {
    control.Height = RowHeight;
    control.Width = ValueWidth;
    return NewRow(name, control);
  }

  private static TableRow NewRow(string name, Control control)
    => new(new TableCell(new Label { Text = name, Width = LabelWidth }, false),
      new TableCell(control, true));

  private static Bitmap CreateColorSwatch(Color color)
  {
    const int size = 16;
    var bitmap = new Bitmap(size, size, PixelFormat.Format32bppRgba);
    using var graphics = new Graphics(bitmap);
    graphics.FillRectangle(color, 0, 0, size, size);
    graphics.DrawRectangle(Colors.Black, 0, 0, size - 1, size - 1);
    return bitmap;
  }

  private static Bitmap CreateCalibrationIcon()
  {
    using var systemBitmap = new System.Drawing.Bitmap(16, 16,
      System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    using (var graphics = System.Drawing.Graphics.FromImage(systemBitmap))
    using (var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(40, 40, 40), 1.4f))
    {
      graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
      graphics.DrawLine(pen, 3, 12, 13, 4);
      graphics.DrawLine(pen, 2, 9, 5, 13);
      graphics.DrawLine(pen, 11, 3, 14, 7);
      graphics.FillEllipse(System.Drawing.Brushes.DodgerBlue, 1, 10, 4, 4);
      graphics.FillEllipse(System.Drawing.Brushes.DodgerBlue, 11, 2, 4, 4);
    }

    using var stream = new MemoryStream();
    systemBitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
    return new Bitmap(stream.ToArray());
  }

  private static Color ToEtoColor(System.Drawing.Color color)
    => Color.FromArgb(color.A, color.R, color.G, color.B);

  private static void DisposeResults(IEnumerable<SharpnessResult> results)
  {
    foreach (SharpnessResult result in results)
      result.Bitmap.Dispose();
  }

  private sealed class SharpnessJob
  {
    internal SharpnessJob(Guid materialId, string sourceFile, Guid[] targetObjectIds)
    {
      MaterialId = materialId;
      SourceFile = sourceFile;
      TargetObjectIds = targetObjectIds;
    }

    internal Guid MaterialId { get; }
    internal string SourceFile { get; }
    internal Guid[] TargetObjectIds { get; }
  }

  private sealed class PictureScaleScope
  {
    internal PictureScaleScope(Guid pictureId, Plane plane,
      double minU, double maxU, double minV, double maxV, Point3d center)
    {
      PictureId = pictureId;
      Plane = plane;
      MinU = minU;
      MaxU = maxU;
      MinV = minV;
      MaxV = maxV;
      Center = center;
    }

    internal Guid PictureId { get; }
    internal Plane Plane { get; }
    internal double MinU { get; }
    internal double MaxU { get; }
    internal double MinV { get; }
    internal double MaxV { get; }
    internal Point3d Center { get; }
    internal double Area => (MaxU - MinU) * (MaxV - MinV);
  }

  private sealed class SharpnessResult
  {
    internal SharpnessResult(SharpnessJob job, System.Drawing.Bitmap bitmap)
    {
      Job = job;
      Bitmap = bitmap;
    }

    internal SharpnessJob Job { get; }
    internal System.Drawing.Bitmap Bitmap { get; }
  }
}
