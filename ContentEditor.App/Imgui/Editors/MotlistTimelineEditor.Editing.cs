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
    private void DrawTimelineContextMenu()
    {
        if (!ImGui.BeginPopup("TimelineContext"u8))
        {
            return;
        }

        if (contextProperty is not null)
        {
            if(ImGui.InputDouble("Key value"u8, ref newKeyValue) && contextKey is not null && double.IsFinite(newKeyValue)) contextKey.Value=newKeyValue;
        }
        else
        {
            if(contextTrack is not null && ImGui.MenuItem("Copy track")) CopyTrack(contextTrack);
            bool canPaste = contextClip is not null && copiedTrack is not null
                && copiedTrack.StartFrame >= contextClip.StartFrame && copiedTrack.EndFrame <= contextClip.EndFrame;
            ImGui.BeginDisabled(!canPaste);
            if(ImGui.MenuItem("Paste as new track")) PasteTrack(contextClip!);
            ImGui.EndDisabled();
            ImGui.Separator();
        }
        if (contextKey is not null && contextProperty is not null)
        {
            selectedKey = contextKey;
            selectedProperty = contextProperty;
            if (ImGui.MenuItem("Copy key"u8))
            {
                copiedKeyValue = contextKey.Value;
            }
            if (copiedKeyValue.HasValue && ImGui.MenuItem("Paste key here"u8))
            {
                AddKey(contextProperty, contextFrame, copiedKeyValue);
            }
            if (ImGui.MenuItem("Remove key"u8))
            {
                contextProperty.Keys.Remove(contextKey);
                selectedKey = null;
            }
        }
        else if (contextProperty is not null)
        {
            selectedProperty = contextProperty;
            if (ImGui.MenuItem("Add key here"u8))
            {
                AddKey(contextProperty, contextFrame, newKeyValue);
            }
            if (copiedKeyValue.HasValue && ImGui.MenuItem("Paste key here"u8))
            {
                AddKey(contextProperty, contextFrame, copiedKeyValue);
            }
        }

        ImGui.EndPopup();
    }

    private TimelineTrack CloneTrack(TimelineTrack source)
    {
        var clone = new TimelineTrack(source.Name, source.Details, source.StartFrame, source.EndFrame);
        foreach (var property in source.Properties)
        {
            var copy = new TimelineProperty(property.Name, property.ValueRange, property.EndsOnMinusOne, property.StartFrame, property.EndFrame);
            foreach (var key in property.Keys) copy.Keys.Add(new TimelineKey(key.Name, key.Frame, key.Value));
            clone.Properties.Add(copy);
        }
        return clone;
    }

    private void CopyTrack(TimelineTrack track) => copiedTrack = CloneTrack(track);

    private TimelineTrack? PasteTrack(Motclip owner)
    {
        if(copiedTrack is null || copiedTrack.StartFrame < owner.StartFrame || copiedTrack.EndFrame > owner.EndFrame) return null;
        var clone = CloneTrack(copiedTrack);
        owner.Tracks.Add(clone);
        overviewView.Initialized = false;
        return clone;
    }

    private void AddKey(TimelineProperty property, double frame, double? value)
    {
        TimelineTrack? track = motlist.Motclips.SelectMany(clip => clip.Tracks)
            .FirstOrDefault(candidate => candidate.Properties.Contains(property));
        if (track is null)
        {
            return;
        }

        double min = Math.Max(track.StartFrame, property.StartFrame ?? track.StartFrame);
        double max = Math.Min(track.EndFrame, property.EndFrame ?? track.EndFrame);
        if (max < min)
        {
            return;
        }

        TimelineKey key = new($"Key {property.Keys.Count + 1}", Math.Clamp(SnapFrame(frame), min, max), value);
        property.Keys.Add(key);
        property.Keys.Sort((left, right) => left.Frame.CompareTo(right.Frame));
        selectedProperty = property;
        selectedKey = key;
    }

    private void HandleKeyboardShortcuts(TimelineView view, double sequenceStart, double sequenceEnd)
    {
        if (!ReferenceEquals(keyboardView,view) || ImGui.GetIO().WantTextInput || ImGui.IsAnyItemActive() || ImGui.IsPopupOpen("",ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel) || activeDrag is not null || rightView is not null || !ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
        {
            return;
        }

        var io=ImGui.GetIO();
        if(io.KeyAlt||io.KeyShift||io.KeySuper)return;
        bool control=io.KeyCtrl;
        if (control && ImGui.IsKeyPressed(ImGuiKey.C) && selectedKey is not null)
        {
            copiedKeyValue = selectedKey.Value;
        }
        if (control && ImGui.IsKeyPressed(ImGuiKey.V) && copiedKeyValue.HasValue && selectedProperty is not null)
        {
            AddKey(selectedProperty, currentFrame, copiedKeyValue);
        }
        if (!control && ImGui.IsKeyPressed(ImGuiKey.Delete) && selectedKey is not null && selectedProperty is not null)
        {
            selectedProperty.Keys.Remove(selectedKey);
            selectedKey = null;
        }
        if (!control && ImGui.IsKeyPressed(ImGuiKey.LeftArrow))
        {
            currentFrame = SnapFrame(Math.Max(sequenceStart, currentFrame - 1));
        }
        if (!control && ImGui.IsKeyPressed(ImGuiKey.RightArrow))
        {
            currentFrame = SnapFrame(Math.Min(sequenceEnd, currentFrame + 1));
        }
        if (!control && ImGui.IsKeyPressed(ImGuiKey.Home))
        {
            currentFrame = sequenceStart;
        }
        if (!control && ImGui.IsKeyPressed(ImGuiKey.End))
        {
            currentFrame = sequenceEnd;
        }
        if (!control && (ImGui.IsKeyPressed(ImGuiKey.UpArrow) || ImGui.IsKeyPressed(ImGuiKey.DownArrow)))
        {
            IEnumerable<double> frames = selectedProperty is not null
                ? selectedProperty.Keys.Select(key => key.Frame)
                : motlist.Motclips.SelectMany(clip => clip.Tracks)
                    .SelectMany(track => track.Properties)
                    .SelectMany(property => property.Keys.Select(key => key.Frame));
            double? target = ImGui.IsKeyPressed(ImGuiKey.UpArrow)
                ? frames.Where(frame => frame < currentFrame - 0.001).DefaultIfEmpty(sequenceStart).Max()
                : frames.Where(frame => frame > currentFrame + 0.001).DefaultIfEmpty(sequenceEnd).Min();
            currentFrame = Math.Clamp(SnapFrame(target.Value), sequenceStart, sequenceEnd);
        }
        if (!control && ImGui.IsKeyPressed(ImGuiKey.K) && selectedProperty is not null)
        {
            AddKey(selectedProperty, currentFrame, selectedProperty.Keys.LastOrDefault()?.Value ?? newKeyValue);
        }
    }

    // One viewport owns a gesture. No ImPlot navigation runs beside this controller.
    private sealed class TimelineView
    {
        public string Id {get;}
        public bool Initialized,FitAllRows;
        public double XMin,XMax,YMin,YMax;
        public Vector2 PlotMin,PlotSize;
        public TimelineView(string id){Id=id;}
        public void Initialize(List<VisibleLane> lanes,double start,double end)
        {
            if(Initialized)return;double pad=Math.Max(1,(end-start)*.025);
            XMin=start-pad;XMax=end+pad;YMax=Math.Max(.5,lanes.Count-.5);
            YMin=YMax-Math.Max(2,FitAllRows?lanes.Count:Math.Min(lanes.Count,6));Initialized=true;FitAllRows=false;
        }
    }
    private readonly TimelineView overviewView=new("##BehaviorOverviewPlot");
    private readonly Dictionary<TimelineTrack,TimelineView> propertyViews=new();
    private TimelineView? keyboardView,rightView;
    private TimelineView PropertyView(TimelineTrack track)
    {
        if(!propertyViews.TryGetValue(track,out var view)){view=new("##PropertyPlot"+propertyViews.Count);propertyViews.Add(track,view);}return view;
    }
    private bool IsTimelinePlotHovered()
    {
        Vector2 min=ImPlot.GetPlotPos(),max=min+ImPlot.GetPlotSize();
        // NoInputs disables ImPlot hover reporting too. ImGui still owns window/popup routing.
        return ImGui.IsWindowHovered()&&ImGui.IsMouseHoveringRect(min,max)
            &&!ImGui.IsPopupOpen("",ImGuiPopupFlags.AnyPopupId|ImGuiPopupFlags.AnyPopupLevel);
    }
    private bool HasModifiers(){var io=ImGui.GetIO();return io.KeyCtrl||io.KeyAlt||io.KeyShift||io.KeySuper;}
    private void SelectTrack(Motclip clip,TimelineTrack track)
    {
        if(!ReferenceEquals(selectedTrack,track)){CancelGesture();selectedProperty=null;selectedKey=null;}
        selectedClip=clip;selectedTrack=track;
    }
    private List<VisibleLane> BuildPropertyLanes(Motclip clip,TimelineTrack track)
    {
        var lanes=new List<VisibleLane>(track.Properties.Count);
        foreach(var p in track.Properties)lanes.Add(new(p.Name,$"{clip.Name}/{track.Name}/{p.Name}",0,p,track,clip,
            p.StartFrame??(p.Keys.Count>0?p.Keys.Min(k=>k.Frame):track.StartFrame),
            p.EndFrame??(p.Keys.Count>0?p.Keys.Max(k=>k.Frame):track.EndFrame)));
        for(int i=0;i<lanes.Count;i++)lanes[i]=lanes[i] with{Y=lanes.Count-i-1};return lanes;
    }
    private void CancelGesture()
    {
        var d=activeDrag;if(d is null)return;
        if(d.Mode==EditMode.ReorderTrack && d.TrackOrder is not null){d.Lane.Clip.Tracks.Clear();d.Lane.Clip.Tracks.AddRange(d.TrackOrder);}
        else if(d.Mode==EditMode.ReorderProperty && d.PropertyOrder is not null){d.Track!.Properties.Clear();d.Track.Properties.AddRange(d.PropertyOrder);}
        else if(d.Mode==EditMode.Pan){d.View.XMin=d.InitialXMin;d.View.XMax=d.InitialXMax;d.View.YMin=d.InitialYMin;d.View.YMax=d.InitialYMax;}
        else if(d.Mode==EditMode.Playhead)currentFrame=d.InitialFrame;
        else if(d.Mode==EditMode.Key&&d.Key is not null)d.Key.Frame=d.InitialFrame;
        else{
            if(d.Track is not null&&d.Property is null){d.Track.StartFrame=d.InitialStart;d.Track.EndFrame=d.InitialEnd;}
            foreach(var p in d.Properties){p.Property.StartFrame=p.Start;p.Property.EndFrame=p.End;foreach(var k in p.Keys)k.Key.Frame=k.Frame;}
        }
        if(d.Property is not null)foreach(var key in d.CreatedKeys){d.Property.Keys.Remove(key);if(ReferenceEquals(selectedKey,key))selectedKey=d.Key;}
        activeDrag=null;
    }
    private double FrameStep(double value)=>snappingEnabled?Math.Round(value,MidpointRounding.AwayFromZero):value;
    private void AdvanceGesture(TimelineView view,double sequenceStart,double sequenceEnd)
    {
        var d=activeDrag;if(d is null||!ReferenceEquals(d.View,view))return;
        if(ImGui.IsKeyPressed(ImGuiKey.Escape,false)){CancelGesture();return;}
        if(!ImGui.IsMouseDown(d.Button))
        {
            if(d.Property is not null && d.Mode is EditMode.Key or EditMode.PropertyEndpoint)d.Property.Keys.Sort((a,b)=>a.Frame.CompareTo(b.Frame));
            activeDrag=null;
            if(d.Mode==EditMode.Pan && !d.Started && d.Button==ImGuiMouseButton.Left || d.Mode is EditMode.ReorderTrack or EditMode.ReorderProperty) SelectLane(d.Lane,d.Key);
            return;
        }
        var mouse=ImGui.GetMousePos();
        if(!d.Started){if(Vector2.DistanceSquared(mouse,new(d.StartMouseX,d.StartMouseY))<25)return;d.Started=true;}
        if(ImGui.GetIO().MouseDelta==Vector2.Zero)return;
        if(d.Mode is EditMode.ReorderTrack or EditMode.ReorderProperty)
        {
            int rows=(int)Math.Round((mouse.Y-d.StartMouseY)/Math.Max(1,d.StartPlotHeight)*(d.InitialYMax-d.InitialYMin),MidpointRounding.AwayFromZero);
            if(d.Mode==EditMode.ReorderTrack && d.TrackOrder is not null && d.Track is not null)
            {
                int target=Math.Clamp(d.TrackOrder.IndexOf(d.Track)+rows,0,d.TrackOrder.Count-1);
                var order=d.TrackOrder.Where(t=>!ReferenceEquals(t,d.Track)).ToList();order.Insert(target,d.Track);
                d.Lane.Clip.Tracks.Clear();d.Lane.Clip.Tracks.AddRange(order);
            }
            else if(d.PropertyOrder is not null && d.Property is not null)
            {
                int target=Math.Clamp(d.PropertyOrder.IndexOf(d.Property)+rows,0,d.PropertyOrder.Count-1);
                var order=d.PropertyOrder.Where(p=>!ReferenceEquals(p,d.Property)).ToList();order.Insert(target,d.Property);
                d.Track!.Properties.Clear();d.Track.Properties.AddRange(order);
            }
            return;
        }
        double delta=(mouse.X-d.StartMouseX)/Math.Max(1,d.StartPlotWidth)*(d.InitialXMax-d.InitialXMin);
        if(d.Mode==EditMode.Pan){
            view.XMin=d.InitialXMin-delta;view.XMax=d.InitialXMax-delta;
            double dy=(mouse.Y-d.StartMouseY)/Math.Max(1,d.StartPlotHeight)*(d.InitialYMax-d.InitialYMin);
            view.YMin=d.InitialYMin+dy;view.YMax=d.InitialYMax+dy;return;
        }
        delta=FrameStep(delta);
        if(d.Mode==EditMode.Playhead){currentFrame=Math.Clamp(d.InitialFrame+delta,sequenceStart,sequenceEnd);return;}
        if(d.Track is null)return;
        if(d.Mode==EditMode.Key&&d.Key is not null&&d.Property is not null){
            double min=Math.Max(d.Track.StartFrame,d.Property.StartFrame??d.Track.StartFrame),max=Math.Min(d.Track.EndFrame,d.Property.EndFrame??d.Track.EndFrame);
            if(min<=max)d.Key.Frame=Math.Clamp(d.InitialFrame+delta,min,max);return;
        }
        if(d.Mode==EditMode.PropertyEndpoint && d.Property is not null)
        {
            double min=Math.Max(d.Track.StartFrame,d.InitialStart+(snappingEnabled?1:.001));
            if(min>d.Track.EndFrame)return;
            double end=Math.Clamp(d.InitialEnd+delta,min,d.Track.EndFrame);
            if(d.EndKey is null)
            {
                d.EndKey=new TimelineKey($"Key {d.Property.Keys.Count+1}",end,-1);
                d.Property.Keys.Add(d.EndKey);d.CreatedKeys.Add(d.EndKey);
            }
            d.EndKey.Frame=end;
            var original=d.Properties[0];
            bool known=original.Start.HasValue&&original.End.HasValue&&!(original.Start==-1&&original.End==-1);
            d.Property.StartFrame=known?Math.Min(original.Start!.Value,d.InitialStart):d.InitialStart;
            d.Property.EndFrame=known?Math.Max(original.End!.Value,end):Math.Max(end,d.Keys.Max(k=>k.Frame));
            selectedKey=d.EndKey;
            return;
        }
        if(d.Mode==EditMode.CreatePropertyRange && d.Property is not null)
        {
            double start=d.InitialStart,end=Math.Clamp(d.InitialEnd+delta,start,d.Track.EndFrame);
            d.Property.StartFrame=start;d.Property.EndFrame=Math.Max(end,d.Keys.Count>0?d.Keys.Max(k=>k.Frame):end);
            return;
        }
        if(d.Mode==EditMode.TrackMove||d.Mode==EditMode.PropertyMove){
            double min=d.Property is null?d.Lane.Clip.StartFrame:d.Track.StartFrame;
            double max=d.Property is null?d.Lane.Clip.EndFrame:d.Track.EndFrame;
            if(max-min<d.InitialEnd-d.InitialStart)return;
            delta=Math.Clamp(delta,min-d.InitialStart,max-d.InitialEnd);
            if(d.Property is null){d.Track.StartFrame=d.InitialStart+delta;d.Track.EndFrame=d.InitialEnd+delta;}
            foreach(var p in d.Properties){
                bool disabled=p.Start==-1&&p.End==-1;
                if(!disabled){if(p.Start.HasValue)p.Property.StartFrame=p.Start.Value+delta;if(p.End.HasValue)p.Property.EndFrame=p.End.Value+delta;}
                foreach(var k in p.Keys)k.Key.Frame=k.Frame+delta;
            }
            return;
        }
        if(d.Mode==EditMode.TrackStart){
            double first=d.Properties.SelectMany(p=>(p.Start.HasValue&&!(p.Start==-1&&p.End==-1)?new[]{p.Start.Value}:Array.Empty<double>()).Concat(p.Keys.Select(k=>k.Frame))).DefaultIfEmpty(d.InitialEnd).Min();
            double max=Math.Min(d.InitialEnd,first);if(d.Lane.Clip.StartFrame<=max)d.Track.StartFrame=Math.Clamp(d.InitialStart+delta,d.Lane.Clip.StartFrame,max);
        }else if(d.Mode==EditMode.TrackEnd){
            double last=d.Properties.SelectMany(p=>(p.End.HasValue&&!(p.Start==-1&&p.End==-1)?new[]{p.End.Value}:Array.Empty<double>()).Concat(p.Keys.Select(k=>k.Frame))).DefaultIfEmpty(d.InitialStart).Max();
            double min=Math.Max(d.InitialStart,last);if(min<=d.Lane.Clip.EndFrame)d.Track.EndFrame=Math.Clamp(d.InitialEnd+delta,min,d.Lane.Clip.EndFrame);
        }else if(d.Property is not null){
            var p=d.Property;
            if(d.Mode==EditMode.PropertyStart){double max=Math.Min(d.InitialEnd,d.Keys.Count>0?d.Keys.Min(k=>k.Frame):d.InitialEnd);if(d.Track.StartFrame<=max)p.StartFrame=Math.Clamp(d.InitialStart+delta,d.Track.StartFrame,max);}
            else if(d.Mode==EditMode.PropertyEnd){double min=Math.Max(d.InitialStart,d.Keys.Count>0?d.Keys.Max(k=>k.Frame):d.InitialStart);if(min<=d.Track.EndFrame)p.EndFrame=Math.Clamp(d.InitialEnd+delta,min,d.Track.EndFrame);}
        }
    }
    private TimelineDrag Gesture(EditMode mode,TimelineView view,VisibleLane lane=default,TimelineKey? key=null, ImGuiMouseButton button=ImGuiMouseButton.Left, double? start=null, double? end=null, TimelineKey? endKey=null)
    {
        var mouse=ImGui.GetMousePos();var t=lane.Track;var p=lane.Property;
        return new TimelineDrag{
            Mode=mode,View=view,Button=button,EndKey=endKey,
            TrackOrder=mode==EditMode.ReorderTrack?lane.Clip.Tracks.ToList():null,
            PropertyOrder=mode==EditMode.ReorderProperty?t?.Properties.ToList():null,
            Lane=lane,Track=t,Property=p,Key=key,InitialStart=start??lane.StartFrame,InitialEnd=end??lane.EndFrame,InitialFrame=key?.Frame??currentFrame,
            StartMouseX=mouse.X,StartMouseY=mouse.Y,StartPlotWidth=view.PlotSize.X,StartPlotHeight=view.PlotSize.Y,
            InitialXMin=view.XMin,InitialXMax=view.XMax,InitialYMin=view.YMin,InitialYMax=view.YMax,
            Keys=p?.Keys.Select(k=>(k,k.Frame)).ToList()??new(),
            Properties=p is not null?new(){CaptureProperty(p)}:t?.Properties.Select(CaptureProperty).ToList()??new()
        };
    }
    private PropertyDragSnapshot CaptureProperty(TimelineProperty p)=>new(p,p.StartFrame,p.EndFrame,p.Keys.Select(k=>(k,k.Frame)).ToList());
    private double SnapFrame(double frame)=>FrameStep(frame);
    private readonly record struct Pick(VisibleLane Lane,TimelineKey? Key,EditMode Mode);
    private Pick? PickTimeline(List<VisibleLane> lanes,Vector2 mouse)
    {
        foreach(var lane in lanes){
            var center=ImPlot.PlotToPixels(0,lane.Y);var one=ImPlot.PlotToPixels(0,lane.Y+1);
            if(Math.Abs(mouse.Y-center.Y)>Math.Abs(one.Y-center.Y)*.5f)continue;
            if(lane.Property is not null){
                TimelineKey? nearest=null;float distance=81;
                foreach(var key in lane.Property.Keys){float d=Vector2.DistanceSquared(mouse,KeyPosition(lane,key));if(d<distance){distance=d;nearest=key;}}
                if(nearest is not null)return new(lane,nearest,EditMode.Key);
            }
            var box=GetRangeBox(lane.StartFrame,lane.EndFrame,lane);
            if(mouse.X<box.Min.X||mouse.X>box.Max.X||mouse.Y<box.Min.Y||mouse.Y>box.Max.Y)continue;
            if(lane.Track is null)return new(lane,null,EditMode.None);
            if(lane.Property is not null&&(!lane.Property.StartFrame.HasValue||!lane.Property.EndFrame.HasValue||lane.Property.StartFrame==-1&&lane.Property.EndFrame==-1))continue;
            float grip=Math.Min(8,(box.Max.X-box.Min.X)*.2f);
            bool start=mouse.X-box.Min.X<=grip,end=box.Max.X-mouse.X<=grip;
            return new(lane,null,lane.Property is null?(start?EditMode.TrackStart:end?EditMode.TrackEnd:EditMode.TrackMove):(start?EditMode.PropertyStart:end?EditMode.PropertyEnd:EditMode.PropertyMove));
        }return null;
    }
    private VisibleLane? LaneUnderMouse(List<VisibleLane> lanes,Vector2 mouse)
    {
        double y=ImPlot.PixelsToPlot(mouse).Y;
        foreach(var lane in lanes) if(Math.Abs(y-lane.Y)<=.5)return lane;
        return null;
    }

    private void SelectLane(VisibleLane lane,TimelineKey? key)
    {
        if(lane.Clip is null)return;
        if(lane.Track is null)
        {
            if(!ReferenceEquals(selectedClip,lane.Clip)){selectedTrack=null;selectedProperty=null;selectedKey=null;}
            selectedClip=lane.Clip;collapsedMotclips.Remove(lane.Clip);collapsedTrackGroups.Remove(lane.Clip);return;
        }
        SelectTrack(lane.Clip,lane.Track);selectedProperty=lane.Property;selectedKey=key;
    }

    private void BeginRangeExtension(TimelineView view,VisibleLane lane,TimelineKey? key,EditMode pickedMode)
    {
        if(lane.Track is null)return;
        SelectLane(lane,key);
        var property=lane.Property;
        if(property is not null && key is not null && property.EndsOnMinusOne && key.Value.HasValue)
        {
            bool terminal=key.Value==-1;
            TimelineKey? end=terminal?key:property.Keys.Where(k=>k.Frame>key.Frame&&k.Value==-1).OrderBy(k=>k.Frame).FirstOrDefault();
            TimelineKey? start=terminal?property.Keys.Where(k=>k.Frame<key.Frame&&k.Value.HasValue&&k.Value!=-1).OrderByDescending(k=>k.Frame).FirstOrDefault():key;
            if(start is not null && start.Frame<lane.Track.EndFrame)
            {
                activeDrag=Gesture(EditMode.PropertyEndpoint,view,lane,key,ImGuiMouseButton.Right,start.Frame,end?.Frame??start.Frame,end);
                return;
            }
        }
        if(property is not null && (!property.StartFrame.HasValue || !property.EndFrame.HasValue || property.StartFrame==-1&&property.EndFrame==-1))
        {
            double start=property.Keys.Count>0?property.Keys.Min(k=>k.Frame):Math.Clamp(FrameStep(ImPlot.PixelsToPlot(ImGui.GetMousePos()).X),lane.Track.StartFrame,lane.Track.EndFrame);
            double end=property.Keys.Count>0?Math.Max(start,property.Keys.Max(k=>k.Frame)):start;
            activeDrag=Gesture(EditMode.CreatePropertyRange,view,lane,key,ImGuiMouseButton.Right,start,end);
            return;
        }
        // Shift+right on a start grip resizes the start; otherwise it extends the end.
        bool startEdge=pickedMode is EditMode.TrackStart or EditMode.PropertyStart;
        EditMode mode=property is null?(startEdge&&lane.EndFrame>lane.StartFrame?EditMode.TrackStart:EditMode.TrackEnd)
            :(startEdge&&lane.EndFrame>lane.StartFrame?EditMode.PropertyStart:EditMode.PropertyEnd);
        activeDrag=Gesture(mode,view,lane,key,ImGuiMouseButton.Right);
    }

    private void HandleTimelineInteraction(List<VisibleLane> lanes,TimelineView view,double sequenceStart,double sequenceEnd)
    {
        view.PlotMin=ImPlot.GetPlotPos();view.PlotSize=ImPlot.GetPlotSize();
        if(activeDrag is not null)return;
        bool hovered=IsTimelinePlotHovered();
        var io=ImGui.GetIO();var mouse=ImGui.GetMousePos();
        bool plain=!HasModifiers(),shift=io.KeyShift&&!io.KeyCtrl&&!io.KeyAlt&&!io.KeySuper,alt=io.KeyAlt&&!io.KeyCtrl&&!io.KeyShift&&!io.KeySuper,context=false;
        if(rightView is not null)
        {
            if(!ReferenceEquals(rightView,view))return;
            if(!ImGui.IsMouseReleased(ImGuiMouseButton.Right))return;
            rightView=null;
            context=hovered&&plain&&Vector2.DistanceSquared(mouse,contextMouseDownPosition)<=16;
            if(!context)return;
        }
        if(!hovered)return;
        bool leftHeld=ImGui.IsMouseDown(ImGuiMouseButton.Left),rightHeld=ImGui.IsMouseDown(ImGuiMouseButton.Right),middleHeld=ImGui.IsMouseDown(ImGuiMouseButton.Middle);
        if(io.MouseWheel!=0&&!leftHeld&&!rightHeld&&!middleHeld&&!io.KeyAlt&&!io.KeySuper&&!(io.KeyCtrl&&io.KeyShift))
        {
            var at=ImPlot.PixelsToPlot(mouse);double factor=Math.Pow(1.15,-io.MouseWheel);
            if(!io.KeyCtrl){double span=Math.Clamp((view.XMax-view.XMin)*factor,2,1e6),ratio=(at.X-view.XMin)/(view.XMax-view.XMin);view.XMin=at.X-ratio*span;view.XMax=view.XMin+span;}
            if(!io.KeyShift){double span=Math.Clamp((view.YMax-view.YMin)*factor,1,1e5),ratio=(at.Y-view.YMin)/(view.YMax-view.YMin);view.YMin=at.Y-ratio*span;view.YMax=view.YMin+span;}
            return;
        }
        if(plain&&rightHeld&&!leftHeld&&!middleHeld&&ImGui.IsMouseClicked(ImGuiMouseButton.Right)){rightView=view;contextMouseDownPosition=mouse;return;}
        bool left=leftHeld&&!rightHeld&&!middleHeld&&ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        bool extend=shift&&rightHeld&&!leftHeld&&!middleHeld&&ImGui.IsMouseClicked(ImGuiMouseButton.Right);
        bool moveRange=alt&&rightHeld&&!leftHeld&&!middleHeld&&ImGui.IsMouseClicked(ImGuiMouseButton.Right);
        if(!left&&!extend&&!moveRange&&!context)return;
        keyboardView=view;
        var pick=PickTimeline(lanes,mouse);
        VisibleLane? lane=pick?.Lane??LaneUnderMouse(lanes,mouse);
        if(context)
        {
            if(lane.HasValue)
            {
                contextClip=lane.Value.Clip;contextTrack=lane.Value.Track;contextProperty=lane.Value.Property;contextKey=pick?.Key;
                contextFrame=FrameStep(ImPlot.PixelsToPlot(mouse).X);newKeyValue=contextKey?.Value??0;ImGui.OpenPopup("TimelineContext");
            }
            return;
        }
        if(alt&&left)
        {
            currentFrame=Math.Clamp(FrameStep(ImPlot.PixelsToPlot(mouse).X),sequenceStart,sequenceEnd);
            activeDrag=Gesture(EditMode.Playhead,view);return;
        }
        if(plain&&left)
        {
            if(ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && lane?.Property is not null
                && pick?.Key is null && ImPlot.PixelsToPlot(mouse).Y < lane.Value.Y-.02)
            {
                // Mirrors the native scalar property's Keys list: create a key, not a nested track.
                SelectLane(lane.Value,null);AddKey(lane.Value.Property,FrameStep(ImPlot.PixelsToPlot(mouse).X),0);return;
            }
            // Selection is deferred until release. Dragging over any item only pans.
            activeDrag=Gesture(EditMode.Pan,view,lane??default,pick?.Key);
            return;
        }
        if(!lane.HasValue)return;
        if(extend){BeginRangeExtension(view,lane.Value,pick?.Key,pick?.Mode??EditMode.None);return;}
        if(moveRange)
        {
            SelectLane(lane.Value,pick?.Key);
            var property=lane.Value.Property;
            if(lane.Value.Track is not null && (property is null || property.StartFrame.HasValue&&property.EndFrame.HasValue&&!(property.StartFrame==-1&&property.EndFrame==-1)))
                activeDrag=Gesture(property is null?EditMode.TrackMove:EditMode.PropertyMove,view,lane.Value,pick?.Key,ImGuiMouseButton.Right);
            return;
        }
        if(shift&&left && lane.Value.Track is not null)
        {
            // Reorder rows within their existing owner. No time or key value changes.
            activeDrag=Gesture(lane.Value.Property is null?EditMode.ReorderTrack:EditMode.ReorderProperty,view,lane.Value,pick?.Key);
        }
    }
}
