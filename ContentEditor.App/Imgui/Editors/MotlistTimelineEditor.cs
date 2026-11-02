using System.Numerics;
using System.Globalization;
using Hexa.NET.ImGui;
using Hexa.NET.ImPlot;
using Hexa.NET.ImGui.Widgets;
using ReeLib;
using ReeLib.Clip;
using ReeLib.Mot;
using ReeLib.Common;
using ContentEditor.App.Graphics;

namespace ContentEditor.App.ImguiHandling;

public partial class MotlistTimelineEditor
{
    private readonly record struct VisibleLane(string Label, string Path, double Y, TimelineProperty? Property, TimelineTrack? Track, Motclip Clip, double StartFrame, double EndFrame);
    private readonly record struct TimelineSpan(double StartFrame, double EndFrame);

    private enum EditMode
    {
        None,
        Pan,
        Playhead,
        TrackStart,
        TrackEnd,
        TrackMove,
        PropertyStart,
        PropertyEnd,
        PropertyMove,
        CreatePropertyRange,
        PropertyEndpoint,
        ReorderTrack,
        ReorderProperty,
        Key
    }

    private sealed class TimelineDrag
    {
        public EditMode Mode { get; init; }
        public VisibleLane Lane { get; init; }
        public TimelineTrack? Track { get; init; }
        public TimelineProperty? Property { get; init; }
        public TimelineKey? Key { get; init; }
        public double InitialStart { get; init; }
        public double InitialEnd { get; init; }
        public double InitialFrame { get; init; }
        public double OwnerStart { get; init; }
        public double OwnerEnd { get; init; }
        public float StartMouseX { get; init; }
        public float StartMouseY { get; init; }
        public float StartPlotWidth { get; init; }
        public float StartPlotHeight { get; init; }
        public double InitialXMin { get; init; }
        public double InitialXMax { get; init; }
        public double InitialYMin { get; init; }
        public double InitialYMax { get; init; }
        public TimelineView View { get; init; } = null!;
        public ImGuiMouseButton Button { get; init; }
        public bool Started { get; set; }
        public TimelineKey? EndKey { get; set; }
        public List<TimelineTrack>? TrackOrder { get; init; }
        public List<TimelineProperty>? PropertyOrder { get; init; }
        public List<TimelineKey> CreatedKeys { get; } = new();
        public List<(TimelineKey Key, double Frame)> Keys { get; init; } = new();
        public List<PropertyDragSnapshot> Properties { get; init; } = new();
    }

    private sealed record PropertyDragSnapshot(TimelineProperty Property, double? Start, double? End, List<(TimelineKey Key, double Frame)> Keys);

    private double currentFrame = 24;
    private bool snappingEnabled = true;
    private bool motlistExpanded = true;
    private bool motclipsExpanded = true;
    private readonly HashSet<Motclip> collapsedMotclips = new();
    private readonly HashSet<Motclip> collapsedTrackGroups = new();
    private readonly HashSet<TimelineTrack> collapsedTracks = new();
    private readonly HashSet<TimelineTrack> collapsedPropertyGroups = new();
    private TimelineDrag? activeDrag;
    private TimelineTrack? selectedTrack;
    private Motclip? selectedClip;
    private TimelineProperty? selectedProperty;
    private TimelineKey? selectedKey;
    private TimelineProperty? contextProperty;
    private TimelineTrack? contextTrack;
    private TimelineKey? contextKey;
    private Motclip? contextClip;
    private TimelineTrack? copiedTrack;
    private double contextFrame;
    private double newKeyValue;
    private Vector2 contextMouseDownPosition;
    private const float MinimumHierarchyWidth = 220;
    private const float MinimumTimelineWidth = 280;
    private const float SplitterWidth = 6;
    private bool showHierarchy = true;
    private float hierarchyWidth = 330;
    private readonly Motlist motlist = new();



    private readonly MotlistEditor? editor;
    private readonly Action markModified;
    private readonly Action<Action, Action, string> recordChange;
    private MotFile? motion;
    private ReeLib.Motlist.MotIndex? motionIndex;
    private bool rebindPending, showNative, showDetails;
    private Hexa.NET.ImPlot.ImPlotContextPtr plotContext;

    public MotlistTimelineEditor(MotlistEditor editor)
    {
        this.editor = editor;
        markModified = editor.MarkTimelineModified;
        recordChange = (apply, undo, id) => UndoRedo.RecordCallback(null, apply, undo, id);
    }

    internal MotlistTimelineEditor(MotFile motion, IEnumerable<MotClip> clips, Action changed,
        Action<Action, Action, string> record)
    {
        markModified = changed;
        recordChange = record;
        this.motion = motion;
        BindMotion(motion, clips);
    }

    public void Reset()
    {
        CancelGesture();
        motion = null; motionIndex = null; selectedClip = null; selectedTrack = null;
        selectedProperty = null; selectedKey = null; rightView = null; keyboardView = null;
        motlist.Motclips.Clear(); clipViews.Clear(); trackViews.Clear(); propertyNodes.Clear(); keyNodes.Clear();
        collapsedMotclips.Clear(); collapsedTracks.Clear(); collapsedTrackGroups.Clear(); collapsedPropertyGroups.Clear();
        propertyViews.Clear(); overviewView.Initialized = false; valueContexts.Clear();
    }

    public void CloseTimelineWindow()
    {
        CancelGesture();
        if (!plotContext.IsNull) { ImPlot.DestroyContext(plotContext); plotContext = default; }
    }
    public void CloseInspectorWindow() { }

    private IEnumerable<MotClip> MotionClips() => (motion?.Clips ?? Enumerable.Empty<MotClip>())
        .Concat(motionIndex?.OverrideClips ?? Enumerable.Empty<MotClip>());

    public void Draw(Action drawNativeEditor)
    {
        if (editor is not null)
        {
            ImGui.SetNextItemWidth(Math.Max(220, ImGui.GetContentRegionAvail().X - 145));
            if (ImGui.BeginCombo("##TimelineMotion", motion is null ? "Choose motion" : $"{motionIndex?.motNumber.ToString() ?? "File"}  {motion.Name}"))
            {
                foreach (var index in editor.File.Motions)
                {
                    if (index.MotFile is not MotFile file) continue;
                    ImGui.PushID(editor.File.Motions.IndexOf(index));
                    if (ImGui.Selectable($"{index.motNumber}  {file.Name}", ReferenceEquals(index, motionIndex))) ChooseMotion(file, index);
                    ImGui.PopID();
                }
                foreach (var file in editor.File.MotFiles.OfType<MotFile>().Where(f => !editor.File.Motions.Any(i => ReferenceEquals(i.MotFile, f))))
                {
                    ImGui.PushID(editor.File.MotFiles.IndexOf(file));
                    if (ImGui.Selectable($"Unindexed / {file.Name}", ReferenceEquals(file, motion))) ChooseMotion(file, null);
                    ImGui.PopID();
                }
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            if (ImGui.Button(showNative ? "Timeline" : "File data")) { CancelGesture(); showNative = !showNative; rebindPending = true; }
            if (showNative) { drawNativeEditor(); return; }
            if (motion is null)
            {
                var first = editor.File.Motions.FirstOrDefault(i => i.MotFile is MotFile);
                if (first?.MotFile is MotFile file) ChooseMotion(file, first);
                else if (editor.File.MotFiles.OfType<MotFile>().FirstOrDefault() is MotFile orphan) ChooseMotion(orphan, null);
            }
        }
        if (motion is null) { ImGui.TextDisabled("This file contains no motion data."); return; }
        if (rebindPending && activeDrag is null)
        {
            BindMotion(motion, MotionClips()); rebindPending = false;
            if (selectedTrack is not null && !motlist.Motclips.Any(c => c.Tracks.Contains(selectedTrack))) { selectedTrack = null; selectedProperty = null; selectedKey = null; }
            if (selectedKey is not null && selectedProperty?.Keys.Contains(selectedKey) != true) selectedKey = null;
        }
        DrawWorkspace();
    }

    private void ChooseMotion(MotFile file, ReeLib.Motlist.MotIndex? index)
    {
        CancelGesture(); motion = file; motionIndex = index;
        selectedClip = null; selectedTrack = null; selectedProperty = null; selectedKey = null;
        BindMotion(file, MotionClips()); overviewView.Initialized = false; propertyViews.Clear();
        currentFrame = file.Header.startFrame; rebindPending = false;
    }

    internal void DrawWorkspace()
    {
        if (motlist.Motclips.Count == 0) { ImGui.TextDisabled("This motion has no behavior clips."); return; }
        if (plotContext.IsNull) { ImPlot.SetImGuiContext(ImGui.GetCurrentContext()); plotContext = ImPlot.CreateContext(); }
        ImPlot.SetCurrentContext(plotContext);
        ImGui.PushID("MotlistSimulation");
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 3f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 3f);
        // The simulation's colors are scoped to this workspace; other editor windows retain their theme.
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(.06f,.06f,.065f,1));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(.12f,.15f,.19f,1));
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(.13f,.23f,.36f,1));
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(.14f,.27f,.39f,1));
        var playback = Animator.LastPlaybackState;
        bool connected = playback is { } state && ReferenceEquals(state.ActiveMotion, motion);
        if (connected && activeDrag is null && playback!.Value.IsPlaying) currentFrame = playback.Value.CurrentFrame;
        double previousFrame = currentFrame;
        ImGui.BeginDisabled(activeDrag is not null || rightView is not null);
        DrawToolbar(connected ? playback : null);
        ImGui.EndDisabled();
        ImGui.Separator();
        if (showHierarchy)
        {
            float availableWidth = ImGui.GetContentRegionAvail().X;
            float minTimeline = Math.Min(MinimumTimelineWidth, availableWidth * .45f);
            float minHierarchy = Math.Min(MinimumHierarchyWidth, Math.Max(0, availableWidth-minTimeline-SplitterWidth));
            float maxHierarchy = Math.Max(minHierarchy,availableWidth-minTimeline-SplitterWidth);
            hierarchyWidth = Math.Clamp(hierarchyWidth,minHierarchy,maxHierarchy);
            float height = Math.Max(1,ImGui.GetContentRegionAvail().Y);
            if (ImGui.BeginChild("MotlistHierarchy", new Vector2(hierarchyWidth,0), ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar)) DrawHierarchy();
            ImGui.EndChild(); ImGui.SameLine(0,0);
            ImGui.BeginDisabled(activeDrag is not null || rightView is not null);
            ImGui.InvisibleButton("HierarchySplitter",new Vector2(SplitterWidth,height));
            if (ImGui.IsItemActive()) hierarchyWidth = Math.Clamp(hierarchyWidth+ImGui.GetIO().MouseDelta.X,minHierarchy,maxHierarchy);
            if (ImGui.IsItemHovered() || ImGui.IsItemActive()) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
            ImGui.EndDisabled(); ImGui.SameLine(0,0);
        }
        float detailsWidth = showDetails ? Math.Min(340,ImGui.GetContentRegionAvail().X*.35f) : 0;
        if (ImGui.BeginChild("DopeSheet", new Vector2(showDetails ? -detailsWidth-8 : 0,0), ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar|ImGuiWindowFlags.NoScrollWithMouse))
        {
            DrawDopeSheet(BuildTimelineLanes());
        }
        ImGui.EndChild();
        if (showDetails)
        {
            ImGui.SameLine();
            if (ImGui.BeginChild("SelectionDetails",Vector2.Zero,ImGuiChildFlags.Borders)) DrawInspector();
            ImGui.EndChild();
        }
        if (connected && previousFrame != currentFrame && playback!.Value.FrameRate > 0)
            playback.Value.Source.Seek((float)currentFrame/playback.Value.FrameRate);
        ImGui.PopStyleColor(4); ImGui.PopStyleVar(2); ImGui.PopID();
    }

    private void DrawToolbar(Animator.PlaybackState? playback)
    {
        if (ImGui.Button(showHierarchy ? "Hide hierarchy" : "Show hierarchy")) showHierarchy = !showHierarchy;
        ImGui.SameLine(); ImGui.Checkbox("Snap",ref snappingEnabled);
        ImGui.SameLine(); ImGui.Text($"Frame: {currentFrame:0.##}");
        ImGui.SameLine(); if (ImGui.Button("|<")) currentFrame = motlist.Motclips.Min(c=>c.StartFrame);
        ImGui.SameLine(); if (ImGui.Button("<")) currentFrame = Math.Max(motlist.Motclips.Min(c=>c.StartFrame),currentFrame-1);
        ImGui.SameLine(); if (ImGui.Button(">")) currentFrame = Math.Min(motlist.Motclips.Max(c=>c.EndFrame),currentFrame+1);
        ImGui.SameLine(); if (ImGui.Button(">|")) currentFrame = motlist.Motclips.Max(c=>c.EndFrame);
        ImGui.SameLine(); ImGui.BeginDisabled(playback is null);
        if (ImGui.Button(playback?.IsPlaying == true ? "Pause" : "Play") && playback is { } state) { if (state.IsPlaying) state.Source.Pause(); else state.Source.Play(); }
        ImGui.EndDisabled();
        ImGui.SameLine(); ImGui.Checkbox("Details",ref showDetails);
        ImGui.SameLine(); if (ImGui.Button("Bindings")) ImGui.OpenPopup("TimelineBindings");
        if (ImGui.BeginPopup("TimelineBindings"))
        {
            ImGui.TextUnformatted("Left drag: pan X/Y; click: select\nShift+left drag: reorder within the same owner\nAlt+left click/drag: scrub\nAlt+right drag: move range and its keys\nShift+right drag: extend range / create typed end\nDouble-left below a property box: add a key\nRight click: context actions and copy/paste tracks\nWheel: zoom X/Y; Shift+wheel: X; Ctrl+wheel: Y\nEscape: cancel gesture\nLeft/Right: frame; Up/Down: previous/next key\nHome/End: bounds; K: add; Ctrl+C/V: copy/paste key; Delete: remove");
            ImGui.EndPopup();
        }
    }
}
