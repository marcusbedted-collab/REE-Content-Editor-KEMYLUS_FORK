using System.Reflection;
using ReeLib.Clip;

namespace ContentEditor.App.ImguiHandling;

public partial class MotlistTimelineEditor
{
    private readonly Dictionary<(object Target,string Member),UIContext> valueContexts=new();
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
        ImGui.TextColored(new System.Numerics.Vector4(.45f,.8f,1,1),"SELECTION");ImGui.Separator();
        if(selectedClip is null){ImGui.TextWrapped("Select a behavior clip, track, property or key.");return;}
        ImGui.TextWrapped(selectedClip.Name);
        ImGui.TextDisabled($"Overall {selectedClip.StartFrame:0.##} ? {selectedClip.EndFrame:0.##}");
        if(selectedTrack is not null)
        {
            ImGui.Spacing();ImGui.TextWrapped(selectedTrack.Name);
            ImGui.TextDisabled($"{selectedTrack.AllProperties.Count()} properties / {selectedTrack.AllProperties.Sum(p=>p.Keys.Count)} keys");
        }
        if(selectedProperty is not null)
        {
            ImGui.Separator();ImGui.TextUnformatted(selectedProperty.Name);
            ImGui.TextDisabled(selectedProperty.Native.Info.DataType+ (selectedProperty.Disabled?" / inactive":""));
            double start=selectedProperty.StartFrame??-1,end=selectedProperty.EndFrame??-1;
            ImGui.BeginDisabled(activeDrag is not null);
            ImGui.SetNextItemWidth(-1);
            if(ImGui.InputDouble("Start",ref start,0,0,"%.3f",ImGuiInputTextFlags.EnterReturnsTrue) && double.IsFinite(start) && start<=end)
                Edit(()=>selectedProperty.StartFrame=start);
            ImGui.SetNextItemWidth(-1);
            if(ImGui.InputDouble("End",ref end,0,0,"%.3f",ImGuiInputTextFlags.EnterReturnsTrue) && double.IsFinite(end) && end>=start)
                Edit(()=>selectedProperty.EndFrame=end);
            ImGui.EndDisabled();
            if(selectedKey is not null)
            {
                ImGui.Separator();ImGui.TextUnformatted(selectedProperty.IsTerminal(selectedKey)?"Terminal key":"Selected key");
                ImGui.TextDisabled($"{selectedKey.Name} / {selectedKey.Native.PropertyType}");
                double frame=selectedKey.Frame;
                ImGui.SetNextItemWidth(-1);
                if(ImGui.InputDouble("Frame",ref frame,0,0,"%.3f",ImGuiInputTextFlags.EnterReturnsTrue) && double.IsFinite(frame))
                    Edit(()=>{selectedKey.Frame=ClampNewKeyFrame(selectedProperty,frame);selectedProperty.Keys.Sort((a,b)=>a.Frame.CompareTo(b.Frame));});
                DrawTypedValue(selectedKey);
                Metadata("Key metadata",selectedKey.Native);
            }
            Metadata("Property metadata",selectedProperty.Native.Info);
        }
        if(selectedTrack is not null)Metadata("Track metadata",selectedTrack.Native);
        Metadata("Behavior clip metadata",selectedClip.Native);
        Metadata("Clip header",selectedClip.Native.ClipEntry.Header);
        var clip=selectedClip.Native.ClipEntry;
        if(ImGui.CollapsingHeader("Additional clip data"))
        {
            ImGui.TextWrapped($"Bezier: {clip.Bezier3DData.Count}\nSpeed points: {clip.SpeedPointData.Count}\nClip info: {clip.ClipInfoList.Count}\nTrack metadata: {selectedClip.Native.TrackData.Count}");
            ImGui.TextWrapped("All auxiliary data remains attached to this clip. Full native structures are available through File data.");
        }
    }
}
