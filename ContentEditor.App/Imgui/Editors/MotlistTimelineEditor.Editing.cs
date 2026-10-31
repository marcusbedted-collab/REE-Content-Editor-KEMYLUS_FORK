using ContentEditor.Core;
using ReeLib.Clip;

namespace ContentEditor.App.ImguiHandling;

internal sealed partial class MotlistTimelineEditor
{
    private void BeginRangeDrag(ClipTrack track, RangeGroup range, int minimumFrame, int maximumFrame, float pixelsOnCanvas, float timelineX)
    {
        var mouseX = ImGui.GetMousePos().X;
        var startX = timelineX + (range.Start - minimumFrame) * pixelsOnCanvas;
        var endX = MathF.Max(startX + MinimumClipWidth * UI.UIScale, timelineX + (range.End - minimumFrame) * pixelsOnCanvas);
        var handle = MathF.Min(HandleWidth * UI.UIScale, (endX - startX) * 0.5f);
        var mode = mouseX <= MathF.Min(startX, endX) + handle
            ? DragMode.ResizeStart
            : mouseX >= MathF.Max(startX, endX) - handle ? DragMode.ResizeEnd : DragMode.MoveRange;

        var bounds = GetTrackBounds(track, minimumFrame, maximumFrame);
        selectedProperty = selectedProperty != null && range.Properties.Contains(selectedProperty) ? selectedProperty : range.Properties[0];
        selectedKey = null;
        drag = new DragState {
            Mode = mode,
            MouseStartX = mouseX,
            PixelsPerFrame = MathF.Max(0.01f, pixelsOnCanvas),
            MinimumFrame = bounds.Minimum,
            MaximumFrame = bounds.Maximum,
            RangeStart = range.Start,
            RangeEnd = range.End,
            Properties = CaptureProperties(range.Properties),
        };
    }

    private void BeginKeyDrag(ClipTrack track, KeyGroup group, int minimumFrame, int maximumFrame, float pixelsOnCanvas, bool moveStack)
    {
        var selected = group.Keys.FirstOrDefault(item => ReferenceEquals(item.Key, selectedKey));
        if (selected.Key == null) selected = group.Keys[0];
        selectedProperty = selected.Property;
        selectedKey = selected.Key;

        var keys = moveStack ? group.Keys : [selected];
        if (keys.Any(item => !float.IsFinite(item.Key.frame))) return;
        var trackBounds = GetTrackBounds(track, minimumFrame, maximumFrame);
        // Clamp a shared delta, not each absolute frame, so stack spacing is preserved.
        var minimum = keys.Max(item => MathF.Max(trackBounds.Minimum, item.Property.Info.startFrame) - item.Key.frame);
        var maximum = keys.Min(item => MathF.Min(trackBounds.Maximum, item.Property.Info.endFrame) - item.Key.frame);

        drag = new DragState {
            Mode = DragMode.MoveKeys,
            MouseStartX = ImGui.GetMousePos().X,
            PixelsPerFrame = MathF.Max(0.01f, pixelsOnCanvas),
            MinimumFrame = minimum,
            MaximumFrame = maximum,
            Keys = keys.DistinctBy(item => item.Key).Select(item => (item.Property, item.Key, item.Key.frame)).ToList(),
        };
    }

    private void BeginTrackEdgeDrag(ClipTrack track, int minimumFrame, int maximumFrame, float pixelsOnCanvas, bool startEdge, bool moveBoundaryKeys)
    {
        if (!TryGetEditableTrackBounds(track, minimumFrame, maximumFrame, out var bounds)) return;

        var boundary = startEdge ? bounds.Minimum : bounds.Maximum;
        var boundaryProperties = EnumerateTimelineProperties(track.Properties)
            .Where(property => IsValidRange(property)
                && MathF.Abs((startEdge ? property.Info.startFrame : property.Info.endFrame) - boundary) < 0.001f)
            .Select(property => new PropertySnapshot(
                property,
                property.Info.startFrame,
                property.Info.endFrame,
                property.Keys?.Select(key => (key, key.frame)).ToList() ?? []))
            .ToList();
        var boundaryKeys = moveBoundaryKeys
            ? EnumerateKeys(track.Properties)
                .Where(item => float.IsFinite(item.Key.frame) && MathF.Abs(item.Key.frame - boundary) < 0.001f)
                .Select(item => (item.Key, item.Key.frame))
                .DistinctBy(item => item.Key)
                .ToList()
            : [];

        drag = new DragState {
            Mode = startEdge ? DragMode.ResizeTrackStart : DragMode.ResizeTrackEnd,
            MouseStartX = ImGui.GetMousePos().X,
            PixelsPerFrame = MathF.Max(0.01f, pixelsOnCanvas),
            MinimumFrame = bounds.Minimum,
            MaximumFrame = bounds.Maximum,
            Track = new TrackSnapshot(track, bounds.Minimum, bounds.Maximum, boundaryProperties, boundaryKeys),
            MoveBoundaryKeys = moveBoundaryKeys,
        };
    }

    private void UpdateDrag()
    {
        if (drag == null) return;
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) {
            FinishDrag(drag);
            drag = null;
            return;
        }

        if (!ImGui.IsMouseDragging(ImGuiMouseButton.Left)) return;
        var delta = MathF.Round((ImGui.GetMousePos().X - drag.MouseStartX) / drag.PixelsPerFrame);
        if (delta == drag.LastDelta) return;
        drag.LastDelta = delta;
        if (ApplyDragDelta(drag, delta)) {
            drag.Changed = true;
            MarkModified();
        }
    }

    private static bool ApplyDragDelta(DragState drag, float delta)
    {
        var before = GetDragFrames(drag).ToArray();
        if (delta == 0f) {
            if (drag.Track != null) {
                drag.Track.Track.startFrame = drag.Track.Start;
                drag.Track.Track.endFrame = drag.Track.End;
                foreach (var property in drag.Track.BoundaryProperties) {
                    property.Property.Info.startFrame = property.Start;
                    property.Property.Info.endFrame = property.End;
                }
                foreach (var (key, frame) in drag.Track.BoundaryKeys) key.frame = frame;
            }
            foreach (var (_, key, frame) in drag.Keys) key.frame = frame;
            foreach (var snapshot in drag.Properties) {
                snapshot.Property.Info.startFrame = snapshot.Start;
                snapshot.Property.Info.endFrame = snapshot.End;
                foreach (var (key, frame) in snapshot.Keys) key.frame = frame;
            }
        } else {
            switch (drag.Mode) {
                case DragMode.MoveKeys:
                    foreach (var (_, key, frame) in drag.Keys) {
                        key.frame = frame + ClampDragDelta(delta, drag.MinimumFrame, drag.MaximumFrame);
                    }
                    break;
                case DragMode.MoveRange:
                    var minimumDelta = MathF.Ceiling(drag.MinimumFrame - drag.RangeStart);
                    var maximumDelta = MathF.Floor(drag.MaximumFrame - drag.RangeEnd);
                    var moveDelta = ClampDragDelta(delta, minimumDelta, maximumDelta);
                    foreach (var snapshot in drag.Properties) {
                        snapshot.Property.Info.startFrame = snapshot.Start + moveDelta;
                        snapshot.Property.Info.endFrame = snapshot.End + moveDelta;
                        foreach (var (key, frame) in snapshot.Keys) key.frame = frame + moveDelta;
                    }
                    break;
                case DragMode.ResizeStart:
                    var firstKey = drag.Properties.SelectMany(item => item.Keys).Select(item => item.Frame).Where(float.IsFinite).DefaultIfEmpty(drag.RangeEnd).Min();
                    var latestStart = MathF.Min(firstKey, drag.RangeEnd);
                    var earliestStart = MathF.Min(drag.MinimumFrame, latestStart);
                    var start = Math.Clamp(drag.RangeStart + delta, earliestStart, latestStart);
                    foreach (var snapshot in drag.Properties) snapshot.Property.Info.startFrame = snapshot.Start + start - drag.RangeStart;
                    break;
                case DragMode.ResizeEnd:
                    var lastKey = drag.Properties.SelectMany(item => item.Keys).Select(item => item.Frame).Where(float.IsFinite).DefaultIfEmpty(drag.RangeStart).Max();
                    var earliestEnd = MathF.Max(lastKey, drag.RangeStart);
                    var latestEnd = MathF.Max(drag.MaximumFrame, earliestEnd);
                    var end = Math.Clamp(drag.RangeEnd + delta, earliestEnd, latestEnd);
                    foreach (var snapshot in drag.Properties) snapshot.Property.Info.endFrame = snapshot.End + end - drag.RangeEnd;
                    break;
                case DragMode.ResizeTrackStart:
                    if (drag.Track == null) break;
                    var trackStartLimits = GetTrackStartLimits(drag.Track.Track);
                    var startLimit = trackStartLimits.Count == 0
                        ? drag.Track.End - 1f
                        : trackStartLimits.Min();
                    var upperStart = MathF.Min(drag.Track.End - 1f, startLimit);
                    if (drag.MoveBoundaryKeys) {
                        upperStart = EnumerateTimelineProperties(drag.Track.Track.Properties).Where(IsValidRange)
                            .Select(property => drag.Track.BoundaryProperties.Any(item => ReferenceEquals(item.Property, property))
                                ? property.Info.endFrame : property.Info.startFrame)
                            .Concat(EnumerateKeys(drag.Track.Track.Properties)
                                .Where(item => IsValidRange(item.Property) && float.IsFinite(item.Key.frame)
                                    && !drag.Track.BoundaryKeys.Any(boundary => ReferenceEquals(boundary.Key, item.Key)))
                                .Select(item => item.Key.frame))
                            .Append(drag.Track.End - 1f).Min();
                    }
                    if (drag.MinimumFrame > upperStart) break;
                    var newStart = Math.Clamp(drag.Track.Start + delta, drag.MinimumFrame, upperStart);
                    drag.Track.Track.startFrame = newStart;
                    if (drag.MoveBoundaryKeys) ApplyBoundaryPropertyDelta(drag.Track.BoundaryProperties, newStart - drag.Track.Start, true);
                    if (drag.MoveBoundaryKeys) ApplyBoundaryKeyDelta(drag.Track.BoundaryKeys, newStart - drag.Track.Start);
                    break;
                case DragMode.ResizeTrackEnd:
                    if (drag.Track == null) break;
                    var trackEndLimits = GetTrackEndLimits(drag.Track.Track);
                    var endLimit = trackEndLimits.Count == 0
                        ? drag.Track.Start + 1f
                        : trackEndLimits.Max();
                    var lowerEnd = MathF.Max(drag.Track.Start + 1f, endLimit);
                    if (drag.MoveBoundaryKeys) {
                        lowerEnd = EnumerateTimelineProperties(drag.Track.Track.Properties).Where(IsValidRange)
                            .Select(property => drag.Track.BoundaryProperties.Any(item => ReferenceEquals(item.Property, property))
                                ? property.Info.startFrame : property.Info.endFrame)
                            .Concat(EnumerateKeys(drag.Track.Track.Properties)
                                .Where(item => IsValidRange(item.Property) && float.IsFinite(item.Key.frame)
                                    && !drag.Track.BoundaryKeys.Any(boundary => ReferenceEquals(boundary.Key, item.Key)))
                                .Select(item => item.Key.frame))
                            .Append(drag.Track.Start + 1f).Max();
                    }
                    if (lowerEnd > drag.MaximumFrame) break;
                    var newEnd = Math.Clamp(drag.Track.End + delta, lowerEnd, drag.MaximumFrame);
                    drag.Track.Track.endFrame = newEnd;
                    if (drag.MoveBoundaryKeys) ApplyBoundaryPropertyDelta(drag.Track.BoundaryProperties, newEnd - drag.Track.End, false);
                    if (drag.MoveBoundaryKeys) ApplyBoundaryKeyDelta(drag.Track.BoundaryKeys, newEnd - drag.Track.End);
                    break;
            }
        }
        return !before.SequenceEqual(GetDragFrames(drag));
    }

    private static void ApplyBoundaryKeyDelta(List<(KeyBase Key, float Frame)> keys, float delta)
    {
        foreach (var (key, frame) in keys) key.frame = frame + delta;
    }

    private static void ApplyBoundaryPropertyDelta(List<PropertySnapshot> properties, float delta, bool startEdge)
    {
        foreach (var snapshot in properties) {
            if (startEdge) snapshot.Property.Info.startFrame = snapshot.Start + delta;
            else snapshot.Property.Info.endFrame = snapshot.End + delta;
        }
    }

    private static List<float> GetTrackStartLimits(ClipTrack track) => EnumerateTimelineProperties(track.Properties)
        .Where(IsValidRange)
        .SelectMany(property => new[] { property.Info.startFrame }.Concat(
            property.Keys?.Select(key => key.frame) ?? []))
        .Where(float.IsFinite)
        .ToList();

    private static List<float> GetTrackEndLimits(ClipTrack track) => EnumerateTimelineProperties(track.Properties)
        .Where(IsValidRange)
        .SelectMany(property => new[] { property.Info.endFrame }.Concat(
            property.Keys?.Select(key => key.frame) ?? []))
        .Where(float.IsFinite)
        .ToList();

    private static IEnumerable<float> GetDragFrames(DragState drag) =>
        drag.Keys.Select(item => item.Key.frame)
            .Concat(drag.Properties.SelectMany(item =>
                new[] { item.Property.Info.startFrame, item.Property.Info.endFrame }.Concat(item.Keys.Select(key => key.Key.frame))))
            .Concat(drag.Track == null ? [] : new[] { drag.Track.Track.startFrame, drag.Track.Track.endFrame }
                .Concat(drag.Track.BoundaryProperties.SelectMany(property => new[] { property.Property.Info.startFrame, property.Property.Info.endFrame }))
                .Concat(drag.Track.BoundaryKeys.Select(key => key.Key.frame)));

    private static float ClampDragDelta(float delta, float minimum, float maximum)
    {
        minimum = MathF.Ceiling(minimum);
        maximum = MathF.Floor(maximum);
        return delta == 0f || minimum > maximum || !float.IsFinite(minimum) || !float.IsFinite(maximum)
            ? 0f : Math.Clamp(delta, minimum, maximum);
    }

    private void FinishDrag(DragState completed)
    {
        if (!completed.Changed || (!completed.Keys.Any(item => item.Key.frame != item.Frame)
            && !completed.Properties.Any(item => item.Property.Info.startFrame != item.Start || item.Property.Info.endFrame != item.End
                || item.Keys.Any(key => key.Key.frame != key.Frame))
            && (completed.Track == null || (completed.Track.Track.startFrame == completed.Track.Start
                && completed.Track.Track.endFrame == completed.Track.End
                && completed.Track.BoundaryProperties.All(property => property.Property.Info.startFrame == property.Start
                    && property.Property.Info.endFrame == property.End)
                && completed.Track.BoundaryKeys.All(key => key.Key.frame == key.Frame))))) return;
        if (completed.Mode == DragMode.MoveKeys) {
            var before = completed.Keys.Select(item => (item.Key, item.Frame)).ToList();
            var after = completed.Keys.Select(item => (item.Key, item.Key.frame)).ToList();
            UndoRedo.RecordCallback(null, () => ApplyKeyFrames(after), () => ApplyKeyFrames(before));
            return;
        }

        if (completed.Track != null) {
            var beforeTrack = completed.Track with {
                BoundaryProperties = completed.Track.BoundaryProperties.ToList(),
                BoundaryKeys = completed.Track.BoundaryKeys.ToList()
            };
            var afterTrack = new TrackSnapshot(completed.Track.Track, completed.Track.Track.startFrame,
                completed.Track.Track.endFrame,
                completed.Track.BoundaryProperties.Select(property => new PropertySnapshot(
                    property.Property,
                    property.Property.Info.startFrame,
                    property.Property.Info.endFrame,
                    property.Keys.Select(key => (key.Key, key.Key.frame)).ToList())).ToList(),
                completed.Track.BoundaryKeys.Select(key => (key.Key, key.Key.frame)).ToList());
            UndoRedo.RecordCallback(null, () => ApplyTrackSnapshot(afterTrack), () => ApplyTrackSnapshot(beforeTrack));
            return;
        }

        var beforeRanges = completed.Properties;
        var afterRanges = CaptureProperties(beforeRanges.Select(item => item.Property));
        UndoRedo.RecordCallback(null, () => ApplyPropertyFrames(afterRanges), () => ApplyPropertyFrames(beforeRanges));
    }

    private void ApplyKeyFrames(List<(KeyBase Key, float Frame)> values)
    {
        foreach (var (key, frame) in values) key.frame = frame;
        MarkModified();
    }

    private void ApplyPropertyFrames(List<PropertySnapshot> values)
    {
        foreach (var snapshot in values) {
            snapshot.Property.Info.startFrame = snapshot.Start;
            snapshot.Property.Info.endFrame = snapshot.End;
            foreach (var (key, frame) in snapshot.Keys) key.frame = frame;
        }
        MarkModified();
    }

    private void ApplyTrackSnapshot(TrackSnapshot snapshot)
    {
        snapshot.Track.startFrame = snapshot.Start;
        snapshot.Track.endFrame = snapshot.End;
        foreach (var property in snapshot.BoundaryProperties) {
            property.Property.Info.startFrame = property.Start;
            property.Property.Info.endFrame = property.End;
        }
        foreach (var (key, frame) in snapshot.BoundaryKeys) key.frame = frame;
        MarkModified();
    }
}
