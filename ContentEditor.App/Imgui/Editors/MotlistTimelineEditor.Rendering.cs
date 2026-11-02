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
    private float overviewHeight=260;
    private void DrawDopeSheet(List<VisibleLane> overviewLanes)
    {
        bool detail = selectedTrack is not null && selectedClip is not null;
        float available = ImGui.GetContentRegionAvail().Y;
        if (ImGui.BeginChild("BehaviorOverview", new Vector2(0, detail ? Math.Clamp(overviewHeight,130,Math.Max(130,available-180)) : 0), ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.TextColored(new Vector4(.65f,.72f,1,1), $"BEHAVIOR CLIPS ({motlist.Motclips.Count}) / TRACK OVERVIEW");
            ImGui.SameLine(); if(ImGui.SmallButton("Fit##overview")){overviewView.FitAllRows=true;overviewView.Initialized=false;}
            DrawScopePlot(overviewLanes, overviewView);
        }
        ImGui.EndChild();
        if (!detail) return;
        ImGui.BeginDisabled(activeDrag is not null || rightView is not null);
        ImGui.InvisibleButton("ScopeSplitter",new Vector2(ImGui.GetContentRegionAvail().X,6));
        if(ImGui.IsItemHovered()||ImGui.IsItemActive())ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNs);
        if(ImGui.IsItemActive())overviewHeight=Math.Clamp(overviewHeight+ImGui.GetIO().MouseDelta.Y,130,Math.Max(130,available-180));
        ImGui.EndDisabled();
        if (ImGui.BeginChild("PropertyDopeSheet", Vector2.Zero, ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.TextColored(new Vector4(.45f,.8f,1,1), $"DOPE SHEET / {selectedClip!.Name} / {selectedTrack!.Name.Split('.').Last()}  ({selectedTrack.Properties.Count} properties)");
            ImGui.SameLine(); if(ImGui.SmallButton("Close scope")){CancelGesture();rightView=null;selectedTrack=null;selectedProperty=null;selectedKey=null;}
            if(selectedTrack is not null)
            {
                var view=PropertyView(selectedTrack); ImGui.SameLine(); if(ImGui.SmallButton("Fit##properties")){view.FitAllRows=true;view.Initialized=false;}
                DrawScopePlot(BuildPropertyLanes(selectedClip!,selectedTrack),view);
            }
        }
        ImGui.EndChild();
    }

    private unsafe void DrawScopePlot(List<VisibleLane> visibleLanes, TimelineView view)
    {
        double sequenceStart=motlist.Motclips.Min(c=>c.StartFrame), sequenceEnd=motlist.Motclips.Max(c=>c.EndFrame);
        view.Initialize(visibleLanes,sequenceStart,sequenceEnd);
        AdvanceGesture(view,sequenceStart,sequenceEnd);
        ImPlot.SetNextAxesLimits(view.XMin,view.XMax,view.YMin,view.YMax,ImPlotCond.Always);
        Vector2 plotSize=new(-1,Math.Max(40,ImGui.GetContentRegionAvail().Y));
        if (!ImPlot.BeginPlot(view.Id, plotSize, ImPlotFlags.NoTitle | ImPlotFlags.NoLegend | ImPlotFlags.NoMenus | ImPlotFlags.NoBoxSelect | ImPlotFlags.NoInputs | ImPlotFlags.NoMouseText))
        {
            return;
        }

        ImPlot.SetupAxis(ImAxis.X1, "Frames"u8, ImPlotAxisFlags.None);
        ImPlot.SetupAxis(ImAxis.Y1, ReferenceEquals(view,overviewView)?"Behavior clips / tracks":"Properties / keys", ImPlotAxisFlags.NoGridLines);

        double[] lanePositions = new double[visibleLanes.Count];
        string[] laneLabels = new string[visibleLanes.Count];
        for (int i = 0; i < visibleLanes.Count; i++)
        {
            lanePositions[i] = visibleLanes[i].Y;
            laneLabels[i] = DescribeLane(visibleLanes[i]);
        }

        if (lanePositions.Length > 0)
        {
            fixed (double* pLanePositions = lanePositions)
            {
                ImPlot.SetupAxisTicks(ImAxis.Y1, pLanePositions, lanePositions.Length, laneLabels, false);
            }
        }

        ImPlot.PushPlotClipRect();
        DrawSequenceBoundaries(sequenceStart, sequenceEnd, visibleLanes.Count);
        foreach (VisibleLane lane in visibleLanes)
        {
            DrawLaneBackground(lane);
            if (lane.Track is null)
            {
                DrawRangeBox(lane.StartFrame, lane.EndFrame, lane, new Vector4(0.56f, 0.53f, 0.95f, 1),
                    $"{lane.Clip.Name}  |  {lane.StartFrame:0.##} - {lane.EndFrame:0.##}");
                continue;
            }
            if (lane.Property is null && lane.Track is not null)
            {
                DrawTrackLane(lane);
                continue;
            }

            if (lane.Property is not TimelineProperty property)
            {
                continue;
            }

            bool selected = ReferenceEquals(property, selectedProperty);
            if (property.StartFrame.HasValue && property.EndFrame.HasValue
                && !(property.StartFrame == -1 && property.EndFrame == -1))
            {
                // Property bounds are visible even when stored keys have not been supplied.
                DrawRangeBox(property.StartFrame.Value, property.EndFrame.Value, lane,
                    new Vector4(0.35f, 0.65f, 0.92f, 1),
                    property.Keys.Count == 0 ? $"{property.Name}  |  {property.StartFrame:0.##} - {property.EndFrame:0.##}  |  0 keys" : null,
                    selected, 0.12f);
            }
            if (property.EndsOnMinusOne)
            {
                foreach (TimelineSpan span in GetPropertySpans(property))
                {
                    DrawRangeBox(span.StartFrame, span.EndFrame, lane,
                        new Vector4(0.35f, 0.72f, 1, 1), null, selected, 0.28f);
                }
            }

            foreach (TimelineKey key in property.Keys)
            {
                DrawPropertyKey(lane, key);
            }
        }

        ImPlot.PopPlotClipRect();
        HandleTimelineInteraction(visibleLanes, view, sequenceStart, sequenceEnd);
        DrawTimelineContextMenu();
        currentFrame = Math.Clamp(currentFrame, sequenceStart, sequenceEnd);
        ImPlot.PushPlotClipRect();
        DrawPlayhead();
        ImPlot.PopPlotClipRect();
        ImPlot.EndPlot();
        HandleKeyboardShortcuts(view,sequenceStart, sequenceEnd);
    }

    private void DrawTrackLane(VisibleLane lane)
    {
        ImDrawListPtr drawList = ImPlot.GetPlotDrawList();
        DrawRangeBox(lane.StartFrame, lane.EndFrame, lane, new Vector4(0.28f, 0.72f, 0.78f, 1),
            $"{lane.Track!.Name.Split('.').Last()}  |  {lane.StartFrame:0.##} - {lane.EndFrame:0.##}", selected:ReferenceEquals(lane.Track,selectedTrack));

        foreach (var group in lane.Track!.Properties.SelectMany(p=>p.Keys).GroupBy(k=>k.Frame).OrderBy(g=>g.Key))
        {
            Vector2 marker=ImPlot.PlotToPixels(group.Key,KeyLaneY(lane));
            uint color=ImGui.GetColorU32(new Vector4(.5f,.82f,.94f,1));
            drawList.AddCircleFilled(marker,3,color,8);
            string values=string.Join(", ",group.Select(k=>k.Value?.ToString("0.##")??"?").Distinct().Take(3));
            DrawKeyCaption(marker,$"{values}  @{group.Key:0.##}",color);
        }
    }

    private string DescribeLane(VisibleLane lane)
    {
        if(lane.Track is null)return $"MOTCLIP  {lane.Clip.Name}\n{lane.StartFrame:0.##} - {lane.EndFrame:0.##} frames";
        if(lane.Property is null)return $"TRACK  {lane.Track.Name.Split('.').Last()}\n{lane.StartFrame:0.##} - {lane.EndFrame:0.##}  |  {lane.Track.Properties.Count} properties";
        var p=lane.Property;string range=p.StartFrame.HasValue&&p.EndFrame.HasValue?$"{p.StartFrame:0.##} - {p.EndFrame:0.##}":"No active range";
        return $"PROPERTY  {p.Name}\n{range}  |  {p.Keys.Count} keys";
    }

    private double KeyLaneY(VisibleLane lane)=>lane.Y-.29;
    private Vector2 KeyPosition(VisibleLane lane,TimelineKey key)=>ImPlot.PlotToPixels(key.Frame,KeyLaneY(lane));
    private void DrawPropertyKey(VisibleLane lane,TimelineKey key)
    {
        Vector2 point=KeyPosition(lane,key);var plot=ImPlot.GetPlotPos();var size=ImPlot.GetPlotSize();
        if(point.X<plot.X-8||point.X>plot.X+size.X+8||point.Y<plot.Y||point.Y>plot.Y+size.Y)return;
        bool selected=ReferenceEquals(key,selectedKey);bool terminal=lane.Property!.EndsOnMinusOne&&key.Value==-1;
        uint color=ImGui.GetColorU32(terminal?new Vector4(1,.48f,.4f,1):selected?new Vector4(1,1,.7f,1):new Vector4(1,.78f,.3f,1));
        float radius=selected?6:4.5f;var draw=ImPlot.GetPlotDrawList();
        draw.AddQuadFilled(point+new Vector2(0,-radius),point+new Vector2(radius,0),point+new Vector2(0,radius),point+new Vector2(-radius,0),color);
        DrawKeyCaption(point,$"{key.Value?.ToString("0.##")??"?"}  @{key.Frame:0.##}",color);
    }
    private void DrawKeyCaption(Vector2 point,string caption,uint color)
    {
        float rowPixels=Math.Abs(ImPlot.PlotToPixels(0,1).Y-ImPlot.PlotToPixels(0,0).Y);
        if(rowPixels<24)return;
        ImPlot.GetPlotDrawList().AddText(point+new Vector2(9,-ImGui.GetTextLineHeight()*.5f),color,caption);
    }

    private void DrawSequenceBoundaries(double sequenceStart, double sequenceEnd, int laneCount)
    {
        if (laneCount == 0)
        {
            return;
        }

        Vector2 position = ImPlot.GetPlotPos();
        Vector2 size = ImPlot.GetPlotSize();
        uint color = ImGui.GetColorU32(new Vector4(1f, 0.72f, 0.16f, 0.85f));
        float startX = ImPlot.PlotToPixels(sequenceStart, 0).X;
        float endX = ImPlot.PlotToPixels(sequenceEnd, 0).X;
        ImDrawListPtr drawList = ImPlot.GetPlotDrawList();
        drawList.AddRectFilled(new Vector2(startX, position.Y), new Vector2(endX, position.Y + size.Y),
            ImGui.GetColorU32(new Vector4(1f, 0.72f, 0.16f, 0.035f)));
        drawList.AddLine(new Vector2(startX, position.Y), new Vector2(startX, position.Y + size.Y), color, 1.5f);
        drawList.AddLine(new Vector2(endX, position.Y), new Vector2(endX, position.Y + size.Y), color, 1.5f);
    }

    private void DrawPlayhead()
    {
        Vector2 plotPosition = ImPlot.GetPlotPos();
        Vector2 plotSize = ImPlot.GetPlotSize();
        float x = ImPlot.PlotToPixels(currentFrame, 0).X;
        ImDrawListPtr drawList = ImPlot.GetPlotDrawList();
        Vector2 top = new(x, plotPosition.Y + 1);
        Vector2 bottom = new(x, plotPosition.Y + plotSize.Y - 1);
        uint color = ImGui.GetColorU32(new Vector4(1, 0.25f, 0.25f, 1));
        drawList.AddLine(top, bottom, color, 2);
        drawList.AddTriangleFilled(new Vector2(x - 6, plotPosition.Y + 1), new Vector2(x + 6, plotPosition.Y + 1),
            new Vector2(x, plotPosition.Y + 11), color);
    }

    private List<TimelineSpan> GetPropertySpans(TimelineProperty property)
    {
        List<TimelineSpan> spans = new();
        if (!property.EndsOnMinusOne || property.Keys.Count == 0)
        {
            return spans;
        }

        double? activeStart = null;
        foreach (TimelineKey key in property.Keys.OrderBy(key => key.Frame))
        {
            if (key.Value == -1)
            {
                if (activeStart.HasValue && key.Frame > activeStart.Value)
                {
                    spans.Add(new TimelineSpan(activeStart.Value, key.Frame));
                }

                activeStart = null;
            }
            else if (key.Value.HasValue && !activeStart.HasValue)
            {
                activeStart = key.Frame;
            }
        }

        if (activeStart.HasValue)
        {
            double endFrame = property.EndFrame ?? property.Keys.Max(key => key.Frame);
            if (endFrame > activeStart.Value)
            {
                spans.Add(new TimelineSpan(activeStart.Value, endFrame));
            }
        }

        return spans;
    }

    // Heights are plot-space fractions of a row: vertical zoom scales the actual boxes.
    // Rendering and range hit testing use this same geometry.
    private (Vector2 Min, Vector2 Max) GetRangeBox(double startFrame, double endFrame, VisibleLane lane)
    {
        double halfHeight = lane.Track is null ? 0.34 : lane.Property is null ? 0.24 : 0.22;
        double center = lane.Y + (lane.Track is null ? 0 : 0.15);
        Vector2 first = ImPlot.PlotToPixels(startFrame, center + halfHeight);
        Vector2 last = ImPlot.PlotToPixels(endFrame, center - halfHeight);
        return (Vector2.Min(first, last), Vector2.Max(first, last));
    }

    private void DrawLaneBackground(VisibleLane lane)
    {
        Vector2 plot = ImPlot.GetPlotPos();
        Vector2 size = ImPlot.GetPlotSize();
        float first = ImPlot.PlotToPixels(0, lane.Y + 0.5).Y;
        float last = ImPlot.PlotToPixels(0, lane.Y - 0.5).Y;
        Vector2 min = new(plot.X, Math.Min(first, last));
        Vector2 max = new(plot.X + size.X, Math.Max(first, last));
        ImDrawListPtr draw = ImPlot.GetPlotDrawList();
        Vector4 tint = lane.Track is null ? new(0.32f, 0.29f, 0.55f, 0.12f)
            : lane.Property is null ? new(0.2f, 0.42f, 0.47f, 0.09f)
            : ReferenceEquals(lane.Property, selectedProperty) ? new(0.22f, 0.43f, 0.65f, 0.13f)
            : new(0.5f, 0.6f, 0.7f, ((int)lane.Y & 1) == 0 ? 0.025f : 0);
        draw.AddRectFilled(min, max, ImGui.GetColorU32(tint));
        draw.AddLine(new Vector2(min.X, max.Y), max, ImGui.GetColorU32(new Vector4(0.5f, 0.6f, 0.7f, 0.09f)));
    }

    private void DrawRangeBox(double startFrame, double endFrame, VisibleLane lane, Vector4 color,
        string? label = null, bool selected = false, float opacity = 0.2f)
    {
        var box = GetRangeBox(startFrame, endFrame, lane);
        if (box.Max.X <= box.Min.X || box.Max.Y <= box.Min.Y) return;
        Vector2 plot = ImPlot.GetPlotPos();
        Vector2 plotMax = plot + ImPlot.GetPlotSize();
        if (box.Max.X < plot.X || box.Min.X > plotMax.X || box.Max.Y < plot.Y || box.Min.Y > plotMax.Y) return;
        ImDrawListPtr draw = ImPlot.GetPlotDrawList();
        bool hovered = IsTimelinePlotHovered() && ImGui.IsMouseHoveringRect(box.Min, box.Max);
        Vector4 fill = color; fill.W = Math.Min(0.5f, opacity + (hovered ? 0.08f : 0) + (selected ? 0.08f : 0));
        Vector4 border = color; border.W = selected || hovered ? 0.95f : 0.65f;
        float rounding = Math.Min(4, Math.Min(box.Max.Y - box.Min.Y, box.Max.X - box.Min.X) * 0.2f);
        draw.AddRectFilled(box.Min, box.Max, ImGui.GetColorU32(fill), rounding);
        draw.AddRect(box.Min, box.Max, ImGui.GetColorU32(border), rounding, ImDrawFlags.None, selected ? 2 : 1.25f);
        // A short span remains at its true frame width, rather than inflating its endpoint.
        if (lane.Track is not null && box.Max.X - box.Min.X >= 18 && box.Max.Y - box.Min.Y >= 18)
        {
            float inset = Math.Min(8, (box.Max.Y - box.Min.Y) * 0.2f);
            draw.AddLine(new Vector2(box.Min.X+3,box.Min.Y+inset),new Vector2(box.Min.X+3,box.Max.Y-inset),ImGui.GetColorU32(border),2);
            draw.AddLine(new Vector2(box.Max.X-3,box.Min.Y+inset),new Vector2(box.Max.X-3,box.Max.Y-inset),ImGui.GetColorU32(border),2);
        }
        if (!string.IsNullOrEmpty(label) && box.Max.Y - box.Min.Y >= ImGui.GetTextLineHeight() + 8)
        {
            Vector2 text = new(Math.Max(box.Min.X + 10, plot.X + 8), (box.Min.Y + box.Max.Y - ImGui.GetTextLineHeight()) * 0.5f);
            float availableWidth = Math.Min(box.Max.X, plotMax.X) - text.X - 8;
            string caption = ImGui.CalcTextSize(label).X <= availableWidth ? label : label.Split('|')[0].Trim();
            if (ImGui.CalcTextSize(caption).X <= availableWidth)
            {
                draw.PushClipRect(Vector2.Max(box.Min, plot), Vector2.Min(box.Max, plotMax), true);
                draw.AddText(text, ImGui.GetColorU32(new Vector4(0.83f, 0.91f, 0.98f, 0.9f)), caption);
                draw.PopClipRect();
            }
        }
    }
}
