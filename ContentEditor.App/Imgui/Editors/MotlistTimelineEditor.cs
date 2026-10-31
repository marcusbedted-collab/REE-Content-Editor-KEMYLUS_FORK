using System.Globalization;
using System.Numerics;
using ContentEditor.App.Graphics;
using ContentEditor.App.Windowing;
using ContentEditor.Core;
using ReeLib;
using ReeLib.Clip;
using ReeLib.Mot;
using ReeLib.Motlist;

namespace ContentEditor.App.ImguiHandling;

/// <summary>
/// Interval timeline for Motlist behavior clips. This renderer intentionally owns
/// its layout and interaction model instead of sharing UI with the native editor.
/// </summary>
internal sealed partial class MotlistTimelineEditor
{
    private const float TrackColumnWidth = 260f;
    private const float RulerHeight = 34f;
    private const float LaneHeight = 64f;
    private const float BlockTop = 5f;
    private const float BlockBottom = 39f;
    private const float KeyRowCenter = 53f;
    private const float HandleWidth = 7f;
    private const float MinimumClipWidth = 10f;
    private const float CanvasInset = 8f;
    private const float MinimumPixelsPerFrame = 1f;
    private const float MaximumPixelsPerFrame = 25f;
    private const float MaximumCanvasWidth = 100_000f;
    private const float DisabledFrame = -1f;

    private static readonly Vector4 CanvasColor = new(0.025f, 0.029f, 0.034f, 1f);
    private static readonly Vector4 LaneColor = new(0.035f, 0.041f, 0.048f, 1f);
    private static readonly Vector4 AlternateLaneColor = new(0.055f, 0.059f, 0.064f, 1f);
    private static readonly Vector4 GridColor = new(0.16f, 0.20f, 0.24f, 0.72f);
    private static readonly Vector4 TextColor = new(0.84f, 0.87f, 0.91f, 1f);
    private static readonly Vector4 MutedTextColor = new(0.52f, 0.57f, 0.64f, 1f);
    private static readonly Vector4 DisabledColor = new(0.42f, 0.45f, 0.49f, 1f);
    private static readonly Vector4 SelectedColor = new(1f, 0.76f, 0.18f, 1f);
    private static readonly Vector4 PlayheadColor = new(1f, 0.72f, 0.05f, 1f);
    private static readonly Vector4 ColliderColor = new(0.95f, 0.33f, 0.25f, 1f);
    private static readonly Vector4 PlayerColor = new(0.23f, 0.67f, 1f, 1f);
    private static readonly Vector4 EventColor = new(0.96f, 0.55f, 0.20f, 1f);
    private static readonly Vector4 VfxColor = new(0.56f, 0.35f, 0.91f, 1f);
    private static readonly Vector4 DefaultColor = new(0.24f, 0.74f, 0.76f, 1f);
    private static readonly Vector4[] PropertyColors = [
        new(0.35f, 0.80f, 1f, 1f), new(0.55f, 0.85f, 0.40f, 1f),
        new(1f, 0.62f, 0.36f, 1f), new(0.75f, 0.60f, 1f, 1f),
        new(1f, 0.50f, 0.65f, 1f), new(0.35f, 0.85f, 0.72f, 1f),
        new(0.95f, 0.80f, 0.35f, 1f), new(0.65f, 0.75f, 1f, 1f),
    ];

    private readonly MotlistEditor editor;

    private bool showNativeEditor;
    private bool showDopeSheet;
    private bool detachedDopeSheet;
    private float masterSplit = 0.45f;
    private readonly HashSet<MotClip> collapsedClips = new();
    private bool fitTimeline = true;
    private bool followPlayback = true;
    private bool localPlaying;
    private int selectedMotionIndex = -1;
    private int selectedClipIndex;
    private int currentFrame;
    private float pixelsPerFrame = 5f;
    private float fittedPixelsPerFrame = 5f;
    private MotFile? lastPlaybackMotion;
    private float localFrameRemainder;

    private ClipTrack? selectedTrack;
    private Property? selectedProperty;
    private KeyBase? selectedKey;
    private DragState? drag;

    private WindowData? inspectorWindowData;
    private MotlistKeyInspectorWindow? inspectorWindow;

    private enum DragMode
    {
        MoveRange,
        ResizeStart,
        ResizeEnd,
        ResizeTrackStart,
        ResizeTrackEnd,
        MoveKeys,
    }

    private sealed record TrackLane(ClipTrack Track, int Depth);
    private sealed record RangeGroup(float Start, float End, List<Property> Properties, string Label, Vector4 Color);
    private sealed record KeyGroup(float Frame, List<(Property Property, KeyBase Key)> Keys);
    private sealed record PropertySnapshot(Property Property, float Start, float End, List<(KeyBase Key, float Frame)> Keys);
    private sealed record TrackSnapshot(
        ClipTrack Track,
        float Start,
        float End,
        List<PropertySnapshot> BoundaryProperties,
        List<(KeyBase Key, float Frame)> BoundaryKeys);

    private sealed class DragState
    {
        public required DragMode Mode { get; init; }
        public required float MouseStartX { get; init; }
        public required float PixelsPerFrame { get; init; }
        public required float MinimumFrame { get; init; }
        public required float MaximumFrame { get; init; }
        public float RangeStart { get; init; }
        public float RangeEnd { get; init; }
        public TrackSnapshot? Track { get; init; }
        public bool MoveBoundaryKeys { get; init; }
        public List<PropertySnapshot> Properties { get; init; } = [];
        public List<(Property Property, KeyBase Key, float Frame)> Keys { get; init; } = [];
        public bool Changed { get; set; }
        public float LastDelta { get; set; }
    }

    public MotlistTimelineEditor(MotlistEditor editor)
    {
        this.editor = editor;
    }

    public void Reset()
    {
        showDopeSheet = false;
        collapsedClips.Clear();
        revealedProperty = null;
        selectedMotionIndex = -1;
        selectedClipIndex = 0;
        currentFrame = 0;
        selectedTrack = null;
        selectedProperty = null;
        selectedKey = null;
        drag = null;
        fitTimeline = true;
        localPlaying = false;
        localFrameRemainder = 0f;
        lastPlaybackMotion = null;
        valueContexts.Clear();
    }

    public void Draw(Action drawNativeEditor)
    {
        if (showNativeEditor) {
            if (ImGui.SmallButton("Timeline")) showNativeEditor = false;
            ImGui.Separator();
            drawNativeEditor();
            return;
        }

        PushTimelineStyle();
        try { DrawTimeline(); }
        finally { PopTimelineStyle(); }
    }

    private static void PushTimelineStyle()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f * UI.UIScale);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(7f, 4f) * UI.UIScale);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.09f, 0.11f, 0.14f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.12f, 0.16f, 0.20f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.18f, 0.27f, 0.34f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.13f, 0.22f, 0.29f, 1f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.18f, 0.29f, 0.36f, 1f));
    }

    private static void PopTimelineStyle()
    {
        ImGui.PopStyleColor(5);
        ImGui.PopStyleVar(2);
    }

    private void DrawTimeline()
    {
        SynchronizeMotionSelectionFromPlayback();
        EnsureMotionSelection();
        UpdateDrag();

        if (!TryGetSelectedMotion(out var motion, out var motFile)) {
            if (ImGui.Button("Native")) showNativeEditor = true;
            ImGui.TextDisabled("No embedded motions are available.");
            return;
        }

        var clips = GetClips(motion, motFile);
        if (clips.Count == 0) {
            DrawContextSelectors(motion, motFile);
            if (ImGui.Button("Native")) showNativeEditor = true;
            ImGui.TextDisabled("The selected motion has no behavior clips.");
            return;
        }

        selectedClipIndex = Math.Clamp(selectedClipIndex, 0, clips.Count - 1);
        var lanes = clips.SelectMany(item => FlattenTracks(item.ClipEntry?.Tracks)).ToList();
        var (minimumFrame, maximumFrame) = GetTimelineRange(lanes, motFile);

        var synchronized = TryGetSynchronizedPlayback(motFile, out var playback);
        UpdatePlayback(minimumFrame, maximumFrame, synchronized, playback, motFile.Header.FrameRate);
        var previousSelection = (selectedMotionIndex, selectedClipIndex);
        DrawTimelineToolbar(motion, motFile, minimumFrame, maximumFrame, synchronized, playback);
        if (previousSelection != (selectedMotionIndex, selectedClipIndex)) return;
        if (selectedTrack != null && !lanes.Any(lane => ReferenceEquals(lane.Track, selectedTrack))) {
            selectedTrack = null;
            selectedProperty = null;
            selectedKey = null;
            valueContexts.Clear();
        }
        DrawSelectedBar();

        if (lanes.Count == 0) {
            ImGui.TextDisabled("The selected behavior clip has no timeline tracks.");
            return;
        }

        var availableHeight = MathF.Max(1f, ImGui.GetContentRegionAvail().Y);
        var inlineSheet = showDopeSheet && !detachedDopeSheet && selectedTrack != null;
        DrawTimelineTable(clips, minimumFrame, maximumFrame, motFile,
            inlineSheet ? availableHeight * masterSplit : availableHeight);
        if (inlineSheet) {
            var dividerHeight = 6f * UI.UIScale;
            AppImguiHelpers.InvisibleButtonSafe("##master_split", new Vector2(MathF.Max(1f, ImGui.GetContentRegionAvail().X), dividerHeight), ImGuiButtonFlags.MouseButtonLeft);
            if (ImGui.IsItemHovered() || ImGui.IsItemActive()) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNs);
            if (ImGui.IsItemActive()) masterSplit = Math.Clamp(masterSplit + ImGui.GetIO().MouseDelta.Y / availableHeight, 0.2f, 0.7f);
            ImGui.GetWindowDrawList().AddRectFilled(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), ImGui.GetColorU32(GridColor));
            DrawInspector();
        }
    }

    private void DrawContextSelectors(MotIndex motion, MotFile motFile)
    {
        var width = MathF.Max(1f, (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f);
        ImGui.SetNextItemWidth(width);
        if (ImGui.BeginCombo("##motlist_interval_motion", GetMotionName(motion, selectedMotionIndex))) {
            for (var index = 0; index < editor.File.Motions.Count; index++) {
                var candidate = editor.File.Motions[index];
                if (candidate.MotFile is not MotFile) continue;
                if (ImGui.Selectable(GetMotionName(candidate, index) + $"##motion{index}", index == selectedMotionIndex)) {
                    SelectMotion(index);
                }
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(width);
        var clips = GetClips(motion, motFile);
        var clipLabel = clips.Count == 0 ? "No clips" : GetClipName(clips[Math.Clamp(selectedClipIndex, 0, clips.Count - 1)], selectedClipIndex);
        if (ImGui.BeginCombo("##motlist_interval_clip", clipLabel)) {
            for (var index = 0; index < clips.Count; index++) {
                var label = GetClipName(clips[index], index) + (motion.OverrideClips.Contains(clips[index]) ? " (override)" : string.Empty);
                if (ImGui.Selectable(label, index == selectedClipIndex)) {
                    SelectClip(index);
                }
            }
            ImGui.EndCombo();
        }
    }

    private void DrawTimelineToolbar(MotIndex motion, MotFile motFile, int minimumFrame, int maximumFrame, bool synchronized, Animator.PlaybackState playback)
    {
        var scale = UI.UIScale;
        var source = synchronized ? playback.Source : null;
        var playing = synchronized ? playback.IsPlaying : localPlaying;
        ImGui.TextColored(DefaultColor, "MOTION TIMELINE");
        NextControl(34f);
        if (ImGui.Button($"{(playing ? AppIcons.Pause : AppIcons.Play)}##timeline_play", new Vector2(34f, 0f) * scale)) {
            if (source != null) {
                if (playing) source.Pause(); else source.Play();
            } else localPlaying = !localPlaying;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(playing ? "Pause" : "Play");
        NextControl(34f);
        if (ImGui.Button($"{AppIcons.SeekStart}##timeline_start", new Vector2(34f, 0f) * scale))
            SetFrame(minimumFrame, minimumFrame, maximumFrame, source);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("First frame");
        NextControl(116f);
        ImGui.SetNextItemWidth(116f * scale);
        var frame = currentFrame;
        if (ImGui.SliderInt("##motlist_interval_frame", ref frame, minimumFrame, maximumFrame, $"Frame {frame}"))
            SetFrame(frame, minimumFrame, maximumFrame, source);
        NextControl(132f);
        ImGui.SetNextItemWidth(132f * scale);
        var zoom = fitTimeline ? fittedPixelsPerFrame : pixelsPerFrame;
        if (ImGui.SliderFloat("##motlist_interval_scale", ref zoom, MinimumPixelsPerFrame, MaximumPixelsPerFrame, "%.1f px/f")) {
            pixelsPerFrame = zoom;
            fitTimeline = false;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Timeline zoom");
        NextControl(55f);
        ImGui.Checkbox("Fit", ref fitTimeline);
        NextControl(85f);
        ImGui.Checkbox("Follow", ref followPlayback);
        NextControl(100f);
        if (ImGui.Button("Dope Sheet")) OpenInspectorWindow();
        NextControl(90f);
        if (ImGui.Checkbox("Detached", ref detachedDopeSheet)) {
            if (detachedDopeSheet && showDopeSheet) OpenInspectorWindow();
            else CloseInspectorWindow();
        }
        NextControl(65f);
        if (ImGui.SmallButton("Native")) showNativeEditor = true;
        DrawContextSelectors(motion, motFile);
    }

    private static void NextControl(float width)
    {
        ImGui.SameLine();
        if (ImGui.GetContentRegionAvail().X < width * UI.UIScale) ImGui.NewLine();
    }

    private void DrawSelectedBar()
    {
        var path = selectedTrack == null ? "Select a track to edit its properties and keys"
            : GetTrackName(selectedTrack) + (selectedProperty == null ? "" : "  /  " + GetPropertyName(selectedProperty));
        ImGui.TextColored(MutedTextColor, FitText(path, ImGui.GetContentRegionAvail().X));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(path);
    }

    private void DrawTimelineTable(List<MotClip> clips, int minimumFrame, int maximumFrame, MotFile motFile, float height)
    {
        if (!BeginTimelineTable("motlist_interval_timeline", "BEHAVIOR CLIPS", clips.Count, minimumFrame, maximumFrame,
            motFile, height, out var width, out var pixels)) return;

        var laneIndex = 0;
        for (var clipIndex = 0; clipIndex < clips.Count; clipIndex++) {
            var clip = clips[clipIndex];
            var lanes = FlattenTracks(clip.ClipEntry?.Tracks);
            ImGui.PushID(clipIndex);
            if (DrawClipHeader(clip, clipIndex, lanes.Count, lanes.Sum(lane => CountProperties(lane.Track.Properties)))) {
                for (var index = 0; index < lanes.Count; index++) {
                    ImGui.PushID(index);
                    ImGui.TableNextRow(ImGuiTableRowFlags.None, LaneHeight * UI.UIScale);
                    ImGui.TableSetColumnIndex(0);
                    DrawTrackName(lanes[index], clipIndex);
                    ImGui.TableSetColumnIndex(1);
                    DrawLane(lanes[index].Track, laneIndex++, minimumFrame, maximumFrame, width, pixels, motFile, clipIndex: clipIndex);
                    ImGui.PopID();
                }
            }
            ImGui.PopID();
        }
        ImGui.EndTable();
        ImGui.PopStyleVar();
    }

    private bool DrawClipHeader(MotClip clip, int index, int trackCount, int propertyCount)
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.None, 38f * UI.UIScale);
        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(new Vector4(0.13f, 0.14f, 0.15f, 1f)));
        ImGui.TableSetColumnIndex(0);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 4f * UI.UIScale);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(8f, 6f) * UI.UIScale);
        ImGui.PushStyleColor(ImGuiCol.Text, TextColor);
        ImGui.SetNextItemOpen(!collapsedClips.Contains(clip), ImGuiCond.Always);
        var flags = ImGuiTreeNodeFlags.Framed | ImGuiTreeNodeFlags.SpanAvailWidth
            | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick;
        if (index == selectedClipIndex) flags |= ImGuiTreeNodeFlags.Selected;
        var name = FitText(GetClipName(clip, index), MathF.Max(1f, ImGui.GetContentRegionAvail().X - 38f * UI.UIScale));
        var open = ImGui.TreeNodeEx(name + "###behavior_clip", flags);
        if (open) collapsedClips.Remove(clip); else collapsedClips.Add(clip);
        if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen()) SelectClip(index);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(clip.MainTrackName ?? "Behavior clip");
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
        ImGui.TableSetColumnIndex(1);
        var cursor = ImGui.GetCursorScreenPos();
        var clipRect = ImGui.GetWindowDrawList().CmdHeader.ClipRect;
        ImGui.SetCursorScreenPos(new Vector2(MathF.Max(cursor.X, clipRect.X) + 10f * UI.UIScale, cursor.Y + 10f * UI.UIScale));
        ImGui.TextDisabled(FitText($"{trackCount} tracks  /  {propertyCount} properties", clipRect.Z - ImGui.GetCursorScreenPos().X - 8f * UI.UIScale));
        return open;
    }

    private bool BeginTimelineTable(string id, string title, int count, int minimumFrame, int maximumFrame,
        MotFile motFile, float height, out float width, out float pixels)
    {
        var available = ImGui.GetContentRegionAvail();
        var labelWidth = MathF.Min(TrackColumnWidth * UI.UIScale, available.X * 0.32f);
        var viewport = MathF.Max(1f, available.X - labelWidth - ImGui.GetStyle().ScrollbarSize - 2f);
        var span = Math.Max(1, maximumFrame - minimumFrame);
        var fit = id == "motlist_dope_sheet" ? dopeFit : fitTimeline;
        var zoom = id == "motlist_dope_sheet" ? dopeZoom : pixelsPerFrame;
        width = fit ? viewport : MathF.Max(viewport, MathF.Min(MaximumCanvasWidth * UI.UIScale, span * zoom * UI.UIScale));
        pixels = MathF.Max(1f, width - CanvasInset * 2f * UI.UIScale) / span;
        if (id == "motlist_interval_timeline") fittedPixelsPerFrame = pixels / UI.UIScale;
        else dopeFittedZoom = pixels / UI.UIScale;
        var flags = ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV
            | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings
            | ImGuiTableFlags.NoPadOuterX | ImGuiTableFlags.NoPadInnerX;

        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, Vector2.Zero);
        if (!ImGui.BeginTable(id, 2, flags, new Vector2(0f, MathF.Max(1f, height)))) {
            ImGui.PopStyleVar();
            return false;
        }
        ImGui.TableSetupScrollFreeze(1, 1);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, labelWidth);
        ImGui.TableSetupColumn("Frames", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, width);
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers, RulerHeight * UI.UIScale);
        ImGui.TableSetColumnIndex(0);
        ImGui.SetCursorPos(ImGui.GetCursorPos() + new Vector2(8f, 7f) * UI.UIScale);
        ImGui.TextUnformatted(FitText($"{title} ({count})", labelWidth - 16f * UI.UIScale));
        ImGui.TableSetColumnIndex(1);
        DrawRuler(minimumFrame, maximumFrame, width, motFile);
        FollowPlayhead(minimumFrame, maximumFrame, width, fit);
        return true;
    }

    private void DrawTrackName(TrackLane lane, int clipIndex)
    {
        var scale = UI.UIScale;
        var position = ImGui.GetCursorScreenPos();
        var size = new Vector2(MathF.Max(1f, ImGui.GetContentRegionAvail().X), LaneHeight * scale);
        var selected = ReferenceEquals(lane.Track, selectedTrack);

        AppImguiHelpers.InvisibleButtonSafe("##track", size, ImGuiButtonFlags.MouseButtonLeft);
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(position, position + size, ImGui.GetColorU32(selected
            ? new Vector4(0.20f, 0.21f, 0.22f, 1f)
            : hovered ? new Vector4(0.17f, 0.18f, 0.19f, 1f) : AlternateLaneColor));

        var indent = Math.Min(5, lane.Depth) * 9f * scale;
        var textX = position.X + 7f * scale + indent;
        var width = MathF.Max(20f, size.X - textX + position.X - 8f * scale);
        draw.AddText(new Vector2(textX, position.Y + 6f * scale), ImGui.GetColorU32(TextColor), FitText(CompactName(GetTrackName(lane.Track)), width));
        var activeCount = CountActiveProperties(lane.Track.Properties);
        var inactiveCount = CountProperties(lane.Track.Properties) - activeCount;
        var summary = $"{activeCount} active / {inactiveCount} inactive  |  {lane.Track.startFrame:0.###} : {lane.Track.endFrame:0.###}";
        draw.AddText(new Vector2(textX, position.Y + 27f * scale), ImGui.GetColorU32(new Vector4(0.50f, 0.63f, 0.76f, 1f)), FitText(summary, width));

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) {
            if (selectedClipIndex != clipIndex) SelectClip(clipIndex);
            SelectTrack(lane.Track);
            OpenInspectorWindow();
        }
        if (hovered) ImGui.SetTooltip(GetTrackName(lane.Track));
    }

    private void DrawRuler(int minimumFrame, int maximumFrame, float width, MotFile motFile)
    {
        var position = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, RulerHeight * UI.UIScale);
        AppImguiHelpers.InvisibleButtonSafe("##ruler", size, ImGuiButtonFlags.MouseButtonLeft);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(position, position + size, ImGui.GetColorU32(CanvasColor));
        DrawGrid(draw, position, minimumFrame, maximumFrame, width, size.Y, true);
        DrawPlayhead(draw, position, minimumFrame, maximumFrame, width, size.Y);

        if (ImGui.IsItemHovered() && ImGui.IsMouseDown(ImGuiMouseButton.Left)) {
            TryGetSynchronizedPlayback(motFile, out var playback);
            SetFrame(XToFrame(ImGui.GetMousePos().X - position.X, minimumFrame, maximumFrame, width), minimumFrame, maximumFrame, playback.Source);
        }
    }

    // Ranges and keys occupy separate vertical hit areas, including at shared endpoints.
    private void DrawLane(ClipTrack track, int laneIndex, int minimumFrame, int maximumFrame,
        float width, float pixelsOnCanvas, MotFile motFile, Property? property = null, int clipIndex = -1)
    {
        var scale = UI.UIScale;
        var position = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, (property == null ? LaneHeight : GetPropertyLaneHeight(property)) * scale);
        AppImguiHelpers.InvisibleButtonSafe("##lane", size, ImGuiButtonFlags.MouseButtonLeft);
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(position, position + size, ImGui.GetColorU32(laneIndex % 2 == 0 ? LaneColor : CanvasColor));
        DrawGrid(draw, position, minimumFrame, maximumFrame, width, size.Y, false);
        var ranges = property == null ? new List<RangeGroup>()
            : IsValidRange(property) ? new List<RangeGroup> { new(property.Info.startFrame, property.Info.endFrame,
                new List<Property> { property }, BuildRangeLabel(new List<Property> { property }, property.Info.startFrame, property.Info.endFrame), GetPropertyColor(property)) } : [];
        var groups = BuildKeyGroups(property == null ? EnumerateKeys(track.Properties)
            : IsValidRange(property) ? (property.Keys ?? []).Select(key => (property, key)) : []);
        RangeGroup? hoveredRange = null;
        KeyGroup? hoveredKeys = null;
        var hoveredTrackEdge = 0;

        if (property == null && TryGetEditableTrackBounds(track, minimumFrame, maximumFrame, out var trackBounds)) {
            var trackStartX = position.X + FrameToX(trackBounds.Minimum, minimumFrame, maximumFrame, width);
            var trackEndX = position.X + FrameToX(trackBounds.Maximum, minimumFrame, maximumFrame, width);
            var trackColor = GetTrackColor(track);
            var trackMin = new Vector2(trackStartX, position.Y + BlockTop * scale);
            var trackMax = new Vector2(MathF.Max(trackStartX + MinimumClipWidth * scale, trackEndX), position.Y + BlockBottom * scale);
            draw.AddRectFilled(trackMin, trackMax, ImGui.GetColorU32(new Vector4(trackColor.X, trackColor.Y, trackColor.Z, 0.65f)), 4f * scale);
            draw.AddRect(trackMin, trackMax, ImGui.GetColorU32(ReferenceEquals(track, selectedTrack) ? SelectedColor : trackColor), 4f * scale);
            var trackLabel = $"{CompactName(GetTrackName(track))}  [{track.startFrame:0.###} - {track.endFrame:0.###}]";
            DrawBlockLabel(draw, trackMin, trackMax, trackLabel);

            draw.AddRectFilled(new Vector2(trackStartX - HandleWidth * scale * 0.5f, position.Y + 6f * scale),
                new Vector2(trackStartX + HandleWidth * scale * 0.5f, position.Y + (BlockBottom - 1f) * scale), ImGui.GetColorU32(trackColor));
            draw.AddRectFilled(new Vector2(trackEndX - HandleWidth * scale * 0.5f, position.Y + 6f * scale),
                new Vector2(trackEndX + HandleWidth * scale * 0.5f, position.Y + (BlockBottom - 1f) * scale), ImGui.GetColorU32(trackColor));

            var startHit = new Vector2(trackStartX - HandleWidth * scale, position.Y);
            var endHit = new Vector2(trackEndX - HandleWidth * scale, position.Y);
            var hitSize = new Vector2(HandleWidth * scale * 2f, BlockBottom * scale);
            if (hovered && ImGui.IsMouseHoveringRect(startHit, startHit + hitSize)) hoveredTrackEdge = -1;
            if (hovered && ImGui.IsMouseHoveringRect(endHit, endHit + hitSize)) hoveredTrackEdge = 1;
            if (hoveredTrackEdge != 0) {
                ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
                ImGui.SetTooltip("Shift: move keys at this track boundary");
            }
        }

        if (property == null && !TryGetEditableTrackBounds(track, minimumFrame, maximumFrame, out _)) {
            var status = IsDisabledRange(track.startFrame, track.endFrame) ? "Inactive track [-1, -1]"
                : track.Version >= ClipVersion.RE8 ? "Property timing (no editable track range)"
                : $"Track range [{track.startFrame:0.###} - {track.endFrame:0.###}]";
            draw.AddText(position + new Vector2(8f, 7f) * scale, ImGui.GetColorU32(MutedTextColor), status);
        }
        foreach (var range in ranges) {
            var x = position.X + FrameToX(range.Start, minimumFrame, maximumFrame, width);
            var endX = position.X + FrameToX(range.End, minimumFrame, maximumFrame, width);
            var min = new Vector2(x, position.Y + BlockTop * scale);
            var max = new Vector2(MathF.Max(x + MinimumClipWidth * scale, endX), position.Y + BlockBottom * scale);
            var selected = selectedProperty != null && range.Properties.Contains(selectedProperty);
            var fill = range.Color;
            fill.W = selected ? 0.78f : 0.60f;
            draw.AddRectFilled(min, max, ImGui.GetColorU32(fill), 3f * scale);
            draw.AddRect(min, max, ImGui.GetColorU32(selected ? SelectedColor : range.Color), 3f * scale, ImDrawFlags.RoundCornersAll, selected ? 2f * scale : scale);
            if (range.Properties.Count == 1) {
                var handle = MathF.Min(HandleWidth * scale, (max.X - min.X) * 0.5f);
                draw.AddRectFilled(min, new Vector2(min.X + handle, max.Y), ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.35f)));
                draw.AddRectFilled(new Vector2(max.X - handle, min.Y), max, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.35f)));
            }
            DrawBlockLabel(draw, min, max, range.Label);
            if (hovered && ImGui.IsMouseHoveringRect(min, max)) {
                hoveredRange = range;
                var edge = ImGui.GetMousePos().X <= min.X + HandleWidth * scale || ImGui.GetMousePos().X >= max.X - HandleWidth * scale;
                ImGui.SetMouseCursor(range.Properties.Count > 1 ? ImGuiMouseCursor.Hand : edge ? ImGuiMouseCursor.ResizeEw : ImGuiMouseCursor.ResizeAll);
                ShowRangeTooltip(track, range);
            }
        }
        var clip = draw.CmdHeader.ClipRect;
        var labelRight = MathF.Max(position.X, clip.X) + 3f * scale;
        for (var i = 0; i < groups.Count; i++) {
            var group = groups[i];
            var x = position.X + FrameToX(group.Frame, minimumFrame, maximumFrame, width);
            if (x < clip.X - 8f * scale || x > clip.Z + 8f * scale) continue;
            var center = new Vector2(x, position.Y + KeyRowCenter * scale);
            var min = center - new Vector2(8f, 8f) * scale;
            var max = center + new Vector2(8f, 8f) * scale;
            var selected = group.Keys.Any(item => ReferenceEquals(item.Key, selectedKey));
            var keyHovered = hovered && ImGui.IsMouseHoveringRect(min, max);
            var color = group.Keys.All(item => ReferenceEquals(item.Property, group.Keys[0].Property))
                ? GetPropertyColor(group.Keys[0].Property) : TextColor;
            DrawKeyDiamond(draw, center, color, selected, keyHovered, group.Keys.Count > 1);
            if (property != null) {
                var nextX = i + 1 < groups.Count ? position.X + FrameToX(groups[i + 1].Frame, minimumFrame, maximumFrame, width) : position.X + width;
                var previousX = i > 0 ? position.X + FrameToX(groups[i - 1].Frame, minimumFrame, maximumFrame, width) : position.X;
                var text = string.Join(", ", group.Keys.Select(item => FormatKeyValue(item.Key)));
                var rightSpace = MathF.Min(nextX - 10f * scale, clip.Z - 3f * scale) - x - 11f * scale;
                var leftSpace = x - 11f * scale - MathF.Max(labelRight, previousX + 10f * scale);
                var useLeft = ImGui.CalcTextSize(text).X > rightSpace && leftSpace > rightSpace;
                text = FitText(text, useLeft ? leftSpace : rightSpace);
                if (text.Length > 0) {
                    var textWidth = ImGui.CalcTextSize(text).X;
                    var textX = useLeft ? x - 11f * scale - textWidth : x + 11f * scale;
                    draw.AddText(new Vector2(textX, center.Y - ImGui.GetTextLineHeight() * 0.5f), ImGui.GetColorU32(TextColor), text);
                    labelRight = textX + textWidth + 4f * scale;
                }
            }
            if (keyHovered) {
                hoveredKeys = group;
                ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
                ShowKeyTooltip(track, group);
            }
        }
        if (property != null && !IsValidRange(property) && !IsDisabled(property))
            draw.AddText(position + new Vector2(8f, 7f) * scale, ImGui.GetColorU32(DisabledColor), "Invalid range");
        DrawPlayhead(draw, position, minimumFrame, maximumFrame, width, size.Y);

        if (!hovered || !ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return;
        if (clipIndex >= 0 && selectedClipIndex != clipIndex) SelectClip(clipIndex);
        SelectTrack(track);
        if (property == null) OpenInspectorWindow();
        if (hoveredTrackEdge != 0 && property == null) {
            BeginTrackEdgeDrag(track, minimumFrame, maximumFrame, pixelsOnCanvas, hoveredTrackEdge < 0, ImGui.GetIO().KeyShift);
            return;
        }
        if (property != null) selectedProperty = property;
        if (hoveredKeys != null) {
            var shift = ImGui.GetIO().KeyShift;
            if (property != null) selectedKey = hoveredKeys.Keys[0].Key;
            if (shift || hoveredKeys.Keys.Count == 1 || hoveredKeys.Keys.Any(item => ReferenceEquals(item.Key, selectedKey))) {
                var stack = shift ? BuildKeyGroups(EnumerateKeys(track.Properties)).First(group => group.Frame == hoveredKeys.Frame) : hoveredKeys;
                BeginKeyDrag(track, stack, minimumFrame, maximumFrame, pixelsOnCanvas, shift);
            } else {
                selectedProperty = hoveredKeys.Keys[0].Property;
                selectedKey = null;
                OpenInspectorWindow();
            }
        } else if (hoveredRange != null) {
            selectedProperty = hoveredRange.Properties[0];
            selectedKey = null;
            if (hoveredRange.Properties.Count == 1) BeginRangeDrag(track, hoveredRange, minimumFrame, maximumFrame, pixelsOnCanvas, position.X + CanvasInset * scale);
            else OpenInspectorWindow();
        } else if (property == null || IsValidRange(property)) {
            TryGetSynchronizedPlayback(motFile, out var playback);
            SetFrame(XToFrame(ImGui.GetMousePos().X - position.X, minimumFrame, maximumFrame, width), minimumFrame, maximumFrame, playback.Source);
        }
    }

    private static void DrawBlockLabel(ImDrawListPtr draw, Vector2 min, Vector2 max, string label)
    {
        // Keep the name on the visible part of a zoomed interval, clear of its handles.
        var left = MathF.Max(min.X + 10f * UI.UIScale, draw.CmdHeader.ClipRect.X + 5f * UI.UIScale);
        var right = MathF.Min(max.X - 10f * UI.UIScale, draw.CmdHeader.ClipRect.Z - 5f * UI.UIScale);
        var text = FitText(label, right - left);
        if (text.Length == 0) return;
        draw.AddText(new Vector2(left, (min.Y + max.Y - ImGui.GetTextLineHeight()) * 0.5f), ImGui.GetColorU32(Vector4.One), text);
    }

    private static Vector4 GetPropertyColor(Property property) =>
        PropertyColors[ReeLib.Common.MurMur3HashUtils.GetAsciiHash(GetPropertyName(property)) % (uint)PropertyColors.Length];

    private static void DrawKeyDiamond(ImDrawListPtr draw, Vector2 center, Vector4 color, bool selected, bool hovered, bool stacked)
    {
        var radius = 6f * UI.UIScale;
        void Diamond(Vector2 point, uint fill, uint outline, float thickness)
        {
            var top = point - new Vector2(0f, radius);
            var right = point + new Vector2(radius, 0f);
            var bottom = point + new Vector2(0f, radius);
            var left = point - new Vector2(radius, 0f);
            draw.AddQuadFilled(top, right, bottom, left, fill);
            draw.AddQuad(top, right, bottom, left, outline, thickness);
        }
        if (stacked) Diamond(center + new Vector2(2f, -2f) * UI.UIScale, ImGui.GetColorU32(CanvasColor), ImGui.GetColorU32(color), UI.UIScale);
        Diamond(center, ImGui.GetColorU32(color), ImGui.GetColorU32(selected ? SelectedColor : hovered ? Vector4.One : CanvasColor),
            (selected || hovered ? 2f : 1f) * UI.UIScale);
    }

    private void UpdatePlayback(int minimumFrame, int maximumFrame, bool synchronized, Animator.PlaybackState playback, int frameRate)
    {
        if (synchronized) {
            currentFrame = Math.Clamp(playback.CurrentFrame, minimumFrame, maximumFrame);
            localPlaying = false;
            return;
        }

        if (!localPlaying) return;
        var fps = frameRate > 0 ? frameRate : 60;
        localFrameRemainder += ImGui.GetIO().DeltaTime * fps;
        var advance = (int)localFrameRemainder;
        if (advance <= 0) return;
        localFrameRemainder -= advance;
        currentFrame += advance;
        if (currentFrame > maximumFrame) currentFrame = minimumFrame + (currentFrame - minimumFrame) % (maximumFrame - minimumFrame + 1);
    }

    private void SetFrame(int frame, int minimumFrame, int maximumFrame, Animator? source)
    {
        currentFrame = Math.Clamp(frame, minimumFrame, maximumFrame);
        localFrameRemainder = 0f;
        if (source is { FrameRate: > 0f }) source.Seek(currentFrame / source.FrameRate);
    }

    private void FollowPlayhead(int minimumFrame, int maximumFrame, float canvasWidth, bool fit)
    {
        if (drag != null) return;
        if (fit) { ImGui.SetScrollX(0f); return; }
        if (!followPlayback || (!localPlaying && Animator.LastPlaybackState is not { IsPlaying: true })) return;
        var x = FrameToX(currentFrame, minimumFrame, maximumFrame, canvasWidth);
        var draw = ImGui.GetWindowDrawList();
        var visibleWidth = MathF.Max(1f, draw.CmdHeader.ClipRect.Z - draw.CmdHeader.ClipRect.X);
        var scroll = ImGui.GetScrollX();
        if (x < scroll + 24f * UI.UIScale || x > scroll + visibleWidth - 24f * UI.UIScale) {
            ImGui.SetScrollX(Math.Clamp(x - visibleWidth * 0.5f, 0f, MathF.Max(0f, ImGui.GetScrollMaxX())));
        }
    }

    private static void DrawGrid(ImDrawListPtr draw, Vector2 position, int minimumFrame, int maximumFrame, float width, float height, bool labels)
    {
        var span = Math.Max(1, maximumFrame - minimumFrame);
        var pixels = MathF.Max(1f, width - CanvasInset * 2f * UI.UIScale) / span;
        var majorStep = 1;
        while (majorStep * pixels < 64f * UI.UIScale) majorStep *= majorStep < 5 ? 5 : 2;
        var minorStep = Math.Max(1, majorStep / 5);

        var clip = draw.CmdHeader.ClipRect;
        var labelRight = clip.X;
        for (var frame = FloorToStep(minimumFrame, minorStep); frame <= maximumFrame; frame += minorStep) {
            if (frame < minimumFrame) continue;
            var x = position.X + FrameToX(frame, minimumFrame, maximumFrame, width);
            var major = frame % majorStep == 0;
            draw.AddLine(new Vector2(x, position.Y), new Vector2(x, position.Y + height), ImGui.GetColorU32(major ? GridColor : new Vector4(GridColor.X, GridColor.Y, GridColor.Z, 0.33f)));
            if (labels && major && x >= clip.X && x <= clip.Z) {
                var text = frame.ToString();
                var textWidth = ImGui.CalcTextSize(text).X;
                var labelX = MathF.Min(x + 4f * UI.UIScale, clip.Z - textWidth - 4f * UI.UIScale);
                if (labelX >= labelRight) {
                    draw.AddText(new Vector2(labelX, position.Y + 7f * UI.UIScale), ImGui.GetColorU32(MutedTextColor), text);
                    labelRight = labelX + textWidth + 6f * UI.UIScale;
                }
            }
        }
    }

    private void DrawPlayhead(ImDrawListPtr draw, Vector2 position, int minimumFrame, int maximumFrame, float width, float height)
    {
        var x = position.X + FrameToX(currentFrame, minimumFrame, maximumFrame, width);
        draw.AddLine(new Vector2(x, position.Y), new Vector2(x, position.Y + height), ImGui.GetColorU32(PlayheadColor), 2f * UI.UIScale);
    }

    private static float FrameToX(float frame, int minimumFrame, int maximumFrame, float width)
    {
        var inset = MathF.Min(CanvasInset * UI.UIScale, width * 0.25f);
        return inset + (frame - minimumFrame) / Math.Max(1f, maximumFrame - minimumFrame) * MathF.Max(1f, width - inset * 2f);
    }

    private static int XToFrame(float x, int minimumFrame, int maximumFrame, float width)
    {
        var inset = MathF.Min(CanvasInset * UI.UIScale, width * 0.25f);
        var ratio = Math.Clamp((x - inset) / MathF.Max(1f, width - inset * 2f), 0f, 1f);
        return (int)MathF.Round(minimumFrame + ratio * (maximumFrame - minimumFrame));
    }

    private static int FloorToStep(int value, int step)
    {
        return (int)MathF.Floor(value / (float)step) * step;
    }

    private static List<KeyGroup> BuildKeyGroups(IEnumerable<(Property Property, KeyBase Key)> keys)
    {
        return keys.Where(item => IsValidRange(item.Property) && float.IsFinite(item.Key.frame))
            .GroupBy(item => item.Key.frame).OrderBy(group => group.Key)
            .Select(group => new KeyGroup(group.Key, group.ToList())).ToList();
    }

    private static List<TrackLane> FlattenTracks(List<ClipTrack>? tracks)
    {
        var result = new List<TrackLane>();
        var visited = new HashSet<ClipTrack>();
        if (tracks == null) return result;
        foreach (var track in tracks) AddTrack(track, 0, visited, result);
        return result;
    }

    private static void AddTrack(ClipTrack track, int depth, HashSet<ClipTrack> visited, List<TrackLane> output)
    {
        if (!visited.Add(track)) return;
        output.Add(new TrackLane(track, depth));
        foreach (var child in track.ChildTracks) AddTrack(child, depth + 1, visited, output);
    }

    private static IEnumerable<Property> EnumerateTimelineProperties(List<Property>? properties)
    {
        if (properties == null) yield break;
        foreach (var property in properties) {
            var hasChildren = property.ChildProperties is { Count: > 0 };
            if (!hasChildren || property.Keys is { Count: > 0 }) yield return property;
            if (!hasChildren) continue;
            foreach (var child in EnumerateTimelineProperties(property.ChildProperties)) yield return child;
        }
    }

    private static IEnumerable<(Property Property, KeyBase Key)> EnumerateKeys(List<Property>? properties)
    {
        if (properties == null) yield break;
        foreach (var property in properties) {
            if (property.Keys != null) {
                foreach (var key in property.Keys) yield return (property, key);
            }
            foreach (var child in EnumerateKeys(property.ChildProperties)) yield return child;
        }
    }

    private static (int Minimum, int Maximum) GetTimelineRange(List<TrackLane> lanes, MotFile motFile)
    {
        var minimum = float.IsFinite(motFile.Header.startFrame) ? motFile.Header.startFrame : 0f;
        var maximum = MathF.Max(motFile.Header.frameCount, float.IsFinite(motFile.Header.endFrame) ? motFile.Header.endFrame : 0f);
        foreach (var lane in lanes) {
            if (IsUsableTrackRange(lane.Track)) {
                minimum = MathF.Min(minimum, lane.Track.startFrame);
                maximum = MathF.Max(maximum, lane.Track.endFrame);
            }
            foreach (var property in EnumerateTimelineProperties(lane.Track.Properties)) {
                if (IsDisabled(property)) continue;
                if (float.IsFinite(property.Info.startFrame)) minimum = MathF.Min(minimum, property.Info.startFrame);
                if (float.IsFinite(property.Info.endFrame)) maximum = MathF.Max(maximum, property.Info.endFrame);
            }
            foreach (var (property, key) in EnumerateKeys(lane.Track.Properties)) {
                if (IsDisabled(property)) continue;
                if (float.IsFinite(key.frame)) {
                    minimum = MathF.Min(minimum, key.frame);
                    maximum = MathF.Max(maximum, key.frame);
                }
            }
        }
        if (!float.IsFinite(minimum)) minimum = 0f;
        if (!float.IsFinite(maximum) || maximum <= minimum) maximum = minimum + 1f;
        return ((int)MathF.Floor(minimum), (int)MathF.Ceiling(maximum));
    }

    private static (int Minimum, int Maximum) GetIsolatedTimelineRange(ClipTrack track, MotFile motFile)
    {
        var hasRange = false;
        var minimum = float.PositiveInfinity;
        var maximum = float.NegativeInfinity;

        foreach (var property in EnumerateTimelineProperties(track.Properties)) {
            if (IsDisabled(property)) continue;
            if (float.IsFinite(property.Info.startFrame)) {
                minimum = MathF.Min(minimum, property.Info.startFrame);
                hasRange = true;
            }
            if (float.IsFinite(property.Info.endFrame)) {
                maximum = MathF.Max(maximum, property.Info.endFrame);
                hasRange = true;
            }
            foreach (var key in property.Keys ?? []) {
                if (!float.IsFinite(key.frame)) continue;
                minimum = MathF.Min(minimum, key.frame);
                maximum = MathF.Max(maximum, key.frame);
                hasRange = true;
            }
        }

        if (!hasRange) {
            if (IsUsableTrackRange(track)) {
                minimum = track.startFrame;
                maximum = track.endFrame;
            } else {
                minimum = float.IsFinite(motFile.Header.startFrame) ? motFile.Header.startFrame : 0f;
                maximum = float.IsFinite(motFile.Header.endFrame) ? motFile.Header.endFrame : motFile.Header.frameCount;
            }
        }
        if (!float.IsFinite(minimum)) minimum = 0f;
        if (!float.IsFinite(maximum) || maximum <= minimum) maximum = minimum + 1f;
        return ((int)MathF.Floor(minimum), (int)MathF.Ceiling(maximum));
    }

    private static bool IsUsableTrackRange(ClipTrack track) =>
        !IsDisabledRange(track.startFrame, track.endFrame)
        && float.IsFinite(track.startFrame)
        && float.IsFinite(track.endFrame)
        && track.endFrame > track.startFrame;

    private static (float Minimum, float Maximum) GetTrackBounds(ClipTrack track, int fallbackMinimum, int fallbackMaximum)
    {
        var validTrackRange = track.Version < ClipVersion.RE8 && !IsDisabledRange(track.startFrame, track.endFrame)
            && float.IsFinite(track.startFrame) && float.IsFinite(track.endFrame) && track.endFrame >= track.startFrame;
        return validTrackRange ? (track.startFrame, track.endFrame) : (fallbackMinimum, fallbackMaximum);
    }

    private static bool TryGetEditableTrackBounds(ClipTrack track, int fallbackMinimum, int fallbackMaximum, out (float Minimum, float Maximum) bounds)
    {
        if (track.Version < ClipVersion.RE8 && !IsDisabledRange(track.startFrame, track.endFrame)
            && float.IsFinite(track.startFrame) && float.IsFinite(track.endFrame) && track.endFrame > track.startFrame) {
            bounds = (track.startFrame, track.endFrame);
            return true;
        }

        bounds = (fallbackMinimum, fallbackMaximum);
        return false;
    }

    private static List<PropertySnapshot> CaptureProperties(IEnumerable<Property> properties)
    {
        return properties.Distinct().Select(property => new PropertySnapshot(
            property,
            property.Info.startFrame,
            property.Info.endFrame,
            property.Keys?.Select(key => (key, key.frame)).ToList() ?? [])).ToList();
    }

    public void CloseTimelineWindow()
    {
        // The timeline is the Motlist tab itself; only the inspector opens separately.
    }

    private void SelectMotion(int index)
    {
        if (drag != null) FinishDrag(drag);
        valueContexts.Clear();
        collapsedClips.Clear();
        selectedMotionIndex = index;
        selectedClipIndex = 0;
        currentFrame = 0;
        localFrameRemainder = 0f;
        selectedTrack = null;
        selectedProperty = null;
        selectedKey = null;
        drag = null;
    }

    private void SelectClip(int index)
    {
        if (drag != null) FinishDrag(drag);
        valueContexts.Clear();
        selectedClipIndex = index;
        selectedTrack = null;
        selectedProperty = null;
        selectedKey = null;
        drag = null;
        localFrameRemainder = 0f;
    }

    private void SelectTrack(ClipTrack track)
    {
        if (ReferenceEquals(track, selectedTrack)) return;
        if (drag != null) FinishDrag(drag);
        valueContexts.Clear();
        propertyFilter = string.Empty;
        revealedProperty = null;
        selectedTrack = track;
        selectedProperty = null;
        selectedKey = null;
        drag = null;
    }

    private void EnsureMotionSelection()
    {
        if (selectedMotionIndex >= 0 && selectedMotionIndex < editor.File.Motions.Count
            && editor.File.Motions[selectedMotionIndex].MotFile is MotFile) return;
        selectedMotionIndex = editor.File.Motions.FindIndex(motion => motion.MotFile is MotFile);
        selectedClipIndex = 0;
    }

    private bool TryGetSelectedMotion(out MotIndex motion, out MotFile motFile)
    {
        motion = null!;
        motFile = null!;
        if (selectedMotionIndex < 0 || selectedMotionIndex >= editor.File.Motions.Count) return false;
        motion = editor.File.Motions[selectedMotionIndex];
        motFile = motion.MotFile as MotFile ?? null!;
        return motFile != null;
    }

    private void SynchronizeMotionSelectionFromPlayback()
    {
        if (drag != null) return;
        var state = Animator.LastPlaybackState;
        var active = state is { IsActive: true } ? state.Value.ActiveMotion : null;
        if (ReferenceEquals(active, lastPlaybackMotion)) return;
        lastPlaybackMotion = active;
        if (active == null) return;
        var index = editor.File.Motions.FindIndex(item => ReferenceEquals(item.MotFile, active));
        if (index >= 0 && index != selectedMotionIndex) SelectMotion(index);
    }

    private bool TryGetSynchronizedPlayback(MotFile motFile, out Animator.PlaybackState playback)
    {
        playback = default;
        var state = Animator.LastPlaybackState;
        if (!state.HasValue || !state.Value.IsActive || !ReferenceEquals(state.Value.ActiveMotion, motFile)) return false;
        playback = state.Value;
        return true;
    }

    private static string GetMotionName(MotIndex motion, int index)
    {
        var name = motion.MotFile?.Name;
        return string.IsNullOrWhiteSpace(name) ? $"{index:D3} Motion" : $"{index:D3} {name}";
    }

    private static List<MotClip> GetClips(MotIndex motion, MotFile motFile) =>
        motFile.Clips.Concat(motion.OverrideClips).Distinct().ToList();

    private static string GetClipName(MotClip clip, int index)
    {
        var name = clip.MainTrackName;
        return string.IsNullOrWhiteSpace(name) ? $"Clip {index}" : $"{index:D2} {CompactName(name)}";
    }

    private static string GetTrackName(ClipTrack track)
    {
        return string.IsNullOrWhiteSpace(track.Name) ? $"Track {track.nameHash1}" : track.Name;
    }

    private static string GetPropertyName(Property property)
    {
        return string.IsNullOrWhiteSpace(property.Info.FunctionName) ? $"Property {property.Info.nameAsciiHash}" : property.Info.FunctionName;
    }

    private static string BuildRangeLabel(List<Property> properties, float start, float end)
    {
        var name = properties.Count == 1 ? CompactName(GetPropertyName(properties[0])) : $"{properties.Count} stacked entries";
        return $"{name} [{start:0.###}-{end:0.###}]";
    }

    private static string CompactName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var compact = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
        return compact
            .Replace("app.", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("Tracks", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("Track", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("ConditionType", "CondType", StringComparison.OrdinalIgnoreCase)
            .Replace("ActionLevel", "ActLvl", StringComparison.OrdinalIgnoreCase)
            .Replace("isAutoClear", "AutoClear", StringComparison.OrdinalIgnoreCase)
            .Replace("CollisionModifier", "ColMod", StringComparison.OrdinalIgnoreCase)
            .Replace("CameraShake", "CamShake", StringComparison.OrdinalIgnoreCase)
            .Replace("ContentGauge", "CGauge", StringComparison.OrdinalIgnoreCase)
            .Replace("RotationSpeed", "RotSpeed", StringComparison.OrdinalIgnoreCase)
            .Replace("reqId", "ReqId", StringComparison.OrdinalIgnoreCase)
            .Trim().PrettyPrint();
    }

    private static string FormatKeyValue(KeyBase key)
    {
        object? value = key switch {
            BoolKey boolean => boolean.value,
            ActionKey action => action.value,
            _ => key.GetType().GetProperty("Value")?.GetValue(key),
        };
        return value switch {
            null => "-",
            bool boolean => boolean ? "True" : "False",
            float number => number.ToString("0.###", CultureInfo.InvariantCulture),
            double number => number.ToString("0.###", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "-",
        };
    }

    private static Vector4 GetTrackColor(ClipTrack track)
    {
        var name = GetTrackName(track);
        if (name.Contains("Collider", StringComparison.OrdinalIgnoreCase)) return ColliderColor;
        if (name.Contains("Player", StringComparison.OrdinalIgnoreCase)) return PlayerColor;
        if (name.Contains("VFX", StringComparison.OrdinalIgnoreCase) || name.Contains("Effect", StringComparison.OrdinalIgnoreCase)) return VfxColor;
        if (name.Contains("Event", StringComparison.OrdinalIgnoreCase) || name.Contains("Mirage", StringComparison.OrdinalIgnoreCase)) return EventColor;
        return DefaultColor;
    }

    private static bool IsValidRange(Property property) => !IsDisabled(property)
        && float.IsFinite(property.Info.startFrame) && float.IsFinite(property.Info.endFrame)
        && property.Info.endFrame >= property.Info.startFrame;

    private static bool IsDisabled(Property property)
    {
        return IsDisabledRange(property.Info.startFrame, property.Info.endFrame);
    }

    private static bool IsDisabledRange(float startFrame, float endFrame)
    {
        return startFrame == DisabledFrame && endFrame == DisabledFrame;
    }

    private static int CountProperties(List<Property>? properties) => EnumerateTimelineProperties(properties).Count();
    private static int CountActiveProperties(List<Property>? properties) => EnumerateTimelineProperties(properties).Count(property => !IsDisabled(property));

    private static string FitText(string text, float width)
    {
        if (width < ImGui.CalcTextSize("...").X) return string.Empty;
        if (ImGui.CalcTextSize(text).X <= width) return text;
        const string ellipsis = "...";
        var low = 0;
        var high = text.Length;
        while (low < high) {
            var middle = (low + high + 1) / 2;
            if (ImGui.CalcTextSize(text[..middle] + ellipsis).X <= width) low = middle;
            else high = middle - 1;
        }
        return text[..low] + ellipsis;
    }

    private static void ShowRangeTooltip(ClipTrack track, RangeGroup range)
    {
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(GetTrackName(track));
        ImGui.TextDisabled($"Frames {range.Start:0.###} - {range.End:0.###}");
        foreach (var property in range.Properties.Take(12)) {
            ImGui.TextUnformatted(GetPropertyName(property));
            if (property.Keys != null) {
                ImGui.SameLine();
                ImGui.TextDisabled(string.Join(", ", property.Keys.Take(6).Select(key => $"{key.frame:0.###}:{FormatKeyValue(key)}")));
            }
        }
        ImGui.Separator();
        ImGui.TextDisabled(range.Properties.Count == 1
            ? "Drag center to move range and keys. Drag an edge to resize the range."
            : "Click to open these overlapping entries in the Dope Sheet.");
        ImGui.EndTooltip();
    }

    private static void ShowKeyTooltip(ClipTrack track, KeyGroup group)
    {
        ImGui.BeginTooltip();
        ImGui.TextUnformatted($"{GetTrackName(track)} | frame {group.Frame}");
        foreach (var (property, key) in group.Keys) ImGui.TextUnformatted($"{GetPropertyName(property)} = {FormatKeyValue(key)}");
        ImGui.Separator();
        ImGui.TextDisabled("Drag one key. Hold Shift while starting the drag to move the full same-frame stack.");
        ImGui.EndTooltip();
    }

    private void MarkModified()
    {
        editor.MarkTimelineModified();
    }
}
