using System.Numerics;
using System.Globalization;
using Hexa.NET.ImGui;
using Hexa.NET.ImGui.Widgets;
using ReeLib;
using ReeLib.Clip;
using ReeLib.Mot;
using ReeLib.Common;


namespace ContentEditor.App.ImguiHandling;

public partial class MotlistTimelineEditor
{
    private void DrawTimelineContextMenu()
    {
        if (!ImGui.BeginPopup("TimelineContext"u8))
        {
            return;
        }

        if (contextKey is not null && contextProperty is not null)
        {
            ImGui.TextUnformatted(contextProperty.Name+" / "+contextKey.Native.PropertyType);
            var sameFrame=contextProperty.Keys.Where(k=>k.Frame==contextKey.Frame).ToArray();
            if(sameFrame.Length>1&&ImGui.BeginCombo("Key at this frame",$"{Array.IndexOf(sameFrame,contextKey)+1}: {contextKey.DisplayValue}"))
            {
                for(int i=0;i<sameFrame.Length;i++)if(ImGui.Selectable($"{i+1}: {sameFrame[i].DisplayValue}##key{i}",ReferenceEquals(contextKey,sameFrame[i])))contextKey=sameFrame[i];
                ImGui.EndCombo();
            }
            selectedClip=contextProperty.Owner.Owner;selectedTrack=contextProperty.Owner;selectedProperty=contextProperty;selectedKey=contextKey;
            DrawNumber(contextKey.Native,"Frame",contextKey.Frame,_=>true,value=>Edit(()=>contextKey.Frame=ClampNewKeyFrame(contextProperty,value)));
            DrawTypedValue(contextKey);
            if(ImGui.MenuItem("Create key / interval..."))RequestKeyCreation(contextProperty,contextFrame);
            if(ImGui.MenuItem("Copy key")) CopyKey(contextKey);
            ImGui.BeginDisabled(!CanPasteKey(contextProperty));
            if(ImGui.MenuItem("Paste key here")) PasteKey(contextProperty,contextFrame);
            ImGui.EndDisabled();
            if(ImGui.MenuItem("Remove key")) Edit(()=> { contextProperty.Keys.Remove(contextKey); selectedKey=null; });
        }
        else if(contextProperty is not null)
        {
            if(contextProperty.Container)
            {
                if(ImGui.MenuItem("Add child property...")) RequestChildPropertyCreation(contextProperty);
            }
            else
            {
                if(ImGui.MenuItem("Create key / interval...")) RequestKeyCreation(contextProperty,contextFrame);
                ImGui.BeginDisabled(!CanPasteKey(contextProperty));
                if(ImGui.MenuItem("Paste key here")) PasteKey(contextProperty,contextFrame);
                ImGui.EndDisabled();
            }
        }
        else
        {
            if(contextTrack is not null)
            {
                if(ImGui.MenuItem("Add property track..."))RequestPropertyCreation(contextTrack);
                if(ImGui.MenuItem("Copy track"))CopyTrack(contextTrack);
            }
            ImGui.BeginDisabled(contextClip is null || copiedTrack is null || contextClip.Native.ClipEntry.Version != copiedTrack.Native.Version);
            if(ImGui.MenuItem("Paste as new track")) PasteTrack(contextClip!);
            ImGui.EndDisabled();
        }
        ImGui.EndPopup();
    }

    private TimelineKey? copiedNativeKey;
    private void CopyKey(TimelineKey key) => copiedNativeKey=new(key.Native.DeepClone<KeyBase>()!,key.Extra);
    private bool CanPasteKey(TimelineProperty property) => !property.Container && copiedNativeKey is { } key
        && key.Native.PropertyType==property.Native.Info.DataType && key.Native.Version==property.Native.Info.Version
        && key.Native.GetType()==NativeKeyClass(property.Native);
    private static Type NativeKeyClass(Property property)=>(property.Info.Version<ClipVersion.MHWilds?0:property.Info.KeyType) switch
    {1=>typeof(BoolKey),2=>typeof(ActionKey),3=>typeof(NoHermiteKey),_=>typeof(NormalKey)};
    private void PasteKey(TimelineProperty property,double frame)
    {
        if(!CanPasteKey(property))return;
        Edit(()=> { var key=new TimelineKey(copiedNativeKey!.Native.DeepClone<KeyBase>()!,copiedNativeKey.Extra){Frame=ClampNewKeyFrame(property,frame)};
            property.Keys.Add(key); property.Keys.Sort((a,b)=>a.Frame.CompareTo(b.Frame)); ExpandActiveBounds(property,key.Frame); selectedProperty=property;selectedKey=key; });
    }

    private TimelineTrack CloneTrack(TimelineTrack source, Motclip owner)
    {
        var native=source.Native.DeepCloneGeneric();
        void CopyAux(ClipTrack original,ClipTrack clone)
        {
            int index=source.Owner.Native.ClipEntry.Tracks.IndexOf(original)-1;
            if(addedTrackData.TryGetValue(original,out var extra))addedTrackData[clone]=extra.DeepCloneGeneric();
            else if(index>=0 && index<source.Owner.Native.TrackData.Count)addedTrackData[clone]=source.Owner.Native.TrackData[index].DeepCloneGeneric();
            for(int i=0;i<original.ChildTracks.Count;i++) CopyAux(original.ChildTracks[i],clone.ChildTracks[i]);
        }
        CopyAux(source.Native,native);
        var view=new TimelineTrack(native,owner,null,++nextTimelineId);
        foreach(var p in native.Properties)view.Properties.Add(BindProperty(p,view,null,new()));
        return view;
    }
    private void CopyTrack(TimelineTrack track) => copiedTrack=CloneTrack(track,track.Owner);
    private TimelineTrack? PasteTrack(Motclip owner)
    {
        if(copiedTrack is null || owner.Native.ClipEntry.Version!=copiedTrack.Native.Version)return null;
        TimelineTrack? result=null;
        Edit(()=> {
            result=CloneTrack(copiedTrack,owner);
            result.ParentNative=owner.Root;
            void Add(TimelineTrack track)
            {
                trackViews[track.Native]=track;owner.Tracks.Add(track);
                foreach(var native in track.Native.ChildTracks)
                {
                    var child=new TimelineTrack(native,owner,track.Native,++nextTimelineId);
                    foreach(var p in native.Properties)child.Properties.Add(BindProperty(p,child,null,new()));
                    Add(child);
                }
            }
            Add(result);overviewView.Initialized=false;
        });
        return result;
    }
    private double ClampNewKeyFrame(TimelineProperty property,double frame)
    {
        return Math.Clamp(SnapFrame(frame),property.Owner.Owner.StartFrame,property.Owner.Owner.EndFrame);
    }

    private static void ExpandActiveBounds(TimelineProperty property,double frame)
    {
        if(property.Disabled)return;
        property.StartFrame=Math.Min(property.StartFrame??frame,frame);property.EndFrame=Math.Max(property.EndFrame??frame,frame);
    }
    private void AddKey(TimelineProperty property,double frame,double? value)
    {
        if(property.Container)return;
        Edit(()=> {
            var key=property.NewKey("Key",ClampNewKeyFrame(property,frame),value);
            property.Keys.Add(key);property.Keys.Sort((a,b)=>a.Frame.CompareTo(b.Frame));
            if(property.Disabled){property.StartFrame=key.Frame;property.EndFrame=key.Frame;}
            selectedProperty=property;selectedKey=key;
        });
    }
    private void AddChildProperty(TimelineProperty property)
    {
        if(!property.Container)return;
        Edit(()=> {
            var child=new Property(property.Native.Info.Version);
            child.Info.startFrame=-1;child.Info.endFrame=-1;
            property.Children.Add(BindProperty(child,property.Owner,property,new()));
        });
    }

    private void DuplicateSelection()
    {
        if(selectedProperty is { } property)
        {
            if(selectedKey is { } key)
            {
                CopyKey(key);PasteKey(property,Math.Min(property.Owner.Owner.EndFrame,key.Frame+1));return;
            }
            var native=property.Native.DeepCloneGeneric();
            var clone=BindProperty(native,property.Owner,property.Parent,new());
            Edit(()=>property.Siblings.Insert(property.Siblings.IndexOf(property)+1,clone));
            selectedProperty=clone;return;
        }
        if(selectedTrack is { } track){CopyTrack(track);var added=PasteTrack(track.Owner);if(added is not null)SelectTrack(added.Owner,added);}
    }
    private void DeleteSelection()
    {
        if(selectedProperty is { } p)
        {
            if(selectedKey is { } key){Edit(()=>p.Keys.Remove(key));selectedKey=null;}
            else {Edit(()=>p.Siblings.Remove(p));selectedProperty=null;}
        }
        else if(selectedTrack is { } track){Edit(()=>track.Owner.Tracks.Remove(track));selectedTrack=null;}
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
        if(control&&ImGui.IsKeyPressed(ImGuiKey.Z)){UndoRedo.Undo();return;}
        if(control&&ImGui.IsKeyPressed(ImGuiKey.Y)){UndoRedo.Redo();return;}
        if (control && ImGui.IsKeyPressed(ImGuiKey.C) && selectedKey is not null)
        {
            CopyKey(selectedKey);
        }
        if (control && ImGui.IsKeyPressed(ImGuiKey.V) && selectedProperty is not null && CanPasteKey(selectedProperty))
        {
            PasteKey(selectedProperty,currentFrame);
        }
        if (!control && ImGui.IsKeyPressed(ImGuiKey.Delete) && selectedKey is not null && selectedProperty is not null)
        {
            Edit(()=> { selectedProperty.Keys.Remove(selectedKey); selectedKey=null; });
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
                    .SelectMany(track => track.AllProperties)
                    .SelectMany(property => property.Keys.Select(key => key.Frame));
            double? target = ImGui.IsKeyPressed(ImGuiKey.UpArrow)
                ? frames.Where(frame => frame < currentFrame - 0.001).DefaultIfEmpty(sequenceStart).Max()
                : frames.Where(frame => frame > currentFrame + 0.001).DefaultIfEmpty(sequenceEnd).Min();
            currentFrame = Math.Clamp(SnapFrame(target.Value), sequenceStart, sequenceEnd);
        }
        if (!control && ImGui.IsKeyPressed(ImGuiKey.K) && selectedProperty is not null)
        {
            RequestKeyCreation(selectedProperty,currentFrame);
        }
    }


}

