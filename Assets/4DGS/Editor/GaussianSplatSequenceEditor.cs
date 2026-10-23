using System.IO;
using System.Linq;
using GaussianSplatting.Editor.Utils;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianSplatSequence))]
    public class GaussianSplatSequenceEditor : UnityEditor.Editor
    {
        const string CreateMenu = "Assets/Create/Gaussian Splats/Sequence from Folder";

        SerializedProperty _frames;
        SerializedProperty _frameRate;

        private void OnEnable()
        {
            _frames = serializedObject.FindProperty(nameof(GaussianSplatSequence.frames));
            _frameRate = serializedObject.FindProperty(nameof(GaussianSplatSequence.frameRate));
        }

        public override void OnInspectorGUI()
        {
            var sequence = (GaussianSplatSequence)target;
            serializedObject.Update();

            EditorGUILayout.PropertyField(_frameRate);
            float duration = sequence.FrameCount / Mathf.Max(sequence.frameRate, 0.01f);
            EditorGUILayout.LabelField("Frames", $"{sequence.FrameCount}, {duration:0.##} s");
            if (sequence.frames != null && sequence.frames.Length > 0)
            {
                int maxSplats = sequence.frames.Where(f => f != null).Select(f => f.splatCount).DefaultIfEmpty(0).Max();
                EditorGUILayout.LabelField("Largest Frame", $"{maxSplats:N0} splats");
                if (sequence.frames.Any(f => f == null))
                    EditorGUILayout.HelpBox("Some frames are missing.", MessageType.Error);
            }

            EditorGUILayout.Space();
            string folder = AssetDatabase.GUIDToAssetPath(sequence.sourceFolderGuid);
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.ObjectField("Source Folder", AssetDatabase.LoadAssetAtPath<DefaultAsset>(folder), typeof(DefaultAsset), false);
            using (new EditorGUI.DisabledScope(!AssetDatabase.IsValidFolder(folder)))
            {
                if (GUILayout.Button("Refresh Frames from Folder"))
                    Refresh(sequence, folder);
            }

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(_frames, true);
            serializedObject.ApplyModifiedProperties();
        }

        // The splat assets directly in folder, in natural file name order
        public static GaussianSplatAsset[] CollectFrames(string folder)
        {
            return AssetDatabase.FindAssets($"t:{nameof(GaussianSplatAsset)}", new[] { folder })
                .Select(guid => AssetDatabase.GUIDToAssetPath(guid))
                .Where(path => Path.GetDirectoryName(path)?.Replace('\\', '/') == folder)
                .Distinct()
                .OrderBy(path => Path.GetFileName(path), NaturalStringComparer.Instance)
                .Select(path => AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(path))
                .Where(asset => asset != null)
                .ToArray();
        }

        static void Refresh(GaussianSplatSequence sequence, string folder)
        {
            Undo.RecordObject(sequence, "Refresh Sequence Frames");
            sequence.frames = CollectFrames(folder);
            EditorUtility.SetDirty(sequence);
            AssetDatabase.SaveAssetIfDirty(sequence);
        }

        [MenuItem(CreateMenu, false, 1)]
        private static void CreateFromFolder()
        {
            string folder = AssetDatabase.GetAssetPath(Selection.activeObject);
            GaussianSplatAsset[] frames = CollectFrames(folder);
            if (frames.Length == 0)
            {
                EditorUtility.DisplayDialog("Gaussian Splat Sequence", $"{folder} has no splat assets directly in it.", "OK");
                return;
            }

            var sequence = CreateInstance<GaussianSplatSequence>();
            sequence.frames = frames;
            sequence.sourceFolderGuid = AssetDatabase.AssetPathToGUID(folder);
            // next to the folder rather than among the frames
            string path = AssetDatabase.GenerateUniqueAssetPath($"{Path.GetDirectoryName(folder)}/{Path.GetFileName(folder)}.asset");
            AssetDatabase.CreateAsset(sequence, path);
            Selection.activeObject = sequence;
            EditorGUIUtility.PingObject(sequence);
        }

        [MenuItem(CreateMenu, true)]
        private static bool CreateFromFolderValidate()
        {
            return Selection.activeObject != null && AssetDatabase.IsValidFolder(AssetDatabase.GetAssetPath(Selection.activeObject));
        }
    }
}
