using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
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
using RhinoSlider = Rhino.UI.Controls.Slider;

namespace vObjectPropertiesPlus.Views;

[SupportedOSPlatform("windows")]
internal sealed class PictureEditorControl : Panel
{
  private const int LabelWidth = 114;
  private const int ValueWidth = 102;
  private const int RowHeight = 20;
  private const string VariesText = "(varies)";
  private const string SharpnessSourceKey = "vObjectPropertiesPlus.Picture.Source";
  private const string SharpnessLevelKey = "vObjectPropertiesPlus.Picture.Sharpness";
  private const string SharpnessAlgorithmKey = "vObjectPropertiesPlus.Picture.SharpenAlgorithm";
  private const string SharpnessProcessedKey = "vObjectPropertiesPlus.Picture.ProcessedImage";
  private const string OriginalPictureAreaKey = "vObjectPropertiesPlus.Picture.OriginalArea";
  private const string HiddenObjectIdsKey = "vObjectPropertiesPlus.Picture.HiddenObjects";
  private const string ScaleContentsSettingKey = "Panel.PictureScaleContents";

  private static readonly object PictureDimensionCacheLock = new();
  private static readonly Dictionary<string, PictureDimensionCacheEntry> PictureDimensionCache =
    new(StringComparer.OrdinalIgnoreCase);

  private readonly Func<RhinoDoc?> _documentProvider;
  private readonly Func<IReadOnlyList<RhinoObject>> _targetProvider;
  private readonly DropDown _imageDrop;
  private readonly TextBox _scaleFactorBox;
  private readonly Button _calibrateScaleButton;
  private readonly CheckBox _scaleContentsCheck;
  private readonly ToggleButton _containedObjectsToggle;
  private readonly RhinoSlider _brightnessSlider;
  private readonly RhinoSlider _contrastSlider;
  private readonly RhinoSlider _saturationSlider;
  private readonly RhinoSlider _sharpnessSlider;
  private readonly DropDown _sharpnessAlgorithmDrop;
  private readonly CheckBox _selfIlluminationCheck;
  private readonly CheckBox _alphaChannelCheck;
  private readonly CheckBox _colorMaskCheck;
  private readonly Button _colorMaskButton;
  private readonly RhinoSlider _toleranceSlider;
  private readonly RhinoSlider _transparencySlider;
  private readonly UITimer _brightnessTimer = new() { Interval = 0.15 };
  private readonly UITimer _contrastTimer = new() { Interval = 0.15 };
  private readonly UITimer _saturationTimer = new() { Interval = 0.15 };
  private readonly UITimer _sharpnessTimer = new() { Interval = 0.3 };
  private readonly UITimer _toleranceTimer = new() { Interval = 0.15 };
  private readonly UITimer _transparencyTimer = new() { Interval = 0.15 };
  private readonly UITimer _busyDelayTimer = new() { Interval = 0.05 };
  private readonly Dictionary<UITimer, Guid[]> _sliderTargetIds = new();
  private readonly Dictionary<RhinoSlider, Button> _sliderResetButtons = new();
  private readonly Dictionary<RhinoSlider, (Color First, Color Second)>
    _sliderMarkerColors = new();
  private readonly Dictionary<string, ImageView> _busyIndicators =
    new(StringComparer.Ordinal);
  private readonly List<Guid> _targetIds = new();
  private readonly List<Guid> _imageDropMap = new();
  private readonly List<string> _imageDropLabels = new();
  private readonly PictureHighlightConduit _selectedPictureConduit = new();
  private readonly PictureHighlightConduit _hoverPictureConduit = new();
  private readonly Bitmap _busyIcon = CreateBusyIcon();
  private readonly Bitmap _eyedropperIcon = CreateEyedropperIcon();
  private readonly Bitmap _resetIcon = CreateResetIcon(true);
  private readonly Bitmap _disabledResetIcon = CreateResetIcon(false);
  private Guid[] _scaleEditTargetIds = Array.Empty<Guid>();

  private RhinoDoc? _doc;
  private bool _isUpdatingUi;
  private bool _isApplying;
  private bool _renderRefreshPending;
  private bool _stopped;
  private bool _scaleFactorDirty;
  private bool _suppressScaleCommit;
  private readonly Queue<(string Name, Action Action, bool PersistsUntilCompleted)> _pendingBusyActions = new();
  private int _persistentBusyActions;
  private string? _persistentBusyOperation;
  private CancellationTokenSource? _sharpnessCancellation;
  private int _sharpnessRequestVersion;
  private System.Windows.Controls.ComboBox? _nativeImageDrop;
  private Guid _focusedPictureId;
  private int _hoveredImageDropIndex = -1;
  private bool _imageDropInputTracking;
  private bool _selectedPictureHighlightSuspended;

  internal PictureEditorControl(Func<RhinoDoc?> documentProvider,
    Func<IReadOnlyList<RhinoObject>> targetProvider)
  {
    _documentProvider = documentProvider;
    _targetProvider = targetProvider;

    _imageDrop = new DropDown();
    _scaleFactorBox = new TextBox
    {
      Text = "1.0000",
      ToolTip = "Absolute scale relative to the original picture size"
    };
    _calibrateScaleButton = new Button
    {
      Width = 28,
      Image = CreateCalibrationIcon(),
      ToolTip = "Calibrate scale from two points"
    };
    _scaleContentsCheck = new CheckBox
    {
      Checked = LoadScaleContentsSetting(),
      ToolTip = "Scale objects contained by the picture"
    };
    _containedObjectsToggle = new ToggleButton
    {
      Text = "Shown",
      ToolTip = "Hide or show objects contained by the picture boundary"
    };
    _brightnessSlider = NewAdjustmentSlider(this);
    _contrastSlider = NewAdjustmentSlider(this);
    _saturationSlider = NewAdjustmentSlider(this);
    _sharpnessSlider = NewPercentageSlider(this);
    _sharpnessAlgorithmDrop = new DropDown
    {
      DataStore = PictureImageProcessor.Algorithms,
      ToolTip = "Sharpening method"
    };
    _selfIlluminationCheck = new CheckBox();
    _alphaChannelCheck = new CheckBox();
    _colorMaskCheck = new CheckBox { ToolTip = "Enable color mask" };
    _colorMaskButton = new Button
    {
      Width = 22,
      Image = _eyedropperIcon,
      ToolTip = "Pick mask color"
    };
    _toleranceSlider = NewPercentageSlider(this);
    _transparencySlider = NewPercentageSlider(this);

    _imageDrop.SelectedIndexChanged += OnImageDropSelectedIndexChanged;
    _imageDrop.DropDownClosed += OnImageDropClosed;
    _scaleFactorBox.TextChanged += (_, _) => OnScaleFactorTextChanged();
    _scaleFactorBox.LostFocus += (_, _) => ApplyScaleFactorField();
    _scaleFactorBox.KeyDown += (_, e) =>
    {
      if (e.KeyData != Keys.Enter)
        return;
      ApplyScaleFactorField();
      e.Handled = true;
    };
    _calibrateScaleButton.MouseDown += (_, _) => _suppressScaleCommit = true;
    _calibrateScaleButton.Click += (_, _) =>
    {
      _suppressScaleCommit = false;
      CalibrateScale();
    };
    _scaleContentsCheck.CheckedChanged += (_, _) => SaveScaleContentsSetting();
    _containedObjectsToggle.CheckedChanged += (_, _) => ApplyContainedObjectVisibility();
    _selfIlluminationCheck.CheckedChanged += (_, _) => ApplyPictureBoolean(
      _selfIlluminationCheck, "self-illuminated", false, "Picture Self Illumination",
      "self illumination");
    _alphaChannelCheck.CheckedChanged += (_, _) => ApplyPictureBoolean(
      _alphaChannelCheck, "use-alpha-channel", true, "Picture Alpha Channel",
      "alpha channel");
    _colorMaskCheck.CheckedChanged += (_, _) => ApplyPictureColorMask();
    _colorMaskButton.Click += (_, _) => PickPictureColorMask();

    WireSlider(_brightnessSlider, _brightnessTimer);
    WireSlider(_contrastSlider, _contrastTimer);
    WireSlider(_saturationSlider, _saturationTimer);
    WireSlider(_sharpnessSlider, _sharpnessTimer, InvalidateSharpnessRequest);
    WireSlider(_toleranceSlider, _toleranceTimer);
    WireSlider(_transparencySlider, _transparencyTimer);
    _sharpnessAlgorithmDrop.SelectedIndexChanged += (_, _) =>
    {
      if (_isUpdatingUi)
        return;
      InvalidateSharpnessRequest();
      QueueSlider(_sharpnessTimer, _sharpnessSlider);
    };

    _brightnessTimer.Elapsed += (_, _) =>
    {
      if (!FinishSliderGesture(_brightnessTimer))
        return;
      RunWithBusyIndicator("brightness", () => ApplyPercentage(_brightnessSlider,
        "rdk-texture-adjust-multiplier", true, "Picture Brightness", 0.01, 1.0,
        TakeSliderTargets(_brightnessTimer)));
    };
    _contrastTimer.Elapsed += (_, _) =>
    {
      if (!FinishSliderGesture(_contrastTimer))
        return;
      RunWithBusyIndicator("contrast", () => ApplyPercentage(_contrastSlider,
        "rdk-texture-adjust-gain", true, "Picture Contrast", 0.005, 0.5,
        TakeSliderTargets(_contrastTimer)));
    };
    _saturationTimer.Elapsed += (_, _) =>
    {
      if (!FinishSliderGesture(_saturationTimer))
        return;
      RunWithBusyIndicator("saturation",
        () => ApplySaturation(TakeSliderTargets(_saturationTimer)));
    };
    _sharpnessTimer.Elapsed += (_, _) =>
    {
      if (!FinishSliderGesture(_sharpnessTimer))
        return;
      int requestVersion = _sharpnessRequestVersion;
      string algorithm = _sharpnessAlgorithmDrop.SelectedValue?.ToString()
        ?? PictureImageProcessor.UnsharpMask;
      _sharpnessCancellation?.Cancel();
      var cancellationSource = new CancellationTokenSource();
      _sharpnessCancellation = cancellationSource;
      RunWithBusyIndicator("sharpness",
        () => ApplySharpnessAsync(TakeSliderTargets(_sharpnessTimer),
        SliderValue(_sharpnessSlider), algorithm, requestVersion, cancellationSource), true);
    };
    _toleranceTimer.Elapsed += (_, _) =>
    {
      if (!FinishSliderGesture(_toleranceTimer))
        return;
      RunWithBusyIndicator("color mask", () => ApplyPercentage(_toleranceSlider,
        "transparent-color-sensitivity", true, "Picture Color Mask Tolerance", 1.0, 0.0,
        TakeSliderTargets(_toleranceTimer)));
    };
    _transparencyTimer.Elapsed += (_, _) =>
    {
      if (!FinishSliderGesture(_transparencyTimer))
        return;
      RunWithBusyIndicator("transparency", () => ApplyPercentage(_transparencySlider,
        "transparency", false, "Picture Transparency", 0.01, 0.0,
        TakeSliderTargets(_transparencyTimer)));
    };
    _busyDelayTimer.Elapsed += (_, _) => RunPendingBusyAction();
    Content = new TableLayout
    {
      Spacing = new Size(4, 1),
      Padding = new Padding(10, 2, 6, 2),
      Rows =
      {
        NewControlRow("Image", "image", _imageDrop),
        NewScaleRow("scale", _scaleFactorBox, _scaleContentsCheck, _calibrateScaleButton),
        NewSliderRow("Brightness", "brightness", _brightnessSlider,
          NewSliderResetButton(_brightnessSlider, _brightnessTimer, "brightness")),
        NewSliderRow("Contrast", "contrast", _contrastSlider,
          NewSliderResetButton(_contrastSlider, _contrastTimer, "contrast")),
        NewSliderRow("Saturation", "saturation", _saturationSlider,
          NewSliderResetButton(_saturationSlider, _saturationTimer, "saturation")),
        NewSharpnessRow("Sharpness", "sharpness", _sharpnessAlgorithmDrop,
          _sharpnessSlider,
          NewSliderResetButton(_sharpnessSlider, _sharpnessTimer, "sharpness",
            InvalidateSharpnessRequest)),
        NewCheckRow("Self illumination", "self illumination", _selfIlluminationCheck),
        NewCheckRow("Use alpha channel", "alpha channel", _alphaChannelCheck),
        NewColorMaskRow("Color mask", "color mask", _colorMaskCheck,
          _colorMaskButton, _toleranceSlider,
          NewSliderResetButton(_toleranceSlider, _toleranceTimer, "mask tolerance")),
        NewSliderRow("Transparency", "transparency", _transparencySlider,
          NewSliderResetButton(_transparencySlider, _transparencyTimer, "transparency")),
        NewControlRow("Contained objects", "contained objects", _containedObjectsToggle),
      }
    };

    RenderContent.ContentChanged += OnRenderContentChanged;
    Load += (_, _) => Application.Instance.AsyncInvoke(InstallImageDropHoverHandlers);
    SetEmptyState();
  }

  internal bool IsApplying => _isApplying;

  internal void Start()
  {
    if (!_stopped)
      return;
    _stopped = false;
    RenderContent.ContentChanged += OnRenderContentChanged;
  }

  internal void RefreshTargets()
  {
    if (_stopped)
      return;

    var totalTimer = Stopwatch.StartNew();
    var stageTimer = Stopwatch.StartNew();
    RhinoDoc? doc = _documentProvider();
    double documentMs = stageTimer.Elapsed.TotalMilliseconds;
    stageTimer.Restart();
    IReadOnlyList<RhinoObject> targets = doc == null
      ? Array.Empty<RhinoObject>()
      : _targetProvider();
    double targetsMs = stageTimer.Elapsed.TotalMilliseconds;
    stageTimer.Restart();
    Update(doc, targets);
    double updateMs = stageTimer.Elapsed.TotalMilliseconds;
    if (totalTimer.Elapsed.TotalMilliseconds >= 25.0)
    {
      Log.Write("PictureTiming", $"Refresh targets: objects={targets.Count}, "
        + $"total={totalTimer.Elapsed.TotalMilliseconds:0.0}ms, "
        + $"document={documentMs:0.0}ms, targets={targetsMs:0.0}ms, "
        + $"update={updateMs:0.0}ms");
    }
  }

  internal void Update(RhinoDoc? doc, IReadOnlyList<RhinoObject> objects)
  {
    _doc = doc;
    var availablePictures = objects
      .Where(obj => IsPictureObject(obj) && obj.RenderMaterial != null)
      .GroupBy(obj => obj.Id)
      .Select(group => group.First())
      .ToList();

    _isUpdatingUi = true;
    try
    {
      UpdateImageDrop(availablePictures);
      var pictures = _focusedPictureId == Guid.Empty
        ? availablePictures
        : availablePictures.Where(obj => obj.Id == _focusedPictureId).ToList();
      _targetIds.Clear();
      _targetIds.AddRange(pictures.Select(obj => obj.Id));
      UpdateSelectedPictureHighlight(availablePictures);

      if (doc == null || pictures.Count == 0)
      {
        SetEmptyState();
        return;
      }

      double modelTolerance = Math.Max(doc.ModelAbsoluteTolerance, RhinoMath.SqrtEpsilon);
      double? pictureScale = CommonDouble(pictures,
        obj => PictureAbsoluteScale(obj, modelTolerance));
      SetScaleFactorValue(pictureScale);
      bool[] hiddenStates = pictures.Select(PictureHasHiddenObjects).ToArray();
      bool anyPictureHidesObjects = hiddenStates.Any(value => value);
      _containedObjectsToggle.Checked = anyPictureHidesObjects;
      _containedObjectsToggle.Text = anyPictureHidesObjects ? "Hidden" : "Shown";

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
      _colorMaskButton.Image = _eyedropperIcon;
      _colorMaskButton.ToolTip = maskColorVaries
        ? "Pick mask color (varies)"
        : maskColors.Count == 0
          ? "Pick mask color"
          : $"Pick mask color ({maskColors[0].R}, {maskColors[0].G}, {maskColors[0].B})";

      SliderValueSummary tolerance = MostCommonSliderValue(pictures,
        obj => ReadPictureDouble(obj, "transparent-color-sensitivity", true) ?? 0.0,
        0, 100);
      SliderValueSummary transparency = MostCommonSliderValue(pictures,
        obj => 100.0 * (ReadPictureDouble(obj, "transparency", false) ?? 0.0),
        0, 100);
      SliderValueSummary brightness = MostCommonSliderValue(pictures,
        obj => 100.0 * ((ReadPictureDouble(obj,
          "rdk-texture-adjust-multiplier", true) ?? 1.0) - 1.0), -100, 100);
      SliderValueSummary contrast = MostCommonSliderValue(pictures,
        obj => 200.0 * ((ReadPictureDouble(obj,
          "rdk-texture-adjust-gain", true) ?? 0.5) - 0.5), -100, 100);
      SliderValueSummary saturation = MostCommonSliderValue(pictures, obj =>
      {
        bool grayscale = ReadPictureBool(obj, "rdk-texture-adjust-grayscale", true) ?? false;
        return grayscale
          ? -100.0
          : 100.0 * ((ReadPictureDouble(obj, "rdk-texture-adjust-saturation", true) ?? 1.0) - 1.0);
      }, -100, 100);
      SliderValueSummary sharpness = MostCommonSliderValue(pictures,
        PictureSharpnessLevel, 0, 100);
      string sharpnessAlgorithm = CommonOrVaries(pictures, PictureSharpnessAlgorithm);

      SetPercentageSliderValue(_toleranceSlider, tolerance);
      SetPercentageSliderValue(_transparencySlider, transparency);
      SetAdjustmentSliderValue(_brightnessSlider, brightness);
      SetAdjustmentSliderValue(_contrastSlider, contrast);
      SetAdjustmentSliderValue(_saturationSlider, saturation);
      SetPercentageSliderValue(_sharpnessSlider, sharpness);
      SetDropValue(_sharpnessAlgorithmDrop, sharpnessAlgorithm, PictureImageProcessor.Algorithms);

      SetEnabled(true);
      bool colorMaskControlsEnabled = colorMask != false;
      _colorMaskButton.Enabled = colorMaskControlsEnabled;
      _toleranceSlider.Enabled = colorMaskControlsEnabled;
      SetResetButtonEnabled(_brightnessSlider, brightness.Value,
        varies: brightness.Varies);
      SetResetButtonEnabled(_contrastSlider, contrast.Value,
        varies: contrast.Varies);
      SetResetButtonEnabled(_saturationSlider, saturation.Value,
        varies: saturation.Varies);
      SetResetButtonEnabled(_sharpnessSlider, sharpness.Value,
        varies: sharpness.Varies);
      SetResetButtonEnabled(_toleranceSlider, tolerance.Value,
        colorMaskControlsEnabled, tolerance.Varies);
      SetResetButtonEnabled(_transparencySlider, transparency.Value,
        varies: transparency.Varies);
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
    CancellationTokenSource? sharpnessCancellation = _sharpnessCancellation;
    _sharpnessCancellation = null;
    sharpnessCancellation?.Cancel();
    foreach (UITimer timer in AllTimers())
      timer.Stop();
    _busyDelayTimer.Stop();
    _pendingBusyActions.Clear();
    _persistentBusyActions = 0;
    _persistentBusyOperation = null;
    SetBusy(false);
    _sliderTargetIds.Clear();
    DetachImageDropHoverHandlers();
    ClearPictureHighlights();
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
    SetScaleFactorValue(null);
    _scaleFactorBox.PlaceholderText = "-";
    _containedObjectsToggle.Checked = false;
    _containedObjectsToggle.Text = "Shown";
    SetCheckState(_selfIlluminationCheck, null);
    SetCheckState(_alphaChannelCheck, null);
    SetCheckState(_colorMaskCheck, null);
    _colorMaskButton.Text = string.Empty;
    _colorMaskButton.Image = _eyedropperIcon;
    _colorMaskButton.ToolTip = "Pick mask color";
    SetPercentageSliderValue(_toleranceSlider, null);
    SetPercentageSliderValue(_transparencySlider, null);
    SetAdjustmentSliderValue(_brightnessSlider, null);
    SetAdjustmentSliderValue(_contrastSlider, null);
    SetAdjustmentSliderValue(_saturationSlider, null);
    SetPercentageSliderValue(_sharpnessSlider, null);
    SetDropValue(_sharpnessAlgorithmDrop, PictureImageProcessor.UnsharpMask,
      PictureImageProcessor.Algorithms);
    SetEnabled(false);
  }

  private void SetEnabled(bool enabled)
  {
    _imageDrop.Enabled = enabled;
    _scaleFactorBox.Enabled = enabled;
    _calibrateScaleButton.Enabled = enabled;
    _scaleContentsCheck.Enabled = enabled;
    _containedObjectsToggle.Enabled = enabled;
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
    foreach (Button button in _sliderResetButtons.Values)
      SetResetButtonState(button, enabled);
  }

  private void UpdateImageDrop(IReadOnlyList<RhinoObject> pictures)
  {
    if (_focusedPictureId != Guid.Empty
      && pictures.All(picture => picture.Id != _focusedPictureId))
      _focusedPictureId = Guid.Empty;

    var labels = new List<string> { "All" };
    var baseLabels = pictures
      .Select((picture, index) => PictureListLabel(picture, index))
      .ToList();
    var totals = baseLabels
      .GroupBy(label => label, StringComparer.OrdinalIgnoreCase)
      .ToDictionary(group => group.Key, group => group.Count(),
        StringComparer.OrdinalIgnoreCase);
    var occurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (string label in baseLabels)
    {
      occurrences.TryGetValue(label, out int occurrence);
      occurrence++;
      occurrences[label] = occurrence;
      labels.Add(totals[label] > 1 ? $"{label} ({occurrence})" : label);
    }

    var ids = new List<Guid> { Guid.Empty };
    ids.AddRange(pictures.Select(picture => picture.Id));
    bool itemsChanged = !_imageDropMap.SequenceEqual(ids)
      || !_imageDropLabels.SequenceEqual(labels, StringComparer.Ordinal);
    if (itemsChanged)
    {
      _imageDropMap.Clear();
      _imageDropMap.AddRange(ids);
      _imageDropLabels.Clear();
      _imageDropLabels.AddRange(labels);
      _imageDrop.DataStore = labels;
    }

    int selectedIndex = _focusedPictureId == Guid.Empty
      ? 0
      : _imageDropMap.IndexOf(_focusedPictureId);
    _imageDrop.SelectedIndex = Math.Max(0, selectedIndex);
    _imageDrop.Enabled = pictures.Count > 0;
  }

  private static string PictureListLabel(RhinoObject picture, int index)
  {
    string objectName = picture.Attributes.Name?.Trim() ?? string.Empty;
    string fileName = Path.GetFileName(PictureSourceFile(picture));
    if (!string.IsNullOrWhiteSpace(objectName) && !string.IsNullOrWhiteSpace(fileName)
      && !string.Equals(objectName, fileName, StringComparison.OrdinalIgnoreCase))
      return $"{objectName} - {fileName}";
    if (!string.IsNullOrWhiteSpace(objectName))
      return objectName;
    return !string.IsNullOrWhiteSpace(fileName) ? fileName : $"Image {index + 1}";
  }

  private void OnImageDropSelectedIndexChanged(object? sender, EventArgs e)
  {
    if (_isUpdatingUi)
      return;

    int index = _imageDrop.SelectedIndex;
    Guid pictureId = index >= 0 && index < _imageDropMap.Count
      ? _imageDropMap[index]
      : Guid.Empty;
    if (_focusedPictureId == pictureId)
      return;

    _focusedPictureId = pictureId;
    RefreshTargets();
  }

  private void OnImageDropClosed(object? sender, EventArgs e)
    => ClearImageDropHoverPreview();

  private void InstallImageDropHoverHandlers()
  {
    DetachImageDropHoverHandlers();
    var root = _imageDrop.ControlObject as System.Windows.DependencyObject;
    _nativeImageDrop = root as System.Windows.Controls.ComboBox
      ?? FindVisualChild<System.Windows.Controls.ComboBox>(root);
    if (_nativeImageDrop == null)
    {
      Log.Write("InstallImageDropHoverHandlers: native ComboBox not found");
      return;
    }

    System.Windows.Input.InputManager.Current.PreProcessInput +=
      OnImageDropPreProcessInput;
    _imageDropInputTracking = true;
  }

  private void DetachImageDropHoverHandlers()
  {
    if (_imageDropInputTracking)
    {
      System.Windows.Input.InputManager.Current.PreProcessInput -=
        OnImageDropPreProcessInput;
      _imageDropInputTracking = false;
    }
    _nativeImageDrop = null;
  }

  private void OnImageDropPreProcessInput(object sender,
    System.Windows.Input.PreProcessInputEventArgs e)
  {
    if (_nativeImageDrop == null || !_nativeImageDrop.IsDropDownOpen
      || e.StagingItem.Input is not System.Windows.Input.MouseEventArgs)
      return;

    var item = FindVisualAncestor<System.Windows.Controls.ComboBoxItem>(
      System.Windows.Input.Mouse.DirectlyOver as System.Windows.DependencyObject);
    var owner = item == null
      ? null
      : System.Windows.Controls.ItemsControl.ItemsControlFromItemContainer(item);
    if (item == null || !ReferenceEquals(owner, _nativeImageDrop))
    {
      ClearImageDropHoverPreview();
      return;
    }

    int index = _nativeImageDrop.ItemContainerGenerator.IndexFromContainer(item);
    ShowImageDropHoverPreview(index);
  }

  private void ShowImageDropHoverPreview(int index)
  {
    if (_doc == null || index <= 0 || index >= _imageDropMap.Count)
    {
      ClearImageDropHoverPreview();
      return;
    }
    if (_hoveredImageDropIndex == index && _hoverPictureConduit.Enabled)
      return;

    RhinoObject? picture = _doc.Objects.FindId(_imageDropMap[index]);
    if (picture == null || !IsPictureObject(picture))
    {
      ClearImageDropHoverPreview();
      return;
    }

    if (!_selectedPictureHighlightSuspended && _selectedPictureConduit.Enabled)
    {
      _selectedPictureConduit.Enabled = false;
      _selectedPictureHighlightSuspended = true;
    }
    _hoverPictureConduit.SetObject(picture,
      Rhino.ApplicationSettings.AppearanceSettings.TrackingColor);
    _hoverPictureConduit.Enabled = true;
    _hoveredImageDropIndex = index;
    _doc.Views.Redraw();
  }

  private void ClearImageDropHoverPreview()
  {
    bool redraw = _hoverPictureConduit.Enabled || _selectedPictureHighlightSuspended;
    _hoverPictureConduit.Clear();
    _hoverPictureConduit.Enabled = false;
    _hoveredImageDropIndex = -1;
    if (_selectedPictureHighlightSuspended)
    {
      _selectedPictureConduit.Enabled = _focusedPictureId != Guid.Empty
        && _selectedPictureConduit.ObjectId != Guid.Empty;
      _selectedPictureHighlightSuspended = false;
    }
    if (redraw)
      _doc?.Views.Redraw();
  }

  private void UpdateSelectedPictureHighlight(IReadOnlyList<RhinoObject> pictures)
  {
    RhinoObject? picture = _focusedPictureId == Guid.Empty
      ? null
      : pictures.FirstOrDefault(item => item.Id == _focusedPictureId);
    Guid previousId = _selectedPictureConduit.ObjectId;
    bool wasEnabled = _selectedPictureConduit.Enabled;
    if (picture == null)
    {
      _selectedPictureConduit.Clear();
      _selectedPictureConduit.Enabled = false;
    }
    else
    {
      _selectedPictureConduit.SetObject(picture,
        Rhino.ApplicationSettings.AppearanceSettings.SelectedObjectColor);
      _selectedPictureConduit.Enabled = !_selectedPictureHighlightSuspended;
    }

    if (previousId != _selectedPictureConduit.ObjectId
      || wasEnabled != _selectedPictureConduit.Enabled)
      _doc?.Views.Redraw();
  }

  private void ClearPictureHighlights()
  {
    bool redraw = _selectedPictureConduit.Enabled || _hoverPictureConduit.Enabled;
    _selectedPictureConduit.Clear();
    _selectedPictureConduit.Enabled = false;
    _hoverPictureConduit.Clear();
    _hoverPictureConduit.Enabled = false;
    _selectedPictureHighlightSuspended = false;
    _hoveredImageDropIndex = -1;
    if (redraw)
      _doc?.Views.Redraw();
  }

  private static T? FindVisualChild<T>(System.Windows.DependencyObject? root)
    where T : System.Windows.DependencyObject
  {
    if (root == null)
      return null;
    if (root is T match)
      return match;
    int childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
    for (int i = 0; i < childCount; i++)
    {
      var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
      T? descendant = FindVisualChild<T>(child);
      if (descendant != null)
        return descendant;
    }
    return null;
  }

  private static T? FindVisualAncestor<T>(System.Windows.DependencyObject? current)
    where T : System.Windows.DependencyObject
  {
    while (current != null)
    {
      if (current is T match)
        return match;
      try
      {
        current = System.Windows.Media.VisualTreeHelper.GetParent(current);
      }
      catch (InvalidOperationException)
      {
        current = System.Windows.LogicalTreeHelper.GetParent(current);
      }
    }
    return null;
  }

  private void SetScaleFactorValue(double? value)
  {
    _scaleFactorDirty = false;
    _scaleEditTargetIds = Array.Empty<Guid>();
    if (!value.HasValue)
    {
      _scaleFactorBox.Text = string.Empty;
      _scaleFactorBox.PlaceholderText = VariesText;
      return;
    }

    _scaleFactorBox.PlaceholderText = string.Empty;
    _scaleFactorBox.Text = value.Value.ToString("0.####", CultureInfo.CurrentCulture);
  }

  private static bool TryParseScale(string? text, out double value)
  {
    string input = (text ?? string.Empty).Trim();
    bool parsed = double.TryParse(input, NumberStyles.Float,
        CultureInfo.CurrentCulture, out value)
      || double.TryParse(input, NumberStyles.Float,
        CultureInfo.InvariantCulture, out value);
    return parsed
      && RhinoMath.IsValidDouble(value)
      && value > RhinoMath.ZeroTolerance;
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

  private void ApplyContainedObjectVisibility()
  {
    if (_isUpdatingUi || _doc == null)
      return;

    Guid[] targetIds = CurrentTargetIds();
    if (targetIds.Length == 0)
      return;
    bool hideObjects = _containedObjectsToggle.Checked == true;
    RunWithBusyIndicator("contained objects",
      () => ApplyContainedObjectVisibility(targetIds, hideObjects));
  }

  private void ApplyContainedObjectVisibility(
    IReadOnlyCollection<Guid> targetObjectIds, bool hideObjects)
  {
    if (_doc == null)
      return;

    List<RhinoObject> pictures = ResolveTargets(targetObjectIds);
    if (pictures.Count == 0)
      return;

    uint undoRecord = _doc.BeginUndoRecord(hideObjects
      ? "Properties+ Hide Background Objects"
      : "Properties+ Show Background Objects");
    bool changed = false;
    _isApplying = true;
    try
    {
      changed = hideObjects
        ? HideContainedObjects(pictures)
        : ShowContainedObjects(pictures);
    }
    catch (Exception ex)
    {
      Log.Write($"Toggle background contained objects failed: {ex}");
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

    _containedObjectsToggle.Text = hideObjects ? "Hidden" : "Shown";
    if (!changed)
      return;
    _doc.Views.Redraw();
    RefreshTargets();
  }

  private bool HideContainedObjects(IReadOnlyList<RhinoObject> pictures)
  {
    if (_doc == null)
      return false;

    double tolerance = Math.Max(_doc.ModelAbsoluteTolerance, RhinoMath.SqrtEpsilon);
    var scopes = pictures
      .Select(picture => TryCreateScaleScope(picture, tolerance,
        out PictureScaleScope? scope) ? scope : null)
      .Where(scope => scope != null)
      .Cast<PictureScaleScope>()
      .OrderBy(scope => scope.Area)
      .ToList();
    if (scopes.Count == 0)
      return false;

    var pictureIds = pictures.Select(picture => picture.Id).ToHashSet();
    var assignments = new Dictionary<Guid, PictureScaleScope>();
    var candidates = _doc.Objects.GetObjectList(ObjectType.AnyObject)
      .Where(obj => obj != null
        && !pictureIds.Contains(obj.Id)
        && !IsPictureObject(obj)
        && obj.IsNormal
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

    bool changed = false;
    foreach (PictureScaleScope scope in scopes)
    {
      RhinoObject? picture = _doc.Objects.FindId(scope.PictureId);
      if (picture == null)
        continue;

      var hiddenIds = ReadHiddenObjectIds(picture)
        .Where(id => _doc.Objects.FindId(id)?.IsHidden == true)
        .ToHashSet();
      string hideGroup = PictureHideGroupName(picture);
      foreach (Guid objectId in assignments
        .Where(pair => pair.Value.PictureId == scope.PictureId)
        .Select(pair => pair.Key))
      {
        if (_doc.Objects.Hide(objectId, false, hideGroup))
        {
          hiddenIds.Add(objectId);
          changed = true;
        }
      }
      changed |= WriteHiddenObjectIds(picture, hiddenIds);
    }
    return changed;
  }

  private bool ShowContainedObjects(IReadOnlyList<RhinoObject> pictures)
  {
    if (_doc == null)
      return false;

    bool changed = false;
    foreach (RhinoObject picture in pictures)
    {
      Guid[] hiddenIds = ReadHiddenObjectIds(picture);
      foreach (Guid objectId in hiddenIds)
      {
        RhinoObject? obj = _doc.Objects.FindId(objectId);
        if (obj?.IsHidden == true)
          changed |= _doc.Objects.Show(objectId, false);
      }

      var remainingIds = hiddenIds
        .Where(id => _doc.Objects.FindId(id)?.IsHidden == true)
        .ToArray();
      changed |= WriteHiddenObjectIds(picture, remainingIds);
    }
    return changed;
  }

  private bool PictureHasHiddenObjects(RhinoObject picture)
  {
    return _doc != null && ReadHiddenObjectIds(picture)
      .Any(id => _doc.Objects.FindId(id)?.IsHidden == true);
  }

  private bool WriteHiddenObjectIds(RhinoObject picture, IEnumerable<Guid> objectIds)
  {
    if (_doc == null)
      return false;

    string value = string.Join(";", objectIds
      .Distinct()
      .OrderBy(id => id)
      .Select(id => id.ToString("N")));
    picture.Attributes.UserDictionary.TryGetString(HiddenObjectIdsKey,
      out string existingValue);
    if (string.Equals(existingValue ?? string.Empty, value,
      StringComparison.OrdinalIgnoreCase))
      return false;

    ObjectAttributes attributes = picture.Attributes.Duplicate();
    if (value.Length == 0)
      attributes.UserDictionary.Remove(HiddenObjectIdsKey);
    else
      attributes.UserDictionary.Set(HiddenObjectIdsKey, value);
    return _doc.Objects.ModifyAttributes(picture, attributes, true);
  }

  private static Guid[] ReadHiddenObjectIds(RhinoObject picture)
  {
    if (!picture.Attributes.UserDictionary.TryGetString(HiddenObjectIdsKey,
      out string value) || string.IsNullOrWhiteSpace(value))
      return Array.Empty<Guid>();

    return value.Split(';', StringSplitOptions.RemoveEmptyEntries)
      .Select(item => Guid.TryParseExact(item, "N", out Guid id) ? id : Guid.Empty)
      .Where(id => id != Guid.Empty)
      .Distinct()
      .ToArray();
  }

  private static string PictureHideGroupName(RhinoObject picture)
  {
    string label = Path.GetFileName(PictureSourceFile(picture));
    if (string.IsNullOrWhiteSpace(label))
      label = "picture";
    return $"vObjectProperties+ Background {label} {picture.Id:N}";
  }

  private void OnScaleFactorTextChanged()
  {
    if (_isUpdatingUi)
      return;

    if (!_scaleFactorDirty)
      _scaleEditTargetIds = CurrentTargetIds();
    _scaleFactorDirty = true;
  }

  private void ApplyScaleFactorField()
  {
    if (_isUpdatingUi || _suppressScaleCommit || !_scaleFactorDirty)
      return;

    _scaleFactorDirty = false;
    Guid[] targetIds = _scaleEditTargetIds.Length > 0
      ? _scaleEditTargetIds
      : CurrentTargetIds();
    _scaleEditTargetIds = Array.Empty<Guid>();

    if (!TryParseScale(_scaleFactorBox.Text, out double absoluteScale))
    {
      RefreshTargets();
      return;
    }

    bool includeContents = _scaleContentsCheck.Checked == true;
    RunWithBusyIndicator("scale", () =>
      ScalePictures(targetIds, absoluteScale, includeContents, relative: false));
  }

  private void CalibrateScale()
  {
    if (_isUpdatingUi || _doc == null || _targetIds.Count == 0)
      return;

    Guid[] targetIds = CurrentTargetIds();
    _scaleFactorDirty = false;
    _scaleEditTargetIds = Array.Empty<Guid>();
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

    double relativeScale = desiredDistance / measuredDistance;
    bool includeContents = _scaleContentsCheck.Checked == true;
    RunWithBusyIndicator("scale", () =>
      ScalePictures(targetIds, relativeScale, includeContents, relative: true));
  }

  private void ScalePictures(IReadOnlyCollection<Guid> targetObjectIds,
    double scaleValue, bool includeContents, bool relative)
  {
    if (_doc == null || !RhinoMath.IsValidDouble(scaleValue)
      || scaleValue <= RhinoMath.ZeroTolerance)
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

    foreach (PictureScaleScope scope in scopes)
    {
      scope.TransformFactor = relative
        ? scaleValue
        : scaleValue / scope.CurrentScale;
    }
    scopes = scopes
      .Where(scope => !RhinoMath.EpsilonEquals(scope.TransformFactor, 1.0, 1e-9))
      .ToList();
    if (scopes.Count == 0)
    {
      RefreshTargets();
      return;
    }

    var targetIds = scopes.Select(scope => scope.PictureId).ToHashSet();
    var assignments = new Dictionary<Guid, PictureScaleScope>();
    if (includeContents)
    {
      var candidates = _doc.Objects.GetObjectList(ObjectType.AnyObject)
        .Where(obj => obj != null
          && !targetIds.Contains(obj.Id)
          && !IsPictureObject(obj)
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
        changed |= EnsureOriginalPictureArea(scope);
        Transform transform = scope.CreateTransform();
        changed |= TransformPreservingSelection(scope.PictureId, transform);
      }

      foreach ((Guid objectId, PictureScaleScope scope) in assignments)
      {
        Transform transform = scope.CreateTransform();
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

  private bool EnsureOriginalPictureArea(PictureScaleScope scope)
  {
    if (_doc == null || scope.HasOriginalArea)
      return false;

    RhinoObject? picture = _doc.Objects.FindId(scope.PictureId);
    if (picture == null)
      return false;

    ObjectAttributes attributes = picture.Attributes.Duplicate();
    attributes.UserDictionary.Set(OriginalPictureAreaKey, scope.OriginalArea);
    return _doc.Objects.ModifyAttributes(picture, attributes, true);
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

    double area = (maxU - minU) * (maxV - minV);
    bool hasOriginalArea = picture.Attributes.UserDictionary.TryGetDouble(
      OriginalPictureAreaKey, out double originalArea)
      && RhinoMath.IsValidDouble(originalArea)
      && originalArea > tolerance * tolerance;
    if (!hasOriginalArea && !TryGetNativePictureArea(picture, out originalArea))
      originalArea = area;

    scope = new PictureScaleScope(
      picture.Id,
      plane,
      minU,
      maxU,
      minV,
      maxV,
      plane.PointAt((minU + maxU) * 0.5, (minV + maxV) * 0.5),
      originalArea,
      hasOriginalArea);
    return true;
  }

  private static double PictureAbsoluteScale(RhinoObject picture, double tolerance)
  {
    return TryCreateScaleScope(picture, tolerance, out PictureScaleScope? scope)
      && scope != null
      ? scope.CurrentScale
      : 1.0;
  }

  private static bool TryGetNativePictureArea(RhinoObject picture, out double area)
  {
    area = 0.0;
    string path = PictureSourceFile(picture);
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
      return false;

    try
    {
      var file = new FileInfo(path);
      long length = file.Length;
      DateTime lastWriteUtc = file.LastWriteTimeUtc;
      lock (PictureDimensionCacheLock)
      {
        if (PictureDimensionCache.TryGetValue(path, out PictureDimensionCacheEntry cached)
          && cached.Length == length
          && cached.LastWriteUtc == lastWriteUtc)
        {
          area = cached.Area;
          return true;
        }
      }

      using var image = System.Drawing.Image.FromFile(path);
      area = (double)image.Width * image.Height;
      if (!RhinoMath.IsValidDouble(area) || area <= 0.0)
        return false;

      lock (PictureDimensionCacheLock)
      {
        PictureDimensionCache[path] = new PictureDimensionCacheEntry(
          length, lastWriteUtc, area);
      }
      return true;
    }
    catch (Exception ex)
    {
      Log.Write($"Read native picture dimensions failed for '{path}': {ex.Message}");
      return false;
    }
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

  private void ApplyPictureBoolean(CheckBox checkBox, string parameterName,
    bool texture, string undoName, string operationName)
  {
    if (_isUpdatingUi || !checkBox.Checked.HasValue)
      return;
    bool value = checkBox.Checked.Value;
    Guid[] targetIds = CurrentTargetIds();
    RunWithBusyIndicator(operationName, () => ApplyParameters(undoName,
      new[] { (parameterName, (object)value) }, texture, false, targetIds));
  }

  private void ApplyPictureColorMask()
  {
    if (_isUpdatingUi || !_colorMaskCheck.Checked.HasValue)
      return;

    bool enabled = _colorMaskCheck.Checked.Value;
    _colorMaskButton.Enabled = enabled;
    _toleranceSlider.Enabled = enabled;
    SetResetButtonEnabled(_toleranceSlider, _toleranceSlider.Value1, enabled);
    Guid[] targetIds = CurrentTargetIds();
    RunWithBusyIndicator("color mask", () => ApplyParameters("Picture Color Mask",
      new[] { ("has-transparent-color", (object)enabled) }, true,
      false, targetIds));
  }

  private void QueueSlider(UITimer timer, RhinoSlider slider)
  {
    if (_isUpdatingUi)
      return;

    if (!_sliderTargetIds.ContainsKey(timer))
      _sliderTargetIds[timer] = CurrentTargetIds();
    timer.Stop();
    timer.Start();
  }

  private void WireSlider(RhinoSlider slider, UITimer timer,
    Action? beforeQueue = null)
  {
    slider.PropertyChanged += (_, e) =>
    {
      if (_isUpdatingUi || e.PropertyName != nameof(RhinoSlider.Value1))
        return;
      SetSliderMixedState(slider, false, SliderValue(slider));
      SetResetButtonEnabled(slider, slider.Value1);
      beforeQueue?.Invoke();
      QueueSlider(timer, slider);
    };
  }

  private static bool FinishSliderGesture(UITimer timer)
  {
    timer.Stop();
    if (Mouse.IsSupported && Mouse.IsAnyButtonPressed(MouseButtons.Primary))
    {
      timer.Start();
      return false;
    }
    return true;
  }

  private void RunWithBusyIndicator(string operationName, Action action,
    bool persistsUntilCompleted = false)
  {
    if (_stopped)
      return;

    _pendingBusyActions.Enqueue((operationName, action, persistsUntilCompleted));
    SetBusy(true, operationName);
    _busyDelayTimer.Stop();
    _busyDelayTimer.Start();
  }

  private void RunPendingBusyAction()
  {
    _busyDelayTimer.Stop();
    if (_stopped || _pendingBusyActions.Count == 0)
    {
      SetBusy(_persistentBusyActions > 0, _persistentBusyOperation);
      return;
    }

    (string operationName, Action action, bool persistsUntilCompleted) =
      _pendingBusyActions.Dequeue();
    SetBusy(true, operationName);
    if (persistsUntilCompleted)
    {
      _persistentBusyActions++;
      _persistentBusyOperation = operationName;
    }
    try
    {
      action();
    }
    catch (Exception ex)
    {
      Log.Write($"Apply picture operation failed: {ex}");
      if (persistsUntilCompleted)
        CompletePersistentBusyAction();
    }

    if (_pendingBusyActions.Count > 0)
      _busyDelayTimer.Start();
    else if (!persistsUntilCompleted)
      SetBusy(_persistentBusyActions > 0, _persistentBusyOperation);
  }

  private void CompletePersistentBusyAction()
  {
    if (_persistentBusyActions > 0)
      _persistentBusyActions--;
    if (_persistentBusyActions == 0)
      _persistentBusyOperation = null;
    string? operationName = _pendingBusyActions.Count > 0
      ? _pendingBusyActions.Peek().Name
      : _persistentBusyOperation;
    SetBusy(operationName != null, operationName);
  }

  private void SetBusy(bool busy, string? operationName = null)
  {
    foreach ((string name, ImageView indicator) in _busyIndicators)
    {
      bool active = busy && string.Equals(name, operationName, StringComparison.Ordinal);
      indicator.Image = active ? _busyIcon : null;
      indicator.ToolTip = active ? $"Applying {name}..." : string.Empty;
    }
  }

  private Button NewSliderResetButton(RhinoSlider slider, UITimer timer,
    string propertyName, Action? beforeQueue = null)
  {
    var button = new Button
    {
      Width = 22,
      Height = RowHeight,
      Image = _resetIcon,
      ToolTip = $"Reset {propertyName}"
    };
    button.Click += (_, _) => ResetSlider(slider, timer, beforeQueue);
    _sliderResetButtons[slider] = button;
    return button;
  }

  private void ResetSlider(RhinoSlider slider, UITimer timer, Action? beforeQueue)
  {
    if (_isUpdatingUi || _doc == null)
      return;
    Guid[] targetIds = CurrentTargetIds();
    if (targetIds.Length == 0)
      return;

    timer.Stop();
    _sliderTargetIds[timer] = targetIds;
    _isUpdatingUi = true;
    try
    {
      slider.SetVaries(false);
      slider.Value1 = 0.0;
      SetSliderMixedState(slider, false, 0);
    }
    finally
    {
      _isUpdatingUi = false;
    }
    beforeQueue?.Invoke();
    SetResetButtonEnabled(slider, 0.0);
    timer.Start();
  }

  private void SetResetButtonEnabled(RhinoSlider slider, double? value,
    bool controlsEnabled = true, bool varies = false)
  {
    if (_sliderResetButtons.TryGetValue(slider, out Button? button))
      SetResetButtonState(button, controlsEnabled
        && (varies || !value.HasValue
          || !RhinoMath.EpsilonEquals(value.Value, 0.0, 1e-6)));
  }

  private void SetResetButtonState(Button button, bool enabled)
  {
    button.Enabled = enabled;
    button.Image = enabled ? _resetIcon : _disabledResetIcon;
  }

  private static int SliderValue(RhinoSlider slider)
    => (int)Math.Round(slider.Value1 ?? 0.0);

  private void InvalidateSharpnessRequest()
  {
    _sharpnessRequestVersion++;
    _sharpnessCancellation?.Cancel();
  }

  private void ApplyPercentage(RhinoSlider slider, string parameterName,
    bool texture, string undoName, double scale, double offset,
    IReadOnlyCollection<Guid> targetObjectIds)
  {
    ApplyParameters(undoName,
      new[] { (parameterName, (object)(offset + SliderValue(slider) * scale)) }, texture,
      false, targetObjectIds,
      changeContext: RenderContent.ChangeContexts.RealTimeUI);
  }

  private void ApplySaturation(IReadOnlyCollection<Guid> targetObjectIds)
  {
    ApplyParameters("Picture Saturation", new[]
    {
      ("rdk-texture-adjust-grayscale", (object)false),
      ("rdk-texture-adjust-saturation", (object)(1.0 + SliderValue(_saturationSlider) * 0.01))
    }, true, false, targetObjectIds,
      changeContext: RenderContent.ChangeContexts.RealTimeUI);
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

    Guid[] targetIds = CurrentTargetIds();
    RhinoObject? selectedPicture = ResolveTargets(targetIds).FirstOrDefault();
    if (selectedPicture == null)
      return;

    var color4f = new Color4f(ReadPictureMaskColor(selectedPicture)
      ?? System.Drawing.Color.Black);
    if (!Rhino.UI.Dialogs.ShowColorDialog(ref color4f, true))
      return;

    RunWithBusyIndicator("color mask", () => ApplyParameters("Picture Color Mask",
      new[] { ("transparent-color", (object)color4f) }, true,
      true, targetIds));
  }

  private void ApplyParameters(string undoName,
    IReadOnlyList<(string Name, object Value)> parameters, bool texture,
    bool refreshControls, IReadOnlyCollection<Guid> targetObjectIds,
    Action<ObjectAttributes>? updateAttributes = null,
    RenderContent.ChangeContexts changeContext = RenderContent.ChangeContexts.UI)
  {
    var totalTimer = Stopwatch.StartNew();
    var stageTimer = Stopwatch.StartNew();
    double resolveTargetsMs = 0.0;
    double resolveContentsMs = 0.0;
    double beginUndoMs = 0.0;
    double beginChangeMs = 0.0;
    double setParameterMs = 0.0;
    double endChangeMs = 0.0;
    double attributesMs = 0.0;
    double endUndoMs = 0.0;
    double redrawMs = 0.0;
    double refreshMs = 0.0;
    int targetCount = 0;
    int contentCount = 0;
    int parameterWrites = 0;
    bool changed = false;
    string outcome = "ignored";
    try
    {
      if (_isUpdatingUi || _doc == null)
        return;

      stageTimer.Restart();
      List<RhinoObject> targetObjects = ResolveTargets(targetObjectIds);
      resolveTargetsMs = stageTimer.Elapsed.TotalMilliseconds;
      targetCount = targetObjects.Count;

      stageTimer.Restart();
      uint undoRecord = _doc.BeginUndoRecord($"Properties+ {undoName}");
      beginUndoMs = stageTimer.Elapsed.TotalMilliseconds;
      _isApplying = true;
      try
      {
        Guid[] resolvedTargetIds = targetObjects.Select(obj => obj.Id).ToArray();
        changed |= EnsureExclusivePictureMaterials(targetObjects);
        targetObjects = ResolveTargets(resolvedTargetIds);

        stageTimer.Restart();
        var contents = targetObjects
          .Select(obj => PictureContent(obj, texture))
          .Where(content => content != null)
          .Cast<RenderContent>()
          .GroupBy(content => content.Id)
          .Select(group => group.First())
          .ToList();
        resolveContentsMs = stageTimer.Elapsed.TotalMilliseconds;
        contentCount = contents.Count;
        if (contents.Count == 0)
        {
          outcome = "no-content";
          return;
        }

        foreach (RenderContent content in contents)
        {
          stageTimer.Restart();
          content.BeginChange(changeContext);
          beginChangeMs += stageTimer.Elapsed.TotalMilliseconds;
          try
          {
            foreach ((string name, object value) in parameters)
            {
              stageTimer.Restart();
              try
              {
                changed |= content.SetParameter(name, value);
              }
              catch (Exception ex)
              {
                Log.Write($"Apply picture parameter failed for {name}: {ex}");
              }
              finally
              {
                setParameterMs += stageTimer.Elapsed.TotalMilliseconds;
                parameterWrites++;
              }
            }
          }
          finally
          {
            stageTimer.Restart();
            try
            {
              content.EndChange();
            }
            finally
            {
              endChangeMs += stageTimer.Elapsed.TotalMilliseconds;
            }
          }
        }

        if (updateAttributes != null)
        {
          stageTimer.Restart();
          foreach (RhinoObject obj in targetObjects)
          {
            ObjectAttributes attributes = obj.Attributes.Duplicate();
            updateAttributes(attributes);
            changed |= _doc.Objects.ModifyAttributes(obj, attributes, true);
          }
          attributesMs = stageTimer.Elapsed.TotalMilliseconds;
        }
      }
      finally
      {
        stageTimer.Restart();
        try
        {
          if (undoRecord != 0)
            _doc.EndUndoRecord(undoRecord);
        }
        finally
        {
          endUndoMs = stageTimer.Elapsed.TotalMilliseconds;
          _isApplying = false;
        }
      }

      if (!changed)
      {
        outcome = "unchanged";
        return;
      }

      stageTimer.Restart();
      _doc.Views.Redraw();
      redrawMs = stageTimer.Elapsed.TotalMilliseconds;
      if (refreshControls)
      {
        stageTimer.Restart();
        RefreshTargets();
        refreshMs = stageTimer.Elapsed.TotalMilliseconds;
      }
      outcome = "changed";
    }
    catch
    {
      outcome = "failed";
      throw;
    }
    finally
    {
      Log.Write("PictureTiming", $"{undoName}: context={changeContext}, outcome={outcome}, "
        + $"requested={targetObjectIds.Count}, targets={targetCount}, contents={contentCount}, "
        + $"writes={parameterWrites}, total={totalTimer.Elapsed.TotalMilliseconds:0.0}ms, "
        + $"resolve-targets={resolveTargetsMs:0.0}ms, resolve-content={resolveContentsMs:0.0}ms, "
        + $"begin-undo={beginUndoMs:0.0}ms, begin-change={beginChangeMs:0.0}ms, "
        + $"set-parameter={setParameterMs:0.0}ms, end-change={endChangeMs:0.0}ms, "
        + $"attributes={attributesMs:0.0}ms, end-undo={endUndoMs:0.0}ms, "
        + $"redraw={redrawMs:0.0}ms, refresh={refreshMs:0.0}ms");
    }
  }

  private bool EnsureExclusivePictureMaterials(IReadOnlyList<RhinoObject> targets)
  {
    if (_doc == null || targets.Count == 0)
      return false;

    var targetIds = targets.Select(target => target.Id).ToHashSet();
    var materialUsers = _doc.Objects.GetObjectList(ObjectType.AnyObject)
      .Where(obj => obj != null && obj.RenderMaterial != null)
      .Cast<RhinoObject>()
      .ToList();
    bool changed = false;
    foreach (IGrouping<Guid, RhinoObject> group in targets
      .Where(target => target.RenderMaterial != null)
      .GroupBy(target => target.RenderMaterial!.Id))
    {
      if (!materialUsers.Any(obj => !targetIds.Contains(obj.Id)
        && obj.RenderMaterial?.Id == group.Key))
        continue;

      RenderMaterial? source = group.First().RenderMaterial;
      RenderMaterial? copy = source?.MakeCopy() as RenderMaterial;
      if (copy == null || !_doc.RenderMaterials.Add(copy))
      {
        Log.Write($"Copy shared picture material failed: {group.Key}");
        continue;
      }

      foreach (RhinoObject target in group)
        changed |= _doc.Objects.ModifyRenderMaterial(target.Id, copy);
    }
    return changed;
  }

  private async void ApplySharpnessAsync(IReadOnlyCollection<Guid> targetObjectIds,
    int level, string algorithm, int requestVersion,
    CancellationTokenSource cancellationSource)
  {
    var totalTimer = Stopwatch.StartNew();
    CancellationToken cancellationToken = cancellationSource.Token;
    if (_doc == null || requestVersion != _sharpnessRequestVersion
      || cancellationToken.IsCancellationRequested)
    {
      FinishSharpnessRequest(cancellationSource);
      return;
    }

    if (level <= 0)
    {
      try
      {
        RestoreOriginalSharpness(targetObjectIds, requestVersion, algorithm);
      }
      finally
      {
        FinishSharpnessRequest(cancellationSource);
      }
      return;
    }

    var stageTimer = Stopwatch.StartNew();
    var jobs = ResolveTargets(targetObjectIds)
      .Where(obj => obj.RenderMaterial != null)
      .Select(obj => new
      {
        ObjectId = obj.Id,
        SourceFile = PictureSourceFile(obj)
      })
      .Where(item => !string.IsNullOrWhiteSpace(item.SourceFile))
      .GroupBy(item => item.SourceFile, StringComparer.OrdinalIgnoreCase)
      .Select(group => new SharpnessJob(
        group.Key,
        group.Select(item => item.ObjectId).Distinct().ToArray()))
      .ToList();
    double prepareMs = stageTimer.Elapsed.TotalMilliseconds;
    if (jobs.Count == 0)
    {
      Log.Write("PictureTiming", $"Picture Sharpness: outcome=no-jobs, "
        + $"requested={targetObjectIds.Count}, prepare={prepareMs:0.0}ms, "
        + $"total={totalTimer.Elapsed.TotalMilliseconds:0.0}ms");
      FinishSharpnessRequest(cancellationSource);
      return;
    }

    List<SharpnessResult> results;
    try
    {
      stageTimer.Restart();
      results = await Task.Run(() =>
      {
        var processed = new List<SharpnessResult>();
        try
        {
          foreach (SharpnessJob job in jobs)
          {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(job.SourceFile))
            {
              Log.Write($"Picture sharpen source was not found: {job.SourceFile}");
              continue;
            }
            try
            {
              processed.Add(new SharpnessResult(job,
                PictureImageProcessor.SharpenToTemporaryFile(
                  job.SourceFile, level, algorithm, cancellationToken)));
            }
            catch (OperationCanceledException)
            {
              throw;
            }
            catch (Exception ex)
            {
              Log.Write($"Picture sharpen failed for '{job.SourceFile}': {ex}");
            }
          }
          return processed;
        }
        catch
        {
          DisposeResults(processed);
          throw;
        }
      }, cancellationToken);
      Log.Write("PictureTiming", $"Picture Sharpness background: algorithm={algorithm}, "
        + $"level={level}, jobs={jobs.Count}, results={results.Count}, "
        + $"prepare={prepareMs:0.0}ms, process={stageTimer.Elapsed.TotalMilliseconds:0.0}ms, "
        + $"elapsed={totalTimer.Elapsed.TotalMilliseconds:0.0}ms");
    }
    catch (OperationCanceledException)
    {
      Log.Write("PictureTiming", $"Picture Sharpness background: outcome=cancelled, "
        + $"algorithm={algorithm}, level={level}, jobs={jobs.Count}, "
        + $"prepare={prepareMs:0.0}ms, elapsed={totalTimer.Elapsed.TotalMilliseconds:0.0}ms");
      FinishSharpnessRequest(cancellationSource);
      return;
    }
    catch (Exception ex)
    {
      Log.Write($"Picture sharpen task failed: {ex}");
      FinishSharpnessRequest(cancellationSource);
      return;
    }

    Application.Instance.AsyncInvoke(() =>
    {
      try
      {
        if (_stopped || requestVersion != _sharpnessRequestVersion)
        {
          DisposeResults(results);
          return;
        }
        CommitSharpness(results, level, algorithm);
      }
      finally
      {
        FinishSharpnessRequest(cancellationSource);
      }
    });
  }

  private void FinishSharpnessRequest(CancellationTokenSource cancellationSource)
  {
    if (ReferenceEquals(_sharpnessCancellation, cancellationSource))
      _sharpnessCancellation = null;
    cancellationSource.Dispose();
    CompletePersistentBusyAction();
  }

  private void CommitSharpness(IReadOnlyList<SharpnessResult> results,
    int level, string algorithm)
  {
    var totalTimer = Stopwatch.StartNew();
    var stageTimer = Stopwatch.StartNew();
    double enumerateMs = 0.0;
    double addBitmapMs = 0.0;
    double setTextureMs = 0.0;
    double attributesMs = 0.0;
    double retireMs = 0.0;
    double endUndoMs = 0.0;
    double cleanupMs = 0.0;
    double redrawMs = 0.0;
    double refreshMs = 0.0;
    int bitmapAdds = 0;
    int attributeWrites = 0;
    if (_doc == null)
    {
      DisposeResults(results);
      return;
    }

    uint undoRecord = _doc.BeginUndoRecord("Properties+ Picture Sharpness");
    bool changed = false;
    var allPictures = new List<RhinoObject>();
    _isApplying = true;
    try
    {
      Guid[] targetIds = results
        .SelectMany(result => result.Job.TargetObjectIds)
        .Distinct()
        .ToArray();
      changed |= EnsureExclusivePictureMaterials(ResolveTargets(targetIds));
      stageTimer.Restart();
      allPictures = _doc.Objects.GetObjectList(ObjectType.AnyObject)
        .Where(obj => obj != null && IsPictureObject(obj) && obj.RenderMaterial != null)
        .Cast<RhinoObject>()
        .ToList();
      enumerateMs = stageTimer.Elapsed.TotalMilliseconds;

      foreach (SharpnessResult result in results)
      {
        stageTimer.Restart();
        int bitmapIndex = _doc.Bitmaps.AddBitmap(result.ProcessedFile, false);
        addBitmapMs += stageTimer.Elapsed.TotalMilliseconds;
        bitmapAdds++;
        if (bitmapIndex < 0)
        {
          Log.Write($"Add sharpened picture to the document failed: {result.ProcessedFile}");
          continue;
        }

        bool resultUsed = false;
        var replacedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Guid[] materialIds = ResolveTargets(result.Job.TargetObjectIds)
          .Where(obj => obj.RenderMaterial != null)
          .Select(obj => obj.RenderMaterial!.Id)
          .Distinct()
          .ToArray();
        foreach (Guid materialId in materialIds)
        {
          List<RhinoObject> materialTargets = ResolveTargets(result.Job.TargetObjectIds)
            .Where(obj => obj.RenderMaterial?.Id == materialId)
            .ToList();
          RhinoObject? firstTarget = materialTargets.FirstOrDefault();
          RenderTexture? oldTexture = firstTarget == null
            ? null
            : PictureContent(firstTarget, true) as RenderTexture;
          if (oldTexture == null)
            continue;

          foreach (RhinoObject obj in materialTargets)
          {
            if (obj.Attributes.UserDictionary.TryGetString(
              SharpnessProcessedKey, out string oldProcessed)
              && !string.IsNullOrWhiteSpace(oldProcessed))
              replacedPaths.Add(oldProcessed);
          }

          stageTimer.Restart();
          changed |= SetTextureFilename(oldTexture, result.ProcessedFile);
          setTextureMs += stageTimer.Elapsed.TotalMilliseconds;
          resultUsed = true;

          foreach (RhinoObject obj in materialTargets)
          {
            stageTimer.Restart();
            ObjectAttributes attributes = obj.Attributes.Duplicate();
            attributes.UserDictionary.Set(SharpnessSourceKey, result.Job.SourceFile);
            attributes.UserDictionary.Set(SharpnessLevelKey, level);
            attributes.UserDictionary.Set(SharpnessAlgorithmKey, algorithm);
            attributes.UserDictionary.Set(SharpnessProcessedKey, result.ProcessedFile);
            changed |= _doc.Objects.ModifyAttributes(obj, attributes, true);
            attributesMs += stageTimer.Elapsed.TotalMilliseconds;
            attributeWrites++;
          }
        }

        if (!resultUsed)
        {
          _doc.Bitmaps.DeleteBitmap(result.ProcessedFile);
          PictureImageProcessor.ReleaseTemporaryFile(result.ProcessedFile);
        }
        else
        {
          result.RetainTemporaryFile = true;
        }
        stageTimer.Restart();
        foreach (string replacedPath in replacedPaths)
          RetireProcessedPicture(replacedPath);
        retireMs += stageTimer.Elapsed.TotalMilliseconds;
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
        stageTimer.Restart();
        if (undoRecord != 0)
          _doc.EndUndoRecord(undoRecord);
        endUndoMs = stageTimer.Elapsed.TotalMilliseconds;
      }
      finally
      {
        _isApplying = false;
        stageTimer.Restart();
        DisposeResults(results);
        cleanupMs = stageTimer.Elapsed.TotalMilliseconds;
      }
    }

    if (changed)
    {
      stageTimer.Restart();
      _doc.Views.Redraw();
      redrawMs = stageTimer.Elapsed.TotalMilliseconds;
      stageTimer.Restart();
      RefreshTargets();
      refreshMs = stageTimer.Elapsed.TotalMilliseconds;
    }
    Log.Write("PictureTiming", $"Picture Sharpness commit: algorithm={algorithm}, "
      + $"level={level}, changed={changed}, results={results.Count}, pictures={allPictures.Count}, "
      + $"bitmap-adds={bitmapAdds}, attribute-writes={attributeWrites}, "
      + $"total={totalTimer.Elapsed.TotalMilliseconds:0.0}ms, enumerate={enumerateMs:0.0}ms, "
      + $"add-bitmap={addBitmapMs:0.0}ms, set-texture={setTextureMs:0.0}ms, "
      + $"attributes={attributesMs:0.0}ms, retire={retireMs:0.0}ms, "
      + $"end-undo={endUndoMs:0.0}ms, cleanup={cleanupMs:0.0}ms, "
      + $"redraw={redrawMs:0.0}ms, refresh={refreshMs:0.0}ms");
  }

  private void RestoreOriginalSharpness(IReadOnlyCollection<Guid> targetObjectIds,
    int requestVersion, string algorithm)
  {
    if (_doc == null || requestVersion != _sharpnessRequestVersion)
      return;

    List<RhinoObject> targetObjects = ResolveTargets(targetObjectIds);
    if (targetObjects.Count == 0)
      return;

    uint undoRecord = _doc.BeginUndoRecord("Properties+ Reset Picture Sharpness");
    bool changed = false;
    var retiredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    _isApplying = true;
    try
    {
      changed |= EnsureExclusivePictureMaterials(targetObjects);
      Guid[] resolvedTargetIds = targetObjects.Select(obj => obj.Id).ToArray();
      var targets = ResolveTargets(resolvedTargetIds)
        .Where(obj => obj.RenderMaterial != null)
        .GroupBy(obj => obj.RenderMaterial!.Id)
        .ToList();
      foreach (IGrouping<Guid, RhinoObject> group in targets)
      {
        RhinoObject? sourceObject = group.FirstOrDefault(obj =>
          obj.Attributes.UserDictionary.TryGetString(
            SharpnessSourceKey, out string source)
          && !string.IsNullOrWhiteSpace(source));
        if (sourceObject != null
          && sourceObject.Attributes.UserDictionary.TryGetString(
            SharpnessSourceKey, out string source)
          && !string.IsNullOrWhiteSpace(source)
          && PictureContent(sourceObject, true) is RenderTexture texture)
          changed |= SetTextureFilename(texture, source);

        foreach (RhinoObject obj in group)
        {
          if (obj.Attributes.UserDictionary.TryGetString(
            SharpnessProcessedKey, out string processed)
            && !string.IsNullOrWhiteSpace(processed))
            retiredPaths.Add(processed);
          ObjectAttributes attributes = obj.Attributes.Duplicate();
          ClearSharpnessMetadata(attributes);
          attributes.UserDictionary.Set(SharpnessLevelKey, 0);
          attributes.UserDictionary.Set(SharpnessAlgorithmKey, algorithm);
          changed |= _doc.Objects.ModifyAttributes(obj, attributes, true);
        }
      }
    }
    catch (Exception ex)
    {
      Log.Write($"Restore original picture sharpness failed: {ex}");
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

    foreach (string path in retiredPaths)
      RetireProcessedPicture(path);
    if (!changed)
      return;
    _doc.Views.Redraw();
    RefreshTargets();
  }

  private static bool SetTextureFilename(RenderTexture texture, string filename)
  {
    if (string.Equals(texture.Filename, filename, StringComparison.OrdinalIgnoreCase))
      return false;

    texture.BeginChange(RenderContent.ChangeContexts.RealTimeUI);
    try
    {
      texture.Filename = filename;
      return true;
    }
    finally
    {
      texture.EndChange();
    }
  }

  private void RetireProcessedPicture(string path)
  {
    if (_doc == null || string.IsNullOrWhiteSpace(path))
      return;
    bool stillUsed = _doc.Objects.GetObjectList(ObjectType.AnyObject)
      .Where(obj => obj != null && IsPictureObject(obj))
      .Any(obj => string.Equals(
        (PictureContent(obj, true) as RenderTexture)?.Filename,
        path, StringComparison.OrdinalIgnoreCase));
    if (stillUsed)
      return;

    _doc.Bitmaps.DeleteBitmap(path);
    PictureImageProcessor.ReleaseTemporaryFile(path);
  }

  private List<RhinoObject> ResolveTargets(IEnumerable<Guid> targetObjectIds)
  {
    if (_doc == null)
      return new List<RhinoObject>();
    return targetObjectIds
      .Select(id => _doc.Objects.FindId(id))
      .Where(obj => obj != null && IsPictureObject(obj) && obj.RenderMaterial != null)
      .Cast<RhinoObject>()
      .GroupBy(obj => obj.Id)
      .Select(group => group.First())
      .ToList();
  }

  private static RenderContent? PictureContent(RhinoObject obj, bool texture)
  {
    if (!IsPictureObject(obj))
      return null;
    RenderMaterial? material = obj.RenderMaterial;
    return texture ? material?.FindChild("bitmap-texture") : material;
  }

  internal static bool IsPictureObject(RhinoObject obj)
  {
    return obj.IsPictureFrame
      || obj.RenderMaterial?.TypeId == RenderMaterial.PictureMaterialGuid;
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
    if (PictureContent(obj, true) is RenderTexture texture
      && !string.IsNullOrWhiteSpace(texture.Filename))
      return texture.Filename.Trim();

    object? value = ReadPictureParameter(obj, "filename", true);
    if (value != null)
      return value.ToString()?.Trim() ?? string.Empty;
    return string.Empty;
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
    attributes.UserDictionary.Remove(SharpnessProcessedKey);
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

  private static SliderValueSummary MostCommonSliderValue(
    IReadOnlyList<RhinoObject> objects, Func<RhinoObject, double> selector,
    int minimum, int maximum)
  {
    var values = objects
      .Select((obj, index) => new
      {
        Value = Math.Clamp((int)Math.Round(selector(obj)), minimum, maximum),
        Index = index
      })
      .ToList();
    if (values.Count == 0)
      return new SliderValueSummary(0, false);

    int mode = values
      .GroupBy(item => item.Value)
      .OrderByDescending(group => group.Count())
      .ThenBy(group => group.Min(item => item.Index))
      .First()
      .Key;
    return new SliderValueSummary(mode,
      values.Any(item => item.Value != values[0].Value));
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

  private void SetPercentageSliderValue(RhinoSlider slider,
    SliderValueSummary? summary)
  {
    SetSliderValue(slider, summary, 0, 100);
  }

  private void SetAdjustmentSliderValue(RhinoSlider slider,
    SliderValueSummary? summary)
  {
    SetSliderValue(slider, summary, -100, 100);
  }

  private void SetSliderValue(RhinoSlider slider, SliderValueSummary? summary,
    int minimum, int maximum)
  {
    int percentage = summary.HasValue
      ? Math.Clamp(summary.Value.Value, minimum, maximum)
      : 0;
    slider.SetVaries(false);
    slider.Value1 = percentage;
    SetSliderMixedState(slider, summary?.Varies == true, percentage);
  }

  private void SetSliderMixedState(RhinoSlider slider, bool varies, int displayedValue)
  {
    if (!_sliderMarkerColors.TryGetValue(slider,
      out (Color First, Color Second) colors))
      return;

    Color markerColor = varies ? MixedSliderColor() : colors.First;
    slider.MarkerPointColor1 = markerColor;
    slider.MarkerPointColor2 = varies ? markerColor : colors.Second;
    slider.ToolTip = varies
      ? $"Values vary; showing the most common value: {displayedValue}%"
      : string.Empty;
  }

  private static Color MixedSliderColor()
  {
    System.Drawing.Color color =
      Rhino.ApplicationSettings.AppearanceSettings.SelectedObjectColor;
    return color.IsEmpty
      ? Color.FromArgb(235, 130, 20, 255)
      : Color.FromArgb(color.R, color.G, color.B, color.A);
  }

  private static void SetDropValue(DropDown dropDown, string value, params string[] options)
  {
    var items = options.Distinct().ToList();
    if (!items.Contains(value))
      items.Insert(0, value);
    dropDown.DataStore = items;
    dropDown.SelectedIndex = Math.Max(0, items.IndexOf(value));
  }

  private RhinoSlider NewPercentageSlider(Control parent)
  {
    var slider = NewRhinoSlider(parent);
    slider.SetMinMax(0.0, 100.0);
    slider.Value1 = 0.0;
    return slider;
  }

  private RhinoSlider NewAdjustmentSlider(Control parent)
  {
    var slider = NewRhinoSlider(parent);
    slider.SetMinMax(-100.0, 100.0);
    slider.Value1 = 0.0;
    return slider;
  }

  private RhinoSlider NewRhinoSlider(Control parent)
  {
    var slider = new RhinoSlider(parent, true)
    {
      Decimals = 0,
      DrawArrows = false,
      DrawEndLines = false,
      DrawNumberUnderPoint = true,
      DrawTextLabels = false,
      Height = RowHeight
    };
    _sliderMarkerColors[slider] =
      (slider.MarkerPointColor1, slider.MarkerPointColor2);
    return slider;
  }

  private TableRow NewScaleRow(string operationName, TextBox scaleBox,
    CheckBox scaleContents, Button calibrateButton)
  {
    scaleBox.Height = RowHeight;
    scaleContents.Height = RowHeight;
    calibrateButton.Height = RowHeight;
    var right = new StackLayout
    {
      Orientation = Orientation.Horizontal,
      Spacing = 3,
      Items =
      {
        new StackLayoutItem(scaleBox, true),
        new StackLayoutItem(scaleContents, false),
        new StackLayoutItem(calibrateButton, false)
      }
    };
    return NewOperationRow("Scale", operationName, right);
  }

  private TableRow NewSliderRow(string name, string operationName,
    RhinoSlider slider, Button resetButton)
  {
    StackLayout left = NewBusyLabel(name, operationName);
    var right = new StackLayout
    {
      Orientation = Orientation.Horizontal,
      Spacing = 3,
      Items =
      {
        new StackLayoutItem(slider, true),
        new StackLayoutItem(resetButton, false)
      }
    };
    return new TableRow(new TableCell(left, false), new TableCell(right, true));
  }

  private TableRow NewSharpnessRow(string name, string operationName,
    DropDown method, RhinoSlider slider, Button resetButton)
  {
    method.Width = 82;
    method.Height = RowHeight;
    StackLayout left = NewBusyLabel(name, operationName);
    var right = new StackLayout
    {
      Orientation = Orientation.Horizontal,
      Spacing = 3,
      Items =
      {
        new StackLayoutItem(method, false),
        new StackLayoutItem(slider, true),
        new StackLayoutItem(resetButton, false)
      }
    };
    return new TableRow(new TableCell(left, false), new TableCell(right, true));
  }

  private TableRow NewColorMaskRow(string name, string operationName,
    CheckBox enabled, Button picker, RhinoSlider slider, Button resetButton)
  {
    enabled.Width = 18;
    enabled.Height = RowHeight;
    picker.Width = 22;
    picker.Height = RowHeight;
    StackLayout left = NewBusyLabel(name, operationName);
    var right = new StackLayout
    {
      Orientation = Orientation.Horizontal,
      Spacing = 3,
      Items =
      {
        new StackLayoutItem(enabled, false),
        new StackLayoutItem(picker, false),
        new StackLayoutItem(slider, true),
        new StackLayoutItem(resetButton, false)
      }
    };
    return new TableRow(new TableCell(left, false), new TableCell(right, true));
  }

  private StackLayout NewBusyLabel(string name, string operationName)
  {
    var indicator = new ImageView
    {
      Width = 14,
      Height = 14
    };
    _busyIndicators[operationName] = indicator;
    var left = new StackLayout
    {
      Orientation = Orientation.Horizontal,
      Width = LabelWidth,
      Spacing = 2,
      Items =
      {
        new StackLayoutItem(new Label { Text = name }, true),
        new StackLayoutItem(indicator, false)
      }
    };
    return left;
  }

  private TableRow NewCheckRow(string name, string operationName, CheckBox checkBox)
  {
    checkBox.Height = RowHeight;
    return NewOperationRow(name, operationName, checkBox);
  }

  private TableRow NewControlRow(string name, string operationName, Control control)
  {
    control.Height = RowHeight;
    control.Width = ValueWidth;
    return NewOperationRow(name, operationName, control);
  }

  private TableRow NewOperationRow(string name, string operationName, Control control)
    => new(new TableCell(NewBusyLabel(name, operationName), false),
      new TableCell(control, true));

  private static Bitmap CreateEyedropperIcon()
  {
    using var systemBitmap = new System.Drawing.Bitmap(16, 16,
      System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    using (var graphics = System.Drawing.Graphics.FromImage(systemBitmap))
    using (var pen = new System.Drawing.Pen(
      System.Drawing.Color.FromArgb(45, 70, 88), 1.4f))
    using (var dropBrush = new System.Drawing.SolidBrush(
      System.Drawing.Color.FromArgb(45, 102, 142)))
    {
      graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
      pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
      pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
      graphics.DrawLine(pen, 3, 12, 10.5f, 4.5f);
      graphics.DrawLine(pen, 5, 14, 12.5f, 6.5f);
      graphics.DrawLine(pen, 3, 12, 5, 14);
      graphics.DrawLine(pen, 10.5f, 4.5f, 12.5f, 6.5f);
      graphics.DrawLine(pen, 9.5f, 3.5f, 12.5f, 0.8f);
      graphics.DrawLine(pen, 11.5f, 5.5f, 14.2f, 2.5f);
      graphics.DrawLine(pen, 12.5f, 0.8f, 14.2f, 2.5f);
      graphics.FillEllipse(dropBrush, 1.2f, 13.1f, 2.5f, 2.5f);
    }

    using var stream = new MemoryStream();
    systemBitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
    return new Bitmap(stream.ToArray());
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

  private static Bitmap CreateResetIcon(bool enabled)
  {
    using var systemBitmap = new System.Drawing.Bitmap(16, 16,
      System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    System.Drawing.Color color = enabled
      ? System.Drawing.Color.FromArgb(45, 102, 142)
      : System.Drawing.Color.FromArgb(170, 170, 170);
    using (var graphics = System.Drawing.Graphics.FromImage(systemBitmap))
    using (var brush = new System.Drawing.SolidBrush(color))
    using (var font = new System.Drawing.Font("Segoe UI Symbol", 14,
      System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel))
    {
      graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
      graphics.DrawString("\u21BA", font, brush, -1.0f, -1.0f);
    }

    using var stream = new MemoryStream();
    systemBitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
    return new Bitmap(stream.ToArray());
  }

  private static Bitmap CreateBusyIcon()
  {
    using var systemBitmap = new System.Drawing.Bitmap(16, 16,
      System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    using (var graphics = System.Drawing.Graphics.FromImage(systemBitmap))
    using (var pen = new System.Drawing.Pen(
      System.Drawing.Color.FromArgb(45, 102, 142), 1.4f))
    using (var brush = new System.Drawing.SolidBrush(
      System.Drawing.Color.FromArgb(45, 102, 142)))
    {
      graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
      pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
      pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
      graphics.DrawEllipse(pen, 2.5f, 2.5f, 11, 11);
      graphics.DrawLine(pen, 8, 4.5f, 8, 8);
      graphics.DrawLine(pen, 8, 8, 10.7f, 9.4f);
      graphics.FillEllipse(brush, 7.1f, 7.1f, 1.8f, 1.8f);
    }

    using var stream = new MemoryStream();
    systemBitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
    return new Bitmap(stream.ToArray());
  }

  private static void DisposeResults(IEnumerable<SharpnessResult> results)
  {
    foreach (SharpnessResult result in results)
    {
      if (!result.RetainTemporaryFile)
        PictureImageProcessor.ReleaseTemporaryFile(result.ProcessedFile);
    }
  }

  private sealed class PictureHighlightConduit : DisplayConduit
  {
    private RhinoObject? _picture;
    private System.Drawing.Color _color = System.Drawing.Color.Orange;

    internal Guid ObjectId => _picture?.Id ?? Guid.Empty;

    internal void SetObject(RhinoObject picture, System.Drawing.Color color)
    {
      _picture = picture;
      _color = color.IsEmpty
        ? System.Drawing.Color.Orange
        : System.Drawing.Color.FromArgb(255, color.R, color.G, color.B);
    }

    internal void Clear() => _picture = null;

    protected override void DrawOverlay(DrawEventArgs e)
    {
      base.DrawOverlay(e);
      if (_picture?.Geometry == null)
        return;

      switch (_picture.Geometry)
      {
        case Brep brep:
          DrawBrepOutline(e, brep);
          break;
        case Extrusion extrusion:
          Brep? extrusionBrep = extrusion.ToBrep(true);
          if (extrusionBrep != null)
            DrawBrepOutline(e, extrusionBrep);
          break;
        default:
          BoundingBox bounds = _picture.Geometry.GetBoundingBox(false);
          if (bounds.IsValid)
            e.Display.DrawBox(new Box(bounds), _color, 3);
          break;
      }
    }

    private void DrawBrepOutline(DrawEventArgs e, Brep brep)
    {
      foreach (BrepEdge edge in brep.Edges)
        e.Display.DrawCurve(edge, _color, 3);
    }
  }

  private sealed class SharpnessJob
  {
    internal SharpnessJob(string sourceFile, Guid[] targetObjectIds)
    {
      SourceFile = sourceFile;
      TargetObjectIds = targetObjectIds;
    }

    internal string SourceFile { get; }
    internal Guid[] TargetObjectIds { get; }
  }

  private sealed class PictureScaleScope
  {
    internal PictureScaleScope(Guid pictureId, Plane plane,
      double minU, double maxU, double minV, double maxV, Point3d center,
      double originalArea, bool hasOriginalArea)
    {
      PictureId = pictureId;
      Plane = plane;
      MinU = minU;
      MaxU = maxU;
      MinV = minV;
      MaxV = maxV;
      Center = center;
      OriginalArea = originalArea;
      HasOriginalArea = hasOriginalArea;
    }

    internal Guid PictureId { get; }
    internal Plane Plane { get; }
    internal double MinU { get; }
    internal double MaxU { get; }
    internal double MinV { get; }
    internal double MaxV { get; }
    internal Point3d Center { get; }
    internal double Area => (MaxU - MinU) * (MaxV - MinV);
    internal double OriginalArea { get; }
    internal bool HasOriginalArea { get; }
    internal double CurrentScale => Math.Sqrt(Area / OriginalArea);
    internal double TransformFactor { get; set; } = 1.0;

    internal Transform CreateTransform()
    {
      Plane scalePlane = Plane;
      scalePlane.Origin = Center;
      return Transform.Scale(scalePlane, TransformFactor, TransformFactor, 1.0);
    }
  }

  private readonly record struct PictureDimensionCacheEntry(
    long Length, DateTime LastWriteUtc, double Area);

  private readonly record struct SliderValueSummary(int Value, bool Varies);

  private sealed class SharpnessResult
  {
    internal SharpnessResult(SharpnessJob job, string processedFile)
    {
      Job = job;
      ProcessedFile = processedFile;
    }

    internal SharpnessJob Job { get; }
    internal string ProcessedFile { get; }
    internal bool RetainTemporaryFile { get; set; }
  }
}
