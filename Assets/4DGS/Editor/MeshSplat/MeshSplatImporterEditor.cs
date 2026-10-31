using System.Collections.Generic;
using System.IO;
using System.Linq;
using GaussianSplatting.Editor.Import;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace GaussianSplatting.Editor.MeshSplat
{
    [CustomEditor(typeof(MeshSplatImporter))]
    [CanEditMultipleObjects]
    public class MeshSplatImporterEditor : ScriptedImporterEditor
    {
        private const string CreateMenu = "Assets/Create/Gaussian Splats/Mesh Splats";
        private const string FileContent = "Mesh splat source: the model and settings are in the .meta file.\n";

        private static readonly GUIContent s_ModelLabel = new("Model",
            "Model asset or prefab to sample. Every MeshRenderer and SkinnedMeshRenderer under it is " +
            "used; skinned meshes in their bind pose.");
        private static readonly GUIContent s_OutputLabel = new("Output",
            "Asset: one splat asset in the rest pose; drag it into the scene and a GaussianMeshDriver moves it with " +
            "the model. Sequence: one splat asset per animation frame, played by a GaussianPlayer. Every frame is " +
            "held in memory while importing.");
        private static readonly GUIContent s_OverridesLabel = new("Overrides",
            "Materials colored by another source than the default one, e.g. a textured character among " +
            "uniformly colored props.");
        private static readonly GUIContent s_LightLabel = new("Bake Lighting",
            "Multiply the color by a fixed directional light. An asset bakes it in the rest pose, so it turns " +
            "with the model as it animates; a sequence bakes it for every frame's pose.");
        private static readonly GUIContent s_LightDirLabel = new("Light Direction", "Towards the light, in model space.");
        private static readonly GUIContent s_SizeLabel = new("Splat Size",
            "Splat sigma relative to the local distance between splats. Lower leaves gaps between points.");
        private static readonly GUIContent s_ThicknessLabel = new("Splat Thickness",
            "Splat extent along the surface normal relative to its size: low values give flat discs.");

        private SerializedProperty _model;
        private SerializedProperty _settings;
        private SerializedProperty _output;
        private SerializedProperty _sequence;
        private SerializedProperty _sampler;
        private SerializedProperty _color;
        private SerializedProperty _colorOverrides;
        private SerializedProperty _bakeLighting;
        private SerializedProperty _lightDirection;
        private SerializedProperty _ambient;
        private SerializedProperty _splatSize;
        private SerializedProperty _splatThickness;
        private SerializedProperty _opacity;
        private SerializedProperty _quality;
        private SerializedProperty _customFormats;

        public override void OnEnable()
        {
            base.OnEnable();
            _model = serializedObject.FindProperty("model");
            var settings = _settings = serializedObject.FindProperty("settings");
            _output = settings.FindPropertyRelative(nameof(MeshSplatSettings.output));
            _sequence = settings.FindPropertyRelative(nameof(MeshSplatSettings.sequence));
            _sampler = settings.FindPropertyRelative(nameof(MeshSplatSettings.sampler));
            _color = settings.FindPropertyRelative(nameof(MeshSplatSettings.color));
            _colorOverrides = settings.FindPropertyRelative(nameof(MeshSplatSettings.colorOverrides));
            _bakeLighting = settings.FindPropertyRelative(nameof(MeshSplatSettings.bakeLighting));
            _lightDirection = settings.FindPropertyRelative(nameof(MeshSplatSettings.lightDirection));
            _ambient = settings.FindPropertyRelative(nameof(MeshSplatSettings.ambient));
            _splatSize = settings.FindPropertyRelative(nameof(MeshSplatSettings.splatSize));
            _splatThickness = settings.FindPropertyRelative(nameof(MeshSplatSettings.splatThickness));
            _opacity = settings.FindPropertyRelative(nameof(MeshSplatSettings.opacity));
            _quality = settings.FindPropertyRelative(nameof(MeshSplatSettings.quality));
            _customFormats = settings.FindPropertyRelative(nameof(MeshSplatSettings.customFormats));
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(_model, s_ModelLabel);
            if (targets.Length == 1 && assetTarget is GaussianSplatAsset asset)
                EditorGUILayout.LabelField("Splats", $"{asset.splatCount:N0}");
            if (targets.Length == 1 && assetTarget is GaussianSplatSequence sequence && sequence.FrameCount > 0)
            {
                int largest = sequence.frames.Max(f => f != null ? f.splatCount : 0);
                long size = sequence.frames.Sum(f => f != null ? f.posData.dataSize + f.otherData.dataSize + f.colorData.dataSize : 0);
                EditorGUILayout.LabelField("Frames", $"{sequence.FrameCount} at {sequence.frameRate:0.##} fps, " +
                                                     $"up to {largest:N0} splats, {EditorUtility.FormatBytes(size)}");
            }

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(_output, s_OutputLabel);
            if (!_output.hasMultipleDifferentValues && (MeshSplatOutput)_output.enumValueIndex == MeshSplatOutput.Sequence)
                DrawFields(_sequence);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Sampling", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_sampler);
            if (!_sampler.hasMultipleDifferentValues)
                DrawFields(SamplerProperty((SurfaceSamplerKind)_sampler.enumValueIndex));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Color", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_color, new GUIContent("Source"));
            if (!_color.hasMultipleDifferentValues)
                DrawFields(ColorProperty((SplatColorKind)_color.enumValueIndex));
            EditorGUILayout.PropertyField(_colorOverrides, s_OverridesLabel, true);
            if (!_color.hasMultipleDifferentValues && !_colorOverrides.hasMultipleDifferentValues)
                DrawOverrideSources();
            EditorGUILayout.PropertyField(_bakeLighting, s_LightLabel);
            if (_bakeLighting.boolValue || _bakeLighting.hasMultipleDifferentValues)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_lightDirection, s_LightDirLabel);
                EditorGUILayout.PropertyField(_ambient);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Splats", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_splatSize, s_SizeLabel);
            EditorGUILayout.PropertyField(_splatThickness, s_ThicknessLabel);
            EditorGUILayout.PropertyField(_opacity);
            EditorGUILayout.PropertyField(_quality);
            if (!_quality.hasMultipleDifferentValues && (GaussianSplatQuality)_quality.enumValueIndex == GaussianSplatQuality.Custom)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_customFormats.FindPropertyRelative(nameof(GaussianSplatFormats.pos)), new GUIContent("Position"));
                EditorGUILayout.PropertyField(_customFormats.FindPropertyRelative(nameof(GaussianSplatFormats.scale)), new GUIContent("Scale"));
                EditorGUILayout.PropertyField(_customFormats.FindPropertyRelative(nameof(GaussianSplatFormats.color)), new GUIContent("Color"));
                EditorGUI.indentLevel--;
            }

            serializedObject.ApplyModifiedProperties();
            ApplyRevertGUI();
        }

        private SerializedProperty SamplerProperty(SurfaceSamplerKind kind) => _settings.FindPropertyRelative(kind switch
        {
            SurfaceSamplerKind.PoissonDisk => nameof(MeshSplatSettings.poissonDisk),
            SurfaceSamplerKind.Random => nameof(MeshSplatSettings.random),
            SurfaceSamplerKind.Vertex => nameof(MeshSplatSettings.vertex),
            SurfaceSamplerKind.Feature => nameof(MeshSplatSettings.feature),
            SurfaceSamplerKind.Scanline => nameof(MeshSplatSettings.scanline),
            SurfaceSamplerKind.Grid => nameof(MeshSplatSettings.grid),
            _ => throw new System.ArgumentOutOfRangeException(nameof(kind)),
        });

        private SerializedProperty ColorProperty(SplatColorKind kind) => _settings.FindPropertyRelative(kind switch
        {
            SplatColorKind.Uniform => nameof(MeshSplatSettings.uniformColor),
            SplatColorKind.Material => nameof(MeshSplatSettings.materialColor),
            SplatColorKind.VertexColor => nameof(MeshSplatSettings.vertexColor),
            _ => throw new System.ArgumentOutOfRangeException(nameof(kind)),
        });

        // Settings of the sources only the overrides use, below the list
        private void DrawOverrideSources()
        {
            var defaultKind = (SplatColorKind)_color.enumValueIndex;
            var shown = new HashSet<SplatColorKind> { defaultKind };
            for (int i = 0; i < _colorOverrides.arraySize; i++)
            {
                SerializedProperty entry = _colorOverrides.GetArrayElementAtIndex(i);
                var kind = (SplatColorKind)entry.FindPropertyRelative(nameof(MaterialColorOverride.color)).enumValueIndex;
                if (!shown.Add(kind))
                    continue;
                EditorGUILayout.LabelField(ObjectNames.NicifyVariableName(kind.ToString()));
                DrawFields(ColorProperty(kind));
            }
        }

        // The selected sampler's or color source's own settings, indented
        // below its popup
        private static void DrawFields(SerializedProperty owner)
        {
            EditorGUI.indentLevel++;
            SerializedProperty end = owner.GetEndProperty();
            SerializedProperty field = owner.Copy();
            for (bool enter = true; field.NextVisible(enter) && !SerializedProperty.EqualContents(field, end); enter = false)
                EditorGUILayout.PropertyField(field, true);
            EditorGUI.indentLevel--;
        }

        // With a model selected, the new file goes next to it and samples it
        [MenuItem(CreateMenu)]
        private static void CreateMeshSplats()
        {
            var model = Selection.activeObject as GameObject;
            string modelPath = model != null ? AssetDatabase.GetAssetPath(model) : null;
            if (string.IsNullOrEmpty(modelPath))
            {
                ProjectWindowUtil.CreateAssetWithContent($"New Mesh Splats.{MeshSplatImporter.Extension}", FileContent);
                return;
            }

            string path = AssetDatabase.GenerateUniqueAssetPath(
                $"{Path.GetDirectoryName(modelPath)?.Replace('\\', '/')}/{model.name}.{MeshSplatImporter.Extension}");
            File.WriteAllText(path, FileContent);
            AssetDatabase.ImportAsset(path);
            var importer = (MeshSplatImporter)AssetImporter.GetAtPath(path);
            importer.Model = model;
            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();
            Selection.activeObject = AssetDatabase.LoadMainAssetAtPath(path);
        }
    }
}
