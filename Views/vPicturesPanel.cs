using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.DocObjects;

namespace vObjectPropertiesPlus.Views;

[SupportedOSPlatform("windows")]
[System.Runtime.InteropServices.Guid("E8B56964-7C65-4CF5-9684-7E95A3A87CA6")]
public sealed class vPicturesPanel : Panel
{
  private readonly PictureEditorControl _editor;
  private bool _refreshPending;
  private bool _stopped = true;
  private int _lastPictureCount = -1;
  private int _startupRefreshAttempts;
  private uint _startupDocumentSerial;
  private DateTime _nextStartupRefreshUtc;

  public vPicturesPanel()
  {
    _editor = new PictureEditorControl(
      () => RhinoDoc.ActiveDoc,
      () => AllPictures(RhinoDoc.ActiveDoc));
    Log.Write("Background images panel constructed");

    Content = new Scrollable
    {
      Border = BorderType.None,
      ExpandContentHeight = false,
      ExpandContentWidth = true,
      Content = _editor
    };
    MinimumSize = new Size(244, 0);

    Load += (_, _) => Start();
    Shown += (_, _) => BeginStartupRefresh(RhinoDoc.ActiveDoc);
    UnLoad += (_, _) => Stop();
    Start();
  }

  private void Start()
  {
    if (!_stopped)
    {
      BeginStartupRefresh(RhinoDoc.ActiveDoc);
      return;
    }

    _stopped = false;
    _editor.Start();
    RhinoDoc.AddRhinoObject += OnObjectChanged;
    RhinoDoc.DeleteRhinoObject += OnObjectChanged;
    RhinoDoc.UndeleteRhinoObject += OnObjectChanged;
    RhinoDoc.ReplaceRhinoObject += OnObjectReplaced;
    RhinoDoc.ModifyObjectAttributes += OnObjectAttributesModified;
    RhinoDoc.EndOpenDocument += OnDocumentOpened;
    RhinoDoc.EndOpenDocumentInitialViewUpdate += OnDocumentOpened;
    RhinoDoc.ActiveDocumentChanged += OnDocumentChanged;
    RhinoDoc.CloseDocument += OnDocumentChanged;
    RhinoDoc.RenderMaterialsTableEvent += OnRenderContentTableChanged;
    RhinoDoc.RenderTextureTableEvent += OnRenderContentTableChanged;
    RhinoApp.RdkNewDocument += OnRdkDocumentReady;
    RhinoApp.Idle += OnRhinoIdle;
    Log.Write("Background images panel started");
    BeginStartupRefresh(RhinoDoc.ActiveDoc);
  }

  private static IReadOnlyList<RhinoObject> AllPictures(RhinoDoc? doc)
  {
    return doc == null
      ? Array.Empty<RhinoObject>()
      : doc.Objects.GetObjectList(ObjectType.AnyObject)
        .Where(obj => obj != null
          && PictureEditorControl.IsPictureObject(obj)
          && obj.RenderMaterial != null)
        .Cast<RhinoObject>()
        .ToList();
  }

  private void OnObjectChanged(object? sender, RhinoObjectEventArgs e)
    => QueueRefresh(e.TheObject?.Document ?? RhinoDoc.ActiveDoc);

  private void OnObjectReplaced(object? sender, RhinoReplaceObjectEventArgs e)
    => QueueRefresh(e.NewRhinoObject?.Document ?? RhinoDoc.ActiveDoc);

  private void OnObjectAttributesModified(object? sender, RhinoModifyObjectAttributesEventArgs e)
  {
    if (!_editor.IsApplying)
      QueueRefresh(e.Document);
  }

  private void OnDocumentOpened(object? sender, DocumentOpenEventArgs e)
    => BeginStartupRefresh(e.Document);

  private void OnDocumentChanged(object? sender, DocumentEventArgs e)
    => BeginStartupRefresh(RhinoDoc.ActiveDoc);

  private void OnRdkDocumentReady(object? sender, EventArgs e)
    => BeginStartupRefresh(RhinoDoc.ActiveDoc);

  private void OnRenderContentTableChanged(object? sender,
    RhinoDoc.RenderContentTableEventArgs e)
    => BeginStartupRefresh(e.Document);

  private void BeginStartupRefresh(RhinoDoc? doc)
  {
    if (_stopped)
      return;
    _startupRefreshAttempts = 30;
    _startupDocumentSerial = doc?.RuntimeSerialNumber ?? 0;
    _nextStartupRefreshUtc = DateTime.MinValue;
    QueueRefresh(doc);
  }

  private void OnRhinoIdle(object? sender, EventArgs e)
  {
    if (_stopped || _startupRefreshAttempts <= 0
      || DateTime.UtcNow < _nextStartupRefreshUtc)
      return;

    _startupRefreshAttempts--;
    _nextStartupRefreshUtc = DateTime.UtcNow.AddSeconds(0.5);
    RhinoDoc? doc = _startupDocumentSerial == 0
      ? RhinoDoc.ActiveDoc
      : RhinoDoc.FromRuntimeSerialNumber(_startupDocumentSerial);
    QueueRefresh(doc ?? RhinoDoc.ActiveDoc);
  }

  internal void RefreshForDocument(RhinoDoc? doc) => BeginStartupRefresh(doc);

  private void QueueRefresh(RhinoDoc? doc)
  {
    if (_stopped || _refreshPending)
      return;
    _refreshPending = true;
    Application.Instance.AsyncInvoke(() =>
    {
      _refreshPending = false;
      if (_stopped)
        return;
      RhinoDoc? activeDoc = RhinoDoc.ActiveDoc ?? doc;
      IReadOnlyList<RhinoObject> pictures = AllPictures(activeDoc);
      if (pictures.Count > 0)
        _startupRefreshAttempts = 0;
      if (_lastPictureCount != pictures.Count)
      {
        _lastPictureCount = pictures.Count;
        Log.Write($"Background images panel refresh: pictures={pictures.Count}");
      }
      _editor.Update(activeDoc, pictures);
    });
  }

  private void Stop()
  {
    if (_stopped)
      return;
    _stopped = true;
    _refreshPending = false;
    _editor.Stop();
    RhinoDoc.AddRhinoObject -= OnObjectChanged;
    RhinoDoc.DeleteRhinoObject -= OnObjectChanged;
    RhinoDoc.UndeleteRhinoObject -= OnObjectChanged;
    RhinoDoc.ReplaceRhinoObject -= OnObjectReplaced;
    RhinoDoc.ModifyObjectAttributes -= OnObjectAttributesModified;
    RhinoDoc.EndOpenDocument -= OnDocumentOpened;
    RhinoDoc.EndOpenDocumentInitialViewUpdate -= OnDocumentOpened;
    RhinoDoc.ActiveDocumentChanged -= OnDocumentChanged;
    RhinoDoc.CloseDocument -= OnDocumentChanged;
    RhinoDoc.RenderMaterialsTableEvent -= OnRenderContentTableChanged;
    RhinoDoc.RenderTextureTableEvent -= OnRenderContentTableChanged;
    RhinoApp.RdkNewDocument -= OnRdkDocumentReady;
    RhinoApp.Idle -= OnRhinoIdle;
    Log.Write("Background images panel stopped");
  }
}
