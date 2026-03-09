// SPDX-License-Identifier: MIT
// VR stereo-compatible splat render shader.
// Computes 2D covariance in the vertex shader (no compute prepass)
// and supports Unity Single-Pass Instanced stereo rendering.
Shader "Gaussian Splatting/Render Splats VR"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        Pass
        {
            ZWrite Off
            Blend One OneMinusSrcAlpha
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma require compute
            #pragma use_dxc
            #pragma multi_compile_instancing

            #include "UnityCG.cginc"

            // Include decode library first (provides SplatChunkData, SplatDecodeFromBuffers)
            #include "GaussianSplatDecode.hlsl"

            // Skip buffer declarations from GaussianSplatting.hlsl to avoid
            // type conflicts (SplatChunkInfo vs SplatChunkData for _SplatChunks)
            #define GAUSSIAN_SPLAT_SKIP_BUFFER_DECLARATIONS
            #include "GaussianSplatting.hlsl"

            // ── Shader parameters ──

            int _SplatCount;
            int _SplatInstanceSize;
            int _SplatFormat;
            int _SplatChunkCount;
            int _SHOrder;
            int _SHOnly;
            float _SplatScale;
            float _SplatOpacityScale;
            float4x4 _MatrixObjectToWorld;
            float4x4 _MatrixWorldToObject;

            StructuredBuffer<uint> _OrderBuffer;
            ByteAddressBuffer _SplatPos;
            ByteAddressBuffer _SplatOther;
            ByteAddressBuffer _SplatSH;
            Texture2D<half4> _SplatColor;
            StructuredBuffer<SplatChunkData> _SplatChunks;

            // Animation data (optional, set by GaussianAnimator)
            int _AnimDataValid;
            StructuredBuffer<float4> _AnimOutputData;

            // ── DecomposeCovariance (from SplatUtilities.compute) ──
            // Decomposes a 2x2 symmetric covariance matrix into two ellipse axes.

            void DecomposeCovariance(float3 cov2d, out float2 v1, out float2 v2)
            {
                float diag1 = cov2d.x, diag2 = cov2d.z, offDiag = cov2d.y;
                float mid = 0.5 * (diag1 + diag2);
                float radius = length(float2((diag1 - diag2) / 2.0, offDiag));
                float lambda1 = mid + radius;
                float lambda2 = max(mid - radius, 0.1);
                float2 diagVec = normalize(float2(offDiag, lambda1 - diag1));
                diagVec.y = -diagVec.y;
                float maxSize = 4096.0;
                v1 = min(sqrt(2.0 * lambda1), maxSize) * diagVec;
                v2 = min(sqrt(2.0 * lambda2), maxSize) * float2(diagVec.y, -diagVec.x);
            }

            // ── Vertex / Fragment structs ──

            struct appdata
            {
                float4 vertex : POSITION;
                #if !defined(UNITY_INSTANCING_ENABLED) && !defined(UNITY_PROCEDURAL_INSTANCING_ENABLED) && !defined(UNITY_STEREO_INSTANCING_ENABLED)
                uint instanceID : SV_InstanceID;
                #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                half4 col : COLOR0;
                float2 centerOffset : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // ── Vertex shader ──

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                // Decode splat index from mesh data
                // vertex.xy = quad corner, vertex.z = packed local splat index
                uint localIdx = asuint(v.vertex.z);
                #if defined(UNITY_INSTANCING_ENABLED) || defined(UNITY_PROCEDURAL_INSTANCING_ENABLED) || defined(UNITY_STEREO_INSTANCING_ENABLED)
                uint instID = unity_InstanceID;
                #else
                uint instID = v.instanceID;
                #endif
                uint globalIdx = instID * (uint)_SplatInstanceSize + localIdx;

                // Bounds check
                if (globalIdx >= (uint)_SplatCount)
                {
                    o.vertex = asfloat(0x7fc00000); // NaN discards primitive
                    return o;
                }

                // Get sorted splat index
                uint splatIdx = _OrderBuffer[globalIdx];

                // Decode splat data from compressed buffers
                DecodedSplat splat = SplatDecodeFromBuffers(
                    _SplatPos, _SplatOther, _SplatSH, _SplatColor,
                    _SplatChunks, (uint)_SplatChunkCount,
                    splatIdx, (uint)_SplatFormat);

                // Apply animation if active
                float animOpacityMul = 1.0;
                float animScaleMul = 1.0;
                float3 animColorTint = float3(1, 1, 1);
                float animColorBlend = 0;

                if (_AnimDataValid != 0)
                {
                    uint animBase = splatIdx * 3;
                    float4 animPosOp = _AnimOutputData[animBase + 0];
                    float4 animScaleColor = _AnimOutputData[animBase + 1];
                    float4 animTint = _AnimOutputData[animBase + 2];
                    splat.pos += animPosOp.xyz;
                    animOpacityMul = animPosOp.w;
                    animScaleMul = animScaleColor.x;
                    animColorBlend = animScaleColor.y;
                    animColorTint = animTint.xyz;
                }

                // Transform to view space using per-eye view matrix
                // UNITY_MATRIX_V is automatically per-eye in Single-Pass Instanced stereo
                float4x4 matMV = mul(UNITY_MATRIX_V, _MatrixObjectToWorld);
                float3 viewPos = mul(matMV, float4(splat.pos, 1)).xyz;

                // Behind camera check (Unity view space: -Z is forward)
                if (viewPos.z > 0)
                {
                    o.vertex = asfloat(0x7fc00000);
                    return o;
                }

                float4 clipPos = mul(UNITY_MATRIX_P, float4(viewPos, 1));
                clipPos.z = clamp(clipPos.z, -abs(clipPos.w), abs(clipPos.w));

                // Opacity check
                float finalOpacity = splat.opacity * _SplatOpacityScale * animOpacityMul;
                if (finalOpacity < 1.0 / 255.0)
                {
                    o.vertex = asfloat(0x7fc00000);
                    return o;
                }

                // Compute 3D covariance from rotation and scale
                float3 boxSize = splat.scale * animScaleMul;
                float3x3 rotScaleMat = CalcMatrixFromRotationScale(splat.rot, boxSize);
                float3 cov3d0, cov3d1;
                CalcCovariance3D(rotScaleMat, cov3d0, cov3d1);
                float splatScale2 = _SplatScale * _SplatScale;
                cov3d0 *= splatScale2;
                cov3d1 *= splatScale2;

                // Project to 2D covariance
                // CalcCovariance2D expects model-space position and model-view matrix
                float3 cov2d = CalcCovariance2D(
                    splat.pos, cov3d0, cov3d1,
                    matMV, UNITY_MATRIX_P, _ScreenParams);

                // Decompose into ellipse axes
                float2 axis1, axis2;
                DecomposeCovariance(cov2d, axis1, axis2);

                // Sub-pixel culling
                float area = length(axis1) * length(axis2);
                if (area < 1.0)
                {
                    o.vertex = asfloat(0x7fc00000);
                    return o;
                }

                // SH color evaluation with per-eye view direction
                // _WorldSpaceCameraPos is per-eye in stereo rendering
                float3 worldPos = mul(_MatrixObjectToWorld, float4(splat.pos, 1)).xyz;
                float3 worldViewDir = _WorldSpaceCameraPos.xyz - worldPos;
                float3 objViewDir = normalize(mul((float3x3)_MatrixWorldToObject, worldViewDir));

                // Convert DecodedSplat SH data (array) to SplatSHData (named fields)
                SplatSHData shData;
                shData.col = splat.color;
                shData.sh1 = splat.sh[0];
                shData.sh2 = splat.sh[1];
                shData.sh3 = splat.sh[2];
                shData.sh4 = splat.sh[3];
                shData.sh5 = splat.sh[4];
                shData.sh6 = splat.sh[5];
                shData.sh7 = splat.sh[6];
                shData.sh8 = splat.sh[7];
                shData.sh9 = splat.sh[8];
                shData.sh10 = splat.sh[9];
                shData.sh11 = splat.sh[10];
                shData.sh12 = splat.sh[11];
                shData.sh13 = splat.sh[12];
                shData.sh14 = splat.sh[13];
                shData.sh15 = splat.sh[14];

                half3 finalColor = ShadeSH(shData, (half3)objViewDir, _SHOrder, _SHOnly != 0);

                // Apply animation color tint
                if (animColorBlend > 0)
                    finalColor = lerp(finalColor, (half3)animColorTint, (half)animColorBlend);

                finalColor = max(finalColor, half3(0, 0, 0));
                o.col = half4(finalColor, (half)finalOpacity);

                // Expand quad using ellipse axes
                float2 quadPos = v.vertex.xy;
                o.centerOffset = quadPos;

                float2 deltaScreen = (quadPos.x * axis1 + quadPos.y * axis2) * 2 / _ScreenParams.xy;
                o.vertex = clipPos;
                o.vertex.xy += deltaScreen * clipPos.w;

                return o;
            }

            // ── Fragment shader ──

            half4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float power = -dot(i.centerOffset, i.centerOffset);
                half alpha = exp(power);
                alpha = saturate(alpha * i.col.a);

                if (alpha < 1.0 / 255.0)
                    discard;

                // Premultiplied alpha output
                return half4(i.col.rgb * alpha, alpha);
            }
            ENDHLSL
        }
    }
}
