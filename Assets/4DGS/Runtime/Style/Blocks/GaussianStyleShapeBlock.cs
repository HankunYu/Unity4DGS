// SPDX-License-Identifier: MIT

using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    // Draws the splats as dots, streaks or solid shapes instead of the
    // render mode's own shape. Mirrors StyleShape in GaussianStyleShape.hlsl.
    [Serializable]
    public sealed class GaussianStyleShapeBlock : GaussianStyleBlock<GaussianStyleShapeBlock.Data>
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Data
        {
            public float on;
            public float shape;
            public float streakAxis;
            public float dotRadius;
            public Vector3 streakDirection;
            public float streakTime;
            public float streakLength;
            public float streakHalfWidth;
            public float minWorldSize;
            public float solidSize;
        }

        [SerializeField] private GaussianSplatStyleShape shape = GaussianSplatStyleShape.Disc;
        [Tooltip("Dot diameter in pixels, for the Disc shape. Below 2 pixels a dot is drawn 2 pixels wide and dimmer, so it does not alias")]
        [SerializeField, Range(0.5f, 16f)] private float dotSize = 2f;
        [SerializeField] private GaussianSplatStyleStreakAxis streakAxis = GaussianSplatStyleStreakAxis.WorldAxis;
        [Tooltip("World direction streaks trail in, for the World Axis streak axis; the direction the hatching runs across, for the Surface streak axis")]
        [SerializeField] private Vector3 streakDirection = Vector3.down;
        [Tooltip("Streak length in world units, for the World Axis, View Ray and Surface streak axes")]
        [SerializeField, Min(0f)] private float streakLength = 0.3f;
        [Tooltip("Seconds of motion a streak covers, for the Velocity streak axis")]
        [SerializeField, Min(0f)] private float streakTime = 0.05f;
        [Tooltip("Streak width in pixels. Below 2 pixels a streak is drawn 2 pixels wide and dimmer, so it does not alias")]
        [SerializeField, Range(0.5f, 8f)] private float streakWidth = 1f;
        [Tooltip("How fast a streak fades from the splat to its tail: higher keeps the light near the splat, 0 keeps it even along the streak")]
        [SerializeField, Range(0f, 8f)] private float streakTailPower = 1.5f;
        [Tooltip("Smallest dot diameter or streak width in world units. Their pixel size holds at a distance; up close this floor takes over, so they grow with the scene instead of shrinking into it. 0 turns it off")]
        [SerializeField, Min(0f)] private float minWorldSize;
        [Tooltip("Diameter of a sphere or edge of a cube in world units, for the Sphere and Cube shapes. They are solid: use a Blend block with no additivity, and thin the splats or snap them to a grid so the shapes stand apart")]
        [SerializeField, Min(0.0001f)] private float solidSize = 0.02f;
        [Tooltip("How much the spheres or cubes differ in size: each takes its own size between the full one and that much smaller. 0 makes them all alike")]
        [SerializeField, Range(0f, 0.95f)] private float solidSizeVariation;
        [Tooltip("How strongly a sphere or cube shows its own form. Its color is the splat's, lit as the model is at that point; the form swings around that color, brighter towards the key light (the scene lighting's shadow light, else its strongest light) and darker away from it. 0 leaves the shape flat, 1 takes the far side to black; above 1 the dark reaches round towards the light and the lit side grows brighter still, so lower the color intensity with it")]
        [SerializeField, Range(0f, 3f)] private float solidShading = 0.75f;

        public GaussianSplatStyleShape Shape => shape;

        internal override Data Pack(GaussianSplatStyle style) => new()
        {
            on = 1,
            shape = (uint)shape,
            streakAxis = (uint)streakAxis,
            dotRadius = dotSize * 0.5f,
            streakDirection = streakDirection.sqrMagnitude > 1e-8f ? streakDirection.normalized : Vector3.down,
            streakTime = streakTime,
            streakLength = streakLength,
            streakHalfWidth = streakWidth * 0.5f,
            minWorldSize = minWorldSize,
            solidSize = solidSize,
        };

        // The streak's length plus the world size floor's radius. Velocity
        // streaks are as long as the fastest splat moves in the streak time,
        // Segment streaks as the longest segment.
        internal override float Reach(float maxSpeed, float maxSegment)
        {
            // A solid shape's farthest point: a cube's corner
            if (shape == GaussianSplatStyleShape.Sphere)
                return solidSize * 0.5f;
            if (shape == GaussianSplatStyleShape.Cube)
                return solidSize * 0.5f * Mathf.Sqrt(3f);
            float floor = minWorldSize * 0.5f;
            if (shape != GaussianSplatStyleShape.Streak)
                return floor;
            return floor + streakAxis switch
            {
                GaussianSplatStyleStreakAxis.Velocity => streakTime * maxSpeed,
                GaussianSplatStyleStreakAxis.Segment => maxSegment,
                _ => streakLength,
            };
        }

        internal override void FillRange(ref GaussianSplatStyleRange range, int slot)
        {
            range.shape[slot] = (float)shape;
            range.streakTailPower[slot] = streakTailPower;
            range.solidSize[slot] = solidSize;
            range.solidShading[slot] = solidShading;
            range.solidSizeVariation[slot] = solidSizeVariation;
        }
    }
}
