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
    private List<VisibleLane> BuildTimelineLanes()
    {
        List<VisibleLane> lanes = new();
        if (!motlistExpanded || !motclipsExpanded)
        {
            return lanes;
        }

        foreach (Motclip motclip in motlist.Motclips)
        {
            lanes.Add(new VisibleLane($"Motclip: {motclip.Name}", motclip.Name, 0, null, null, motclip, motclip.StartFrame, motclip.EndFrame));
            if (collapsedMotclips.Contains(motclip) || collapsedTrackGroups.Contains(motclip))
            {
                continue;
            }

            foreach (TimelineTrack track in motclip.Tracks)
            {
                lanes.Add(new VisibleLane(track.Name, $"{motclip.Name}/{track.Name}", 0, null, track, motclip, track.StartFrame, track.EndFrame));

            }
        }

        for (int i = 0; i < lanes.Count; i++)
        {
            lanes[i] = lanes[i] with { Y = lanes.Count - i - 1 };
        }

        return lanes;
    }

    private void DrawHierarchy()
    {
        ImGui.SetNextItemOpen(motlistExpanded, ImGuiCond.Once);
        motlistExpanded = TimelineTreeNode($"{motlist.Name} ({motlist.Motclips.Count})", "M",
            new Vector4(0.35f, 0.75f, 1f, 1f), ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick | ImGuiTreeNodeFlags.SpanAvailWidth);
        if (motlistExpanded)
        {
            ImGui.SetNextItemOpen(motclipsExpanded, ImGuiCond.Once);
            motclipsExpanded = TimelineTreeNode("Motclips", "C",
                new Vector4(0.95f, 0.68f, 0.28f, 1f), ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick | ImGuiTreeNodeFlags.SpanAvailWidth);
            if (motclipsExpanded)
            {
                for(int index=0;index<motlist.Motclips.Count;index++)
                {
                    ImGui.PushID(index);DrawMotclip(motlist.Motclips[index]);ImGui.PopID();
                }

                ImGui.TreePop();
            }

            ImGui.TreePop();
        }

    }

    private void DrawMotclip(Motclip motclip)
    {
        bool motclipOpen = !collapsedMotclips.Contains(motclip);
        ImGui.SetNextItemOpen(motclipOpen, ImGuiCond.Always);
        motclipOpen = TimelineTreeNode(motclip.Name, "C",
            new Vector4(0.95f, 0.68f, 0.28f, 1f), ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick | ImGuiTreeNodeFlags.SpanAvailWidth);
        if(ImGui.IsItemClicked(ImGuiMouseButton.Right)&&!HasModifiers())
        {
            contextClip=motclip;contextTrack=null;contextProperty=null;contextKey=null;ImGui.OpenPopup("TimelineContext");
        }
        DrawTimelineContextMenu();
        if (motclipOpen)
        {
            collapsedMotclips.Remove(motclip);
        }
        else
        {
            collapsedMotclips.Add(motclip);
        }

        if (!motclipOpen)
        {
            return;
        }

        bool tracksOpen = !collapsedTrackGroups.Contains(motclip);
        ImGui.SetNextItemOpen(tracksOpen, ImGuiCond.Always);
        tracksOpen = TimelineTreeNode($"Tracks ({motclip.Tracks.Count})###tracks", "T",
            new Vector4(0.45f, 0.78f, 0.58f, 1f), ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick | ImGuiTreeNodeFlags.SpanAvailWidth);
        if (tracksOpen)
        {
            collapsedTrackGroups.Remove(motclip);
        }
        else
        {
            collapsedTrackGroups.Add(motclip);
        }

        if (tracksOpen)
        {
            for (int index = 0; index < motclip.Tracks.Count; index++)
            {
                ImGui.PushID(motclip.Tracks[index].UiId);
                DrawTrack(motclip, motclip.Tracks[index]);
                ImGui.PopID();
            }

            ImGui.TreePop();
        }

        ImGui.TreePop();
    }

    private void DrawTrack(Motclip motclip, TimelineTrack track)
    {
        ImGui.SetNextItemOpen(!collapsedTracks.Contains(track), ImGuiCond.Always);
        bool trackOpen = TimelineTreeNode($"{track.Name}  [{track.StartFrame:0.##} - {track.EndFrame:0.##}]###track", "T",
            new Vector4(0.45f, 0.78f, 0.58f, 1f), ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick | ImGuiTreeNodeFlags.SpanAvailWidth);
        if(ImGui.IsItemClicked(ImGuiMouseButton.Right)&&!HasModifiers())
        {
            contextClip=motclip;contextTrack=track;contextProperty=null;contextKey=null;ImGui.OpenPopup("TimelineContext");
        }
        DrawTimelineContextMenu();
        if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen()) SelectTrack(motclip, track);
        if (trackOpen)
        {
            collapsedTracks.Remove(track);
        }
        else
        {
            collapsedTracks.Add(track);
        }

        if (!trackOpen)
        {
            return;
        }

        if (!string.IsNullOrEmpty(track.Details))
        {
            ImGui.TextDisabled(track.Details);
        }

        bool propertiesOpen = !collapsedPropertyGroups.Contains(track);
        ImGui.SetNextItemOpen(propertiesOpen, ImGuiCond.Once);
        propertiesOpen = TimelineTreeNode($"Properties ({track.Properties.Count})###properties", "P",
            new Vector4(0.62f, 0.69f, 0.96f, 1f), ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick | ImGuiTreeNodeFlags.SpanAvailWidth);
        if (propertiesOpen)
        {
            collapsedPropertyGroups.Remove(track);
        }
        else
        {
            collapsedPropertyGroups.Add(track);
        }

        if (propertiesOpen)
        {
            for (int i = 0; i < track.Properties.Count; i++)
            {
                TimelineProperty property = track.Properties[i];
                ImGui.PushID(property.UiId);
                DrawProperty(property);
                ImGui.PopID();
            }

            ImGui.TreePop();
        }

        ImGui.TreePop();
    }

    private void DrawProperty(TimelineProperty property)
    {
        ImGui.SetNextItemOpen(false, ImGuiCond.Once);
        bool propertyOpen = TimelineTreeNode(property.Name, "P",
            new Vector4(0.62f, 0.69f, 0.96f, 1f), ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick | ImGuiTreeNodeFlags.SpanAvailWidth);
        if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen())
        {
            var owner = motlist.Motclips.SelectMany(c => c.Tracks.Select(t => (Clip:c, Track:t))).First(x => x.Track.Properties.Contains(property));
            SelectTrack(owner.Clip, owner.Track); selectedProperty = property; selectedKey = null;
        }
        TooltipHelper.Tooltip($"Property {property.Name}; {property.Keys.Count} keys. Right-click its timeline row to add or paste a key.");
        if (!propertyOpen)
        {
            return;
        }

        if (!string.IsNullOrEmpty(property.ValueRange))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(property.StartFrame.HasValue && property.EndFrame.HasValue ? $"[{property.StartFrame:0.##} - {property.EndFrame:0.##}]" : property.ValueRange);
        }

        ImGui.SetNextItemOpen(false, ImGuiCond.Once);
        if (TimelineTreeNode($"Keys ({property.Keys.Count})###keys", "K",
            new Vector4(0.95f, 0.76f, 0.32f, 1f), ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick | ImGuiTreeNodeFlags.SpanAvailWidth))
        {
            for (int keyIndex = 0; keyIndex < property.Keys.Count; keyIndex++)
            {
                TimelineKey key = property.Keys[keyIndex];
                string value = key.Value.HasValue ? key.Value.Value.ToString("0.##") : "value unavailable";
                ImGui.Text($"{keyIndex} [{key.Name}, Frame {key.Frame:0.##}]: {value}");
            }

            ImGui.TreePop();
        }

        ImGui.TreePop();
    }

    private bool TimelineTreeNode(string label, string icon, Vector4 color, ImGuiTreeNodeFlags flags)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        bool open = ImGui.TreeNodeEx($"{icon}  {label}", flags);
        ImGui.PopStyleColor();
        return open;
    }

}
