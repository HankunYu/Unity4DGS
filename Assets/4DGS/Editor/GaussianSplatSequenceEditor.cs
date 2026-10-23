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

        static readonly GUIContent s_FolderLabel = new("Source Folder",
            "Folder the frames come from: every splat asset directly in it, in natural file name order. " +
            "Setting it fills the frames; Refresh picks up files added later.");

        SerializedProperty _frames;
        SerializedProperty _frameRate;
        SerializedProperty _sourceFolderGuid;
        // The folder this asset is in, and how many frames it holds
        string _ownFolder;
        int _ownFolderFrames;

        private void OnEnable()
        {
            _ownFolder = Path.GetDirectoryName(AssetDatabase.GetAssetPath(target))?.Replace('\\', '/');
            _ownFolderFrames = string.IsNullOrEmpty(_ownFolder) ? 0 : CollectFrames(_ownFolder).Length;
            _frames = serializedObject.FindProperty(nameof(GaussianSplatSequence.frames));
            _frameRate = serializedObject.FindProperty(nameof(GaussianSplatSequence.frameRate));
            _sourceFolderGuid = serializedObject.FindProperty(nameof(GaussianSplatSequence.sourceFolderGuid));
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
            DrawSourceFolder(sequence);

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

        void DrawSourceFolder(GaussianSplatSequence sequence)
        {
            string folder = AssetDatabase.GUIDToAssetPath(_sourceFolderGuid.stringValue);
            var current = AssetDatabase.IsValidFolder(folder) ? AssetDatabase.LoadAssetAtPath<DefaultAsset>(folder) : null;
            EditorGUI.BeginChangeCheck();
            var picked = EditorGUILayout.ObjectField(s_FolderLabel, current, typeof(DefaultAsset), false) as DefaultAsset;
            if (EditorGUI.EndChangeCheck())
            {
                string path = AssetDatabase.GetAssetPath(picked);
                if (picked == null)
                    _sourceFolderGuid.stringValue = string.Empty;
                else if (AssetDatabase.IsValidFolder(path))
                    UseFolder(path);
                else
                    Debug.LogWarning($"{path} is not a folder");
            }

            if (current != null)
            {
                if (GUILayout.Button("Refresh Frames from Folder"))
                    UseFolder(folder);
                return;
            }

            // A sequence made with the plain Create menu often sits among its frames
            if (sequence.FrameCount == 0)
            {
                if (_ownFolderFrames > 0)
                {
                    if (GUILayout.Button($"Use the {_ownFolderFrames} Frames in {_ownFolder}"))
                        UseFolder(_ownFolder);
                }
                else
                {
                    EditorGUILayout.HelpBox("No frames. Drag the folder holding them into Source Folder.", MessageType.Info);
                }
            }
        }

        // Points the sequence at folder and takes the frames from it
        void UseFolder(string folder)
        {
            _sourceFolderGuid.stringValue = AssetDatabase.AssetPathToGUID(folder);
            GaussianSplatAsset[] frames = CollectFrames(folder);
            _frames.arraySize = frames.Length;
            for (int i = 0; i < frames.Length; i++)
                _frames.GetArrayElementAtIndex(i).objectReferenceValue = frames[i];
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
