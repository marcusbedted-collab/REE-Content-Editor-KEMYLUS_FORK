using ReeLib;
using ReeLib.Clip;
using ReeLib.Mot;

namespace ContentEditor.App.ImguiHandling;

public partial class MotlistTimelineEditor
{
    // Snapshots retain object identity. They restore the native lists and timing fields,
    // rather than replacing an EmbeddedClip and invalidating other editor references.
    private sealed class GraphState
    {
        private readonly List<Action> restore = new();
        private readonly List<object?> values = new();
        public bool SameAs(GraphState other) => values.SequenceEqual(other.values);
        public void Apply() { foreach (var action in restore) action(); }
        private void Field<T>(T value, Action<T> setter) { values.Add(value); restore.Add(()=>setter(value)); }
        private void List<T>(List<T> list) { var saved=list.ToArray(); values.Add(list); values.Add(saved.Length); values.AddRange(saved.Cast<object?>()); restore.Add(()=>Replace(list,saved)); }
        private void PropertyList<T>(List<T>? list, Action<List<T>?> setter)
        { values.Add(list); if(list is not null) List(list); restore.Add(()=>setter(list)); }
        public GraphState(IEnumerable<Motclip> clips)
        {
            var tracks=new HashSet<ClipTrack>();var properties=new HashSet<Property>();var keys=new HashSet<KeyBase>();
            void Key(KeyBase k)
            {
                if(!keys.Add(k))return;
                Field(k.frame,v=>k.frame=v);
                if(k is NoHermiteKey n) Field(n.Value,v=>n.Value=v!);
                else if(k is BoolKey b) Field(b.value,v=>b.value=v);
                else if(k is ActionKey a) Field(a.value,v=>a.value=v);
            }
            void Property(Property p)
            {
                if(!properties.Add(p))return;
                Field(p.Info.startFrame,v=>p.Info.startFrame=v);Field(p.Info.endFrame,v=>p.Info.endFrame=v);
                PropertyList(p.ChildProperties,v=>p.ChildProperties=v);PropertyList(p.Keys,v=>p.Keys=v);PropertyList(p.ExtraKeys,v=>p.ExtraKeys=v);
                foreach(var k in (p.Keys??Enumerable.Empty<KeyBase>()).Concat(p.ExtraKeys??Enumerable.Empty<KeyBase>())) Key(k);
                foreach(var child in p.ChildProperties??Enumerable.Empty<Property>()) Property(child);
            }
            void Track(ClipTrack t)
            {
                if(!tracks.Add(t))return;
                Field(t.startFrame,v=>t.startFrame=v);Field(t.endFrame,v=>t.endFrame=v);
                List(t.Properties);List(t.ChildTracks);
                foreach(var p in t.Properties) Property(p);
                foreach(var child in t.ChildTracks) Track(child);
            }
            foreach(var clip in clips)
            {
                List(clip.Native.ClipEntry.Tracks);List(clip.Native.TrackData);
                foreach(var t in clip.Native.ClipEntry.Tracks) Track(t);
            }
        }
    }
    private GraphState? pendingEdit;
    private readonly Dictionary<ClipTrack,TracksData> addedTrackData = new();
    private void BeginEdit() { pendingEdit ??= new GraphState(motlist.Motclips); }
    private void FinishEdit()
    {
        if(pendingEdit is not { } before)return;
        pendingEdit=null;SynchronizeNativeLists();var after=new GraphState(motlist.Motclips);
        if(before.SameAs(after))return;
        void Apply(GraphState state){state.Apply();rebindPending=true;markModified();}
        recordChange(()=>Apply(after),()=>Apply(before),"MotlistTimeline/"+Guid.NewGuid().ToString("N"));
    }
    private void Edit(Action action)
    {
        BeginEdit();
        try { action(); FinishEdit(); }
        catch { pendingEdit?.Apply(); pendingEdit=null; rebindPending=true; throw; }
    }
}
