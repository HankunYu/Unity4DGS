using System;
using System.IO;
using System.Text;
using GaussianSplatting.Runtime;
using Unity.Collections.LowLevel.Unsafe;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    /// <summary>
    /// PLY export logic, extracted from GaussianSplatRendererEditor.
    /// Accessible via Inspector button or menu item.
    /// </summary>
    internal static class GaussianSplatExporter
    {
        const string PrefExportBake = "nesnausk.GaussianSplatting.ExportBakeTransform";

        /// <summary>
        /// Draws the export section in the Renderer Inspector.
        /// </summary>
        public static void DrawExportGUI(GaussianSplatRenderer gs, ref bool bakeTransform)
        {
            var asset = gs.asset;
            EditorGUILayout.Space();

            EditorGUI.BeginChangeCheck();
            bakeTransform = EditorGUILayout.Toggle("Export in world space", bakeTransform);
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetBool(PrefExportBake, bakeTransform);
            }

            if (GUILayout.Button("Export PLY"))
                ExportPlyFile(gs, bakeTransform);

            if (asset.posFormat > GaussianSplatAsset.VectorFormat.Norm16 ||
                asset.scaleFormat > GaussianSplatAsset.VectorFormat.Norm16 ||
                asset.colorFormat > GaussianSplatAsset.ColorFormat.Float16x4 ||
                asset.shFormat > GaussianSplatAsset.SHFormat.Float16)
            {
                EditorGUILayout.HelpBox(
                    "It is recommended to use High or VeryHigh quality preset for editing splats, lower levels are lossy",
                    MessageType.Warning);
            }
        }

        [MenuItem("Tools/Gaussian Splatting/Export Selected as PLY")]
        private static void ExportSelectedMenuItem()
        {
            var go = Selection.activeGameObject;
            if (go == null)
            {
                Debug.LogWarning("No GameObject selected");
                return;
            }

            var gs = go.GetComponent<GaussianSplatRenderer>();
            if (gs == null || !gs.HasValidAsset)
            {
                Debug.LogWarning("Selected object does not have a valid GaussianSplatRenderer");
                return;
            }

            bool bake = EditorPrefs.GetBool(PrefExportBake, false);
            ExportPlyFile(gs, bake);
        }

        [MenuItem("Tools/Gaussian Splatting/Export Selected as PLY", true)]
        private static bool ExportSelectedMenuItemValidate()
        {
            var go = Selection.activeGameObject;
            if (go == null) return false;
            var gs = go.GetComponent<GaussianSplatRenderer>();
            return gs != null && gs.HasValidAsset;
        }

        internal static void ExportPlyFile(GaussianSplatRenderer gs, bool bakeTransform)
        {
            var path = EditorUtility.SaveFilePanel(
                "Export Gaussian Splat PLY file", "", $"{gs.asset.name}-edit.ply", "ply");
            if (string.IsNullOrWhiteSpace(path))
                return;
            WritePlyFile(gs, path, bakeTransform);
        }

        internal static unsafe void WritePlyFile(GaussianSplatRenderer gs, string path, bool bakeTransform)
        {
            int kSplatSize = UnsafeUtility.SizeOf<Import.InputSplatData>();
            using var gpuData = new GraphicsBuffer(GraphicsBuffer.Target.Structured, gs.splatCount, kSplatSize);

            if (!gs.EditExportData(gpuData, bakeTransform))
                return;

            Import.InputSplatData[] data = new Import.InputSplatData[gpuData.count];
            gpuData.GetData(data);

            var gpuDeleted = gs.GpuEditDeleted;
            uint[] deleted = new uint[gpuDeleted.count];
            gpuDeleted.GetData(deleted);

            // count non-deleted splats
            int aliveCount = 0;
            for (int i = 0; i < data.Length; ++i)
            {
                int wordIdx = i >> 5;
                int bitIdx = i & 31;
                bool isDeleted = (deleted[wordIdx] & (1u << bitIdx)) != 0;
                bool isCutout = data[i].nor.sqrMagnitude > 0;
                if (!isDeleted && !isCutout)
                    ++aliveCount;
            }

            // The export kernel fills all 15 SH coefficients per channel
            // (channel-major, as PLY stores them); write the ones the asset has.
            const int SHFloatOffset = 9;
            const int SHPerChannel = 15;
            int coeffs = GaussianSplatAsset.GetSHCoeffCount(gs.asset.shDegree);

            // LF line endings, as PLY readers expect
            var header = new StringBuilder();
            header.Append($"ply\nformat binary_little_endian 1.0\nelement vertex {aliveCount}\n");
            foreach (var name in new[] { "x", "y", "z", "nx", "ny", "nz", "f_dc_0", "f_dc_1", "f_dc_2" })
                header.Append($"property float {name}\n");
            for (int i = 0; i < coeffs * 3; ++i)
                header.Append($"property float f_rest_{i}\n");
            foreach (var name in new[] { "opacity", "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3" })
                header.Append($"property float {name}\n");
            header.Append("end_header\n");

            using FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            fs.Write(Encoding.UTF8.GetBytes(header.ToString()));
            for (int i = 0; i < data.Length; ++i)
            {
                int wordIdx = i >> 5;
                int bitIdx = i & 31;
                bool isDeleted = (deleted[wordIdx] & (1u << bitIdx)) != 0;
                bool isCutout = data[i].nor.sqrMagnitude > 0;
                if (!isDeleted && !isCutout)
                {
                    var splat = data[i];
                    float* ptr = (float*)&splat;
                    fs.Write(new ReadOnlySpan<byte>(ptr, SHFloatOffset * 4));
                    for (int c = 0; c < 3; ++c)
                        fs.Write(new ReadOnlySpan<byte>(ptr + SHFloatOffset + c * SHPerChannel, coeffs * 4));
                    int tail = SHFloatOffset + 3 * SHPerChannel;
                    fs.Write(new ReadOnlySpan<byte>(ptr + tail, kSplatSize - tail * 4));
                }
            }

            Debug.Log($"Exported PLY {path} with {aliveCount:N0} splats");
        }
    }
}
