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
  private bool _stopped;

  public vPicturesPanel()
  {
    _editor = new PictureEditorControl(
      () => RhinoDoc.ActiveDoc,
      () => AllPictures(RhinoDoc.ActiveDoc));

    Content = new Scrollable
    {
      Border = BorderType.None,
      ExpandContentHeight = false,
      ExpandContentWidth = true,
      Content = _editor
    };
    MinimumSize = new Size(244, 0);

    Load += (_, _) => QueueRefresh(RhinoDoc.ActiveDoc);
    UnLoad += (_, _) => Stop();
    RhinoDoc.AddRhinoObject += OnObjectChanged;
    RhinoDoc.DeleteRhinoObject += OnObjectChanged;
    RhinoDoc.UndeleteRhinoObject += OnObjectChanged;
    RhinoDoc.ReplaceRhinoObject += OnObjectReplaced;
    RhinoDoc.ModifyObjectAttributes += OnObjectAttributesModified;
    RhinoDoc.EndOpenDocument += OnDocumentOpened;
    RhinoDoc.ActiveDocumentChanged += OnDocumentChanged;
    RhinoDoc.CloseDocument += OnDocumentChanged;
  }

  private static IReadOnlyList<RhinoObject> AllPictures(RhinoDoc? doc)
  {
    return doc == null
      ? Array.Empty<RhinoObject>()
      : doc.Objects.GetObjectList(ObjectType.AnyObject)
        .Where(obj => obj?.IsPictureFrame == true && obj.RenderMaterial != null)
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
    => QueueRefresh(e.Document);

  private void OnDocumentChanged(object? sender, DocumentEventArgs e)
    => QueueRefresh(RhinoDoc.ActiveDoc);

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
      _editor.Update(activeDoc, AllPictures(activeDoc));
    });
  }

  private void Stop()
  {
    if (_stopped)
      return;
    _stopped = true;
    _editor.Stop();
    RhinoDoc.AddRhinoObject -= OnObjectChanged;
    RhinoDoc.DeleteRhinoObject -= OnObjectChanged;
    RhinoDoc.UndeleteRhinoObject -= OnObjectChanged;
    RhinoDoc.ReplaceRhinoObject -= OnObjectReplaced;
    RhinoDoc.ModifyObjectAttributes -= OnObjectAttributesModified;
    RhinoDoc.EndOpenDocument -= OnDocumentOpened;
    RhinoDoc.ActiveDocumentChanged -= OnDocumentChanged;
    RhinoDoc.CloseDocument -= OnDocumentChanged;
  }
}
