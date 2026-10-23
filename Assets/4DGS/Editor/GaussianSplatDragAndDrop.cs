using System.Collections.Generic;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    // Dragging splat assets (imported .ply/.spz files) into the Scene view or
    // the Hierarchy creates a GameObject with a GaussianSplatRenderer for
    // each, plus the scene's GaussianSplatConfig when there is none yet.
    [InitializeOnLoad]
    internal static class GaussianSplatDragAndDrop
    {
        const string UndoName = "Create Gaussian Splat";

        // Training tools write splats y-down (right, down, forward). Flipping
        // Y makes them upright in Unity and also fixes the handedness, which
        // a rotation could not.
        static readonly Vector3 SourceToUnityScale = new(1, -1, 1);

        static GaussianSplatDragAndDrop()
        {
            DragAndDrop.AddDropHandlerV2(OnSceneDrop);
            DragAndDrop.AddDropHandlerV2(OnHierarchyDrop);
        }

        static DragAndDropVisualMode OnSceneDrop(Object dropUpon, Vector3 worldPosition, Vector2 viewportPosition,
            Transform parentForDraggedObjects, bool perform)
        {
            List<GaussianSplatAsset> assets = DraggedAssets();
            if (assets.Count == 0)
                return DragAndDropVisualMode.None;
            if (perform)
                Create(assets, parentForDraggedObjects, -1, worldPosition);
            return DragAndDropVisualMode.Copy;
        }

        static DragAndDropVisualMode OnHierarchyDrop(EntityId dropTargetEntityId, HierarchyDropFlags dropMode,
            Transform parentForDraggedObjects, bool perform)
        {
            List<GaussianSplatAsset> assets = DraggedAssets();
            if (assets.Count == 0)
                return DragAndDropVisualMode.None;
            if (perform)
            {
                Transform parent = parentForDraggedObjects;
                int siblingIndex = -1;
                if (parent == null && EditorUtility.EntityIdToObject(dropTargetEntityId) is GameObject target)
                {
                    if ((dropMode & HierarchyDropFlags.DropUpon) != 0)
                    {
                        parent = target.transform;
                    }
                    else
                    {
                        parent = target.transform.parent;
                        siblingIndex = target.transform.GetSiblingIndex() + ((dropMode & HierarchyDropFlags.DropAbove) != 0 ? 0 : 1);
                    }
                }
                Create(assets, parent, siblingIndex, null);
            }
            return DragAndDropVisualMode.Copy;
        }

        static List<GaussianSplatAsset> DraggedAssets()
        {
            var assets = new List<GaussianSplatAsset>();
            foreach (Object obj in DragAndDrop.objectReferences)
            {
                if (obj is GaussianSplatAsset asset)
                    assets.Add(asset);
            }
            return assets;
        }

        // worldPosition null keeps the local origin of the parent
        static void Create(List<GaussianSplatAsset> assets, Transform parent, int siblingIndex, Vector3? worldPosition)
        {
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();

            // A prefab being edited must not get a scene-wide config
            if (PrefabStageUtility.GetCurrentPrefabStage() == null && Object.FindFirstObjectByType<GaussianSplatConfig>() == null)
            {
                var configGo = new GameObject("Gaussian Splat Config");
                configGo.AddComponent<GaussianSplatConfig>();
                Undo.RegisterCreatedObjectUndo(configGo, UndoName);
            }

            var created = new List<Object>();
            foreach (GaussianSplatAsset asset in assets)
            {
                // Inactive until the asset is assigned, so OnEnable loads it once
                var go = new GameObject(asset.name);
                go.SetActive(false);
                if (parent != null)
                    go.transform.SetParent(parent, false);
                if (siblingIndex >= 0)
                    go.transform.SetSiblingIndex(siblingIndex++);
                if (worldPosition.HasValue)
                    go.transform.position = worldPosition.Value;
                go.transform.localScale = SourceToUnityScale;

                var renderer = go.AddComponent<GaussianSplatRenderer>();
                renderer.splatAsset = asset;
                renderer.nextAsset = asset;
                go.SetActive(true);

                GameObjectUtility.EnsureUniqueNameForSibling(go);
                Undo.RegisterCreatedObjectUndo(go, UndoName);
                created.Add(go);
            }

            Selection.objects = created.ToArray();
            Undo.SetCurrentGroupName(UndoName);
            Undo.CollapseUndoOperations(undoGroup);
        }
    }
}
