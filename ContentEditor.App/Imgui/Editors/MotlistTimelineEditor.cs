using System.Numerics;
using Hexa.NET.ImGui;
using ReeLib;
using ReeLib.Clip;
using ReeLib.Mot;
using ContentEditor.App.Graphics;


namespace ContentEditor.App.ImguiHandling;

public partial class MotlistTimelineEditor
{
    private readonly record struct VisibleLane(string Label, string Path, double Y, TimelineProperty? Property,
        TimelineTrack? Track, Motclip Clip, double StartFrame, double EndFrame, int Depth = 0);
    private readonly record struct TimelineSpan(double StartFrame, double EndFrame);
    private enum EditMode { None, Pan, Playhead, TrackStart, TrackEnd, TrackMove, PropertyStart, PropertyEnd,
        PropertyMove, CreatePropertyRange, PropertyEndpoint, KeySpanMove, ReorderTrack, ReorderProperty, Key }
    private sealed class TimelineDrag
    {
        public EditMode Mode;
        public VisibleLane Lane;
        public TimelineTrack? Track;
        public TimelineProperty? Property;
        public TimelineKey? Key, EndKey;
        public double InitialStart, InitialEnd, InitialFrame, OwnerStart, OwnerEnd;
        public float StartMouseX, StartMouseY, StartPlotWidth, StartPlotHeight, InitialRowHeight;
        public double InitialXMin, InitialXMax, InitialYMin, InitialYMax;
        public TimelineView View = null!;
        public ImGuiMouseButton Button;
        public bool Started, BoundaryKey;
        public List<TimelineTrack>? TrackOrder;
        public List<TimelineProperty>? PropertyOrder;
        public List<TimelineKey> CreatedKeys = new();
        public List<(TimelineKey Key, double Frame)> Keys = new();
        public List<PropertyDragSnapshot> Properties = new();
    }
    private sealed record PropertyDragSnapshot(TimelineProperty Property, double? Start, double? End, List<(TimelineKey Key, double Frame)> Keys);
    private double currentFrame;
    private bool snappingEnabled = true, showHierarchy = true, showInactive = true, isolateTrack, showCurves = true;
    private readonly HashSet<Motclip> collapsedMotclips = new();
    private readonly HashSet<TimelineTrack> expandedTracks = new();
    private readonly HashSet<TimelineProperty> collapsedProperties = new();
    private TimelineDrag? activeDrag;
    private TimelineTrack? selectedTrack, contextTrack, copiedTrack;
    private Motclip? selectedClip, contextClip;
    private TimelineProperty? selectedProperty, contextProperty;
    private TimelineKey? selectedKey, contextKey;
    private double contextFrame;
    private Vector2 contextMouseDownPosition;
    private float hierarchyWidth = 320, inspectorWidth = 300;
    private string timelineFilter = "";
    private readonly Motlist motlist = new();
    private readonly MotlistEditor? editor;
    private readonly Action markModified;
    private readonly Action<Action, Action, string> recordChange;
    private MotFile? motion;
    private ReeLib.Motlist.MotIndex? motionIndex;
    private bool rebindPending, showNative, showDetails;

    public MotlistTimelineEditor(MotlistEditor editor)
    {
        this.editor = editor; markModified = editor.MarkTimelineModified;
        recordChange = (apply, undo, id) => UndoRedo.RecordCallback(null, apply, undo, id);
        InitializeRuntimeDesign();
    }
    internal MotlistTimelineEditor(MotFile motion, IEnumerable<MotClip> clips, Action changed, Action<Action, Action, string> record)
    {
        markModified = changed; recordChange = record; this.motion = motion;
        currentFrame = motion.Header.startFrame; BindMotion(motion, clips); InitializeRuntimeDesign();
    }
    public void Reset()
    {
        CancelGesture(); motion = null; motionIndex = null;
        selectedClip = contextClip = null; selectedTrack = contextTrack = copiedTrack = null;
        selectedProperty = contextProperty = null; selectedKey = contextKey = null;
        rightView = keyboardView = null; motlist.Motclips.Clear();
        clipViews.Clear(); trackViews.Clear(); propertyNodes.Clear(); keyNodes.Clear(); addedTrackData.Clear();
        collapsedMotclips.Clear(); expandedTracks.Clear(); collapsedProperties.Clear();
        overviewView.Initialized = false; overviewView.RowCount = 0;
        memberDrafts.Clear(); numberDrafts.Clear();
        creatingKeyFor = null; creatingPropertyFor = null; draftKey = extendingKey = null;
        openKeyCreator = openPropertyCreator = false;
        openRename=false;renamingTrack=null;renamingProperty=null;creatingPropertyParent=null;
    }
    public void CloseTimelineWindow() => CancelGesture();
    public void CloseInspectorWindow() { }
    private IEnumerable<MotClip> MotionClips() => (motion?.Clips ?? Enumerable.Empty<MotClip>())
        .Concat(motionIndex?.OverrideClips ?? Enumerable.Empty<MotClip>());
    public void Draw(Action drawNativeEditor)
    {
        if (editor is not null)
        {
            ImGui.SetNextItemWidth(Math.Max(180, ImGui.GetContentRegionAvail().X - 120));
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
                    ImGui.PushID(file.GetHashCode());
                    if (ImGui.Selectable($"Unindexed / {file.Name}", ReferenceEquals(file, motion))) ChooseMotion(file, null);
                    ImGui.PopID();
                }
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            if (ImGui.Button(showNative ? "Timeline" : "File data")) { CancelGesture(); showNative = !showNative; rebindPending = true; }
            if (showNative) { drawNativeEditor(); return; }
            var playback = Animator.LastPlaybackState;
            bool connected = playback is { } playing && ReferenceEquals(playing.ActiveMotion, motion);
            ImGui.SameLine(); ImGui.BeginDisabled(!connected);
            if (ImGui.Button(connected && playback!.Value.IsPlaying ? "Pause" : "Play") && playback is { } active)
            { if (active.IsPlaying) active.Source.Pause(); else active.Source.Play(); }
            ImGui.EndDisabled();
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
            // Standalone previews have no editor/MotIndex; retain their supplied clips.
            BindMotion(motion, editor is null ? motlist.Motclips.Select(c => c.Native).ToArray() : MotionClips());
            rebindPending = false;
            if (selectedClip is not null && !motlist.Motclips.Contains(selectedClip)) selectedClip = null;
            if (selectedTrack is not null && !motlist.Motclips.Any(c => c.Tracks.Contains(selectedTrack))) selectedTrack = null;
            if (selectedProperty is not null && (selectedTrack is null || !selectedTrack.AllProperties.Contains(selectedProperty))) selectedProperty = null;
            if (selectedKey is not null && selectedProperty?.Keys.Contains(selectedKey) != true) selectedKey = null;
        }
        DrawWorkspace();
    }
    private void ChooseMotion(MotFile file, ReeLib.Motlist.MotIndex? index)
    {
        Reset(); motion = file; motionIndex = index; BindMotion(file, MotionClips());
        currentFrame = file.Header.startFrame; rebindPending = false;
    }
    internal void DrawWorkspace()
    {
        if (motlist.Motclips.Count == 0) { ImGui.TextDisabled("This motion has no behavior clips."); return; }
        ImGui.PushID("MotlistTimeline");
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5f); ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 5f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10, 6));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(.075f, .079f, .095f, 1));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(.10f, .105f, .13f, 1));
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(.15f, .16f, .195f, 1));
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(.22f, .32f, .49f, 1));
        var playback = Animator.LastPlaybackState;
        bool connected = playback is { } state && ReferenceEquals(state.ActiveMotion, motion);
        if (connected && activeDrag is null && playback!.Value.IsPlaying) currentFrame = playback.Value.CurrentFrame;
        double previousFrame = currentFrame;
        ImGui.BeginDisabled(activeDrag is not null || rightView is not null); DrawDesignedToolbar(); ImGui.EndDisabled();
        bool leftAssets = HasAssets(DesignerTarget.LeftPanel), rightAssets = HasAssets(DesignerTarget.RightPanel);
        float rightAssetsWidth = rightAssets ? Math.Min(design.RightPanelWidth, ImGui.GetContentRegionAvail().X * .25f) : 0;
        if (leftAssets) { DrawAssetPanel(DesignerTarget.LeftPanel, Math.Min(design.LeftPanelWidth, ImGui.GetContentRegionAvail().X * .25f)); ImGui.SameLine(); }
        float detailsWidth = showDetails ? Math.Clamp(inspectorWidth,Math.Min(220,ImGui.GetContentRegionAvail().X*.3f),Math.Max(220,ImGui.GetContentRegionAvail().X*.4f)) : 0;
        float bodyHeight=ImGui.GetContentRegionAvail().Y;
        if (ImGui.BeginChild("TimelineCanvasHost", new Vector2(-(detailsWidth + rightAssetsWidth + (showDetails ? 6 : 0) + (rightAssets ? 8 : 0)), 0), ImGuiChildFlags.Borders,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            DrawTimelineCanvas();
            DrawAssetSurface(DesignerTarget.TimelineOverlay, canvasMin, canvasMax - canvasMin);
        }
        ImGui.EndChild();
        if (showDetails)
        {
            ImGui.SameLine(0,0);ImGui.InvisibleButton("##InspectorWidth",new Vector2(6,bodyHeight));
            if(ImGui.IsItemActive())inspectorWidth=Math.Clamp(detailsWidth-ImGui.GetIO().MouseDelta.X,220,600);
            if(ImGui.IsItemActive()||ImGui.IsItemHovered())ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
            ImGui.SameLine(0,0);
            if (ImGui.BeginChild("SelectionDetails", new Vector2(rightAssets ? -(rightAssetsWidth + 8) : 0, 0), ImGuiChildFlags.Borders)) DrawInspector();
            ImGui.EndChild();
        }
        if (rightAssets) { ImGui.SameLine(); DrawAssetPanel(DesignerTarget.RightPanel, rightAssetsWidth); }
        DispatchFrameChanged();
        DrawCreationPopups();
        DrawRuntimeWindows();
        if (connected && previousFrame != currentFrame && playback!.Value.FrameRate > 0)
            playback.Value.Source.Seek((float)currentFrame / playback.Value.FrameRate);
        ImGui.PopStyleColor(4); ImGui.PopStyleVar(3); ImGui.PopID();
    }


}

