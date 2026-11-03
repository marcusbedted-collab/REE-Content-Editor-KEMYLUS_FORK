using System.Numerics;
using Hexa.NET.ImGui;
using ReeLib.Clip;
using ReeLib.Common;

namespace ContentEditor.App.ImguiHandling;

public partial class MotlistTimelineEditor
{
    private sealed class TimelineView
    {
        public string Id { get; }
        public bool Initialized;
        public double XMin, XMax, YMin, YMax;
        public int RowCount;
        public float RowHeight = 48;
        public Vector2 PlotMin, PlotSize;
        public TimelineView(string id) { Id = id; }
        public void Initialize(List<VisibleLane> lanes, double start, double end)
        {
            if (Initialized) return;
            double pad = Math.Max(1, (end-start)*.025);
            XMin = start-pad; XMax = Math.Max(XMin+2,end+pad);
            YMax = Math.Max(.5,lanes.Count-.5); RowCount = lanes.Count;
            Initialized = true;
        }
    }
    private readonly TimelineView overviewView = new("BehaviorCanvas");
    private TimelineView? keyboardView, rightView;
    private bool HasModifiers() { var io=ImGui.GetIO(); return io.KeyCtrl||io.KeyAlt||io.KeyShift||io.KeySuper; }
    private double FrameStep(double value) => snappingEnabled ? Math.Round(value,MidpointRounding.AwayFromZero) : value;
    private double SnapFrame(double value) => FrameStep(value);
    private void SelectTrack(Motclip clip, TimelineTrack track)
    {
        if (!ReferenceEquals(selectedTrack,track)) { CancelGesture(); selectedProperty=null; selectedKey=null; }
        selectedClip=clip; selectedTrack=track;
    }
    private void SelectLane(VisibleLane lane, TimelineKey? key)
    {
        if(lane.Clip is null)return;
        if(lane.Track is null) { selectedClip=lane.Clip; selectedTrack=null; selectedProperty=null; selectedKey=null; return; }
        SelectTrack(lane.Clip,lane.Track); selectedProperty=lane.Property; selectedKey=key;
    }
    private PropertyDragSnapshot CaptureProperty(TimelineProperty p) => new(p,p.StartFrame,p.EndFrame,p.Keys.Select(k=>(k,k.Frame)).ToList());
    private TimelineDrag Gesture(EditMode mode, TimelineView view, VisibleLane lane=default, TimelineKey? key=null,
        ImGuiMouseButton button=ImGuiMouseButton.Left, double? start=null, double? end=null, TimelineKey? endKey=null)
    {
        var mouse=ImGui.GetMousePos(); var t=lane.Track; var p=lane.Property;
        bool editing=mode is not EditMode.Pan and not EditMode.Playhead and not EditMode.None;
        return new TimelineDrag {
            Mode=mode, View=view, Lane=lane, Track=t, Property=p, Key=key, EndKey=endKey, Button=button,
            BoundaryKey=p is not null&&key is not null&&IsBoundaryKey(p,key),
            InitialStart=start??lane.StartFrame, InitialEnd=end??lane.EndFrame, InitialFrame=key?.Frame??currentFrame,
            OwnerStart=t?.Owner.StartFrame??0, OwnerEnd=t?.Owner.EndFrame??1,
            StartMouseX=mouse.X, StartMouseY=mouse.Y, StartPlotWidth=view.PlotSize.X, StartPlotHeight=view.PlotSize.Y,
            InitialXMin=view.XMin, InitialXMax=view.XMax, InitialYMin=view.YMin, InitialYMax=view.YMax, InitialRowHeight=view.RowHeight,
            TrackOrder=mode==EditMode.ReorderTrack?lane.Clip.Tracks.ToList():null,
            PropertyOrder=mode==EditMode.ReorderProperty?p?.Siblings.ToList():null,
            Keys=editing&&p is not null?p.Keys.Select(k=>(k,k.Frame)).ToList():new(),
            Properties=editing?(p is not null?p.Descendants():t?.AllProperties??Enumerable.Empty<TimelineProperty>()).Select(CaptureProperty).ToList():new()
        };
    }
    private void CancelGesture()
    {
        if(activeDrag is not { } d)return;
        if(d.Mode==EditMode.Pan)
        { d.View.XMin=d.InitialXMin; d.View.XMax=d.InitialXMax; d.View.YMin=d.InitialYMin; d.View.YMax=d.InitialYMax; }
        else if(d.Mode==EditMode.Playhead)currentFrame=d.InitialFrame;
        pendingEdit?.Apply();pendingEdit=null;
        if(d.TrackOrder is not null) { d.Lane.Clip.Tracks.Clear(); d.Lane.Clip.Tracks.AddRange(d.TrackOrder); }
        if(d.PropertyOrder is not null&&d.Property is not null) { d.Property.Siblings.Clear(); d.Property.Siblings.AddRange(d.PropertyOrder); }
        foreach(var p in d.Properties)
        { p.Property.StartFrame=p.Start; p.Property.EndFrame=p.End; foreach(var k in p.Keys)k.Key.Frame=k.Frame; }
        if(d.Property is not null)foreach(var key in d.CreatedKeys){d.Property.Keys.Remove(key);if(ReferenceEquals(selectedKey,key))selectedKey=d.Key;}
        activeDrag=null;
    }
    private void AdvanceGesture(TimelineView view,double sequenceStart,double sequenceEnd)
    {
        var d=activeDrag;if(d is null)return;
        if(ImGui.IsKeyPressed(ImGuiKey.Escape,false)){CancelGesture();return;}
        if(!ImGui.IsMouseDown(d.Button))
        {
            activeDrag=null;
            if(d.Started)
            {
                if(d.Property is not null)d.Property.Keys.Sort((a,b)=>a.Frame.CompareTo(b.Frame));
                FinishEdit();
            }
            else { pendingEdit=null; SelectLane(d.Lane,d.Key); }
            return;
        }
        var mouse=ImGui.GetMousePos();
        if(!d.Started)
        {
            if(Vector2.DistanceSquared(mouse,new(d.StartMouseX,d.StartMouseY))<25)return;
            if(d.Mode is not EditMode.Pan and not EditMode.Playhead and not EditMode.None)BeginEdit();
            d.Started=true;
        }
        if(ImGui.GetIO().MouseDelta==Vector2.Zero)return;
        double delta=(mouse.X-d.StartMouseX)/Math.Max(1,d.StartPlotWidth)*(d.InitialXMax-d.InitialXMin);
        if(d.Mode==EditMode.Pan)
        {
            view.XMin=d.InitialXMin-delta;view.XMax=d.InitialXMax-delta;
            view.YMax=d.InitialYMax+(mouse.Y-d.StartMouseY)/d.InitialRowHeight;return;
        }
        delta=FrameStep(delta);
        if(d.Mode==EditMode.Playhead){currentFrame=Math.Clamp(d.InitialFrame+delta,sequenceStart,sequenceEnd);return;}
        if(d.Track is null)return;
        if(d.Mode==EditMode.ReorderTrack&&d.TrackOrder is not null)
        {
            var target=lastLanes.Where(l=>l.Property is null&&l.Track is not null&&ReferenceEquals(l.Clip,d.Lane.Clip)).MinBy(l=>Math.Abs(RowY(l.Y)-mouse.Y));
            if(target.Track is null)return;
            int index=d.TrackOrder.IndexOf(target.Track);
            var order=d.TrackOrder.Where(t=>!ReferenceEquals(t,d.Track)).ToList();order.Insert(Math.Clamp(index,0,order.Count),d.Track);
            d.Lane.Clip.Tracks.Clear();d.Lane.Clip.Tracks.AddRange(order);return;
        }
        if(d.Mode==EditMode.ReorderProperty&&d.PropertyOrder is not null&&d.Property is not null)
        {
            var target=lastLanes.Where(l=>l.Property is not null&&d.PropertyOrder.Contains(l.Property)).MinBy(l=>Math.Abs(RowY(l.Y)-mouse.Y));
            if(target.Property is null)return;
            int index=d.PropertyOrder.IndexOf(target.Property);
            var order=d.PropertyOrder.Where(p=>!ReferenceEquals(p,d.Property)).ToList();order.Insert(Math.Clamp(index,0,order.Count),d.Property);
            d.Property.Siblings.Clear();d.Property.Siblings.AddRange(order);return;
        }
        if(d.Mode==EditMode.Key&&d.Key is not null&&d.Property is not null)
        {
            double min=d.BoundaryKey?Math.Max(d.OwnerStart,d.Keys.Where(k=>k.Frame<d.InitialFrame).Select(k=>k.Frame).DefaultIfEmpty(d.OwnerStart).Max()):d.OwnerStart;
            double max=!d.BoundaryKey&&d.EndKey is not null?Math.Min(d.OwnerEnd,d.EndKey.Frame):d.OwnerEnd;
            if(min>max)return;d.Key.Frame=Math.Clamp(d.InitialFrame+delta,min,max);
            var original=d.Properties[0];
            if(!(original.Start==-1&&original.End==-1))
            {d.Property.StartFrame=Math.Min(original.Start??d.Key.Frame,d.Key.Frame);d.Property.EndFrame=Math.Max(original.End??d.Key.Frame,d.Key.Frame);}
            return;
        }
        if(d.Mode==EditMode.KeySpanMove&&d.Key is not null&&d.Property is not null)
        {
            if(d.OwnerEnd-d.OwnerStart<d.InitialEnd-d.InitialStart)return;
            delta=Math.Clamp(delta,d.OwnerStart-d.InitialStart,d.OwnerEnd-d.InitialEnd);
            d.Key.Frame=d.InitialStart+delta;if(d.EndKey is not null)d.EndKey.Frame=d.InitialEnd+delta;
            var original=d.Properties[0];
            if(original.Start==d.InitialStart&&original.End==d.InitialEnd){d.Property.StartFrame=d.Key.Frame;d.Property.EndFrame=d.InitialEnd+delta;}
            else if(!(original.Start==-1&&original.End==-1))
            {d.Property.StartFrame=Math.Min(original.Start??d.Key.Frame,d.Key.Frame);d.Property.EndFrame=Math.Max(original.End??d.InitialEnd,d.InitialEnd+delta);}
            return;
        }
        if(d.Mode==EditMode.PropertyEndpoint&&d.Property is not null)
        {
            double min=Math.Max(d.OwnerStart,d.InitialStart+(snappingEnabled?1:.001));if(min>d.OwnerEnd)return;
            double end=Math.Clamp(d.InitialEnd+delta,min,d.OwnerEnd);
            if(d.EndKey is null)
            {
                d.EndKey=d.Key is not null?new TimelineKey(d.Key.Native.DeepClone<KeyBase>()!):d.Property.NewKey("End",end,null);
                if(d.Property.CanCreateTerminal)d.EndKey.Value=d.Property.TerminalNumber;
                d.Property.Keys.Add(d.EndKey);d.CreatedKeys.Add(d.EndKey);
            }
            d.EndKey.Frame=end;
            var original=d.Properties[0];bool disabled=original.Start==-1&&original.End==-1;
            d.Property.StartFrame=disabled?d.InitialStart:Math.Min(original.Start??d.InitialStart,d.InitialStart);
            d.Property.EndFrame=disabled||original.End==d.InitialEnd?end:Math.Max(original.End??end,end);
            selectedKey=d.EndKey;return;
        }
        if(d.Mode is EditMode.TrackMove or EditMode.PropertyMove)
        {
            var frames=d.Properties.SelectMany(p=>p.Keys.Select(k=>k.Frame)
                .Concat(p.Start==-1&&p.End==-1?Array.Empty<double>():new[]{p.Start??d.InitialStart,p.End??d.InitialEnd}))
                .Where(double.IsFinite).Append(d.InitialStart).Append(d.InitialEnd).ToArray();
            double first=frames.Min(),last=frames.Max();
            if(d.OwnerEnd-d.OwnerStart<last-first)return;
            delta=Math.Clamp(delta,d.OwnerStart-first,d.OwnerEnd-last);
            if(d.Property is null){d.Track.StartFrame=d.InitialStart+delta;d.Track.EndFrame=d.InitialEnd+delta;}
            foreach(var p in d.Properties)
            {
                if(!(p.Start==-1&&p.End==-1)){p.Property.StartFrame=p.Start+delta;p.Property.EndFrame=p.End+delta;}
                foreach(var key in p.Keys)key.Key.Frame=key.Frame+delta;
            }
            return;
        }
        if(d.Mode==EditMode.TrackStart)
        {
            double first=d.Properties.SelectMany(p=>p.Keys.Select(k=>k.Frame)
                .Concat(p.Start.HasValue&&!(p.Start==-1&&p.End==-1)?new[]{p.Start.Value}:Array.Empty<double>())).DefaultIfEmpty(d.InitialEnd).Min();
            double max=Math.Min(first,d.InitialEnd);if(d.OwnerStart<=max)d.Track.StartFrame=Math.Clamp(d.InitialStart+delta,d.OwnerStart,max);
        }
        else if(d.Mode==EditMode.TrackEnd)
        {
            double last=d.Properties.SelectMany(p=>p.Keys.Select(k=>k.Frame)
                .Concat(p.End.HasValue&&!(p.Start==-1&&p.End==-1)?new[]{p.End.Value}:Array.Empty<double>())).DefaultIfEmpty(d.InitialStart).Max();
            double min=Math.Max(last,d.InitialStart);if(min<=d.OwnerEnd)d.Track.EndFrame=Math.Clamp(d.InitialEnd+delta,min,d.OwnerEnd);
        }
        else if(d.Property is not null)
        {
            if(d.Mode==EditMode.PropertyStart)d.Property.StartFrame=Math.Clamp(d.InitialStart+delta,d.OwnerStart,d.InitialEnd);
            else if(d.Mode==EditMode.PropertyEnd)d.Property.EndFrame=Math.Clamp(d.InitialEnd+delta,d.InitialStart,d.OwnerEnd);
        }
    }
    private readonly record struct Pick(VisibleLane Lane,TimelineKey? Key,EditMode Mode,TimelineKey? EndKey=null,double? EndFrame=null);
    private Pick? PickTimeline(List<VisibleLane> lanes,Vector2 mouse)
    {
        if(mouse.X<overviewView.PlotMin.X)return null;
        foreach(var lane in lanes)
        {
            if(Math.Abs(mouse.Y-RowY(lane.Y))>overviewView.RowHeight/2)continue;
            if(lane.Property is { } p)
            {
                var nearest=p.Keys.Where(k=>double.IsFinite(k.Frame)).OrderBy(k=>Vector2.DistanceSquared(mouse,KeyPosition(lane,k))).FirstOrDefault();
                if(nearest is not null&&Vector2.DistanceSquared(mouse,KeyPosition(lane,nearest))<=100)return new(lane,nearest,EditMode.Key);
                foreach(var span in GetKeySegments(p))
                {
                    if(CurveLimits(p) is not null&&span.End is { } curveEnd)
                    {
                        var a=KeyPosition(lane,span.Start);var b=KeyPosition(lane,curveEnd);
                        if(mouse.X<a.X||mouse.X>b.X)continue;
                        float ratio=(mouse.X-a.X)/Math.Max(1,b.X-a.X);
                        float lineY=((NormalKey)span.Start.Native).interpolation==InterpolationType.Discrete?a.Y:a.Y+(b.Y-a.Y)*ratio;
                        if(Math.Abs(mouse.Y-lineY)<=6)return new(lane,span.Start,EditMode.KeySpanMove,span.End,span.EndFrame);
                        continue;
                    }
                    var box=GetKeyRangeBox(lane,span.Start.Frame,span.EndFrame);
                    if(mouse.X>=box.Min.X&&mouse.X<=box.Max.X&&mouse.Y>=box.Min.Y&&mouse.Y<=box.Max.Y)
                        return new(lane,span.Start,EditMode.KeySpanMove,span.End,span.EndFrame);
                }
                return null;
            }
            if(lane.Track is null)return null;
            var range=GetRangeBox(lane.StartFrame,lane.EndFrame,lane);
            if(mouse.X<range.Min.X-4||mouse.X>range.Max.X+4||mouse.Y<range.Min.Y||mouse.Y>range.Max.Y)return null;
            return new(lane,null,!lane.Track.HasStoredBounds?EditMode.TrackMove:mouse.X-range.Min.X<7?EditMode.TrackStart:range.Max.X-mouse.X<7?EditMode.TrackEnd:EditMode.TrackMove);
        }
        return null;
    }
    private VisibleLane? LaneUnderMouse(List<VisibleLane> lanes,Vector2 mouse)
    {
        if(mouse.Y<overviewView.PlotMin.Y||mouse.Y>canvasMax.Y)return null;
        foreach(var lane in lanes)if(Math.Abs(mouse.Y-RowY(lane.Y))<=overviewView.RowHeight/2)return lane;
        return null;
    }
    private void BeginRangeExtension(TimelineView view,VisibleLane lane,TimelineKey? key,EditMode mode)
    {
        if(lane.Track is null||lane.Property?.Container==true)return;
        SelectLane(lane,key);
        if(lane.Property is { } p)
        {
            if(key is null) { RequestKeyCreation(p,FrameStep(MouseFrame(ImGui.GetMousePos())));draftHasEnd=true;return; }
            if(p.Native.Info.DataType!=PropertyType.Action)
            {
                var span=GetKeySegments(p).FirstOrDefault(s=>ReferenceEquals(s.Start,key)||ReferenceEquals(s.End,key));
                var start=span.Start??key;var end=span.End;
                activeDrag=Gesture(EditMode.PropertyEndpoint,view,lane,start,ImGuiMouseButton.Right,start.Frame,end?.Frame??start.Frame,end);
            }
            return;
        }
        if(!lane.Track.HasStoredBounds)return;
        activeDrag=Gesture(mode==EditMode.TrackStart?EditMode.TrackStart:EditMode.TrackEnd,view,lane,null,ImGuiMouseButton.Right);
    }
    private void HandleTimelineInteraction(List<VisibleLane> lanes,TimelineView view,double sequenceStart,double sequenceEnd,bool hovered)
    {
        // One captured owner is updated before hover tests, including outside the widget.
        if(activeDrag is not null)return;
        var io=ImGui.GetIO();var mouse=ImGui.GetMousePos();
        bool plain=!HasModifiers(),shift=io.KeyShift&&!io.KeyCtrl&&!io.KeyAlt&&!io.KeySuper,alt=io.KeyAlt&&!io.KeyCtrl&&!io.KeyShift&&!io.KeySuper;
        bool context=false;
        if(rightView is not null)
        {
            if(!ImGui.IsMouseReleased(ImGuiMouseButton.Right))return;
            rightView=null;context=hovered&&plain&&Vector2.DistanceSquared(mouse,contextMouseDownPosition)<=16;
            if(!context)return;
        }
        if(!hovered||ImGui.IsPopupOpen("",ImGuiPopupFlags.AnyPopupId|ImGuiPopupFlags.AnyPopupLevel))return;
        bool leftHeld=ImGui.IsMouseDown(ImGuiMouseButton.Left),rightHeld=ImGui.IsMouseDown(ImGuiMouseButton.Right);
        if(io.MouseWheel!=0&&!leftHeld&&!rightHeld&&!io.KeyAlt&&!io.KeySuper&&!(io.KeyCtrl&&io.KeyShift))
        {
            double factor=Math.Pow(1.15,-io.MouseWheel);
            if(!io.KeyCtrl)
            {
                double at=MouseFrame(mouse),ratio=(at-view.XMin)/(view.XMax-view.XMin),span=Math.Clamp((view.XMax-view.XMin)*factor,2,1e6);
                view.XMin=at-ratio*span;view.XMax=view.XMin+span;
            }
            if(!io.KeyShift)
            {
                double at=view.YMax-(mouse.Y-view.PlotMin.Y)/view.RowHeight;
                view.RowHeight=Math.Clamp(view.RowHeight/(float)factor,ImGui.GetTextLineHeight()+20,120);
                view.YMax=at+(mouse.Y-view.PlotMin.Y)/view.RowHeight;
            }
            keyboardView=view;return;
        }
        var lane=LaneUnderMouse(lanes,mouse);var pick=PickTimeline(lanes,mouse);
        if(!leftHeld&&!rightHeld&&pick is { Key: { } key } hit)
            ImGui.SetTooltip(hit.Lane.Property!.Name+"\n"+ValueText(hit.Lane.Property,key)+"\n"+TimeText(key.Frame,hit.EndFrame??key.Frame)+"\nAlt + right: move  |  Shift + right: extend end");
        bool left=ImGui.IsMouseClicked(ImGuiMouseButton.Left)&&!rightHeld, right=ImGui.IsMouseClicked(ImGuiMouseButton.Right)&&!leftHeld;
        if(!left&&!right&&!context)return;
        keyboardView=view;
        if(context)
        {
            contextClip=lane?.Clip??selectedClip??motlist.Motclips.FirstOrDefault();contextTrack=lane?.Track;contextProperty=lane?.Property;contextKey=pick?.Key;
            contextFrame=FrameStep(MouseFrame(mouse));ImGui.OpenPopup("TimelineContext");return;
        }
        if(plain&&right){rightView=view;contextMouseDownPosition=mouse;return;}
        if(alt&&left)
        {
            currentFrame=Math.Clamp(FrameStep(MouseFrame(mouse)),sequenceStart,sequenceEnd);
            activeDrag=Gesture(EditMode.Playhead,view);activeDrag.Started=true;return;
        }
        if(plain&&left)
        {
            if(lane is { } row)
            {
                float arrowX=canvasMin.X+14+row.Depth*17;
                if(mouse.X<view.PlotMin.X&&Math.Abs(mouse.X-arrowX)<10&&LaneCanExpand(row)){ToggleLane(row);return;}
                if(ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    SelectLane(row,pick?.Key);
                    if(mouse.X<view.PlotMin.X&&LaneCanExpand(row)){ToggleLane(row);return;}
                    if(mouse.X>=view.PlotMin.X&&row.Property is { Container:false } p&&pick?.Key is null){RequestKeyCreation(p,FrameStep(MouseFrame(mouse)));return;}
                    if(pick?.Key is not null)showDetails=true;
                }
            }
            activeDrag=Gesture(EditMode.Pan,view,lane??default,pick?.Key);return;
        }
        if(lane is not { } editLane)return;
        if(shift&&right){BeginRangeExtension(view,editLane,pick?.Key,pick?.Mode??EditMode.None);return;}
        if(alt&&right&&pick is { } move)
        {
            SelectLane(editLane,move.Key);
            activeDrag=Gesture(move.Key is not null?move.Mode:EditMode.TrackMove,view,editLane,move.Key,ImGuiMouseButton.Right,
                move.Key?.Frame??editLane.StartFrame,move.EndFrame??move.Key?.Frame??editLane.EndFrame,
                move.EndKey??(move.Key is not null&&editLane.Property is { } p?GetKeySegments(p).FirstOrDefault(s=>ReferenceEquals(s.Start,move.Key)).End:null));return;
        }
        if(shift&&left&&editLane.Track is not null)
        {SelectLane(editLane,pick?.Key);activeDrag=Gesture(editLane.Property is null?EditMode.ReorderTrack:EditMode.ReorderProperty,view,editLane,pick?.Key);}
    }
}
