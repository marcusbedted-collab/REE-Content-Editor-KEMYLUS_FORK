using System.Globalization;
using ReeLib;
using ReeLib.Clip;
using ReeLib.Common;
using ReeLib.Mot;

namespace ContentEditor.App.ImguiHandling;

public partial class MotlistTimelineEditor
{
    // These objects are UI references, not a second editable MOTLIST data model.
    internal sealed class TimelineKey
    {
        internal readonly KeyBase Native;
        internal bool Extra;
        public string Name => Native.GetType().Name + (Extra ? " / extra" : "");
        public double Frame { get => Native.frame; set { if(double.IsFinite(value)) Native.frame=(float)value; } }
        public object? RawValue => Native switch { NoHermiteKey n=>n.Value, BoolKey b=>b.value, ActionKey a=>a.value, _=>null };
        public double? Value
        {
            get => RawValue switch { bool b=>b?1:0, byte n=>n,sbyte n=>n,short n=>n,ushort n=>n,int n=>n,uint n=>n,long n=>n,ulong n=>n,float n=>n,double n=>n,_=>null };
            set
            {
                if(!value.HasValue || !double.IsFinite(value.Value))return;
                if(Native is BoolKey b) b.value=checked((short)value.Value);
                else if(Native is ActionKey a) a.value=checked((uint)value.Value);
                else if(Native is NoHermiteKey n && RawValue is { } raw && raw is not string)
                {
                    if(raw is bool)n.Value=value.Value!=0;
                    else if(Value.HasValue)n.Value=Convert.ChangeType(value.Value,raw.GetType(),CultureInfo.InvariantCulture);
                }
            }
        }
        public string DisplayValue => Native.PropertyType==PropertyType.Bool && Value is 0 or 1
            ? (Value==0?"False":"True") : Convert.ToString(RawValue,CultureInfo.InvariantCulture)??"(no scalar value)";
        internal TimelineKey(KeyBase native,bool extra=false){Native=native;Extra=extra;}
    }

    internal sealed class TimelineProperty
    {
        internal readonly Property Native;
        internal readonly TimelineTrack Owner;
        internal TimelineProperty? Parent;
        public int UiId {get;}
        public string Name => string.IsNullOrEmpty(Native.Info.FunctionName)?$"Property {Native.Info.nameAsciiHash:X8}":Native.Info.FunctionName;
        public string ValueRange => $"[{StartFrame:0.##} - {EndFrame:0.##}]";
        public bool Container => Native.IsPropertyContainer;
        public List<TimelineProperty> Children {get;}=new();
        public List<TimelineKey> Keys {get;}=new();
        public List<TimelineProperty> Siblings => Parent?.Children??Owner.Properties;
        public double? StartFrame {get=>Native.Info.startFrame;set {if(value.HasValue&&double.IsFinite(value.Value))Native.Info.startFrame=(float)value.Value;}}
        public double? EndFrame {get=>Native.Info.endFrame;set {if(value.HasValue&&double.IsFinite(value.Value))Native.Info.endFrame=(float)value.Value;}}
        public bool Disabled => StartFrame==-1&&EndFrame==-1;
        public bool CanCreateTerminal => Native.Info.DataType==PropertyType.Bool ||
            Native.Info.DataType is PropertyType.S8 or PropertyType.S16 or PropertyType.S32 or PropertyType.S64
            && (Owner.Native.Name.Contains("ColliderReq",StringComparison.OrdinalIgnoreCase)||Owner.Native.Name.Contains("ReqSet",StringComparison.OrdinalIgnoreCase))
            && Name.Contains("req",StringComparison.OrdinalIgnoreCase);
        public double TerminalNumber => Native.Info.DataType==PropertyType.Bool?0:-1;
        public bool IsTerminal(TimelineKey key)=>CanCreateTerminal&&key.Value==TerminalNumber;
        internal TimelineProperty(Property native,TimelineTrack owner,TimelineProperty? parent,int id){Native=native;Owner=owner;Parent=parent;UiId=id;}
        public IEnumerable<TimelineProperty> Descendants(){yield return this;foreach(var p in Children)foreach(var child in p.Descendants())yield return child;}
        public TimelineKey NewKey(string name,double frame,double? value)
        {
            KeyBase native=CreateNativeKey(Native);
            var key=new TimelineKey(native){Frame=frame};
            if(value.HasValue && key.Value.HasValue)key.Value=value;
            return key;
        }
    }

    internal sealed class TimelineTrack
    {
        internal readonly ClipTrack Native;
        internal readonly Motclip Owner;
        internal ClipTrack? ParentNative;
        public int UiId {get;}
        public string Name=>string.IsNullOrEmpty(Native.Name)?$"Track {Native.nameHash1:X8}":Native.Name;
        public string Details=>Native.Version.ToString();
        public List<TimelineProperty> Properties {get;}=new();
        public IEnumerable<TimelineProperty> AllProperties=>Properties.SelectMany(p=>p.Descendants());
        public bool HasStoredBounds=>Native.Version<ClipVersion.RE8;
        private (double Start,double End) Bounds
        {
            get
            {
                if(Native.Version<ClipVersion.RE8)return(Native.startFrame,Native.endFrame);
                var frames=AllProperties.Where(p=>!p.Disabled).SelectMany(p=>new[]{p.StartFrame??0,p.EndFrame??0}.Concat(p.Keys.Select(k=>k.Frame))).Where(double.IsFinite).ToArray();
                return frames.Length>0?(frames.Min(),frames.Max()):(Owner.StartFrame,Owner.EndFrame);
            }
        }
        public double StartFrame {get=>Bounds.Start;set {if(Native.Version<ClipVersion.RE8&&double.IsFinite(value))Native.startFrame=(float)value;}}
        public double EndFrame {get=>Bounds.End;set {if(Native.Version<ClipVersion.RE8&&double.IsFinite(value))Native.endFrame=(float)value;}}
        internal TimelineTrack(ClipTrack native,Motclip owner,ClipTrack? parent,int id){Native=native;Owner=owner;ParentNative=parent;UiId=id;}
    }

    internal sealed class Motclip
    {
        internal readonly MotClip Native;
        internal ClipTrack? Root=>Native.ClipEntry.Tracks.FirstOrDefault();
        internal readonly MotFile Motion;
        public string Name=>string.IsNullOrWhiteSpace(Native.MainTrackName)?"Behavior clip":Native.MainTrackName;
        public double StartFrame=>Root is { Version:<ClipVersion.RE8 } && float.IsFinite(Root.startFrame)?Root.startFrame:Motion.Header.startFrame;
        public double EndFrame=>Math.Max(StartFrame+1,Root is { Version:<ClipVersion.RE8 }&&Root.endFrame>Root.startFrame?Root.endFrame:Math.Max(Native.ClipEntry.FrameCount,Motion.Header.frameCount));
        public List<TimelineTrack> Tracks {get;}=new();
        internal Motclip(MotClip native,MotFile motion)
        {
            Native=native;Motion=motion;
        }
    }
    private sealed class Motlist
    {
        public string Name="BehaviorClips";
        public List<Motclip> Motclips {get;}=new();
    }

    private readonly Dictionary<MotClip,Motclip> clipViews=new();
    private readonly Dictionary<ClipTrack,TimelineTrack> trackViews=new();
    private readonly Dictionary<Property,TimelineProperty> propertyNodes=new();
    private readonly Dictionary<KeyBase,TimelineKey> keyNodes=new();
    private int nextTimelineId;

    internal static KeyBase CreateNativeKey(Property property)
    {
        // PropertyInfo.GetKeyList in REE-Lib reads every pre-MHWilds property
        // from NormalKeys, including DMC5 bools. Payload type does not choose storage type.
        int keyType=property.Info.Version<ClipVersion.MHWilds?0:property.Info.KeyType;
        KeyBase key=keyType switch {
            1=>new BoolKey(property.Info.Version),
            2=>new ActionKey(property.Info.Version),
            3=>new NoHermiteKey(property.Info.Version),
            0=>new NormalKey(property.Info.Version){interpolation=InterpolationType.Linear},
            _=>throw new NotSupportedException("Unsupported key storage type "+keyType)
        };
        key.PropertyType=property.Info.DataType;key.ResetValue();return key;
    }

    private void BindMotion(MotFile motion,IEnumerable<MotClip> clips)
    {
        motlist.Motclips.Clear();
        foreach(var native in clips.Distinct())
        {
            if(!clipViews.TryGetValue(native,out var clip)){clip=new(native,motion);clipViews.Add(native,clip);}
            clip.Tracks.Clear();
            // EmbeddedClip.Write and MotClip.TrackData use this exact flat order.
            // TML's recursively nested TimelineChildTracks are a different format.
            foreach(var track in native.ClipEntry.Tracks)
            {
                if(ReferenceEquals(track,clip.Root))continue;
                if(!trackViews.TryGetValue(track,out var view)){view=new(track,clip,clip.Root,++nextTimelineId);trackViews.Add(track,view);}
                view.ParentNative=clip.Root;view.Properties.Clear();
                foreach(var property in track.Properties)view.Properties.Add(BindProperty(property,view,null,new()));
                clip.Tracks.Add(view);
            }
            motlist.Motclips.Add(clip);
        }
    }

    private TimelineProperty BindProperty(Property native,TimelineTrack owner,TimelineProperty? parent,HashSet<Property> visited)
    {
        if(!propertyNodes.TryGetValue(native,out var view)){view=new(native,owner,parent,++nextTimelineId);propertyNodes.Add(native,view);}
        if(!visited.Add(native))return view;
        view.Parent=parent;view.Children.Clear();view.Keys.Clear();
        foreach(var key in native.Keys??Enumerable.Empty<KeyBase>())view.Keys.Add(WrapKey(key,false));
        foreach(var key in native.ExtraKeys??Enumerable.Empty<KeyBase>())view.Keys.Add(WrapKey(key,true));
        foreach(var child in native.ChildProperties??Enumerable.Empty<Property>())if(!visited.Contains(child))view.Children.Add(BindProperty(child,owner,view,visited));
        return view;
    }
    private TimelineKey WrapKey(KeyBase native,bool extra){if(!keyNodes.TryGetValue(native,out var key)){key=new(native,extra);keyNodes.Add(native,key);}key.Extra=extra;return key;}

    private void SynchronizeNativeLists()
    {
        foreach(var clip in motlist.Motclips)
        {
            var oldTracks=clip.Native.ClipEntry.Tracks.ToArray();
            var oldData=clip.Native.TrackData.ToArray();
            var dataByTrack=new Dictionary<ClipTrack,TracksData>();
            for(int i=1;i<oldTracks.Length && i-1<oldData.Length;i++)dataByTrack[oldTracks[i]]=oldData[i-1];
            // Keep per-track metadata with its track when a row is reordered or cloned.
            if(clip.Native.ClipEntry.Version>ClipVersion.RE7)
            {
                var data=clip.Tracks.Select(t=>dataByTrack.TryGetValue(t.Native,out var existing)?existing:
                    addedTrackData.TryGetValue(t.Native,out var added)?added:new TracksData{Version=clip.Native.ClipEntry.Version}).ToList();
                data.AddRange(oldData.Skip(Math.Max(0,oldTracks.Length-1)));
                Replace(clip.Native.TrackData,data);
            }
            foreach(var track in clip.Tracks)
            {
                Replace(track.Native.Properties,track.Properties.Select(p=>p.Native));
                foreach(var p in track.AllProperties)
                {
                    if(p.Native.ChildProperties is not null || p.Children.Count>0){p.Native.ChildProperties??=new();Replace(p.Native.ChildProperties,p.Children.Select(c=>c.Native));}
                    if(p.Native.Keys is not null || p.Keys.Any(k=>!k.Extra)){p.Native.Keys??=new();Replace(p.Native.Keys,p.Keys.Where(k=>!k.Extra).Select(k=>k.Native));}
                    if(p.Native.ExtraKeys is not null || p.Keys.Any(k=>k.Extra)){p.Native.ExtraKeys??=new();Replace(p.Native.ExtraKeys,p.Keys.Where(k=>k.Extra).Select(k=>k.Native));}
                }
            }
            foreach(var parent in clip.Tracks.Select(t=>t.Native).Concat(clip.Root is null?Array.Empty<ClipTrack>():new[]{clip.Root}))
            {
                var children=clip.Tracks.Where(t=>ReferenceEquals(t.ParentNative,parent)).Select(t=>t.Native).ToArray();
                if(parent.ChildTracks.Count>0||children.Length>0)Replace(parent.ChildTracks,children);
            }
            Replace(clip.Native.ClipEntry.Tracks,(clip.Root is null?Enumerable.Empty<ClipTrack>():new[]{clip.Root}).Concat(clip.Tracks.Select(t=>t.Native)));
        }
    }
    private static void Replace<T>(List<T> list,IEnumerable<T> items){var copy=items.ToArray();list.Clear();list.AddRange(copy);}
}
