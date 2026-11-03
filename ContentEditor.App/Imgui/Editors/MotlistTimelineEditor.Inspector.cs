using System.Reflection;
using ReeLib.Clip;

namespace ContentEditor.App.ImguiHandling;

public partial class MotlistTimelineEditor
{
    private readonly Dictionary<(object Target,string Member),UIContext> valueContexts=new();
    private sealed class NumberDraft
    {
        public double Value;
        public bool Active;
    }
    private readonly Dictionary<(object Target,string Member),NumberDraft> numberDrafts=new();

    private void DrawNumber(object target,string label,double value,Func<double,bool> valid,Action<double> commit)
    {
        if(!numberDrafts.TryGetValue((target,label),out var draft))
            numberDrafts.Add((target,label),draft=new NumberDraft());
        if(!draft.Active)draft.Value=value;
        ImGui.PushID(target.GetHashCode());
        ImGui.TextDisabled(label);ImGui.SetNextItemWidth(-1);
        // InputDouble is an InputScalar: EnterReturnsTrue is explicitly unsupported.
        // Retain the draft while typing and record one edit when the field deactivates.
        ImGui.InputDouble("##"+label,ref draft.Value,0,0,"%.3f");
        bool finished=ImGui.IsItemDeactivatedAfterEdit();
        draft.Active=ImGui.IsItemActive();
        ImGui.PopID();
        if(finished && double.IsFinite(draft.Value) && draft.Value!=value && valid(draft.Value))
            commit(draft.Value);
    }
    private void DrawTypedValue(TimelineKey key)
    {
        DrawMember(key.Native,key.Native is NoHermiteKey?"Value":"value",false);
    }
    private void DrawMember(object target,string name,bool label=true)
    {
        var member=target.GetType().GetMember(name,BindingFlags.Public|BindingFlags.Instance).FirstOrDefault();
        Func<object?> get=member switch {FieldInfo f=>()=>f.GetValue(target),System.Reflection.PropertyInfo p=>()=>p.GetValue(target),_=>()=>null};
        Action<object?> set=member switch {FieldInfo f=>v=>f.SetValue(target,v),System.Reflection.PropertyInfo p when p.CanWrite=>v=>p.SetValue(target,v),_=>_=>{}};
        var raw=get();
        if(raw is null){if(label)ImGui.TextDisabled(name+": (none)");return;}
        if(label)ImGui.TextDisabled(name);
        if(!valueContexts.TryGetValue((target,name),out var context))
        {
            context=new UIContext("##"+name,target,null!,_=>get(),(_,v)=>{set(v);markModified();},UIOptions.None);
            context.root=context;valueContexts.Add((target,name),context);
        }
        context.uiHandler=WindowHandlerFactory.CreateUIHandler(raw,raw.GetType());
        ImGui.PushID(target.GetHashCode());ImGui.SetNextItemWidth(-1);context.ShowUI();ImGui.PopID();
    }
    private void Metadata(string label,object target)
    {
        if(!ImGui.CollapsingHeader(label))return;
        ImGui.PushID(label);
        foreach(var field in target.GetType().GetFields(BindingFlags.Public|BindingFlags.Instance))
        {
            if(field.IsInitOnly || field.GetValue(target) is System.Collections.ICollection)continue;
            DrawMember(target,field.Name);
        }
        ImGui.PopID();
    }
    private void DrawInspector()
    {
        var clip=selectedClip;
        ImGui.TextDisabled("SELECTION");ImGui.Separator();ImGui.Spacing();
        if(clip is null){ImGui.TextWrapped("Select a track, property or key in the sequencer.");return;}
        ImGui.TextWrapped(CompactTrackName(clip.Name));
        if(selectedTrack is { } track)
        {
            ImGui.TextColored(TrackColor(track),CompactTrackName(track.Name));
            ImGui.TextDisabled($"{track.AllProperties.Count()} properties / {track.AllProperties.Sum(p=>p.Keys.Count)} keys");
        }
        var property=selectedProperty;
        if(property is not null)
        {
            ImGui.Spacing();ImGui.Separator();ImGui.Spacing();
            ImGui.TextColored(TrackColor(property.Owner),property.Name);
            ImGui.TextDisabled(property.Native.Info.DataType+(property.Disabled?" / bounds disabled":""));
            var key=selectedKey;
            if(key is not null&&property.Keys.Contains(key))
            {
                ImGui.Spacing();
                ImGui.TextDisabled(IsBoundaryKey(property,key)?"BOUNDARY KEY":"KEY VALUE");
                DrawTypedValue(key);
                DrawNumber(key.Native,"Frame",key.Frame,v=>v>=clip.StartFrame&&v<=clip.EndFrame,v=>
                    Edit(()=>{key.Frame=ClampNewKeyFrame(property,v);ExpandActiveBounds(property,key.Frame);property.Keys.Sort((a,b)=>a.Frame.CompareTo(b.Frame));}));
                var span=GetKeySegments(property).FirstOrDefault(s=>ReferenceEquals(s.Start,key));
                if(span.End is { } end)
                {
                    DrawNumber(end.Native,"Paired end frame",end.Frame,v=>v>key.Frame&&v<=clip.EndFrame,
                        v=>Edit(()=>{end.Frame=ClampNewKeyFrame(property,v);ExpandActiveBounds(property,end.Frame);}));
                    ImGui.TextDisabled("End payload: "+end.DisplayValue);
                }
                else if(!property.Container&&property.Native.Info.DataType!=PropertyType.Action&&ImGui.Button("Create interval end..."))
                {RequestKeyCreation(property,key.Frame);draftHasEnd=true;extendingKey=key;SetRawValue(draftKey!,key.RawValue!);}
                ImGui.Spacing();
            }
            if(!property.Container&&ImGui.Button("+ Key / interval"))RequestKeyCreation(property,currentFrame);
            if(property.Container&&ImGui.Button("+ Child property"))RequestChildPropertyCreation(property);
            ImGui.Spacing();ImGui.TextDisabled($"STORED KEYS ({property.Keys.Count})");
            if(ImGui.BeginTable("StoredKeys",3,ImGuiTableFlags.RowBg|ImGuiTableFlags.BordersInnerH|ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Frame",ImGuiTableColumnFlags.WidthFixed,60);
                ImGui.TableSetupColumn("Value");
                ImGui.TableSetupColumn("Role",ImGuiTableColumnFlags.WidthFixed,65);
                ImGui.TableHeadersRow();
                for(int i=0;i<property.Keys.Count;i++)
                {
                    var stored=property.Keys[i];ImGui.PushID(i);
                    ImGui.TableNextRow();ImGui.TableSetColumnIndex(0);
                    if(ImGui.Selectable($"{stored.Frame:0.###}##key",ReferenceEquals(stored,selectedKey),ImGuiSelectableFlags.SpanAllColumns))
                        selectedKey=stored;
                    ImGui.TableSetColumnIndex(1);ImGui.TextUnformatted(stored.DisplayValue);
                    ImGui.TableSetColumnIndex(2);
                    ImGui.TextDisabled(stored.Extra?"Extra":IsBoundaryKey(property,stored)?"End":property.Native.Info.DataType==PropertyType.Action?"Event":"Key");
                    ImGui.PopID();
                }
                ImGui.EndTable();
            }
            ImGui.Spacing();
            if(ImGui.CollapsingHeader("Property bounds"))
            {
                ImGui.TextWrapped("Property bounds are separate from individual key frames.");
                if(property.Disabled&&property.Keys.Count>0&&ImGui.Button("Enable from stored keys"))
                    Edit(()=>{property.StartFrame=property.Keys.Min(k=>k.Frame);property.EndFrame=property.Keys.Max(k=>k.Frame);});
                DrawNumber(property.Native,"Start",property.StartFrame??-1,v=>v<=(property.EndFrame??-1),v=>Edit(()=>property.StartFrame=v));
                DrawNumber(property.Native,"End",property.EndFrame??-1,v=>v>=(property.StartFrame??-1),v=>Edit(()=>property.EndFrame=v));
            }
        }
        if(selectedTrack is { } owner)
        {
            ImGui.Spacing();ImGui.Separator();ImGui.Spacing();
            if(ImGui.Button("+ Property"))RequestPropertyCreation(owner);
            ImGui.SameLine();if(ImGui.Button("Copy track"))CopyTrack(owner);
            ImGui.BeginDisabled(copiedTrack is null||copiedTrack.Native.Version!=clip.Native.ClipEntry.Version);
            if(ImGui.Button("Paste as new track"))PasteTrack(clip);
            ImGui.EndDisabled();
        }
        ImGui.Spacing();ImGui.Separator();ImGui.Spacing();
        if(ImGui.CollapsingHeader("Advanced"))
        {
            if(selectedKey is not null)Metadata("Key flags / interpolation",selectedKey.Native);
            if(property is not null)Metadata("Property metadata",property.Native.Info);
            if(selectedTrack is not null)Metadata("Track metadata",selectedTrack.Native);
            Metadata("Behavior clip metadata",clip.Native);Metadata("Clip header",clip.Native.ClipEntry.Header);
            if(clip.Root is not null)
            {
                Metadata("Root metadata",clip.Root);
                if(clip.Root.Properties.Count>0)ImGui.TextWrapped($"{clip.Root.Properties.Count} root-owned properties are retained; use File data for their native structure.");
            }
            var entry=clip.Native.ClipEntry;
            ImGui.TextWrapped($"Bezier data: {entry.Bezier3DData.Count}\nSpeed points: {entry.SpeedPointData.Count}\nClip info: {entry.ClipInfoList.Count}\nTrack metadata: {clip.Native.TrackData.Count}");
        }
    }
}
