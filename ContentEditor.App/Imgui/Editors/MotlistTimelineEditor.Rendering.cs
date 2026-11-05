using System.Numerics;
using Hexa.NET.ImGui;
using ReeLib.Clip;

namespace ContentEditor.App.ImguiHandling;

public partial class MotlistTimelineEditor
{
    // ------------------------------------------------------------------
    //  THEME - every colour / size of the timeline look lives here.
    //  Change these values to restyle the whole editor.
    // ------------------------------------------------------------------
    private static class Theme
    {
        public static readonly Vector4 CanvasBg   = new(.063f, .067f, .078f, 1);
        public static readonly Vector4 LabelBg    = new(.082f, .086f, .102f, 1);
        public static readonly Vector4 RulerBg    = new(.100f, .106f, .125f, 1);
        public static readonly Vector4 ClipRow    = new(.125f, .133f, .165f, 1);
        public static readonly Vector4 Divider    = new(1f, 1f, 1f, 1);       // used with low alpha
        public static readonly Vector4 Text       = new(.90f, .91f, .94f, 1);
        public static readonly Vector4 TextDim    = new(.53f, .56f, .64f, 1);
        public static readonly Vector4 Playhead   = new(.98f, .38f, .38f, 1);
        public const float Indent       = 17f;   // must match arrowX in Interaction.cs
        public const float AccentWidth  = 3f;    // coloured stripe at the left of clip/track rows
        public const float BarRounding  = 3f;
    }

    private List<VisibleLane> lastLanes = new();
    private readonly HashSet<int> trackSummaryPixels = new();
    private Vector2 canvasMin, canvasMax;
    private float labelWidth, rulerHeight;
    private static readonly Vector4 MutedText = Theme.TextDim;
    private static uint Color(Vector4 color, float alpha = 1) => ImGui.GetColorU32(new Vector4(color.X, color.Y, color.Z, alpha));
    private static uint Line(float alpha = .06f) => Color(Theme.Divider, alpha);
    private static Vector4 TrackColor(TimelineTrack? track) => NameColor(track?.Name ?? "");
    private static Vector4 PropertyColor(Vector4 trackColor, TimelineProperty property)
    {
        Vector4 target = property.Native.Info.DataType switch
        {
            PropertyType.Bool => new(.42f, .82f, .70f, 1),
            PropertyType.Action => new(.96f, .68f, .36f, 1),
            PropertyType.Enum => new(.72f, .62f, .92f, 1),
            _ => new(.76f, .79f, .88f, 1)
        };
        return Vector4.Lerp(trackColor, target, property.Disabled ? .52f : .34f);
    }
    private static Vector4 NameColor(string name)
    {
        if (name.Contains("Collider", StringComparison.OrdinalIgnoreCase) || name.Contains("ReqSet", StringComparison.OrdinalIgnoreCase)) return new(.96f, .38f, .32f, 1);
        if (name.Contains("VFX", StringComparison.OrdinalIgnoreCase) || name.Contains("effect", StringComparison.OrdinalIgnoreCase)) return new(.72f, .50f, .96f, 1);
        if (name.Contains("Player", StringComparison.OrdinalIgnoreCase)) return new(.32f, .67f, .98f, 1);
        if (name.Contains("Wwise", StringComparison.OrdinalIgnoreCase) || name.Contains("Sound", StringComparison.OrdinalIgnoreCase)) return new(.34f, .77f, .62f, 1);
        if (name.Contains("Camera", StringComparison.OrdinalIgnoreCase)) return new(.95f, .67f, .30f, 1);
        return new(.31f, .75f, .75f, 1);
    }
    private static string CompactTrackName(string name)
    {
        name = name.Split('.').Last();
        if (name.EndsWith("Tracks")) name = name[..^6]; else if (name.EndsWith("Track")) name = name[..^5];
        return System.Text.RegularExpressions.Regex.Replace(name, "([a-z])([A-Z])", "$1 $2");
    }
    private static string FitText(string text, float width)
    {
        if (width <= 10) return "";
        if (ImGui.CalcTextSize(text).X <= width) return text;
        while (text.Length > 0 && ImGui.CalcTextSize(text + "...").X > width) text = text[..^1];
        return text + "...";
    }

    private void DrawTimelineCanvas()
    {
        var lanes = lastLanes = BuildTimelineLanes();
        var view = overviewView;
        canvasMin = ImGui.GetCursorScreenPos();
        var size = Vector2.Max(ImGui.GetContentRegionAvail(), new Vector2(80, 100));
        canvasMax = canvasMin + size;
        float font = ImGui.GetTextLineHeight();
        rulerHeight = Math.Max(40, font * 2 + 8);
        labelWidth = showHierarchy ? Math.Clamp(hierarchyWidth, Math.Min(150, size.X * .35f), Math.Max(150, size.X * .42f)) : 0;
        double start = motlist.Motclips.Min(c => c.StartFrame), end = motlist.Motclips.Max(c => c.EndFrame);
        if (!view.Initialized)
        {
            double[] keys = motlist.Motclips.SelectMany(c => c.Tracks).SelectMany(t => t.AllProperties).SelectMany(p => p.Keys)
                .Select(k => k.Frame).Where(double.IsFinite).ToArray();
            view.Initialize(lanes, Math.Min(start, keys.DefaultIfEmpty(start).Min()), Math.Max(end, keys.DefaultIfEmpty(end).Max()));
        }
        view.RowHeight = Math.Max(view.RowHeight, font + 16);
        view.PlotMin = canvasMin + new Vector2(labelWidth, rulerHeight);
        view.PlotSize = new Vector2(Math.Max(1, size.X - labelWidth - 15), Math.Max(1, size.Y - rulerHeight));
        view.YMax += lanes.Count - view.RowCount;
        view.RowCount = lanes.Count;
        ClampVerticalView(view);
        AdvanceGesture(view, start, end);
        ClampVerticalView(view);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(canvasMin, canvasMax, Color(Theme.LabelBg), 4);
        draw.AddRectFilled(canvasMin + new Vector2(labelWidth, 0), canvasMax, Color(Theme.CanvasBg));
        ImGui.SetCursorScreenPos(new(view.PlotMin.X, canvasMin.Y));
        if (HasAssets(DesignerTarget.TimelineOverlay)) ImGui.SetNextItemAllowOverlap();
        ImGui.InvisibleButton("##RulerInput", new Vector2(view.PlotSize.X, rulerHeight),
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        bool rulerHovered = ImGui.IsItemHovered();
        ImGui.SetCursorScreenPos(view.PlotMin - new Vector2(labelWidth, 0));
        if (HasAssets(DesignerTarget.TimelineOverlay)) ImGui.SetNextItemAllowOverlap();
        ImGui.InvisibleButton("##TimelineInput", new Vector2(size.X - 15, view.PlotSize.Y),
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        bool canvasHovered = ImGui.IsItemHovered();
        draw.PushClipRect(view.PlotMin, view.PlotMin + view.PlotSize, true);
        // Row backgrounds first, then grid on top, then content.
        foreach (var lane in lanes)
        {
            float y = RowY(lane.Y);
            if (y + view.RowHeight / 2 < view.PlotMin.Y || y - view.RowHeight / 2 > view.PlotMin.Y + view.PlotSize.Y) continue;
            DrawCanvasRowBackground(draw, lane);
        }
        DrawTimeGrid(draw, view);
        foreach (var lane in lanes)
        {
            float y = RowY(lane.Y);
            if (y + view.RowHeight / 2 < view.PlotMin.Y || y - view.RowHeight / 2 > view.PlotMin.Y + view.PlotSize.Y) continue;
            DrawCanvasRow(draw, lane);
        }
        DrawPlayhead(draw, view);
        draw.PopClipRect();
        DrawLaneLabels(draw, lanes);
        DrawRuler(draw, view);
        HandleTimelineInteraction(lanes, view, start, end, (canvasHovered || rulerHovered) && !OverlayContainsMouse());
        DrawTimelineContextMenu();
        DrawCanvasScrollbar(view, lanes.Count);
        if (showHierarchy)
        {
            ImGui.SetCursorScreenPos(canvasMin + new Vector2(labelWidth - 3, 0));
            ImGui.InvisibleButton("##NameColumnWidth", new Vector2(6, rulerHeight));
            if (ImGui.IsItemActive()) hierarchyWidth = Math.Clamp(hierarchyWidth + ImGui.GetIO().MouseDelta.X, 150, size.X * .42f);
            if (ImGui.IsItemHovered() || ImGui.IsItemActive()) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
        }
        HandleKeyboardShortcuts(view, start, end);
    }
    private void ClampVerticalView(TimelineView view)
    {
        double count = view.RowCount, visible = view.PlotSize.Y / view.RowHeight;
        double top = Math.Max(.5, count - .5);
        view.YMax = Math.Clamp(view.YMax, Math.Min(top, visible - .5), top);
        view.YMin = view.YMax - visible;
    }
    private float FrameX(double frame) => overviewView.PlotMin.X + (float)((frame - overviewView.XMin) /
        (overviewView.XMax - overviewView.XMin) * overviewView.PlotSize.X);
    private double MouseFrame(Vector2 mouse) => overviewView.XMin + (mouse.X - overviewView.PlotMin.X) /
        overviewView.PlotSize.X * (overviewView.XMax - overviewView.XMin);
    private float RowY(double y) => overviewView.PlotMin.Y + (float)(overviewView.YMax - y) * overviewView.RowHeight;
    private static double TickStep(double span, float width)
    {
        double ideal = span / Math.Max(1, width / 85), power = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(ideal, .001))));
        return new[] { 1d, 2, 5, 10 }.Select(v => v * power).First(v => v >= ideal);
    }
    private void DrawTimeGrid(ImDrawListPtr draw, TimelineView view)
    {
        double step = TickStep(view.XMax - view.XMin, view.PlotSize.X);
        // Only major lines: minor lines made the canvas noisy.
        for (double f = Math.Ceiling(view.XMin / step) * step; f <= view.XMax; f += step)
        {
            float x = FrameX(f);
            draw.AddLine(new(x, view.PlotMin.Y), new(x, view.PlotMin.Y + view.PlotSize.Y), Line(.07f));
        }
    }
    private void DrawRuler(ImDrawListPtr draw, TimelineView view)
    {
        var right = new Vector2(view.PlotMin.X + view.PlotSize.X, canvasMin.Y + rulerHeight);
        draw.AddRectFilled(canvasMin, new(canvasMax.X, right.Y), Color(Theme.RulerBg));
        if (showHierarchy) draw.AddText(canvasMin + new Vector2(12, rulerHeight / 2 - ImGui.GetTextLineHeight() / 2), Color(Theme.TextDim), $"BEHAVIOR CLIPS  {motlist.Motclips.Count}");
        draw.PushClipRect(new(view.PlotMin.X, canvasMin.Y), right, true);
        double step = TickStep(view.XMax - view.XMin, view.PlotSize.X), minor = step / 5;
        for (double f = Math.Ceiling(view.XMin / minor) * minor; f <= view.XMax; f += minor)
        {
            float x = FrameX(f); bool major = Math.Abs(f / step - Math.Round(f / step)) < .001;
            draw.AddLine(new(x, right.Y - (major ? 10 : 4)), new(x, right.Y), Color(Theme.TextDim, major ? .7f : .35f));
            if (major)
            {
                draw.AddText(new(x + 4, canvasMin.Y + 4), Color(Theme.Text), $"{f:0.##}");
                if (motion?.Header.FrameRate > 0) draw.AddText(new(x + 4, canvasMin.Y + ImGui.GetTextLineHeight() + 5),
                    Color(Theme.TextDim), $"{f / motion.Header.FrameRate:0.00}s");
            }
        }
        float head = FrameX(currentFrame);
        draw.AddTriangleFilled(new(head - 6, canvasMin.Y + 2), new(head + 6, canvasMin.Y + 2), new(head, canvasMin.Y + 13), Color(Theme.Playhead));
        draw.PopClipRect();
        draw.AddLine(new(canvasMin.X, right.Y), new(canvasMax.X, right.Y), Line(.12f));
    }

    private bool IsLaneSelected(VisibleLane lane) => lane.Property is not null ? ReferenceEquals(lane.Property, selectedProperty)
        : lane.Track is not null ? selectedProperty is null && ReferenceEquals(lane.Track, selectedTrack)
        : selectedTrack is null && ReferenceEquals(lane.Clip, selectedClip);
    private static Vector4 LaneTint(VisibleLane lane)
    {
        Vector4 trackTint = lane.Track is null ? NameColor(lane.Clip.Name) : TrackColor(lane.Track);
        return lane.Property is { } p ? PropertyColor(trackTint, p) : trackTint;
    }

    // Flat row backgrounds, shared look for canvas and label column.
    private void PaintRowBackground(ImDrawListPtr draw, VisibleLane lane, Vector2 min, Vector2 max)
    {
        bool selected = IsLaneSelected(lane);
        var tint = LaneTint(lane);
        if (lane.Track is null) draw.AddRectFilled(min, max, Color(Theme.ClipRow));
        else if (lane.Property is null) draw.AddRectFilled(min, max, Color(tint, selected ? .16f : .06f));
        else if (selected) draw.AddRectFilled(min, max, Color(tint, .14f));
        else if (((int)lane.Y & 1) == 0) draw.AddRectFilled(min, max, Line(.018f));
        draw.AddLine(new(min.X, max.Y), max, Line(.05f));
    }
    private void DrawCanvasRowBackground(ImDrawListPtr draw, VisibleLane lane)
    {
        float y = RowY(lane.Y), half = overviewView.RowHeight * .5f;
        PaintRowBackground(draw, lane, new(overviewView.PlotMin.X, y - half), new(overviewView.PlotMin.X + overviewView.PlotSize.X, y + half));
    }
    private void DrawCanvasRow(ImDrawListPtr draw, VisibleLane lane)
    {
        float y = RowY(lane.Y), half = overviewView.RowHeight * .5f;
        var min = new Vector2(overviewView.PlotMin.X, y - half); var max = new Vector2(min.X + overviewView.PlotSize.X, y + half);
        bool selected = IsLaneSelected(lane);
        var tint = LaneTint(lane);
        float textY = y - ImGui.GetTextLineHeight() / 2;
        if (lane.Track is null)
        {
            string source = motionIndex?.OverrideClips.Contains(lane.Clip.Native) == true ? "Override" : "Embedded";
            string label = FitText($"{source}   {lane.StartFrame:0.##} - {lane.EndFrame:0.##} f", overviewView.PlotSize.X - 24);
            draw.AddText(new(min.X + 12, textY), Color(Theme.TextDim), label);
            return;
        }
        if (lane.Property is null)
        {
            // Track row: one flat range bar, no tick marks, no gradient.
            var box = GetRangeBox(lane.StartFrame, lane.EndFrame, lane);
            if (box.Max.X < min.X || box.Min.X > max.X) return;
            DrawBar(draw, box.Min, box.Max, tint, selected, .30f);
            string text = $"{CompactTrackName(lane.Track.Name)}   {lane.StartFrame:0.##} - {lane.EndFrame:0.##}";
            float left = Math.Max(box.Min.X + 10, min.X + 8), available = Math.Min(box.Max.X, max.X) - left - 8;
            if (available > 50) draw.AddText(new(left, textY), Color(Theme.Text), FitText(text, available));
            return;
        }
        DrawPropertyLane(draw, lane, lane.Property, tint);
    }

    private void DrawLaneLabels(ImDrawListPtr draw, List<VisibleLane> lanes)
    {
        if (!showHierarchy) return;
        TimelineView view = overviewView;
        draw.PushClipRect(new Vector2(canvasMin.X, view.PlotMin.Y), new Vector2(view.PlotMin.X, canvasMax.Y), true);
        foreach (VisibleLane lane in lanes)
        {
            float y = RowY(lane.Y), half = view.RowHeight / 2;
            if (y + half < view.PlotMin.Y || y - half > canvasMax.Y) continue;
            var tint = LaneTint(lane);
            bool selected = IsLaneSelected(lane);
            var min = new Vector2(canvasMin.X, y - half); var max = new Vector2(view.PlotMin.X, y + half);
            PaintRowBackground(draw, lane, min, max);

            // Accent stripe: clip + track rows only.
            if (lane.Track is null) draw.AddRectFilled(min, new(min.X + Theme.AccentWidth, max.Y), Color(tint));
            else if (lane.Property is null) draw.AddRectFilled(new(min.X + Theme.Indent - 4, min.Y + 4), new(min.X + Theme.Indent - 4 + Theme.AccentWidth - 1, max.Y - 4), Color(tint));

            // Thin indent guides for property depth (replaces the boxed groups / branch lines).
            for (int depth = 2; depth <= lane.Depth; depth++)
            {
                float gx = min.X + 14 + (depth - 1) * Theme.Indent;
                draw.AddLine(new(gx, min.Y), new(gx, max.Y), Line(.07f));
            }

            float arrowX = min.X + 14 + lane.Depth * Theme.Indent;
            if (LaneCanExpand(lane))
            {
                var c = Color(Theme.TextDim);
                if (LaneExpanded(lane)) draw.AddTriangleFilled(new(arrowX - 4, y - 2), new(arrowX + 4, y - 2), new(arrowX, y + 3), c);
                else draw.AddTriangleFilled(new(arrowX - 2, y - 4), new(arrowX - 2, y + 4), new(arrowX + 3, y), c);
            }
            float textX = arrowX + 14;
            if (lane.Property is not null)
                draw.AddCircleFilled(new(textX - 5, y), 2.5f, Color(tint, lane.Property.Disabled ? .4f : .95f));

            string title = lane.Property?.Name ?? CompactTrackName(lane.Track?.Name ?? lane.Clip.Name);
            if (lane.Track is null) title = title.ToUpperInvariant();
            string count = lane.Property is { } p ? p.Container ? "GROUP" : p.Native.Info.DataType.ToString().ToUpperInvariant()
                : lane.Track is { } t ? t.AllProperties.Count() + " PROPS"
                : lane.Clip.Tracks.Count + " TRACKS";
            float countWidth = ImGui.CalcTextSize(count).X;
            uint titleColor = lane.Property?.Disabled == true ? Color(Theme.TextDim)
                : lane.Property is not null ? Color(Theme.Text, .85f) : Color(Theme.Text);
            draw.AddText(new(textX, y - ImGui.GetTextLineHeight() / 2), titleColor, FitText(title, max.X - textX - countWidth - 24));
            draw.AddText(new(max.X - countWidth - 10, y - ImGui.GetTextLineHeight() / 2), Color(Theme.TextDim, .8f), count);

            if (ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(min, max))
                ImGui.SetTooltip(lane.Path + (lane.Property is { } prop ? $"\n{prop.Keys.Count} stored keys / {prop.Native.Info.DataType}" : ""));
        }
        draw.PopClipRect();
        draw.AddLine(new(view.PlotMin.X, canvasMin.Y), new(view.PlotMin.X, canvasMax.Y), Line(.12f));
    }

    // Flat bar: solid fill, accent edge on the left, outline only when selected.
    private void DrawBar(ImDrawListPtr draw, Vector2 min, Vector2 max, Vector4 tint, bool selected, float alpha)
    {
        if (max.X <= min.X) { draw.AddLine(min, new(min.X, max.Y), Color(tint), 2); return; }
        draw.AddRectFilled(min, max, Color(tint, alpha), Theme.BarRounding);
        draw.AddRectFilled(min, new(Math.Min(max.X, min.X + 3), max.Y), Color(tint, .95f), Theme.BarRounding, ImDrawFlags.RoundCornersLeft);
        if (selected) draw.AddRect(min, max, 0xFFF8F8F8, Theme.BarRounding, ImDrawFlags.None, 1.5f);
    }

    private readonly record struct KeySegment(TimelineKey Start, TimelineKey? End, double EndFrame);
    private bool IsBoundaryAt(TimelineProperty property, TimelineKey[] ordered, int index)
    {
        if (index <= 0) return false;
        var key = ordered[index]; var previous = ordered[index - 1];
        return property.IsTerminal(key) && !property.IsTerminal(previous) ||
            index == ordered.Length - 1 && !property.Disabled && key.Frame == property.EndFrame && key.Frame > previous.Frame && Equals(previous.RawValue, key.RawValue);
    }
    private bool IsBoundaryKey(TimelineProperty property, TimelineKey key)
    {
        var ordered = property.Keys.Where(k => double.IsFinite(k.Frame)).OrderBy(k => k.Frame).ToArray();
        return IsBoundaryAt(property, ordered, Array.IndexOf(ordered, key));
    }
    private List<KeySegment> GetKeySegments(TimelineProperty property)
    {
        var keys = property.Keys.Where(k => double.IsFinite(k.Frame)).OrderBy(k => k.Frame).ToArray();
        return BuildKeySegments(property, keys);
    }
    private List<KeySegment> BuildKeySegments(TimelineProperty property, TimelineKey[] keys)
    {
        var result = new List<KeySegment>();
        if (property.Container || property.Native.Info.DataType == PropertyType.Action) return result;
        for (int i = 0; i + 1 < keys.Length; i++)
        {
            bool boundary = IsBoundaryAt(property, keys, i);
            if (boundary || property.IsTerminal(keys[i]) && property.Native.Info.DataType != PropertyType.Bool) continue;
            if (keys[i + 1].Frame > keys[i].Frame) result.Add(new(keys[i], keys[i + 1], keys[i + 1].Frame));
        }
        return result;
    }
    private static string ValueKind(TimelineProperty property) => property.Native.Info.DataType switch
    { PropertyType.Bool => "Bool", PropertyType.Action => "Action", PropertyType.Enum => "Enum",
        _ => System.Text.RegularExpressions.Regex.IsMatch(property.Name, @"(?:Id|ID)[0-9]*$") ? "ID" : "Value" };
    private static string ValueText(TimelineProperty property, TimelineKey key) => $"{ValueKind(property)}: {key.DisplayValue}";
    private static string TimeText(double first, double last) => first == last ? $"Frame: {first:0.##}" : $"Frames: {first:0.##} - {last:0.##}";
    private static string SegmentCaption(TimelineProperty property, KeySegment span)
    {
        string caption = ValueText(property, span.Start) + "\n" + TimeText(span.Start.Frame, span.EndFrame);
        if (span.End is { } end && property.IsTerminal(end) && !property.IsTerminal(span.Start)) caption += $"\nEnd value: {end.DisplayValue}";
        return caption;
    }

    private void DrawPropertyLane(ImDrawListPtr draw, VisibleLane lane, TimelineProperty property, Vector4 tint)
    {
        float y = RowY(lane.Y);
        float textY = y - ImGui.GetTextLineHeight() / 2;
        if (property.Container)
        {
            draw.AddText(new(overviewView.PlotMin.X + 12, textY), Color(Theme.TextDim), $"{property.Children.Count} child properties");
            return;
        }
        var ordered = property.Keys.Where(k => double.IsFinite(k.Frame)).OrderBy(k => k.Frame).ToArray();
        var groups = ordered.GroupBy(k => k.Frame).ToArray();
        var segments = BuildKeySegments(property, ordered);
        var boundaryKeys = new HashSet<TimelineKey>();
        for (int i = 1; i < ordered.Length; i++) if (IsBoundaryAt(property, ordered, i)) boundaryKeys.Add(ordered[i]);
        var curveLimits = CurveLimits(property);
        if (curveLimits is { } limits)
        {
            for (int i = 0; i + 1 < ordered.Length; i++)
            {
                var a = KeyPosition(lane, ordered[i], limits); var b = KeyPosition(lane, ordered[i + 1], limits);
                if (((NormalKey)ordered[i].Native).interpolation == InterpolationType.Discrete)
                { draw.AddLine(a, new(b.X, a.Y), Color(tint), 1.5f); draw.AddLine(new(b.X, a.Y), b, Color(tint, .45f), 1); }
                else draw.AddLine(a, b, Color(tint), 1.5f);
            }
            foreach (var group in groups)
            {
                var key = group.FirstOrDefault(k => ReferenceEquals(k, selectedKey)) ?? group.First();
                DrawKeyMarker(draw, KeyPosition(lane, key, limits), property, key, boundaryKeys.Contains(key), ReferenceEquals(key, selectedKey), tint);
            }
            return;
        }
        foreach (var span in segments)
        {
            var box = GetPropertyVisualRangeBox(lane, span.Start.Frame, span.EndFrame);
            bool selected = ReferenceEquals(span.Start, selectedKey) || ReferenceEquals(span.End, selectedKey);
            float opacity = property.Disabled ? .10f : property.Native.Info.DataType == PropertyType.Bool && span.Start.Value == 0 ? .10f : .30f;
            DrawBar(draw, box.Min, box.Max, tint, selected, opacity);
            float x = Math.Max(box.Min.X + 12, overviewView.PlotMin.X + 8);
            string value = (ValueKind(property) == "ID" ? "ID " : "") + span.Start.DisplayValue;
            string range = $"{span.Start.Frame:0.##} - {span.EndFrame:0.##}";
            float room = Math.Min(box.Max.X, overviewView.PlotMin.X + overviewView.PlotSize.X) - x - 9;
            float rangeWidth = ImGui.CalcTextSize(range).X;
            if (ImGui.CalcTextSize(value).X + rangeWidth + 30 < room)
                draw.AddText(new(Math.Min(box.Max.X, overviewView.PlotMin.X + overviewView.PlotSize.X) - rangeWidth - 9, textY), Color(tint, .8f), range);
            if (ImGui.CalcTextSize(value).X <= room) draw.AddText(new(x, textY), Color(Theme.Text), value);
        }
        for (int i = 0; i < groups.Length; i++)
        {
            var key = groups[i].FirstOrDefault(k => ReferenceEquals(k, selectedKey)) ?? groups[i].First();
            float x = FrameX(key.Frame);
            if (x < overviewView.PlotMin.X - 8 || x > overviewView.PlotMin.X + overviewView.PlotSize.X + 8) continue;
            bool boundary = boundaryKeys.Contains(key), selected = ReferenceEquals(key, selectedKey);
            DrawKeyMarker(draw, new(x, y), property, key, boundary, selected, tint);
            bool hasSpan = segments.Any(s => ReferenceEquals(s.Start, key));
            if (!hasSpan && !boundary)
            {
                string label = groups[i].Count() > 1 ? $"{groups[i].Count()} keys" : key.DisplayValue;
                float next = i + 1 < groups.Length ? FrameX(groups[i + 1].Key) : overviewView.PlotMin.X + overviewView.PlotSize.X;
                if (ImGui.CalcTextSize(label).X + 12 < next - x) draw.AddText(new(x + 11, textY), Color(tint), label);
            }
            if (groups[i].Count() > 1) draw.AddText(new(x + 7, y - overviewView.RowHeight * .43f), Color(Theme.Text), $"{groups[i].Count()}");
        }
        if (ordered.Length == 0 && ReferenceEquals(selectedProperty, property))
            draw.AddText(new(overviewView.PlotMin.X + 12, textY), Color(Theme.TextDim), "Double-click to create a key...");
    }
    private void DrawKeyMarker(ImDrawListPtr draw, Vector2 point, TimelineProperty property, TimelineKey key, bool boundary, bool selected, Vector4 tint)
    {
        float r = design.KeySize + (selected ? 1.5f : 0);
        uint color = Color(tint, property.Disabled ? .45f : 1), outline = selected ? 0xFFFFFFFF : Color(Theme.CanvasBg, .9f);
        float ow = selected ? 1.8f : 1f;
        if (boundary)
        {
            // End marker: slim vertical cap.
            float h = Math.Max(6, r + 2);
            draw.AddRectFilled(point + new Vector2(-1.5f, -h), point + new Vector2(1.5f, h), selected ? 0xFFFFFFFF : color, 1);
        }
        else if (property.Native.Info.DataType == PropertyType.Bool)
        {
            var min = point - new Vector2(r); var max = point + new Vector2(r);
            draw.AddRectFilled(min, max, key.Value == 0 ? Color(Theme.CanvasBg, 1) : color, 2);
            draw.AddRect(min, max, selected ? 0xFFFFFFFF : color, 2, ImDrawFlags.None, ow);
        }
        else if (property.Native.Info.DataType == PropertyType.Action)
        {
            var a = point + new Vector2(0, -r - 1); var b = point + new Vector2(r + 1, r); var c = point + new Vector2(-r - 1, r);
            draw.AddTriangleFilled(a, b, c, color); draw.AddTriangle(a, b, c, outline, ow);
        }
        else
        {
            var a = point + new Vector2(0, -r); var b = point + new Vector2(r, 0); var c = point + new Vector2(0, r); var d = point + new Vector2(-r, 0);
            draw.AddQuadFilled(a, b, c, d, color); draw.AddQuad(a, b, c, d, outline, ow);
        }
    }
    // Hit-test boxes in Interaction.cs use these three, so visuals and picking stay in sync.
    private (Vector2 Min, Vector2 Max) GetKeyRangeBox(VisibleLane lane, double start, double end)
    {
        float y = RowY(lane.Y), half = Math.Clamp(overviewView.RowHeight * .29f, 11, 28);
        return (new(Math.Min(FrameX(start), FrameX(end)), y - half), new(Math.Max(FrameX(start), FrameX(end)), y + half));
    }
    private (Vector2 Min, Vector2 Max) GetPropertyVisualRangeBox(VisibleLane lane, double start, double end)
    {
        float y = RowY(lane.Y), half = Math.Clamp(overviewView.RowHeight * .20f, 6, 11);
        return (new(Math.Min(FrameX(start), FrameX(end)), y - half), new(Math.Max(FrameX(start), FrameX(end)), y + half));
    }
    private (Vector2 Min, Vector2 Max) GetRangeBox(double start, double end, VisibleLane lane)
    {
        if (lane.Property is not null) return GetKeyRangeBox(lane, start, end);
        float y = RowY(lane.Y), half = Math.Clamp(overviewView.RowHeight * .30f, 11, 24);
        return (new(Math.Min(FrameX(start), FrameX(end)), y - half), new(Math.Max(FrameX(start), FrameX(end)), y + half));
    }
    private (double Min, double Max)? CurveLimits(TimelineProperty property)
    {
        if (!showCurves || property.Container || property.Native.Info.DataType is not (PropertyType.F64 or PropertyType.F64_alt) || property.Keys.Count < 2) return null;
        if (property.Keys.Any(k => k.Native is not NormalKey n || n.interpolation is not (InterpolationType.Linear or InterpolationType.Discrete)
            || k.Value is not { } v || !double.IsFinite(v))) return null;
        double min = property.Keys.Min(k => k.Value!.Value), max = property.Keys.Max(k => k.Value!.Value);
        return max - min < 1e-9 ? (min - .5, max + .5) : (min, max);
    }
    private Vector2 KeyPosition(VisibleLane lane, TimelineKey key)
    {
        return KeyPosition(lane, key, lane.Property is { } property ? CurveLimits(property) : null);
    }
    private Vector2 KeyPosition(VisibleLane lane, TimelineKey key, (double Min, double Max)? limits)
    {
        float y = RowY(lane.Y);
        if (limits is { } range && key.Value is { } value)
            y += (float)(.5 - (value - range.Min) / (range.Max - range.Min)) * overviewView.RowHeight * .62f;
        return new(FrameX(key.Frame), y);
    }
    private void DrawPlayhead(ImDrawListPtr draw, TimelineView view)
    {
        float x = FrameX(currentFrame);
        draw.AddLine(new(x, view.PlotMin.Y), new(x, view.PlotMin.Y + view.PlotSize.Y), Color(Theme.Playhead), 1.5f);
    }
    private void DrawCanvasScrollbar(TimelineView view, int count)
    {
        float content = count * view.RowHeight, visible = view.PlotSize.Y;
        if (content <= visible) return;
        float scroll = (float)(count - .5 - view.YMax) * view.RowHeight, max = content - visible, position = max - scroll;
        ImGui.SetCursorScreenPos(new(canvasMax.X - 12, view.PlotMin.Y));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5f);
        if (ImGui.VSliderFloat("##Rows", new Vector2(10, visible), ref position, 0, max, "")) view.YMax = count - .5 - (max - position) / view.RowHeight;
        ImGui.PopStyleVar();
    }
}
