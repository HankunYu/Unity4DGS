using System;

namespace Tide.Editor
{
    /// <summary>
    /// Clip-specific tools for one clip type in the timeline window: buttons
    /// below the clip's fields (fill it from the scene, fit it to its
    /// content...) and the contents a new clip starts with. Subclass it in any
    /// editor assembly; the window finds every subclass, and a clip uses the
    /// editor of its own type or else of the nearest base type that has one.
    /// </summary>
    public abstract class TideClipEditor
    {
        /// <summary>The clip type this editor handles, subclasses included.</summary>
        public abstract Type ClipType { get; }

        /// <summary>Draws the tools for the selected clip.</summary>
        public virtual void OnToolsGUI(TideClipEditorContext context)
        {
        }

        /// <summary>
        /// Fills a clip just added to a track from what the track is bound
        /// to. Runs with the timeline's preview lifted, so the scene shows its
        /// own values.
        /// </summary>
        public virtual void Initialise(TideClipEditorContext context)
        {
        }
    }
}
