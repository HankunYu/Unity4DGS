using UnityEngine;

namespace GaussianSplatting.Runtime
{
    // Decides when a set of sorted keys is stale.
    //
    // Keys are only valid for the camera and sort settings that produced them,
    // and sortNthFrame caps how often they are refreshed. On top of that, a
    // throttled sort (radial metric, static content) is skipped until the
    // camera has actually moved: radial order does not change when the camera
    // only rotates, so a head-tracked VR camera re-sorts on translation, not
    // on every frame it renders.
    internal sealed class GaussianSplatSortState
    {
        // How the keys of one sort are produced. Keys sorted under different
        // settings are never reused.
        public struct Settings
        {
            // Radial distance instead of view depth.
            public bool Radial;
            // Descending order, for the direct over-blend (offscreen under-blend
            // wants front-to-back).
            public bool BackToFront;
            // Throttled sorting: keys outlive the view they were sorted for, so
            // they must not drop the splats that view happened to cull — a
            // later, rotated view would be missing them.
            public bool Throttled;

            public int Key => (Radial ? 1 : 0) | (BackToFront ? 2 : 0) | (Throttled ? 4 : 0);
        }

        private bool _valid;
        private int _cameraId;
        private int _frame;
        private int _settingsKey;
        private int _contentKey;
        private Vector3 _cameraPos;

        public void Invalidate() => _valid = false;

        // cameraPos must be in a space that also moves when the sorted object
        // moves (object-local for one renderer), so either motion re-sorts.
        public bool NeedsSort(int cameraId, int settingsKey, int contentKey, Vector3 cameraPos,
            int nthFrame, bool throttle, float moveThreshold)
        {
            if (!_valid || cameraId != _cameraId || settingsKey != _settingsKey)
                return true;
            if (Time.frameCount - _frame < Mathf.Max(1, nthFrame))
                return false;
            if (!throttle)
                return true;
            return contentKey != _contentKey ||
                   (cameraPos - _cameraPos).sqrMagnitude > moveThreshold * moveThreshold;
        }

        public void MarkSorted(int cameraId, int settingsKey, int contentKey, Vector3 cameraPos)
        {
            _valid = true;
            _cameraId = cameraId;
            _frame = Time.frameCount;
            _settingsKey = settingsKey;
            _contentKey = contentKey;
            _cameraPos = cameraPos;
        }
    }
}
