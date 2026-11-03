namespace ContentEditor.App.ImguiHandling;

public partial class MotlistTimelineEditor
{
    // One row list drives tree and canvas. Keys never become child tracks.
    private List<VisibleLane> BuildTimelineLanes()
    {
        var lanes = new List<VisibleLane>();
        bool filtering = timelineFilter.Length > 0;
        bool Match(string value) => value.Contains(timelineFilter, StringComparison.OrdinalIgnoreCase);
        bool Active(TimelineProperty p) => showInactive || !p.Disabled || p.Keys.Count > 0 || p.Children.Any(Active);
        bool Matches(TimelineProperty p) => Match(p.Name) || p.Children.Any(Matches);
        foreach (var clip in motlist.Motclips)
        {
            if (isolateTrack && selectedTrack is not null && !ReferenceEquals(clip, selectedTrack.Owner)) continue;
            var tracks = clip.Tracks.Where(t => (!isolateTrack || selectedTrack is null || ReferenceEquals(t, selectedTrack)) &&
                (!filtering || Match(clip.Name) || Match(t.Name) || t.AllProperties.Any(Matches))).ToList();
            if (filtering && tracks.Count == 0 && !Match(clip.Name)) continue;
            lanes.Add(new(clip.Name, clip.Name, 0, null, null, clip, clip.StartFrame, clip.EndFrame));
            if (!filtering && collapsedMotclips.Contains(clip)) continue;
            foreach (var track in tracks)
            {
                lanes.Add(new(track.Name, clip.Name + "/" + track.Name, 0, null, track, clip, track.StartFrame, track.EndFrame, 1));
                if (!filtering && !expandedTracks.Contains(track)) continue;
                bool includeAll = !filtering || Match(clip.Name) || Match(track.Name);
                void AddProperty(TimelineProperty property, int depth, bool parentMatches)
                {
                    if (!Active(property) || !parentMatches && !Matches(property)) return;
                    lanes.Add(new(property.Name, clip.Name + "/" + track.Name + "/" + property.Name, 0, property, track, clip,
                        property.StartFrame ?? track.StartFrame, property.EndFrame ?? track.EndFrame, depth));
                    if (!filtering && collapsedProperties.Contains(property)) return;
                    foreach (var child in property.Children) AddProperty(child, depth + 1, parentMatches || Match(property.Name));
                }
                foreach (var property in track.Properties) AddProperty(property, 2, includeAll);
            }
        }
        for (int i = 0; i < lanes.Count; i++) lanes[i] = lanes[i] with { Y = lanes.Count - i - 1 };
        return lanes;
    }
    private bool LaneCanExpand(VisibleLane lane) => lane.Property is { } property ? property.Children.Count > 0
        : lane.Track is { } track ? track.Properties.Count > 0 : lane.Clip.Tracks.Count > 0;
    private bool LaneExpanded(VisibleLane lane) => lane.Property is { } property ? !collapsedProperties.Contains(property)
        : lane.Track is { } track ? expandedTracks.Contains(track) : !collapsedMotclips.Contains(lane.Clip);
    private void ToggleLane(VisibleLane lane)
    {
        CancelGesture();
        if (lane.Property is { } property)
        { if (!collapsedProperties.Remove(property)) collapsedProperties.Add(property); }
        else if (lane.Track is { } track)
        { if (!expandedTracks.Remove(track)) expandedTracks.Add(track); }
        else if (!collapsedMotclips.Remove(lane.Clip)) collapsedMotclips.Add(lane.Clip);
    }
}
