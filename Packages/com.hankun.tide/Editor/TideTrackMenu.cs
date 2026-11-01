using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Tide.Editor
{
    /// <summary>
    /// The track and clip types the editor can create (every concrete
    /// subclass, so new kinds show up on their own), and the tracks a scene
    /// object can be bound to when it is dropped on the timeline.
    /// </summary>
    public static class TideTrackMenu
    {
        public static IEnumerable<Type> TrackTypes()
        {
            return TypeCache.GetTypesDerivedFrom<TideTrack>()
                .Where(t => !t.IsAbstract && !t.IsGenericType && t.GetConstructor(Type.EmptyTypes) != null)
                .OrderBy(t => TideStyles.KindName(t));
        }

        public static IEnumerable<Type> ClipTypes(TideClipTrack track)
        {
            return TypeCache.GetTypesDerivedFrom(track.ClipType)
                .Append(track.ClipType)
                .Where(t => !t.IsAbstract && !t.IsGenericType && t.GetConstructor(Type.EmptyTypes) != null)
                .Distinct()
                .OrderBy(t => TideStyles.KindName(t));
        }

        /// <summary>
        /// One menu entry per track that could drive <paramref name="gameObject"/>
        /// or one of its components; channel tracks list every channel.
        /// <paramref name="create"/> receives the new, named track and its binding.
        /// </summary>
        public static void AddBindingItems(GenericMenu menu, GameObject gameObject, TidePlayer player,
            Action<TideTrack, Object> create)
        {
            var candidates = new List<Object> { gameObject };
            candidates.AddRange(gameObject.GetComponents<Component>().Where(c => c != null));

            foreach (Type type in TrackTypes())
            {
                if (type == typeof(TideChannelTrack))
                    continue;
                var probe = (TideTrack)Activator.CreateInstance(type);
                foreach (Object candidate in candidates)
                {
                    if (candidate == player || !probe.CanBind(candidate))
                        continue;
                    Object target = candidate;
                    string kind = TideStyles.KindName(type);
                    string targetLabel = target is GameObject ? "GameObject" : target.GetType().Name;
                    menu.AddItem(new GUIContent($"{kind} ({targetLabel})"), false, () =>
                    {
                        var track = (TideTrack)Activator.CreateInstance(type);
                        track.Name = $"{gameObject.name} {kind}";
                        create(track, target);
                    });
                }
            }

            foreach (Component component in candidates.OfType<Component>())
            {
                if (component is not ITideChannelTarget target)
                    continue;
                for (int i = 0; i < target.ChannelCount; i++)
                {
                    string channel = target.GetChannelName(i);
                    menu.AddItem(new GUIContent($"Channel/{component.GetType().Name}/{channel}"), false, () =>
                    {
                        var track = new TideChannelTrack { Channel = channel, Name = $"{component.name} {channel}" };
                        create(track, component);
                    });
                }
            }
        }

        public static string[] ChannelNames(Object binding)
        {
            if (binding is not ITideChannelTarget target)
                return Array.Empty<string>();
            var names = new string[target.ChannelCount];
            for (int i = 0; i < names.Length; i++)
                names[i] = target.GetChannelName(i);
            return names;
        }
    }
}
