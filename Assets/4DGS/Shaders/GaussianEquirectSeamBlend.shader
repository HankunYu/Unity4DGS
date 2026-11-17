// SPDX-License-Identifier: MIT
// Joins equirect renders of the same view into one without seams.
//
// An equirect's left and right edges meet behind the viewer, but nothing
// drawn into it wraps around: a splat on the edge loses the half that falls
// outside, and screen-space effects such as bloom stop at it. A second
// render is turned half a turn, so its own edges lie in front and its middle
// is whole where the first one is cut.
//
// Its top and bottom rows are each a single point on the sphere, so
// screen-space effects are squeezed there and a splat on a pole would have
// to ring it. A third render is rolled a quarter turn, which puts the poles
// on its horizon, to its left and right.
//
// Each render is used away from where it fails, with a wide cross-fade
// between.
Shader "Hidden/Gaussian Splatting/Equirect Seam Blend"
{
    Properties
    {
        _MainTex ("Render", 2D) = "black" {}
        _TurnedTex ("Render turned half a turn", 2D) = "black" {}
        _PoleTex ("Render rolled a quarter turn", 2D) = "black" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag

            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _TurnedTex;
            sampler2D _PoleTex;
            // x, y: distance from the edge, as a fraction of the width,
            // where the cross-fade to the turned render starts and ends
            float4 _SeamFade;
            // x, y: elevation off the horizon, in radians, where the
            // cross-fade to the rolled render starts and ends
            float4 _PoleFade;

            half4 frag (v2f_img i) : SV_Target
            {
                half4 main = tex2D(_MainTex, i.uv);
                // Half a turn is half the width
                half4 turned = tex2D(_TurnedTex, float2(frac(i.uv.x + 0.5), i.uv.y));
                float edgeDistance = min(i.uv.x, 1.0 - i.uv.x);
                half4 color = lerp(turned, main, smoothstep(_SeamFade.x, _SeamFade.y, edgeDistance));

                // This pixel's direction in the rig (+x right, +y up,
                // +z forward), then in the rig rolled a quarter turn, whose
                // right is this one's up
                float theta = (i.uv.x - 0.5) * UNITY_TWO_PI;
                float elev = (i.uv.y - 0.5) * UNITY_PI;
                float3 dir = float3(cos(elev) * sin(theta), sin(elev), cos(elev) * cos(theta));
                float3 rolled = float3(dir.y, -dir.x, dir.z);
                float2 poleUv = float2(atan2(rolled.x, rolled.z) / UNITY_TWO_PI + 0.5,
                                       asin(clamp(rolled.y, -1.0, 1.0)) / UNITY_PI + 0.5);
                // Explicit level: the mapping swings round at the pole, and
                // derivatives across it would pick a blurred one
                half4 pole = tex2Dlod(_PoleTex, float4(poleUv, 0, 0));
                return lerp(color, pole, smoothstep(_PoleFade.x, _PoleFade.y, abs(elev)));
            }
            ENDCG
        }
    }
}
