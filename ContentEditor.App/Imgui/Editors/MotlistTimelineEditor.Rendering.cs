using System.Numerics;
using Hexa.NET.ImGui;
using ReeLib.Clip;

namespace ContentEditor.App.ImguiHandling;

public partial class MotlistTimelineEditor
{
    private List<VisibleLane> lastLanes = new();
    private Vector2 canvasMin, canvasMax;
    private float labelWidth, rulerHeight;
    private static readonly Vector4 MutedText = new(.53f, .56f, .64f, 1);
    private static uint Color(Vector4 color, float alpha = 1) => ImGui.GetColorU32(new Vector4(color.X, color.Y, color.Z, alpha));
    private static Vector4 TrackColor(TimelineTrack? track) => NameColor(track?.Name ?? "");
    private static Vector4 NameColor(string name)
    {
        if (name.Contains("Collider", StringComparison.OrdinalIgnoreCase) || name.Contains("ReqSet", StringComparison.OrdinalIgnoreCase)) return new(.96f,.38f,.32f,1);
        if (name.Contains("VFX", StringComparison.OrdinalIgnoreCase) || name.Contains("effect", StringComparison.OrdinalIgnoreCase)) return new(.72f,.50f,.96f,1);
        if (name.Contains("Player", StringComparison.OrdinalIgnoreCase)) return new(.32f,.67f,.98f,1);
        if (name.Contains("Wwise", StringComparison.OrdinalIgnoreCase) || name.Contains("Sound", StringComparison.OrdinalIgnoreCase)) return new(.34f,.77f,.62f,1);
        if (name.Contains("Camera", StringComparison.OrdinalIgnoreCase)) return new(.95f,.67f,.30f,1);
        return new(.31f,.75f,.75f,1);
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
        rulerHeight = Math.Max(47, font * 2 + 12);
        labelWidth = showHierarchy ? Math.Clamp(hierarchyWidth, Math.Min(150, size.X * .35f), Math.Max(150, size.X * .42f)) : 0;
        double start = motlist.Motclips.Min(c => c.StartFrame), end = motlist.Motclips.Max(c => c.EndFrame);
        if (!view.Initialized)
        {
            double[] keys = motlist.Motclips.SelectMany(c => c.Tracks).SelectMany(t => t.AllProperties).SelectMany(p => p.Keys)
                .Select(k => k.Frame).Where(double.IsFinite).ToArray();
            view.Initialize(lanes, Math.Min(start, keys.DefaultIfEmpty(start).Min()), Math.Max(end, keys.DefaultIfEmpty(end).Max()));
        }
        view.RowHeight = Math.Max(view.RowHeight, font + 20);
        view.PlotMin = canvasMin + new Vector2(labelWidth, rulerHeight);
        view.PlotSize = new Vector2(Math.Max(1, size.X - labelWidth - 15), Math.Max(1, size.Y - rulerHeight));
        // Preserve the top visible row when expanding or filtering changes the row count.
        view.YMax += lanes.Count - view.RowCount;
        view.RowCount = lanes.Count;
        ClampVerticalView(view);
        AdvanceGesture(view, start, end);
        ClampVerticalView(view);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(canvasMin, canvasMax, Color(new(.075f,.079f,.095f,1)), 4);
        draw.AddRectFilled(canvasMin + new Vector2(labelWidth, 0), canvasMax, Color(new(.068f,.072f,.085f,1)));
        ImGui.SetCursorScreenPos(new(view.PlotMin.X, canvasMin.Y));
        ImGui.InvisibleButton("##RulerInput", new Vector2(view.PlotSize.X, rulerHeight),
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        bool rulerHovered = ImGui.IsItemHovered();
        ImGui.SetCursorScreenPos(view.PlotMin - new Vector2(labelWidth, 0));
        ImGui.InvisibleButton("##TimelineInput", new Vector2(size.X - 15, view.PlotSize.Y),
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        bool canvasHovered = ImGui.IsItemHovered();
        draw.PushClipRect(view.PlotMin, view.PlotMin + view.PlotSize, true);
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
        HandleTimelineInteraction(lanes, view, start, end, canvasHovered || rulerHovered);
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
        // The viewport is a custom ImGui item; do not manufacture scrollable child content.
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
        double step = TickStep(view.XMax - view.XMin, view.PlotSize.X), minor = step / 5;
        for (double f = Math.Ceiling(view.XMin / minor) * minor; f <= view.XMax; f += minor)
        {
            float x = FrameX(f); bool major = Math.Abs(f / step - Math.Round(f / step)) < .001;
            draw.AddLine(new(x, view.PlotMin.Y), new(x, view.PlotMin.Y + view.PlotSize.Y),
                Color(new(.60f,.63f,.73f,1), major ? .15f : .055f));
        }
    }
    private void DrawRuler(ImDrawListPtr draw, TimelineView view)
    {
        var right = new Vector2(view.PlotMin.X + view.PlotSize.X, canvasMin.Y + rulerHeight);
        draw.AddRectFilled(canvasMin, new(canvasMax.X, right.Y), Color(new(.095f,.10f,.12f,1)));
        if (showHierarchy) draw.AddText(canvasMin + new Vector2(12, 13), Color(MutedText), $"BEHAVIOR CLIPS  {motlist.Motclips.Count}");
        draw.PushClipRect(new(view.PlotMin.X, canvasMin.Y), right, true);
        double step = TickStep(view.XMax - view.XMin, view.PlotSize.X), minor = step / 5;
        for (double f = Math.Ceiling(view.XMin / minor) * minor; f <= view.XMax; f += minor)
        {
            float x = FrameX(f); bool major = Math.Abs(f / step - Math.Round(f / step)) < .001;
            draw.AddLine(new(x, right.Y - (major ? 14 : 6)), new(x, right.Y), Color(MutedText, .6f));
            if (major)
            {
                draw.AddText(new(x + 5, canvasMin.Y + 4), 0xFFE6E2DA, $"{f:0.##}");
                if (motion?.Header.FrameRate > 0) draw.AddText(new(x + 5, canvasMin.Y + ImGui.GetTextLineHeight() + 5),
                    Color(MutedText), $"{f / motion.Header.FrameRate:0.00}s");
            }
        }
        float head = FrameX(currentFrame);
        draw.AddTriangleFilled(new(head - 7, canvasMin.Y + 2), new(head + 7, canvasMin.Y + 2),
            new(head, canvasMin.Y + 15), 0xFF6060FA);
        draw.PopClipRect();
        draw.AddLine(new(canvasMin.X, right.Y), new(canvasMax.X, right.Y), 0xFF39363D);
    }
    private void DrawCanvasRow(ImDrawListPtr draw, VisibleLane lane)
    {
        float y = RowY(lane.Y), half = overviewView.RowHeight * .5f;
        var min = new Vector2(overviewView.PlotMin.X, y - half); var max = new Vector2(min.X + overviewView.PlotSize.X, y + half);
        bool selected = lane.Property is not null ? ReferenceEquals(lane.Property, selectedProperty)
            : lane.Track is not null ? selectedProperty is null && ReferenceEquals(lane.Track, selectedTrack)
            : selectedTrack is null && ReferenceEquals(lane.Clip, selectedClip);
        Vector4 tint = lane.Track is null ? NameColor(lane.Clip.Name) : TrackColor(lane.Track);
        if (lane.Track is null) draw.AddRectFilled(min, max, Color(new(.13f,.135f,.16f,1)));
        else if (selected) draw.AddRectFilled(min, max, Color(tint, .10f));
        else if (((int)lane.Y & 1) == 0) draw.AddRectFilled(min, max, 0x08000000);
        draw.AddLine(new(min.X, max.Y), max, 0x183E3C46);
        if (lane.Track is null)
        {
            string source = motionIndex?.OverrideClips.Contains(lane.Clip.Native) == true ? "Override" : "Embedded";
            string summary = $"{lane.Clip.Tracks.Count} tracks   /   {lane.Clip.Tracks.Sum(t => t.AllProperties.Count())} properties";
            string label=FitText($"{summary}     {source}    {lane.StartFrame:0.##} - {lane.EndFrame:0.##} frames",overviewView.PlotSize.X-24);
            draw.AddText(new(min.X + 12, y - ImGui.GetTextLineHeight() / 2), Color(MutedText),label);
            return;
        }
        if (lane.Property is null)
        {
            var box = GetRangeBox(lane.StartFrame, lane.EndFrame, lane);
            if (box.Max.X < min.X || box.Min.X > max.X) return;
            DrawBar(draw, box.Min, box.Max, tint, selected, .34f);
            string text = $"{CompactTrackName(lane.Track.Name)}    {(lane.Track.HasStoredBounds?"":"summary ")}{lane.StartFrame:0.##} - {lane.EndFrame:0.##} f";
            float left = Math.Max(box.Min.X + 10, min.X + 8), available = Math.Min(box.Max.X, max.X) - left - 8;
            if (available > 50) draw.AddText(new(left, y - ImGui.GetTextLineHeight() / 2), 0xFFF2EEE9, FitText(text, available));
            foreach (double frame in lane.Track.AllProperties.SelectMany(p => p.Keys).Select(k => k.Frame).Where(double.IsFinite).Distinct())
            {
                float x = FrameX(frame); if (x < box.Min.X || x > box.Max.X) continue;
                draw.AddLine(new(x, box.Max.Y - 5), new(x, box.Max.Y - 1), Color(tint), 2);
            }
            return;
        }
        DrawPropertyLane(draw, lane, lane.Property, tint);
    }
    private void DrawLaneLabels(ImDrawListPtr draw, List<VisibleLane> lanes)
    {
        if (!showHierarchy) return;
        var view = overviewView;
        draw.PushClipRect(new(canvasMin.X, view.PlotMin.Y), new(view.PlotMin.X, canvasMax.Y), true);
        foreach (var lane in lanes)
        {
            float y = RowY(lane.Y), half = view.RowHeight / 2;
            if (y + half < view.PlotMin.Y || y - half > canvasMax.Y) continue;
            Vector4 tint = lane.Track is null ? NameColor(lane.Clip.Name) : TrackColor(lane.Track);
            bool selected = lane.Property is not null ? ReferenceEquals(lane.Property, selectedProperty)
                : lane.Track is not null ? selectedProperty is null && ReferenceEquals(lane.Track, selectedTrack)
                : selectedTrack is null && ReferenceEquals(lane.Clip, selectedClip);
            var min = new Vector2(canvasMin.X, y - half); var max = new Vector2(view.PlotMin.X, y + half);
            if (lane.Track is null) draw.AddRectFilled(min, max, Color(new(.13f,.135f,.16f,1)));
            else if (selected) draw.AddRectFilled(min, max, Color(tint, .17f));
            draw.AddLine(new(min.X, max.Y), max, 0x183E3C46);
            float arrowX = min.X + 14 + lane.Depth * 17;
            if (LaneCanExpand(lane))
            {
                if (LaneExpanded(lane)) draw.AddTriangleFilled(new(arrowX-4,y-2), new(arrowX+4,y-2), new(arrowX,y+3), Color(MutedText));
                else draw.AddTriangleFilled(new(arrowX-2,y-4), new(arrowX-2,y+4), new(arrowX+3,y), Color(MutedText));
            }
            float textX = arrowX + 14;
            if(lane.Track is null)
            {
                var folder=new Vector2(arrowX+13,y-5);
                draw.AddRectFilled(folder+new Vector2(0,-3),folder+new Vector2(7,0),0xFF7B98C8,2);
                draw.AddRect(folder,folder+new Vector2(15,11),0xFF7B98C8,2,ImDrawFlags.None,1.5f);
                textX=arrowX+35;
            }
            string title = lane.Property?.Name ?? CompactTrackName(lane.Track?.Name ?? lane.Clip.Name);
            string count = lane.Property is { } p ? p.Container ? "GROUP" : p.Native.Info.DataType.ToString().ToUpperInvariant()
                : lane.Track is { } t ? t.AllProperties.Count() + " props" : "";
            float countWidth = ImGui.CalcTextSize(count).X;
            draw.AddText(new(textX, y-ImGui.GetTextLineHeight()/2), lane.Property?.Disabled == true ? Color(MutedText) : 0xFFE6E2DA,
                FitText(title, max.X-textX-countWidth-27));
            draw.AddText(new(max.X-countWidth-12, y-ImGui.GetTextLineHeight()/2), Color(MutedText), count);
            if (lane.Track is not null) draw.AddRectFilled(new(max.X-3,y-half+6), new(max.X-1,y+half-6), Color(tint,.8f));
            if (ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(min, max))
                ImGui.SetTooltip(lane.Path + (lane.Property is { } prop ? $"\n{prop.Keys.Count} stored keys / {prop.Native.Info.DataType}" : ""));
        }
        draw.PopClipRect();
        draw.AddLine(new(view.PlotMin.X, canvasMin.Y), new(view.PlotMin.X, canvasMax.Y), 0xFF39363D);
    }
    private void DrawBar(ImDrawListPtr draw, Vector2 min, Vector2 max, Vector4 tint, bool selected, float alpha)
    {
        if (max.X <= min.X) { draw.AddLine(min, new(min.X,max.Y), Color(tint), 2); return; }
        draw.AddRectFilled(min, max, Color(tint, alpha), 4);
        draw.AddRectFilledMultiColor(min+Vector2.One, max-Vector2.One, Color(Vector4.One,.09f), Color(Vector4.One,.09f), 0, 0);
        draw.AddRect(min, max, selected ? 0xFFF8F8F8 : Color(tint,.70f), 4, ImDrawFlags.None, selected ? 2 : 1);
    }
    private readonly record struct KeySegment(TimelineKey Start, TimelineKey? End, double EndFrame);
    private bool IsBoundaryKey(TimelineProperty property, TimelineKey key)
    {
        var ordered = property.Keys.Where(k => double.IsFinite(k.Frame)).OrderBy(k => k.Frame).ToList();
        int i = ordered.IndexOf(key); if (i <= 0) return false;
        var previous = ordered[i-1];
        return property.IsTerminal(key) && !property.IsTerminal(previous) ||
            i == ordered.Count-1 && !property.Disabled && key.Frame == property.EndFrame && key.Frame > previous.Frame && Equals(previous.RawValue,key.RawValue);
    }
    private List<KeySegment> GetKeySegments(TimelineProperty property)
    {
        var result = new List<KeySegment>();
        if (property.Container || property.Native.Info.DataType == PropertyType.Action) return result;
        var keys = property.Keys.Where(k => double.IsFinite(k.Frame)).OrderBy(k => k.Frame).ToArray();
        for (int i=0; i+1<keys.Length; i++)
        {
            bool boundary = i > 0 && (property.IsTerminal(keys[i]) && !property.IsTerminal(keys[i-1]) ||
                i == keys.Length-1 && !property.Disabled && keys[i].Frame == property.EndFrame &&
                keys[i].Frame > keys[i-1].Frame && Equals(keys[i].RawValue,keys[i-1].RawValue));
            if (boundary || property.IsTerminal(keys[i]) && property.Native.Info.DataType != PropertyType.Bool) continue;
            if (keys[i+1].Frame > keys[i].Frame) result.Add(new(keys[i],keys[i+1],keys[i+1].Frame));
        }
        return result;
    }
    private static string ValueKind(TimelineProperty property) => property.Native.Info.DataType switch
    { PropertyType.Bool => "Bool", PropertyType.Action => "Action", PropertyType.Enum => "Enum",
        _ => System.Text.RegularExpressions.Regex.IsMatch(property.Name, @"(?:Id|ID)[0-9]*$") ? "ID" : "Value" };
    private static string ValueText(TimelineProperty property, TimelineKey key) => $"{ValueKind(property)}: {key.DisplayValue}";
    private static string TimeText(double first,double last) => first==last ? $"Frame: {first:0.##}" : $"Frames: {first:0.##} - {last:0.##}";
    private static string SegmentCaption(TimelineProperty property, KeySegment span)
    {
        string caption = ValueText(property,span.Start)+"\n"+TimeText(span.Start.Frame,span.EndFrame);
        if (span.End is { } end && property.IsTerminal(end) && !property.IsTerminal(span.Start)) caption += $"\nEnd value: {end.DisplayValue}";
        return caption;
    }
    private void DrawPropertyLane(ImDrawListPtr draw, VisibleLane lane, TimelineProperty property, Vector4 tint)
    {
        float y = RowY(lane.Y);
        if (property.Container)
        {
            draw.AddText(new(overviewView.PlotMin.X+12,y-ImGui.GetTextLineHeight()/2), Color(MutedText), $"{property.Children.Count} child properties");
            return;
        }
        var ordered = property.Keys.Where(k=>double.IsFinite(k.Frame)).OrderBy(k=>k.Frame).ToArray();
        var groups = ordered.GroupBy(k=>k.Frame).ToArray();
        var segments = GetKeySegments(property);
        if(CurveLimits(property) is { } limits)
        {
            for(int i=0;i+1<ordered.Length;i++)
            {
                var a=KeyPosition(lane,ordered[i]);var b=KeyPosition(lane,ordered[i+1]);
                if(((NormalKey)ordered[i].Native).interpolation==InterpolationType.Discrete)
                {draw.AddLine(a,new(b.X,a.Y),Color(tint),2);draw.AddLine(new(b.X,a.Y),b,Color(tint,.5f),1);}
                else draw.AddLine(a,b,Color(tint),2);
            }
            foreach(var group in groups)
            {
                var key=group.FirstOrDefault(k=>ReferenceEquals(k,selectedKey))??group.First();
                DrawKeyMarker(draw,KeyPosition(lane,key),property,key,IsBoundaryKey(property,key),ReferenceEquals(key,selectedKey),tint);
            }
            return;
        }
        foreach (var span in segments)
        {
            var box = GetKeyRangeBox(lane,span.Start.Frame,span.EndFrame);
            bool selected = ReferenceEquals(span.Start,selectedKey) || ReferenceEquals(span.End,selectedKey);
            float opacity = property.Disabled ? .08f : property.Native.Info.DataType==PropertyType.Bool && span.Start.Value==0 ? .08f : .22f;
            DrawBar(draw,box.Min,box.Max,tint,selected,opacity);
            float x = Math.Max(box.Min.X+12,overviewView.PlotMin.X+8);
            string value = (ValueKind(property)=="ID" ? "ID " : "")+span.Start.DisplayValue;
            string range = $"{span.Start.Frame:0.##} - {span.EndFrame:0.##} f";
            string label = value;
            float room = Math.Min(box.Max.X,overviewView.PlotMin.X+overviewView.PlotSize.X)-x-9;
            float rangeWidth = ImGui.CalcTextSize(range).X;
            if (ImGui.CalcTextSize(value).X+rangeWidth+30<room)
                draw.AddText(new(Math.Min(box.Max.X,overviewView.PlotMin.X+overviewView.PlotSize.X)-rangeWidth-9,y-ImGui.GetTextLineHeight()/2),Color(tint,.85f),range);
            if (ImGui.CalcTextSize(label).X>room) label = "";
            if (label.Length>0) draw.AddText(new(x,y-ImGui.GetTextLineHeight()/2),0xFFF2EEE9,label);
        }
        for (int i=0;i<groups.Length;i++)
        {
            var key = groups[i].FirstOrDefault(k=>ReferenceEquals(k,selectedKey)) ?? groups[i].First();
            float x=FrameX(key.Frame);
            if(x<overviewView.PlotMin.X-8 || x>overviewView.PlotMin.X+overviewView.PlotSize.X+8)continue;
            bool boundary=IsBoundaryKey(property,key), selected=ReferenceEquals(key,selectedKey);
            DrawKeyMarker(draw,new(x,y),property,key,boundary,selected,tint);
            bool hasSpan=segments.Any(s=>ReferenceEquals(s.Start,key));
            if(!hasSpan && !boundary)
            {
                string label=groups[i].Count()>1?$"{groups[i].Count()} keys":key.DisplayValue;
                float next=i+1<groups.Length?FrameX(groups[i+1].Key):overviewView.PlotMin.X+overviewView.PlotSize.X;
                if(ImGui.CalcTextSize(label).X+12<next-x)draw.AddText(new(x+11,y-ImGui.GetTextLineHeight()/2),Color(tint),label);
            }
            if(groups[i].Count()>1)draw.AddText(new(x+7,y-overviewView.RowHeight*.43f),0xFFE6E2DA,$"{groups[i].Count()}");
        }
        if(ordered.Length==0 && ReferenceEquals(selectedProperty,property))
            draw.AddText(new(overviewView.PlotMin.X+12,y-ImGui.GetTextLineHeight()/2),Color(MutedText),"Double-click to create a key...");
    }
    private void DrawKeyMarker(ImDrawListPtr draw,Vector2 point,TimelineProperty property,TimelineKey key,bool boundary,bool selected,Vector4 tint)
    {
        float r=selected?7:5; uint color=Color(tint,property.Disabled?.45f:1), outline=selected?0xFFFFFFFF:color;
        if(boundary)
        {
            draw.AddLine(point+new Vector2(0,-10),point+new Vector2(0,10),outline,selected?3:2);
            draw.AddLine(point+new Vector2(-4,-10),point+new Vector2(0,-10),outline,2);
            draw.AddLine(point+new Vector2(-4,10),point+new Vector2(0,10),outline,2);
        }
        else if(property.Native.Info.DataType==PropertyType.Bool)
        {
            var min=point-new Vector2(r);var max=point+new Vector2(r);
            draw.AddRectFilled(min,max,key.Value==0?0xFF28232A:color,1);
            draw.AddRect(min,max,outline,1,ImDrawFlags.None,selected?2:1.5f);
        }
        else if(property.Native.Info.DataType==PropertyType.Action)
        {
            var a=point+new Vector2(0,-r-1);var b=point+new Vector2(r+1,r);var c=point+new Vector2(-r-1,r);
            draw.AddTriangleFilled(a,b,c,color);draw.AddTriangle(a,b,c,outline,selected?2:1);
        }
        else
        {
            var a=point+new Vector2(0,-r);var b=point+new Vector2(r,0);var c=point+new Vector2(0,r);var d=point+new Vector2(-r,0);
            draw.AddQuadFilled(a,b,c,d,color);draw.AddQuad(a,b,c,d,selected?0xFFFFFFFF:Color(tint),selected?2:1);
        }
    }
    private (Vector2 Min,Vector2 Max) GetKeyRangeBox(VisibleLane lane,double start,double end)
    {
        float y=RowY(lane.Y), half=Math.Clamp(overviewView.RowHeight*.29f,11,28);
        return(new(Math.Min(FrameX(start),FrameX(end)),y-half),new(Math.Max(FrameX(start),FrameX(end)),y+half));
    }
    private (Vector2 Min,Vector2 Max) GetRangeBox(double start,double end,VisibleLane lane)
    {
        if(lane.Property is not null)return GetKeyRangeBox(lane,start,end);
        float y=RowY(lane.Y),half=Math.Clamp(overviewView.RowHeight*.34f,13,32);
        return(new(Math.Min(FrameX(start),FrameX(end)),y-half),new(Math.Max(FrameX(start),FrameX(end)),y+half));
    }
    private (double Min,double Max)? CurveLimits(TimelineProperty property)
    {
        if(!showCurves||property.Container||property.Native.Info.DataType is not (PropertyType.F64 or PropertyType.F64_alt)||property.Keys.Count<2)return null;
        // Only display interpolation that can be represented faithfully here.
        // Hermite/Bezier remain editable keys with their native metadata retained.
        if(property.Keys.Any(k=>k.Native is not NormalKey n||n.interpolation is not (InterpolationType.Linear or InterpolationType.Discrete)
            ||k.Value is not { } v||!double.IsFinite(v)))return null;
        double min=property.Keys.Min(k=>k.Value!.Value),max=property.Keys.Max(k=>k.Value!.Value);
        return max-min<1e-9?(min-.5,max+.5):(min,max);
    }
    private Vector2 KeyPosition(VisibleLane lane,TimelineKey key)
    {
        float y=RowY(lane.Y);
        if(lane.Property is { } p&&CurveLimits(p) is { } limits&&key.Value is { } value)
            y+=(float)(.5-(value-limits.Min)/(limits.Max-limits.Min))*overviewView.RowHeight*.62f;
        return new(FrameX(key.Frame),y);
    }
    private void DrawPlayhead(ImDrawListPtr draw,TimelineView view)
    {
        float x=FrameX(currentFrame);
        draw.AddLine(new(x,view.PlotMin.Y),new(x,view.PlotMin.Y+view.PlotSize.Y),0xFF6060FA,2);
    }
    private void DrawCanvasScrollbar(TimelineView view,int count)
    {
        float content=count*view.RowHeight, visible=view.PlotSize.Y;
        if(content<=visible)return;
        float scroll=(float)(count-.5-view.YMax)*view.RowHeight, max=content-visible, position=max-scroll;
        ImGui.SetCursorScreenPos(new(canvasMax.X-12,view.PlotMin.Y));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding,5f);
        if(ImGui.VSliderFloat("##Rows",new Vector2(10,visible),ref position,0,max,""))view.YMax=count-.5-(max-position)/view.RowHeight;
        ImGui.PopStyleVar();
    }
}
