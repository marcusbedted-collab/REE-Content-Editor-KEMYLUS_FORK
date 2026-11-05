using System.Numerics;
using System.Text.Json;
using Hexa.NET.ImGui;
using Hexa.NET.ImPlot;

namespace ContentEditor.App.ImguiHandling;
internal enum DesignerWidgetKind { BoundControl, Button, Checkbox, Text, TextInput, Slider, ImPlot, Tabs, Table, ColorEdit, ProgressBar, List }
internal enum DesignerTarget { Toolbar, LeftPanel, RightPanel, TimelineOverlay, Window }

internal sealed class DesignerWidget
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Binding { get; set; } = "";
    public string Label { get; set; } = "New widget";
    public DesignerWidgetKind Kind { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; } = 120;
    public float Height { get; set; } = 32;
    public bool Visible { get; set; } = true;
    public string Text { get; set; } = "";
    public bool Checked { get; set; }
    public float Value { get; set; }
    public DesignerTarget Target { get; set; }
    public bool CustomColor { get; set; }
    public uint Color { get; set; } = 0xFFCC8844;
    public GuiActionGraph Graph { get; set; } = new();
    public bool GraphOwnsActivation { get; set; }
    public List<DesignerWidget>? Children { get; set; }
    public bool? SavedBooleanValue { get; set; }
}

internal sealed class TimelineDesignDocument
{
    public int Version { get; set; } = 1;
    public bool GridSnap { get; set; } = true;
    public float GridSize { get; set; } = 8;
    public float ToolbarHeight { get; set; } = 112;
    public float RowHeight { get; set; } = 48;
    public float HierarchyWidth { get; set; } = 320;
    public float InspectorWidth { get; set; } = 300;
    public List<DesignerWidget> Widgets { get; set; } = new();
    public bool DirectTimelineDrag { get; set; } = true;
    public float DragThreshold { get; set; } = 5;
    public float FrameSnapStep { get; set; } = 1;
    public float BarRounding { get; set; } = 4;
    public float KeySize { get; set; } = 5;
    public float LeftPanelWidth { get; set; } = 220;
    public float RightPanelWidth { get; set; } = 260;

    public static TimelineDesignDocument Default()
    {
        TimelineDesignDocument? result = new TimelineDesignDocument();
        void Add(string binding, string label, float x, float y, float width) => result.Widgets.Add(new()
        { Binding = binding, Label = label, X = x, Y = y, Width = width });
        Add("duplicate", "Duplicate", 0, 0, 96);
        Add("delete", "Delete selected", 104, 0, 136);
        Add("rename", "Rename", 248, 0, 88);
        Add("undo", "Undo", 344, 0, 64);
        Add("redo", "Redo", 416, 0, 64);
        Add("frame", "Frame", 488, 0, 184);
        Add("filter", "Find track or property...", 0, 48, 224);
        Add("fit", "Fit", 232, 48, 48);
        Add("curves", "Curves", 288, 48, 88);
        Add("snap", "Snap", 384, 48, 80);
        Add("inactive", "Inactive", 472, 48, 96);
        Add("inspector", "Inspector", 576, 48, 104);
        Add("options", "Options", 688, 48, 88);
        return result;
    }

    internal void Validate()
    {
        if (Version != 1 || Widgets is null || Widgets.Count > 200) throw new InvalidDataException("Unsupported design file.");
        if (!float.IsFinite(GridSize) || !float.IsFinite(ToolbarHeight) || !float.IsFinite(RowHeight)
            || !float.IsFinite(HierarchyWidth) || !float.IsFinite(InspectorWidth)) throw new InvalidDataException("Invalid design dimensions.");
        GridSize = Math.Clamp(GridSize, 1, 64); ToolbarHeight = Math.Clamp(ToolbarHeight, 80, 400);
        RowHeight = Math.Clamp(RowHeight, 36, 120); HierarchyWidth = Math.Clamp(HierarchyWidth, 150, 600);
        InspectorWidth = Math.Clamp(InspectorWidth, 220, 600);
        if (!float.IsFinite(FrameSnapStep) || !float.IsFinite(DragThreshold) || !float.IsFinite(BarRounding) || !float.IsFinite(KeySize)
            || !float.IsFinite(LeftPanelWidth) || !float.IsFinite(RightPanelWidth)) throw new InvalidDataException("Invalid interaction/style settings.");
        LeftPanelWidth = Math.Clamp(LeftPanelWidth, 120, 600); RightPanelWidth = Math.Clamp(RightPanelWidth, 120, 600);
        FrameSnapStep = Math.Clamp(FrameSnapStep, 1, 10); DragThreshold = Math.Clamp(DragThreshold, 2, 12);
        BarRounding = Math.Clamp(BarRounding, 0, 14); KeySize = Math.Clamp(KeySize, 3, 12);
        var ids = new HashSet<string>();
        int count = 0;
        void ValidateWidget(DesignerWidget w, int depth)
        {
            if (++count > 200 || depth > 4) throw new InvalidDataException("Too many nested components.");
            if (w is null || string.IsNullOrEmpty(w.Id) || !ids.Add(w.Id) || w.Label is null || w.Binding is null || w.Text is null
                || !Enum.IsDefined(w.Kind) || !Enum.IsDefined(w.Target) || !float.IsFinite(w.X) || !float.IsFinite(w.Y)
                || !float.IsFinite(w.Width) || !float.IsFinite(w.Height) || !float.IsFinite(w.Value))
                throw new InvalidDataException("Invalid widget data.");
            w.Width = Math.Clamp(w.Width, 40, 1600); w.Height = Math.Clamp(w.Height, 24, w.Target == DesignerTarget.Toolbar ? 160 : 1000);
            w.X = Math.Max(0, w.X); w.Y = Math.Max(0, w.Y);
            if (w.Target == DesignerTarget.Toolbar) { w.Y = Math.Min(w.Y, ToolbarHeight - 24); w.Height = Math.Min(w.Height, ToolbarHeight - w.Y); }
            if (w.Graph is null) throw new InvalidDataException("Missing component graph.");
            w.Graph.Validate();
            if (w.Children is not null) foreach (var child in w.Children) ValidateWidget(child, depth + 1);
        }
        foreach (var w in Widgets) ValidateWidget(w, 0);
        var options = Widgets.FirstOrDefault(w => w.Target == DesignerTarget.Toolbar && w.Label.Equals("Options", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(w.Binding));
        if (options is not null) options.Binding = "options";
    }

    internal static float Snap(float value, bool enabled, float grid) => enabled
        ? MathF.Round(value / grid, MidpointRounding.AwayFromZero) * grid : value;
}

internal enum GuiTrigger { Activated, Changed, Submitted, FrameChanged }
internal enum GuiNodeKind
{
    Event, ComponentValue, Number, Boolean, Text, CurrentFrame, SelectedKeyValue, ScaleNumber,
    SetFrame, SetSelectedKeyValue, CreateKey, RenameSelection, FitTimeline, ToggleInspector,
    SetCurves, SetSnap, SetRowHeight, DuplicateSelection, DeleteSelection, Undo, Redo, SetComponentValue, RunExistingBinding
}
internal enum GuiPortType { Flow, Number, Boolean, Text }
internal sealed class GuiActionNode
{
    public int Id { get; set; }
    public GuiNodeKind Kind { get; set; }
    public GuiTrigger Trigger { get; set; }
    public double Number { get; set; }
    public bool Boolean { get; set; }
    public string Text { get; set; } = "";
    public float X { get; set; } = 20;
    public float Y { get; set; } = 40;
}
internal sealed class GuiActionLink
{
    public int Id { get; set; }
    public int Output { get; set; }
    public int Input { get; set; }
}
internal sealed class GuiActionGraph
{
    public List<GuiActionNode> Nodes { get; set; } = new();
    public List<GuiActionLink> Links { get; set; } = new();
    internal void Validate()
    {
        if (Nodes is null || Links is null || Nodes.Count > 64 || Links.Count > 128
            || Nodes.Any(n => n is null || n.Id < 1 || n.Id > 10000 || !Enum.IsDefined(n.Kind) || !Enum.IsDefined(n.Trigger)
                || !double.IsFinite(n.Number) || !float.IsFinite(n.X) || !float.IsFinite(n.Y) || n.Text is null)
            || Nodes.Select(n => n.Id).Distinct().Count() != Nodes.Count)
            throw new InvalidDataException("Invalid component graph.");
        if (Links.Any(l => l is null || l.Id < 1 || !Nodes.Any(n => n.Id == l.Output / 10) || !Nodes.Any(n => n.Id == l.Input / 10)))
            throw new InvalidDataException("Connection points to a missing node.");
        if (Links.Select(l => l.Id).Distinct().Count() != Links.Count || Links.Select(l => l.Input).Distinct().Count() != Links.Count)
            throw new InvalidDataException("Duplicate graph links or inputs.");
        foreach (var link in Links)
        {
            var from = Nodes.First(n => n.Id == link.Output / 10); var to = Nodes.First(n => n.Id == link.Input / 10);
            bool flow = link.Output % 10 == 1 && link.Input % 10 == 0 && (from.Kind == GuiNodeKind.Event || from.Kind >= GuiNodeKind.SetFrame) && to.Kind >= GuiNodeKind.SetFrame;
            bool data = link.Output % 10 == 3 && link.Input % 10 == 2 && from.Kind is not GuiNodeKind.Event && from.Kind < GuiNodeKind.SetFrame && MotlistTimelineEditor.HasDataInput(to.Kind);
            if (!flow && !data) throw new InvalidDataException("Invalid graph pin direction.");
        }
        bool Visit(int id, HashSet<int> path)
        { if (!path.Add(id)) return false; foreach (var link in Links.Where(l => l.Output / 10 == id)) if (!Visit(link.Input / 10, path)) return false; path.Remove(id); return true; }
        if (Nodes.Any(n => !Visit(n.Id, new()))) throw new InvalidDataException("Graph contains a connection cycle.");
    }
}

// Runtime portions of the current prototype. No component browser, designer UI,
// node editor UI, source inspector, demo window or dummy-track creation is included.
public partial class MotlistTimelineEditor
{
    private TimelineDesignDocument design = TimelineDesignDocument.Default();
    private readonly Dictionary<DesignerTarget, Vector2> surfaceSizes = new();
    private readonly Dictionary<string, string> componentExecutionStatus = new();
    private string connectionStatus = "";
    private double lastGraphFrame = double.NaN;

    private void InitializeRuntimeDesign()
    {
        using var stream = typeof(MotlistTimelineEditor).Assembly.GetManifestResourceStream("MotlistTimelineDesign")
            ?? throw new InvalidOperationException("Embedded timeline design is missing.");
        design = JsonSerializer.Deserialize<TimelineDesignDocument>(stream) ?? throw new InvalidOperationException("Empty timeline design.");
        design.Validate();
        overviewView.RowHeight = design.RowHeight; hierarchyWidth = design.HierarchyWidth; inspectorWidth = design.InspectorWidth;
        foreach (var widget in AllComponents()) if (widget.SavedBooleanValue is { } value) WriteBoolean(widget, value);
    }

    private void DrawDesignedToolbar()
    {
        if (ImGui.BeginChild("DesignedToolbar", new Vector2(0, design.ToolbarHeight + ImGui.GetStyle().WindowPadding.Y * 2 + 16), ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar))
        {
            var origin = ImGui.GetCursorScreenPos();
            DrawAssetSurface(DesignerTarget.Toolbar, origin, new Vector2(ImGui.GetContentRegionAvail().X, design.ToolbarHeight));
            ImGui.SetCursorScreenPos(origin + new Vector2(0, design.ToolbarHeight - 24)); ImGui.Dummy(new Vector2(1, 1));
        }
        ImGui.EndChild();
    }

    private void DrawRuntimeWindows()
    {
        foreach (var widget in design.Widgets.Where(w => w.Visible && w.Target == DesignerTarget.Window))
        {
            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(viewport.WorkPos + new Vector2(widget.X, widget.Y), ImGuiCond.Once);
            ImGui.SetNextWindowSize(new Vector2(widget.Width, widget.Height), ImGuiCond.Once);
            if (ImGui.Begin(widget.Label + "###TimelineComponent/" + widget.Id))
            {
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X); DrawDesignerWidget(widget, ImGui.GetContentRegionAvail());
            }
            ImGui.End();
        }
    }
    private void DrawDesignerWidget(DesignerWidget w, Vector2 size)
    {
        if (w.CustomColor)
        {
            var color = ImGui.ColorConvertU32ToFloat4(w.Color);
            ImGui.PushStyleColor(ImGuiCol.Button, color); ImGui.PushStyleColor(ImGuiCol.Header, color); ImGui.PushStyleColor(ImGuiCol.FrameBg, color * new Vector4(1, 1, 1, .5f));
        }
        try
        {
        string label = w.Label + "##Control";
        if (w.Kind == DesignerWidgetKind.BoundControl)
        {
            switch (w.Binding)
            {
                case "filter": if (ImGui.InputTextWithHint("##Filter", w.Label, ref timelineFilter, 256)) FireAssetEvent(w, GuiTrigger.Changed); if (ImGui.IsItemDeactivatedAfterEdit()) FireAssetEvent(w, GuiTrigger.Submitted); return;
                case "frame": ImGui.TextUnformatted($"Frame {currentFrame:0.##} / {motlist.Motclips.Max(c => c.EndFrame):0.##}"); return;
                case "curves": case "snap": case "inactive": case "inspector": case "hierarchy": case "isolate": DrawBoundCheckbox(w); return;
            }
        }
        switch (w.Kind)
        {
            case DesignerWidgetKind.ImPlot: DrawBoundPlot(w, size); break;
            case DesignerWidgetKind.Text: ImGui.TextUnformatted(w.Text.Length > 0 ? w.Text : w.Label); break;
            case DesignerWidgetKind.Checkbox:
                DrawBoundCheckbox(w); break;
            case DesignerWidgetKind.TextInput:
                string text = w.Text; if (ImGui.InputTextWithHint("##Text", w.Label, ref text, 1024)) { w.Text = text; FireAssetEvent(w, GuiTrigger.Changed); }
                if (ImGui.IsItemDeactivatedAfterEdit()) FireAssetEvent(w, GuiTrigger.Submitted); break;
            case DesignerWidgetKind.Slider:
                float value = w.Binding == "currentFrame" ? (float)currentFrame : w.Value;
                float maximum = w.Binding == "currentFrame" ? (float)motlist.Motclips.Max(c => c.EndFrame) : 1;
                if (ImGui.SliderFloat(label, ref value, 0, maximum)) { w.Value = value; if (w.Binding == "currentFrame") currentFrame = value; FireAssetEvent(w, GuiTrigger.Changed); } break;
            case DesignerWidgetKind.ColorEdit:
                var color = ImGui.ColorConvertU32ToFloat4(w.Color); if (ImGui.ColorEdit4(label, ref color)) { w.Color = ImGui.ColorConvertFloat4ToU32(color); FireAssetEvent(w, GuiTrigger.Changed); } break;
            case DesignerWidgetKind.ProgressBar: ImGui.ProgressBar(w.Value, size, w.Label); break;
            case DesignerWidgetKind.List:
                if (ImGui.BeginChild("List", size, ImGuiChildFlags.Borders))
                {
                    var items = w.Text.Split('\n');
                    for (int i = 0; i < items.Length; i++) if (ImGui.Selectable(items[i] + "##" + i, (int)w.Value == i)) { w.Value = i; FireAssetEvent(w, GuiTrigger.Changed); }
                }
                ImGui.EndChild(); break;
            case DesignerWidgetKind.Tabs:
                if (ImGui.BeginTabBar("Tabs"))
                {
                    var items = w.Text.Split('\n');
                    for (int i = 0; i < items.Length; i++) if (ImGui.BeginTabItem(items[i] + "##" + i))
                    { if ((int)w.Value != i) { w.Value = i; FireAssetEvent(w, GuiTrigger.Changed); } ImGui.TextUnformatted(w.Label + " / " + items[i]); ImGui.EndTabItem(); }
                    ImGui.EndTabBar();
                }
                break;
            case DesignerWidgetKind.Table:
                var rows = w.Text.Split('\n'); int columns = Math.Clamp(rows[0].Split('|').Length, 1, 8);
                if (ImGui.BeginTable("Table", columns, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY, size))
                {
                    for (int row = 0; row < rows.Length; row++)
                    {
                        ImGui.TableNextRow(); var cells = rows[row].Split('|');
                        for (int column = 0; column < columns; column++) { ImGui.TableSetColumnIndex(column); if (ImGui.Selectable((column < cells.Length ? cells[column] : "") + "##" + row + "/" + column, (int)w.Value == row)) { w.Value = row; FireAssetEvent(w, GuiTrigger.Changed); } }
                    }
                    ImGui.EndTable();
                }
                break;
            default:
                bool disabled = w.Binding is "duplicate" or "delete" or "rename" && selectedTrack is null && selectedProperty is null
                    || w.Binding == "undo" && (editor is null || !UndoRedo.CanUndo()) || w.Binding == "redo" && (editor is null || !UndoRedo.CanRedo());
                ImGui.BeginDisabled(disabled);
                if (ImGui.Button(label, size)) ActivateComponent(w);
                ImGui.EndDisabled();
                if (w.Binding == "options" && ImGui.BeginPopup("Options"))
                {
                    DrawOptionsContents(w);
                    ImGui.EndPopup();
                }
                break;
        }
        }
        finally { if (w.CustomColor) ImGui.PopStyleColor(3); }
    }
    private void InvokeDesignerAction(string binding)
    {
        switch (binding)
        {
            case "duplicate": DuplicateSelection(); break;
            case "delete": DeleteSelection(); break;
            case "rename": RequestRename(); break;
            case "undo": UndoRedo.Undo(); break;
            case "redo": UndoRedo.Redo(); break;
            case "fit": overviewView.Initialized = false; break;
            case "options": ImGui.OpenPopup("Options"); break;
            case "toggleInspector": showDetails = !showDetails; break;
            case "expandTracks": foreach (var track in motlist.Motclips.SelectMany(c => c.Tracks)) expandedTracks.Add(track); break;
            case "collapseTracks": expandedTracks.Clear(); break;
        }
    }
    private static bool IsBooleanBinding(string binding) => binding is "curves" or "snap" or "inactive" or "inspector" or "hierarchy" or "isolate";
    private static bool IsActionBinding(string binding) => binding is "options" or "fit" or "duplicate" or "delete" or "rename" or "undo" or "redo" or "toggleInspector" or "expandTracks" or "collapseTracks";
    private static bool HasComponentValue(DesignerWidget w) => IsBooleanBinding(w.Binding) || w.Binding is "filter" or "frame" or "currentFrame"
        || w.Kind is DesignerWidgetKind.Checkbox or DesignerWidgetKind.TextInput or DesignerWidgetKind.Slider or DesignerWidgetKind.ColorEdit or DesignerWidgetKind.List or DesignerWidgetKind.Table or DesignerWidgetKind.Tabs or DesignerWidgetKind.ProgressBar;
    private IEnumerable<DesignerWidget> AllComponents()
    {
        static IEnumerable<DesignerWidget> Walk(DesignerWidget w)
        { yield return w; if (w.Children is not null) foreach (var child in w.Children) foreach (var item in Walk(child)) yield return item; }
        return design.Widgets.SelectMany(Walk);
    }
    private void EnsureOptionsContents(DesignerWidget w)
    {
        if (w.Binding != "options" || w.Children is not null) return;
        w.Children = new()
        {
            new() { Kind = DesignerWidgetKind.Checkbox, Label = "Track names", Binding = "hierarchy" },
            new() { Kind = DesignerWidgetKind.Checkbox, Label = "Isolate selected track", Binding = "isolate" },
            new() { Kind = DesignerWidgetKind.Button, Label = "Expand all tracks", Binding = "expandTracks" },
            new() { Kind = DesignerWidgetKind.Button, Label = "Collapse all tracks", Binding = "collapseTracks" },
            new() { Kind = DesignerWidgetKind.Text, Label = "Gesture help", Text = "Alt + right: move key / range\nShift + right: extend end\nLeft drag: move keys/ranges | Middle drag: pan | Alt + left: scrub" }
        };
    }
    private void MaterializeExistingAction(DesignerWidget w)
    {
        if (w.GraphOwnsActivation || !IsActionBinding(w.Binding) || w.Kind is not (DesignerWidgetKind.Button or DesignerWidgetKind.BoundControl)) return;
        int Next() => w.Graph.Nodes.Select(n => n.Id).DefaultIfEmpty(0).Max() + 1;
        var trigger = w.Graph.Nodes.FirstOrDefault(n => n.Kind == GuiNodeKind.Event && n.Trigger == GuiTrigger.Activated);
        if (trigger is null) { trigger = new() { Id = Next(), Kind = GuiNodeKind.Event }; w.Graph.Nodes.Add(trigger); }
        var action = new GuiActionNode { Id = Next(), Kind = GuiNodeKind.RunExistingBinding, X = 380, Y = 40 };
        w.Graph.Nodes.Add(action); Connect(w, trigger.Id * 10 + 1, action.Id * 10);
        var existingLink = w.Graph.Links.Last(); w.Graph.Links.Remove(existingLink); w.Graph.Links.Insert(0, existingLink);
        w.GraphOwnsActivation = true;
        if (!HasComponentValue(w)) w.Graph.Nodes.RemoveAll(n => n.Kind == GuiNodeKind.ComponentValue && !w.Graph.Links.Any(l => l.Output / 10 == n.Id || l.Input / 10 == n.Id));
    }
    private void ActivateComponent(DesignerWidget w)
    {
        
        MaterializeExistingAction(w);
        if (!w.GraphOwnsActivation) InvokeDesignerAction(w.Binding);
        FireAssetEvent(w, GuiTrigger.Activated);
    }
    private bool ReadBoolean(DesignerWidget w) => w.Binding switch
    { "curves" => showCurves, "snap" => snappingEnabled, "inactive" => showInactive, "inspector" => showDetails, "hierarchy" => showHierarchy, "isolate" => isolateTrack, _ => w.Checked };
    private void WriteBoolean(DesignerWidget w, bool value)
    {
        switch (w.Binding)
        {
            case "curves": showCurves = value; break; case "snap": snappingEnabled = value; break;
            case "inactive": showInactive = value; break; case "inspector": showDetails = value; break;
            case "hierarchy": showHierarchy = value; break; case "isolate": isolateTrack = value; break;
        }
        w.Checked = value;
    }
    private void DrawBoundCheckbox(DesignerWidget w)
    {
        bool value = ReadBoolean(w);
        if (ImGui.Checkbox(w.Label + "##Control", ref value)) { WriteBoolean(w, value); FireAssetEvent(w, GuiTrigger.Changed); }
    }
    private void DrawOptionsContents(DesignerWidget parent)
    {
        EnsureOptionsContents(parent);
        foreach (var child in parent.Children!.Where(w => w.Visible).ToArray())
        {
            ImGui.PushID(child.Id);
            if (child.Kind == DesignerWidgetKind.Checkbox) DrawBoundCheckbox(child);
            else if (child.Kind == DesignerWidgetKind.Text) ImGui.TextUnformatted(child.Text);
            else if (ImGui.MenuItem(child.Label)) ActivateComponent(child);
            ImGui.PopID();
        }
    }
    private static string BindingTitle(string binding) => binding switch
    {
        "options" => "Open this component's Options popup", "hierarchy" => "Show track names", "isolate" => "Show only the selected track",
        "expandTracks" => "Expand all tracks", "collapseTracks" => "Collapse all tracks", "fit" => "Fit timeline to content",
        "toggleInspector" => "Toggle selection inspector", "curves" => "Show numeric curves", "snap" => "Snap timeline frames",
        "inactive" => "Show inactive properties", "inspector" => "Show selection inspector", "filter" => "Filter tracks and properties",
        "frame" => "Display current frame", "currentFrame" => "Current playhead frame", "selectedCurve" => "Plot selected property's keys",
        "duplicate" => "Duplicate timeline selection", "delete" => "Delete timeline selection", "rename" => "Open rename for timeline selection",
        "undo" => "Undo last timeline edit", "redo" => "Redo timeline edit", "" => "No built-in binding", _ => binding
    };
    private static bool IsAction(GuiNodeKind kind) => kind >= GuiNodeKind.SetFrame;
    private static GuiPortType ComponentType(DesignerWidget w) => w.Kind == DesignerWidgetKind.Checkbox || IsBooleanBinding(w.Binding)
        ? GuiPortType.Boolean : w.Kind == DesignerWidgetKind.TextInput || w.Binding == "filter" ? GuiPortType.Text : GuiPortType.Number;
    private static GuiPortType OutputType(DesignerWidget w, GuiActionNode n) => n.Kind switch
    { GuiNodeKind.Event => GuiPortType.Flow, GuiNodeKind.ComponentValue => ComponentType(w), GuiNodeKind.Boolean => GuiPortType.Boolean,
        GuiNodeKind.Text => GuiPortType.Text, _ => IsAction(n.Kind) ? GuiPortType.Flow : GuiPortType.Number };
    private static GuiPortType InputType(DesignerWidget w, GuiActionNode n) => n.Kind switch
    { GuiNodeKind.SetSnap or GuiNodeKind.SetCurves => GuiPortType.Boolean, GuiNodeKind.RenameSelection => GuiPortType.Text,
        GuiNodeKind.SetComponentValue => ComponentType(w), _ => GuiPortType.Number };
    internal static bool HasDataInput(GuiNodeKind kind) => kind is GuiNodeKind.SetFrame or GuiNodeKind.SetSelectedKeyValue or GuiNodeKind.CreateKey
        or GuiNodeKind.RenameSelection or GuiNodeKind.SetCurves or GuiNodeKind.SetSnap or GuiNodeKind.SetRowHeight or GuiNodeKind.SetComponentValue or GuiNodeKind.ScaleNumber;
    private bool Connect(DesignerWidget widget, int output, int input)
    {
        var graph = widget.Graph;
        var from = graph.Nodes.FirstOrDefault(n => n.Id == output / 10); var to = graph.Nodes.FirstOrDefault(n => n.Id == input / 10);
        if (from is null || to is null || from == to || output % 10 is not (1 or 3) || input % 10 is not (0 or 2))
        { connectionStatus = "Connect an output pin to an input pin on another node."; return false; }
        bool flow = output % 10 == 1 && input % 10 == 0 && (from.Kind == GuiNodeKind.Event || IsAction(from.Kind)) && IsAction(to.Kind);
        bool data = output % 10 == 3 && input % 10 == 2 && HasDataInput(to.Kind) && OutputType(widget, from) == InputType(widget, to) && !IsAction(from.Kind) && from.Kind != GuiNodeKind.Event;
        if (!flow && !data) { connectionStatus = "Pin types must match: flow, number, boolean or text."; return false; }
        bool Reaches(int id, HashSet<int> visited) => id == from.Id || visited.Add(id) && graph.Links.Where(l => l.Output / 10 == id).Any(l => Reaches(l.Input / 10, visited));
        if (Reaches(to.Id, new())) { connectionStatus = "That connection would create a cycle."; return false; }
        graph.Links.RemoveAll(l => l.Input == input);
        graph.Links.Add(new() { Id = graph.Links.Select(l => l.Id).DefaultIfEmpty(0).Max() + 1, Output = output, Input = input });
        connectionStatus = "Connected. Switch Edit layout off to test the component."; return true;
    }
    private object ComponentValue(DesignerWidget w) => ComponentType(w) switch
    {
        GuiPortType.Boolean => ReadBoolean(w),
        GuiPortType.Text => w.Binding == "filter" ? timelineFilter : w.Text,
        _ => w.Kind == DesignerWidgetKind.ColorEdit ? (double)w.Color : w.Binding is "currentFrame" or "frame" ? currentFrame : (double)w.Value
    };
    private object ReadGraphValue(DesignerWidget w, GuiActionNode node, HashSet<int> visited)
    {
        if (!visited.Add(node.Id)) throw new InvalidOperationException("Value connection cycle.");
        return node.Kind switch
        {
            GuiNodeKind.ComponentValue => ComponentValue(w), GuiNodeKind.Number => node.Number, GuiNodeKind.Boolean => node.Boolean,
            GuiNodeKind.Text => node.Text, GuiNodeKind.CurrentFrame => currentFrame,
            GuiNodeKind.SelectedKeyValue => selectedKey?.Value ?? throw new InvalidOperationException("Select a numeric key first."),
            GuiNodeKind.ScaleNumber => Convert.ToDouble(ReadGraphInput(w, node, visited)) * node.Number,
            _ => throw new InvalidOperationException("This node has no value output.")
        };
    }
    private object ReadGraphInput(DesignerWidget w, GuiActionNode node, HashSet<int> visited)
    {
        var link = w.Graph.Links.FirstOrDefault(l => l.Input == node.Id * 10 + 2);
        if (link is not null) return ReadGraphValue(w, w.Graph.Nodes.First(n => n.Id == link.Output / 10), visited);
        return InputType(w, node) switch { GuiPortType.Boolean => node.Boolean, GuiPortType.Text => node.Text, _ => node.Number };
    }
    private void FireAssetEvent(DesignerWidget widget, GuiTrigger trigger)
    {
        if (widget.Graph.Nodes.Count == 0) return;
        try
        {
            var visited = new HashSet<int>();
            void Follow(int id)
            {
                if (!visited.Add(id)) throw new InvalidOperationException("Flow connection cycle.");
                var node = widget.Graph.Nodes.First(n => n.Id == id);
                if (IsAction(node.Kind)) ExecuteGraphNode(widget, node);
                foreach (var link in widget.Graph.Links.Where(l => l.Output == id * 10 + 1).ToArray()) Follow(link.Input / 10);
            }
            foreach (var node in widget.Graph.Nodes.Where(n => n.Kind == GuiNodeKind.Event && n.Trigger == trigger).ToArray()) Follow(node.Id);
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException or InvalidCastException)
        { connectionStatus = "Action stopped: " + error.Message; componentExecutionStatus[widget.Id] = connectionStatus; }
    }
    private void ExecuteGraphNode(DesignerWidget widget, GuiActionNode node)
    {
        object Value() => ReadGraphInput(widget, node, new());
        double Number() { double value = Convert.ToDouble(Value()); return double.IsFinite(value) ? value : throw new InvalidOperationException("Input must be finite."); }
        switch (node.Kind)
        {
            case GuiNodeKind.RunExistingBinding: InvokeDesignerAction(widget.Binding); break;
            case GuiNodeKind.SetFrame: currentFrame = Math.Clamp(Number(), motlist.Motclips.Min(c => c.StartFrame), motlist.Motclips.Max(c => c.EndFrame)); break;
            case GuiNodeKind.SetSelectedKeyValue:
                if (selectedKey is null) throw new InvalidOperationException("Select a key first.");
                double value = Number(); Edit(() => selectedKey.Value = value); break;
            case GuiNodeKind.CreateKey:
                if (selectedProperty is null || selectedProperty.Container) throw new InvalidOperationException("Select a scalar property first.");
                AddKey(selectedProperty, currentFrame, Number()); break;
            case GuiNodeKind.RenameSelection:
                string name = Convert.ToString(Value()) ?? "";
                if (string.IsNullOrWhiteSpace(name) || selectedTrack is null && selectedProperty is null) throw new InvalidOperationException("Select a track/property and supply a name.");
                Edit(() => { if (selectedProperty is not null) selectedProperty.Native.Info.FunctionName = name; else selectedTrack!.Native.Name = name; }); break;
            case GuiNodeKind.FitTimeline: overviewView.Initialized = false; break;
            case GuiNodeKind.ToggleInspector: showDetails = !showDetails; break;
            case GuiNodeKind.SetCurves: showCurves = Convert.ToBoolean(Value()); break;
            case GuiNodeKind.SetSnap: snappingEnabled = Convert.ToBoolean(Value()); break;
            case GuiNodeKind.SetRowHeight: overviewView.RowHeight = design.RowHeight = Math.Clamp((float)Number(), ImGui.GetTextLineHeight() + 20, 120); break;
            case GuiNodeKind.DuplicateSelection: DuplicateSelection(); break;
            case GuiNodeKind.DeleteSelection: DeleteSelection(); break;
            case GuiNodeKind.Undo: UndoRedo.Undo(); break;
            case GuiNodeKind.Redo: UndoRedo.Redo(); break;
            case GuiNodeKind.SetComponentValue:
                object input = Value();
                if (ComponentType(widget) == GuiPortType.Boolean) WriteBoolean(widget, Convert.ToBoolean(input));
                else if (ComponentType(widget) == GuiPortType.Text) widget.Text = Convert.ToString(input) ?? "";
                else if (widget.Kind == DesignerWidgetKind.ColorEdit) widget.Color = Convert.ToUInt32(input);
                else widget.Value = (float)Convert.ToDouble(input); break;
        }
        componentExecutionStatus[widget.Id] = "Ran " + (node.Kind == GuiNodeKind.RunExistingBinding ? BindingTitle(widget.Binding) : node.Kind.ToString());
    }
    private void DispatchFrameChanged()
    {
        if (lastGraphFrame == currentFrame) return;
        lastGraphFrame = currentFrame;
        foreach (var widget in AllComponents().ToArray()) FireAssetEvent(widget, GuiTrigger.FrameChanged);
    }
    private bool HasAssets(DesignerTarget target) => design.Widgets.Any(w => w.Visible && w.Target == target);
    private void DrawAssetPanel(DesignerTarget target, float width)
    {
        if (ImGui.BeginChild("Assets/" + target, new Vector2(width, 0), ImGuiChildFlags.Borders))
        {
            var origin = ImGui.GetCursorScreenPos(); var size = ImGui.GetContentRegionAvail();
            DrawAssetSurface(target, origin, size);
            ImGui.SetCursorScreenPos(origin); ImGui.Dummy(size);
        }
        ImGui.EndChild();
    }
    private bool OverlayContainsMouse()
    {
        var mouse = ImGui.GetMousePos();
        return design.Widgets.Any(w => w.Visible && w.Target == DesignerTarget.TimelineOverlay
            && mouse.X >= canvasMin.X + w.X && mouse.X <= canvasMin.X + w.X + w.Width
            && mouse.Y >= canvasMin.Y + w.Y && mouse.Y <= canvasMin.Y + w.Y + w.Height);
    }
    private void DrawBoundPlot(DesignerWidget widget, Vector2 size)
    {
        if (selectedProperty is null) { ImGui.TextWrapped("Select a numeric property to plot its keys."); return; }
        var keys = selectedProperty.Keys.Where(k => k.Value.HasValue).OrderBy(k => k.Frame).ToArray();
        if (keys.Length == 0) { ImGui.TextWrapped("Select a property with numeric keys."); return; }
        var frames = keys.Select(k => k.Frame).ToArray(); var values = keys.Select(k => k.Value!.Value).ToArray();
        if (ImPlot.GetCurrentContext().IsNull) { ImPlot.SetImGuiContext(ImGui.GetCurrentContext()); ImPlot.CreateContext(); }
        if (ImPlot.BeginPlot("##SelectedProperty", size)) { ImPlot.PlotLine("Keys", ref frames[0], ref values[0], keys.Length); ImPlot.EndPlot(); }
    }
    private void DrawAssetSurface(DesignerTarget target, Vector2 origin, Vector2 sizeAvailable)
    {
        surfaceSizes[target] = sizeAvailable;
        var draw = ImGui.GetWindowDrawList();
            foreach (var widget in design.Widgets.Where(w => w.Visible && w.Target == target).ToArray())
            {
                ImGui.PushID(widget.Id);
                var position = origin + new Vector2(widget.X, widget.Y);
                var size = new Vector2(widget.Width, widget.Height);
                ImGui.SetCursorScreenPos(position);
                ImGui.SetNextItemWidth(size.X);
                ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(ImGui.GetStyle().FramePadding.X,
                    Math.Max(0, (size.Y - ImGui.GetTextLineHeight()) * .5f)));
                ImGui.PushStyleVar(ImGuiStyleVar.DisabledAlpha, 1);
                ImGui.BeginDisabled(false);
                DrawDesignerWidget(widget, size);
                ImGui.EndDisabled();
                ImGui.PopStyleVar(2);
                ImGui.PopID();
            }

    }
}
