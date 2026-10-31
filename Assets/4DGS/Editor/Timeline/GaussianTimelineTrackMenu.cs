using System;
using System.Collections.Generic;
using System.Linq;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GaussianSplatting.Editor
{
    /// <summary>
    /// The track and clip types the editor can create (every concrete
    /// subclass, so new kinds show up on their own), and the tracks a scene
    /// object can be bound to when it is dropped on the timeline.
    /// </summary>
    public static class GaussianTimelineTrackMenu
    {
        public static IEnumerable<Type> TrackTypes()
        {
            return TypeCache.GetTypesDerivedFrom<GaussianTimelineTrack>()
                .Where(t => !t.IsAbstract && !t.IsGenericType && t.GetConstructor(Type.EmptyTypes) != null)
                .OrderBy(t => GaussianTimelineStyles.KindName(t));
        }

        public static IEnumerable<Type> ClipTypes(GaussianTimelineClipTrack track)
        {
            return TypeCache.GetTypesDerivedFrom(track.ClipType)
                .Append(track.ClipType)
                .Where(t => !t.IsAbstract && !t.IsGenericType && t.GetConstructor(Type.EmptyTypes) != null)
                .Distinct()
                .OrderBy(t => GaussianTimelineStyles.KindName(t));
        }

        /// <summary>
        /// One menu entry per track that could drive <paramref name="gameObject"/>
        /// or one of its components; channel tracks list every channel.
        /// <paramref name="create"/> receives the new, named track and its binding.
        /// </summary>
        public static void AddBindingItems(GenericMenu menu, GameObject gameObject, GaussianTimelinePlayer player,
            Action<GaussianTimelineTrack, Object> create)
        {
            var candidates = new List<Object> { gameObject };
            candidates.AddRange(gameObject.GetComponents<Component>().Where(c => c != null));

            foreach (Type type in TrackTypes())
            {
                if (type == typeof(GaussianChannelTrack))
                    continue;
                var probe = (GaussianTimelineTrack)Activator.CreateInstance(type);
                foreach (Object candidate in candidates)
                {
                    if (candidate == player || !probe.CanBind(candidate))
                        continue;
                    Object target = candidate;
                    string kind = GaussianTimelineStyles.KindName(type);
                    string targetLabel = target is GameObject ? "GameObject" : target.GetType().Name;
                    menu.AddItem(new GUIContent($"{kind} ({targetLabel})"), false, () =>
                    {
                        var track = (GaussianTimelineTrack)Activator.CreateInstance(type);
                        track.Name = $"{gameObject.name} {kind}";
                        create(track, target);
                    });
                }
            }

            foreach (Component component in candidates.OfType<Component>())
            {
                if (component is not IGaussianTimelineTarget target)
                    continue;
                for (int i = 0; i < target.ChannelCount; i++)
                {
                    string channel = target.GetChannelName(i);
                    menu.AddItem(new GUIContent($"Channel/{component.GetType().Name}/{channel}"), false, () =>
                    {
                        var track = new GaussianChannelTrack { Channel = channel, Name = $"{component.name} {channel}" };
                        create(track, component);
                    });
                }
            }
        }

        public static string[] ChannelNames(Object binding)
        {
            if (binding is not IGaussianTimelineTarget target)
                return Array.Empty<string>();
            var names = new string[target.ChannelCount];
            for (int i = 0; i < names.Length; i++)
                names[i] = target.GetChannelName(i);
            return names;
        }
    }
}
