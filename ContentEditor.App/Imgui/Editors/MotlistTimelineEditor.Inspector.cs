using System.Numerics;
using ContentEditor.App.Windowing;
using ContentEditor.Core;
using ReeLib;
using ReeLib.Clip;

namespace ContentEditor.App.ImguiHandling;

internal sealed partial class MotlistTimelineEditor
{
    private const float PropertyLaneHeight = 68f;
    private static float GetPropertyLaneHeight(Property property) => IsDisabled(property) ? 32f : PropertyLaneHeight;
    private readonly Dictionary<(object Target, string Member), UIContext> valueContexts = new();
    private float inspectorSplit = 0.65f;
    private Property? revealedProperty;
    private string propertyFilter = string.Empty;
    private bool showInactiveProperties = true;
    private bool dopeFit = true;
    private float dopeZoom = 8f;
    private float dopeFittedZoom = 8f;

    private void DrawInspector()
    {
        if (selectedTrack == null || !TryGetSelectedMotion(out var motion, out var motFile)) {
            ImGui.TextDisabled("Select a timeline track.");
            return;
        }
        var clips = GetClips(motion, motFile);
        if (selectedClipIndex >= clips.Count || !FlattenTracks(clips[selectedClipIndex].ClipEntry?.Tracks).Any(lane => ReferenceEquals(lane.Track, selectedTrack))) {
            selectedTrack = null;
            selectedProperty = null;
            selectedKey = null;
            drag = null;
            valueContexts.Clear();
            return;
        }
        UpdateDrag();
        var properties = EnumerateTimelineProperties(selectedTrack.Properties).ToList();
        if (selectedProperty == null || !properties.Contains(selectedProperty)) {
            selectedProperty = properties.FirstOrDefault();
            selectedKey = null;
        }
        if (selectedKey != null && selectedProperty?.Keys?.Contains(selectedKey) != true) selectedKey = null;

        ImGui.TextColored(GetTrackColor(selectedTrack), FitText($"DOPE SHEET  /  {CompactName(GetTrackName(selectedTrack))}", MathF.Max(1f, ImGui.GetContentRegionAvail().X - 125f * UI.UIScale)));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(GetTrackName(selectedTrack));
        ImGui.SameLine();
        ImGui.TextDisabled($"{properties.Count} properties");
        if (selectedProperty == null) {
            ImGui.TextDisabled("No properties on this track.");
            return;
        }
        ImGui.SetNextItemWidth(180f * UI.UIScale);
        ImGui.InputTextWithHint("##property_filter", "Filter properties", ref propertyFilter, 256);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Filter properties by their exact function name");
        NextControl(130f);
        ImGui.Checkbox("Show inactive", ref showInactiveProperties);
        NextControl(60f);
        ImGui.Checkbox("Fit##dope", ref dopeFit);
        NextControl(120f);
        ImGui.SetNextItemWidth(120f * UI.UIScale);
        var zoom = dopeFit ? dopeFittedZoom : dopeZoom;
        if (ImGui.SliderFloat("##dope_zoom", ref zoom, MinimumPixelsPerFrame, MaximumPixelsPerFrame, "%.1f px/f")) {
            dopeZoom = zoom;
            dopeFit = false;
        }
        var visibleProperties = properties.Where(property => (showInactiveProperties || !IsDisabled(property))
            && GetPropertyName(property).Contains(propertyFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        var (minimumFrame, maximumFrame) = GetIsolatedTimelineRange(selectedTrack, motFile);
        var available = ImGui.GetContentRegionAvail();
        var height = MathF.Max(1f, available.Y);
        if (available.X >= 800f * UI.UIScale) {
            if (ImGui.BeginTable("motlist_inspector_split", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp, new Vector2(0f, height))) {
                ImGui.TableSetupColumn("Dope Sheet", ImGuiTableColumnFlags.WidthStretch, 0.65f);
                ImGui.TableSetupColumn("Key values", ImGuiTableColumnFlags.WidthStretch, 0.35f);
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                DrawDopeSheetTable(visibleProperties, minimumFrame, maximumFrame, motFile, height);
                ImGui.TableSetColumnIndex(1);
                DrawInspectorPanel(height);
                ImGui.EndTable();
            }
        } else {
            var divider = 6f * UI.UIScale;
            var usableHeight = MathF.Max(1f, height - divider - ImGui.GetStyle().ItemSpacing.Y * 2f);
            DrawDopeSheetTable(visibleProperties, minimumFrame, maximumFrame, motFile, usableHeight * inspectorSplit);
            AppImguiHelpers.InvisibleButtonSafe("##inspector_divider", new Vector2(MathF.Max(1f, available.X), divider), ImGuiButtonFlags.MouseButtonLeft);
            if (ImGui.IsItemHovered() || ImGui.IsItemActive()) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNs);
            if (ImGui.IsItemActive()) inspectorSplit = Math.Clamp(inspectorSplit + ImGui.GetIO().MouseDelta.Y / usableHeight, 0.2f, 0.85f);
            var min = ImGui.GetItemRectMin();
            ImGui.GetWindowDrawList().AddRectFilled(min, ImGui.GetItemRectMax(), ImGui.GetColorU32(ImGui.IsItemActive() ? SelectedColor : GridColor));
            DrawInspectorPanel(MathF.Max(1f, ImGui.GetContentRegionAvail().Y));
        }
    }

    private void DrawInspectorPanel(float height)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12f, 10f) * UI.UIScale);
        if (ImGui.BeginChild("##property_inspector", new Vector2(0f, height), ImGuiChildFlags.AlwaysUseWindowPadding))
            DrawPropertyTable(ImGui.GetContentRegionAvail().Y);
        ImGui.EndChild();
        ImGui.PopStyleVar();
    }

    private void DrawDopeSheetSelectedBar()
    {
        var property = selectedProperty!;
        ImGui.TextWrapped(GetPropertyName(property));
        if (!ImGui.BeginTable("selected_range", 3, ImGuiTableFlags.SizingStretchSame)) return;
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        DrawRangeInput(property, true);
        ImGui.TableSetColumnIndex(1);
        DrawRangeInput(property, false);
        ImGui.TableSetColumnIndex(2);
        ImGui.TextDisabled(property.Info.DataType.ToString());
        var enabled = !IsDisabled(property);
        if (ImGui.Checkbox("Enabled", ref enabled)) {
            var bounds = GetTrackBounds(selectedTrack!, 0, 1);
            var keys = (property.Keys ?? []).Where(key => float.IsFinite(key.frame)).ToList();
            var start = enabled ? MathF.Min(bounds.Minimum, keys.Select(key => key.frame).DefaultIfEmpty(bounds.Minimum).Min()) : DisabledFrame;
            var end = enabled ? MathF.Max(bounds.Maximum, keys.Select(key => key.frame).DefaultIfEmpty(bounds.Maximum).Max()) : DisabledFrame;
            RecordRangeEdit(property, start, end);
        }
        ImGui.EndTable();
    }

    private void DrawRangeInput(Property property, bool startEdge, bool showLabel = true)
    {
        if (showLabel) ImGui.TextDisabled(startEdge ? "Start" : "End");
        ImGui.SetNextItemWidth(-1f);
        ImGui.BeginDisabled(!IsValidRange(property));
        var value = startEdge ? property.Info.startFrame : property.Info.endFrame;
        if (ImGui.InputFloat(startEdge ? "##start" : "##end", ref value, 0f, 0f, "%.3f")
            && float.IsFinite(value)) {
            var bounds = GetTrackBounds(selectedTrack!, (int)MathF.Floor(property.Info.startFrame), (int)MathF.Ceiling(property.Info.endFrame));
            var keys = (property.Keys ?? []).Where(key => float.IsFinite(key.frame)).Select(key => key.frame);
            var minimum = startEdge ? bounds.Minimum : MathF.Max(property.Info.startFrame, keys.DefaultIfEmpty(property.Info.startFrame).Max());
            var maximum = startEdge ? MathF.Min(property.Info.endFrame, keys.DefaultIfEmpty(property.Info.endFrame).Min()) : bounds.Maximum;
            if (minimum <= maximum) {
                value = Math.Clamp(value, minimum, maximum);
                RecordRangeEdit(property, startEdge ? value : property.Info.startFrame, startEdge ? property.Info.endFrame : value);
            }
        }
        ImGui.EndDisabled();
    }

    private void RecordRangeEdit(Property property, float start, float end)
    {
        var before = CaptureProperties(new[] { property });
        var after = new List<PropertySnapshot> { before[0] with { Start = start, End = end } };
        if (before[0].Start != start || before[0].End != end)
            UndoRedo.RecordCallback(null, () => ApplyPropertyFrames(after), () => ApplyPropertyFrames(before));
    }

    private void DrawDopeSheetTable(List<Property> properties, int minimumFrame, int maximumFrame, MotFile motFile, float height)
    {
        if (!BeginTimelineTable("motlist_dope_sheet", "PROPERTIES", properties.Count, minimumFrame, maximumFrame,
            motFile, height, out var width, out var pixels)) return;
        for (var index = 0; index < properties.Count; index++) {
            var property = properties[index];
            ImGui.PushID(index);
            ImGui.TableNextRow(ImGuiTableRowFlags.None, GetPropertyLaneHeight(property) * UI.UIScale);
            ImGui.TableSetColumnIndex(0);
            DrawDopeSheetPropertyName(property);
            if (ReferenceEquals(property, selectedProperty) && !ReferenceEquals(property, revealedProperty))
                ImGui.SetScrollHereY(0.5f);
            ImGui.TableSetColumnIndex(1);
            DrawLane(selectedTrack!, index, minimumFrame, maximumFrame, width, pixels, motFile, property);
            ImGui.PopID();
        }
        revealedProperty = selectedProperty;
        ImGui.EndTable();
        ImGui.PopStyleVar();
    }

    private void DrawDopeSheetPropertyName(Property property)
    {
        var position = ImGui.GetCursorScreenPos();
        var size = new Vector2(MathF.Max(1f, ImGui.GetContentRegionAvail().X), GetPropertyLaneHeight(property) * UI.UIScale);
        var selected = ReferenceEquals(property, selectedProperty);
        if (ImGui.Selectable("##property_name", selected, ImGuiSelectableFlags.None, size)) {
            selectedProperty = property;
            selectedKey = null;
        }
        var draw = ImGui.GetWindowDrawList();
        var inactive = IsDisabled(property);
        var name = CompactName(GetPropertyName(property)) + (inactive ? " (inactive)" : string.Empty);
        draw.AddText(position + new Vector2(10f, 7f) * UI.UIScale, ImGui.GetColorU32(inactive ? DisabledColor : GetPropertyColor(property)), FitText(name, size.X - 20f * UI.UIScale));
        if (!inactive) {
            var status = $"{property.Keys?.Count ?? 0} keys  /  {property.Info.startFrame:0.###} - {property.Info.endFrame:0.###}";
            draw.AddText(position + new Vector2(10f, 32f) * UI.UIScale, ImGui.GetColorU32(MutedTextColor), FitText(status, size.X - 20f * UI.UIScale));
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(GetPropertyName(property));
    }

    private void DrawPropertyTable(float height)
    {
        if (selectedTrack == null) return;
        if (selectedProperty == null) { ImGui.TextDisabled("Select a property."); return; }
        ImGui.SeparatorText("Property inspector");
        DrawDopeSheetSelectedBar();
        if (ImGui.CollapsingHeader("Property metadata")) {
            ImGui.TextDisabled("Function name");
            DrawNativeMember(selectedProperty.Info, "FunctionName");
            ImGui.TextDisabled("Array index");
            DrawNativeMember(selectedProperty.Info, "arrayIndex");
            ImGui.TextDisabled("Flags");
            DrawNativeMember(selectedProperty.Info, "flags");
            ImGui.TextDisabled($"Key type: {selectedProperty.Info.KeyType}  |  Extra keys: {selectedProperty.Info.ExtraKeyCount}");
        }
        var remainingHeight = MathF.Max(1f, ImGui.GetContentRegionAvail().Y);
        var flags = ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("motlist_track_values", 4, flags, new Vector2(0f, MathF.Min(height, remainingHeight)))) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Key", ImGuiTableColumnFlags.WidthFixed, 38f * UI.UIScale);
        ImGui.TableSetupColumn("Frame", ImGuiTableColumnFlags.WidthFixed, 70f * UI.UIScale);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Flags", ImGuiTableColumnFlags.WidthFixed, 48f * UI.UIScale);
        ImGui.TableHeadersRow();
        var property = selectedProperty;
        var keys = property.Keys ?? [];
        for (var i = 0; i < keys.Count; i++) {
            var key = keys[i];
            ImGui.PushID(i);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            if (ImGui.Selectable(i.ToString(), ReferenceEquals(key, selectedKey))) selectedKey = key;
            ImGui.TableSetColumnIndex(1);
            ImGui.SetNextItemWidth(-1f);
            ImGui.BeginDisabled(!IsValidRange(property));
            var frame = key.frame;
            if (ImGui.InputFloat("##frame", ref frame, 0f, 0f, "%.3f") && float.IsFinite(frame)) {
                frame = Math.Clamp(frame, property.Info.startFrame, property.Info.endFrame);
                var before = key.frame;
                var after = frame;
                if (before != after) UndoRedo.RecordCallback(null, () => ApplyKeyFrames([(key, after)]), () => ApplyKeyFrames([(key, before)]));
            }
            ImGui.EndDisabled();
            ImGui.TableSetColumnIndex(2);
            DrawNativeMember(key, key is NormalKey or NoHermiteKey ? "Value" : "value");
            ImGui.TableSetColumnIndex(3);
            if (key is ActionKey) ImGui.TextDisabled("-");
            else DrawNativeMember(key, "flags");
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    // Native typed handlers own value editing and Undo; the setter also notifies the file on Undo/Redo.
    private void DrawNativeMember(object key, string memberName)
    {
        var cacheKey = ((object)key, memberName);
        if (!valueContexts.TryGetValue(cacheKey, out var context)) {
            var member = key.GetType().GetMember(memberName).FirstOrDefault();
            // The native flags-enum handler expands vertically; use its byte value in table cells.
            Func<object?> get = memberName == "flags" && key is KeyBase flagKey ? () => (byte)flagKey.flags : member switch {
                System.Reflection.FieldInfo field => () => field.GetValue(key),
                System.Reflection.PropertyInfo property => () => property.GetValue(key),
                _ => () => null,
            };
            Action<object?> set = memberName == "flags" && key is KeyBase flagTarget ? value => flagTarget.flags = (KeyBase.KeyFlags)(byte)value! : member switch {
                System.Reflection.FieldInfo field => value => field.SetValue(key, value),
                System.Reflection.PropertyInfo property => value => property.SetValue(key, value),
                _ => _ => { },
            };
            if (member == null || get() == null) { ImGui.TextDisabled("-"); return; }
            context = new UIContext("##" + memberName, key, null!, _ => get(), (_, value) => { set(value); MarkModified(); }, UIOptions.None);
            context.root = context;
            valueContexts.Add(cacheKey, context);
        }
        var current = context.GetRaw();
        context.uiHandler = WindowHandlerFactory.CreateUIHandler(current, current?.GetType());
        ImGui.SetNextItemWidth(-1f);
        context.ShowUI();
    }

    private void OpenInspectorWindow()
    {
        showDopeSheet = true;
        if (!detachedDopeSheet) return;
        if (inspectorWindowData != null && inspectorWindow != null) {
            ImGui.SetWindowFocus(inspectorWindow.GetWindowName(inspectorWindowData.ID));
            return;
        }
        var owner = EditorWindow.CurrentWindow;
        if (owner == null) return;
        inspectorWindow = new MotlistKeyInspectorWindow(this, editor.Handle.Filename.ToString());
        inspectorWindowData = owner.AddSubwindow(inspectorWindow);
        inspectorWindowData.Size = new Vector2(1180f, 720f) * UI.UIScale;
    }

    internal void DrawInspectorWindow()
    {
        PushTimelineStyle();
        try { DrawInspector(); }
        finally { PopTimelineStyle(); }
    }

    internal void InspectorWindowClosed(MotlistKeyInspectorWindow window)
    {
        if (!ReferenceEquals(window, inspectorWindow)) return;
        inspectorWindow = null;
        inspectorWindowData = null;
        detachedDopeSheet = false;
    }

    public void CloseInspectorWindow()
    {
        if (inspectorWindow != null) EditorWindow.CurrentWindow?.CloseSubwindow(inspectorWindow);
        inspectorWindow = null;
        inspectorWindowData = null;
    }
}

internal sealed class MotlistKeyInspectorWindow(MotlistTimelineEditor timeline, string sourceName) : IWindowHandler
{
    private UIContext context = null!;

    public string HandlerName => "Motlist Dope Sheet";
    public bool HasUnsavedChanges => false;

    public void Init(UIContext context)
    {
        this.context = context;
    }

    internal string GetWindowName(int id) => $"Motlist Dope Sheet: {sourceName}###MotlistKeyInspector{id}";

    public void OnWindow()
    {
        var data = context.Get<WindowData>();
        if (!ImguiHelpers.BeginWindow(data, GetWindowName(data.ID), ImGuiWindowFlags.NoCollapse)) {
            EditorWindow.CurrentWindow?.CloseSubwindow(data);
            return;
        }
        timeline.DrawInspectorWindow();
        ImGui.End();
    }

    public void OnIMGUI() => timeline.DrawInspectorWindow();
    public bool RequestClose() => false;
    public void OnClosed() => timeline.InspectorWindowClosed(this);
}
