using System.Globalization;
using ReeLib.Clip;
using ReeLib.Common;

namespace ContentEditor.App.ImguiHandling;

public partial class MotlistTimelineEditor
{
    private TimelineProperty? creatingKeyFor;
    private TimelineTrack? creatingPropertyFor;
    private TimelineProperty? creatingPropertyParent;
    private TimelineKey? draftKey,extendingKey;
    private bool openKeyCreator,openPropertyCreator,draftHasEnd;
    private double draftFrame,draftEnd;
    private string draftValueText="",creationError="",draftPropertyName="";
    private PropertyType draftPropertyType=PropertyType.S32;
    private static readonly PropertyType[] CreatableTypes=Enum.GetValues<PropertyType>();
    private bool openRename;
    private TimelineTrack? renamingTrack;
    private TimelineProperty? renamingProperty;
    private string renameDraft="";

    private void RequestRename()
    {
        renamingTrack=selectedTrack;renamingProperty=selectedProperty;
        renameDraft=renamingProperty?.Name??renamingTrack?.Name??"";
        openRename=renameDraft.Length>0;
    }

    private void RequestKeyCreation(TimelineProperty property,double frame)
    {
        if(property.Container)return;
        creatingKeyFor=property;extendingKey=null;creationError="";
        try { draftKey=property.NewKey("Key",frame,null); }
        catch(NotSupportedException e) { creationError=e.Message;draftKey=null; }
        draftFrame=Math.Clamp(SnapFrame(frame),property.Owner.Owner.StartFrame,property.Owner.Owner.EndFrame);
        draftEnd=Math.Min(property.Owner.Owner.EndFrame,draftFrame+5);draftHasEnd=false;
        if(draftKey is not null && property.Native.Info.DataType==PropertyType.Bool)draftKey.Value=1;
        draftValueText=draftKey?.DisplayValue??"";openKeyCreator=true;
    }
    private void RequestPropertyCreation(TimelineTrack track)
    { creatingPropertyFor=track;creatingPropertyParent=null;draftPropertyName="";creationError="";openPropertyCreator=true; }
    private void RequestChildPropertyCreation(TimelineProperty parent)
    { RequestPropertyCreation(parent.Owner);creatingPropertyParent=parent; }

    private static void SetRawValue(TimelineKey key,object value)
    {
        if(key.Native is NoHermiteKey n)n.Value=value;
        else if(key.Native is BoolKey b)b.value=Convert.ToInt16(value,CultureInfo.InvariantCulture);
        else if(key.Native is ActionKey a)a.value=Convert.ToUInt32(value,CultureInfo.InvariantCulture);
    }
    private bool DrawDraftValue(TimelineKey key)
    {
        ImGui.TextDisabled("Value / "+key.Native.PropertyType);
        if(key.Native.PropertyType==PropertyType.Bool)
        {
            bool value=key.Value!=0;
            if(ImGui.Checkbox(value?"True":"False",ref value))key.Value=value?1:0;
            return true;
        }
        ImGui.SetNextItemWidth(-1);ImGui.InputText("##TypedValue",ref draftValueText,1024);
        var type=key.RawValue?.GetType();
        if(type is null)return false;
        try { SetRawValue(key,Convert.ChangeType(draftValueText,type,CultureInfo.InvariantCulture));return true; }
        catch(Exception e) when(e is FormatException or OverflowException or InvalidCastException)
        { ImGui.TextColored(new System.Numerics.Vector4(1,.5f,.4f,1),"Enter a valid "+type.Name+" value.");return false; }
    }
    private void InsertKey(TimelineProperty property,TimelineKey key,TimelineKey? end)
    {
        Edit(()=>
        {
            property.Keys.Add(key);if(end is not null)property.Keys.Add(end);
            property.Keys.Sort((a,b)=>a.Frame.CompareTo(b.Frame));
            double first=property.Keys.Min(k=>k.Frame),last=property.Keys.Max(k=>k.Frame);
            if(property.Disabled){property.StartFrame=first;property.EndFrame=last;}
            else {property.StartFrame=Math.Min(property.StartFrame??first,first);property.EndFrame=Math.Max(property.EndFrame??last,last);}
            selectedClip=property.Owner.Owner;selectedTrack=property.Owner;selectedProperty=property;selectedKey=key;showDetails=true;
        });
    }
    private TimelineProperty AddProperty(TimelineTrack track,string name,PropertyType type)
    {
        var native=new Property(track.Native.Version);
        native.Info.FunctionName=name;native.Info.DataType=type;native.Info.KeyType=type==PropertyType.Action?2:0;
        native.Info.startFrame=-1;native.Info.endFrame=-1;
        var property=BindProperty(native,track,null,new());
        Edit(()=>track.Properties.Add(property));
        selectedClip=track.Owner;selectedTrack=track;selectedProperty=property;selectedKey=null;
        expandedTracks.Add(track);
        return property;
    }
    private void CreateChildProperty(TimelineProperty parent,string name,PropertyType type)
    {
        var native=new Property(parent.Native.Info.Version);native.Info.FunctionName=name;native.Info.DataType=type;
        native.Info.startFrame=native.Info.endFrame=-1;
        var property=BindProperty(native,parent.Owner,parent,new());
        Edit(()=>parent.Children.Add(property));collapsedProperties.Remove(parent);expandedTracks.Add(parent.Owner);
        selectedClip=parent.Owner.Owner;selectedTrack=parent.Owner;selectedProperty=property;selectedKey=null;
    }
    private void DrawCreationPopups()
    {
        if(openRename){ImGui.OpenPopup("Rename selection");openRename=false;}
        if(ImGui.BeginPopupModal("Rename selection",ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.SetNextItemWidth(380);ImGui.InputText("Name",ref renameDraft,512);
            ImGui.BeginDisabled(string.IsNullOrWhiteSpace(renameDraft));
            if(ImGui.Button("Rename"))
            {
                Edit(()=>{if(renamingProperty is { } p)p.Native.Info.FunctionName=renameDraft.Trim();
                    else if(renamingTrack is { } t)t.Native.Name=renameDraft.Trim();});
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();ImGui.SameLine();if(ImGui.Button("Cancel"))ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        if(openKeyCreator){ImGui.OpenPopup("Create property key");openKeyCreator=false;}
        ImGui.SetNextWindowSizeConstraints(new System.Numerics.Vector2(430,0),new System.Numerics.Vector2(430,float.MaxValue));
        if(ImGui.BeginPopupModal("Create property key",ImGuiWindowFlags.AlwaysAutoResize))
        {
            if(creatingKeyFor is { } property)
            {
                ImGui.TextColored(TrackColor(property.Owner),property.Name);
                ImGui.TextDisabled(CompactTrackName(property.Owner.Name)+" / "+property.Native.Info.DataType);
                ImGui.Separator();
                ImGui.BeginDisabled(extendingKey is not null);ImGui.SetNextItemWidth(180);ImGui.InputDouble("Frame",ref draftFrame,0,0,"%.3f");
                bool valid=draftKey is not null && DrawDraftValue(draftKey);ImGui.EndDisabled();
                if(property.Native.Info.DataType!=PropertyType.Action)
                {
                    ImGui.BeginDisabled(extendingKey is not null);ImGui.Checkbox("Create an end key / interval",ref draftHasEnd);ImGui.EndDisabled();
                    if(draftHasEnd){ImGui.SetNextItemWidth(180);ImGui.InputDouble("End frame",ref draftEnd,0,0,"%.3f");
                        ImGui.TextDisabled(property.CanCreateTerminal?$"End value: {(property.Native.Info.DataType==PropertyType.Bool?"False":"-1")}":"End boundary preserves this key's value.");}
                }
                valid &= double.IsFinite(draftFrame)&&draftFrame>=property.Owner.Owner.StartFrame&&draftFrame<=property.Owner.Owner.EndFrame
                    &&(!draftHasEnd||double.IsFinite(draftEnd)&&draftEnd>draftFrame&&draftEnd<=property.Owner.Owner.EndFrame);
                if(creationError.Length>0)ImGui.TextWrapped(creationError);
                ImGui.BeginDisabled(!valid);
                if(ImGui.Button("Create key"))
                {
                    draftKey!.Frame=SnapFrame(draftFrame);TimelineKey? end=null;
                    if(draftHasEnd){end=new TimelineKey(draftKey.Native.DeepClone<KeyBase>()!){Frame=SnapFrame(draftEnd)};
                        if(property.CanCreateTerminal)end.Value=property.TerminalNumber;}
                    if(extendingKey is not null&&end is not null)
                    {
                        Edit(()=>{property.Keys.Add(end);property.Keys.Sort((a,b)=>a.Frame.CompareTo(b.Frame));
                            if(property.Disabled){property.StartFrame=extendingKey.Frame;property.EndFrame=end.Frame;}else ExpandActiveBounds(property,end.Frame);
                            selectedKey=extendingKey;});
                    }
                    else InsertKey(property,draftKey,end);
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndDisabled();ImGui.SameLine();
            }
            if(ImGui.Button("Cancel"))ImGui.CloseCurrentPopup();ImGui.EndPopup();
        }
        if(openPropertyCreator){ImGui.OpenPopup("Add property track");openPropertyCreator=false;}
        ImGui.SetNextWindowSizeConstraints(new System.Numerics.Vector2(430,0),new System.Numerics.Vector2(430,float.MaxValue));
        if(ImGui.BeginPopupModal("Add property track",ImGuiWindowFlags.AlwaysAutoResize))
        {
            if(creatingPropertyFor is { } track)
            {
                ImGui.TextColored(TrackColor(track),CompactTrackName(track.Name));
                ImGui.TextUnformatted("New property lane");
                ImGui.SetNextItemWidth(-1);ImGui.InputTextWithHint("##PropertyName","Property name",ref draftPropertyName,256);
                ImGui.SetNextItemWidth(-1);
                if(ImGui.BeginCombo("##ValueType",draftPropertyType.ToString()))
                {
                    foreach(var type in CreatableTypes)if(ImGui.Selectable(type.ToString(),type==draftPropertyType))draftPropertyType=type;
                    ImGui.EndCombo();
                }
                ImGui.BeginDisabled(string.IsNullOrWhiteSpace(draftPropertyName));
                if(ImGui.Button("Add property"))
                {if(creatingPropertyParent is { } parent)CreateChildProperty(parent,draftPropertyName.Trim(),draftPropertyType);
                    else AddProperty(track,draftPropertyName.Trim(),draftPropertyType);ImGui.CloseCurrentPopup();}
                ImGui.EndDisabled();ImGui.SameLine();
            }
            if(ImGui.Button("Cancel"))ImGui.CloseCurrentPopup();ImGui.EndPopup();
        }
    }
}
