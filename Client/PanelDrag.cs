using UnityEngine;

namespace SiSpectator;

// Draggable panel: the title row (left of RESET / CLOSE) is the drag handle. Position persists in PanelX / PanelY
// (-1 = default placement: left edge, vertically centred), saved on drag end, clamped on screen every frame (so a
// resolution change re-clamps). RESET returns to the default placement. Uses Event.current.mousePosition like the rest
// of the IMGUI code; the cursor is free while the panel is open (OpenPanel / PanelLateUpdate).
public sealed partial class ClientMod
{
    private bool panelDragging; private Vector2 panelDragOffset; private int panelDragControl;
    private float panelPosX = -1, panelPosY = -1; private bool panelPosLoaded;

    private Rect PanelRect(float width, float height)
    {
        if (!panelPosLoaded) { panelPosLoaded = true; panelPosX = panelX?.Value ?? -1; panelPosY = panelY?.Value ?? -1; }
        float x = 12, y = Math.Max(12, (Screen.height - height) / 2);
        if (panelPosX >= 0 && panelPosY >= 0) { x = panelPosX; y = panelPosY; }
        x = Math.Clamp(x, 0, Math.Max(0, Screen.width - width));
        y = Math.Clamp(y, 0, Math.Max(0, Screen.height - height));
        return new Rect(x, y, width, height);
    }
    // Handles the drag events for the title handle; returns true when the event was consumed.
    private bool PanelDragEvents(Rect panel, Rect handle)
    {
        var e = Event.current;
        if (e == null) return false;
        int id = GUIUtility.GetControlID(FocusType.Passive);   // every event, so the other controls keep stable ids
        if (e.type == EventType.MouseDown && e.button == 0 && GUI.enabled && handle.Contains(e.mousePosition))
        {
            panelDragging = true; panelDragOffset = e.mousePosition - panel.position;
            panelDragControl = id; GUIUtility.hotControl = id;
            e.Use(); return true;
        }
        if (!panelDragging) return false;
        if (e.type == EventType.MouseDrag)
        {
            Vector2 p = e.mousePosition - panelDragOffset;
            panelPosX = Math.Clamp(p.x, 0, Math.Max(0, Screen.width - panel.width));
            panelPosY = Math.Clamp(p.y, 0, Math.Max(0, Screen.height - panel.height));
            e.Use(); return true;
        }
        if (e.rawType == EventType.MouseUp)
        {
            EndPanelDrag();
            e.Use(); return true;
        }
        return false;
    }
    private void EndPanelDrag()
    {
        if (!panelDragging) return;
        panelDragging = false;
        if (GUIUtility.hotControl == panelDragControl) GUIUtility.hotControl = 0;
        if (panelPosX < 0 || panelPosY < 0) return;
        panelX.Value = (int)Math.Round(panelPosX); panelY.Value = (int)Math.Round(panelPosY);
        MelonLoader.MelonPreferences.Save();
        Write("Panel moved to " + panelX.Value + "," + panelY.Value + ".");
    }
    private void ResetPanelPosition()
    {
        panelPosX = panelPosY = -1; panelX.Value = -1; panelY.Value = -1;
        MelonLoader.MelonPreferences.Save();
        Write("Panel position reset to the default placement.");
    }
}
