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
    private double? copiedKeyValue;
    private double newKeyValue;
    private Vector2 contextMouseDownPosition;
    private bool showMetricsTool;
    private bool showIdStackTool;
    private bool showDebugLog;
    private bool showImGuiDemo;
    private bool fullscreenMode;
    private bool imGuiFullscreenActive;
    private bool dockingEnabled;
    private const float MinimumHierarchyWidth = 220;
    private const float MinimumTimelineWidth = 280;
    private const float SplitterWidth = 6;
    private bool showHierarchy = true;
    private float hierarchyWidth = 330;
    private readonly Motlist motlist = new();
    private Vector2 workspaceRestorePosition;
    private Vector2 workspaceRestoreSize;
    private bool hasWorkspaceRestoreLayout;


}
