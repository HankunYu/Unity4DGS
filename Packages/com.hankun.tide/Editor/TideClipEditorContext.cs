using System;
using Object = UnityEngine.Object;

namespace Tide.Editor
{
    /// <summary>The clip a <see cref="TideClipEditor"/> works on, and what it may do to the timeline.</summary>
    public sealed class TideClipEditorContext
    {
        private readonly Action<string, Action> _edit;
        private readonly Action<Action> _readScene;
        private readonly Action<TidePlayer> _openPlayer;

        internal TideClipEditorContext(TideClipTrack track, TideClip clip, Object binding,
            Action<string, Action> edit, Action<Action> readScene, Action<TidePlayer> openPlayer)
        {
            Track = track;
            Clip = clip;
            Binding = binding;
            _edit = edit;
            _readScene = readScene;
            _openPlayer = openPlayer;
        }

        public TideClipTrack Track { get; }

        public TideClip Clip { get; }

        /// <summary>The scene object the track is bound to, or null.</summary>
        public Object Binding { get; }

        /// <summary>Changes the timeline asset as one undo step named <paramref name="label"/>.</summary>
        public void Edit(string label, Action edit) => _edit(label, edit);

        /// <summary>
        /// Runs <paramref name="read"/> with the timeline's preview lifted, so
        /// the scene shows its own values rather than the previewed ones.
        /// </summary>
        public void ReadScene(Action read) => _readScene(read);

        /// <summary>Switches the window to another player, e.g. a sub-timeline's.</summary>
        public void OpenPlayer(TidePlayer player) => _openPlayer(player);

        /// <summary>The channel a channel track drives on its binding.</summary>
        public bool TryChannel(out ITideChannelTarget target, out int index)
        {
            target = Binding as ITideChannelTarget;
            index = target != null && Track is TideChannelTrack channelTrack
                ? TideChannelTrack.FindChannel(target, channelTrack.Channel)
                : -1;
            return index >= 0;
        }
    }
}
