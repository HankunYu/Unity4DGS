using System.Collections.Generic;
using System.Linq;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    // Dragging splat assets (imported .ply/.spz files) or sequences into the
    // Scene view or the Hierarchy creates a GameObject with a
    // GaussianSplatRenderer for each (and a GaussianPlayer for a sequence),
    // plus the scene's GaussianSplatConfig when there is none yet. A splat
    // asset sampled from a model (.meshsplat) also brings in that model, with
    // the splats below it and a GaussianMeshDriver moving them with it.
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
            List<Object> assets = DraggedAssets();
            if (assets.Count == 0)
                return DragAndDropVisualMode.None;
            if (perform)
                Create(assets, parentForDraggedObjects, -1, worldPosition);
            return DragAndDropVisualMode.Copy;
        }

        static DragAndDropVisualMode OnHierarchyDrop(EntityId dropTargetEntityId, HierarchyDropFlags dropMode,
            Transform parentForDraggedObjects, bool perform)
        {
            List<Object> assets = DraggedAssets();
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

        // The dragged splat assets and sequences
        static List<Object> DraggedAssets()
        {
            var assets = new List<Object>();
            foreach (Object obj in DragAndDrop.objectReferences)
            {
                if (obj is GaussianSplatAsset or GaussianSplatSequence)
                    assets.Add(obj);
            }
            return assets;
        }

        // The mesh binding imported along with a splat asset, if it has one
        static GaussianMeshBinding FindBinding(GaussianSplatAsset asset)
        {
            string path = asset != null ? AssetDatabase.GetAssetPath(asset) : null;
            return string.IsNullOrEmpty(path)
                ? null
                : AssetDatabase.LoadAllAssetsAtPath(path).OfType<GaussianMeshBinding>().FirstOrDefault();
        }

        // worldPosition null keeps the local origin of the parent
        static void Create(List<Object> assets, Transform parent, int siblingIndex, Vector3? worldPosition)
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
            foreach (Object asset in assets)
            {
                var sequence = asset as GaussianSplatSequence;
                // a sequence shows its first frame until it plays
                GaussianSplatAsset shown = sequence != null ? sequence.frames?.FirstOrDefault() : (GaussianSplatAsset)asset;
                GaussianMeshBinding binding = sequence == null ? FindBinding(shown) : null;

                // Inactive until the asset is assigned, so OnEnable loads it once
                var go = new GameObject(asset.name);
                go.SetActive(false);
                Transform placed = go.transform;
                GameObject model = null;
                if (binding != null && binding.model != null)
                {
                    model = (GameObject)PrefabUtility.InstantiatePrefab(binding.model);
                    placed = model.transform;
                    go.transform.SetParent(model.transform, false);
                }
                if (parent != null)
                    placed.SetParent(parent, false);
                if (siblingIndex >= 0)
                    placed.SetSiblingIndex(siblingIndex++);
                if (worldPosition.HasValue)
                    placed.position = worldPosition.Value;
                go.transform.localScale = SourceToUnityScale;

                var renderer = go.AddComponent<GaussianSplatRenderer>();
                renderer.splatAsset = shown;
                renderer.nextAsset = shown;
                if (sequence != null)
                    go.AddComponent<GaussianPlayer>().sequence = sequence;
                if (model != null)
                {
                    var driver = go.AddComponent<GaussianMeshDriver>();
                    driver.binding = binding;
                    driver.source = model.transform;
                    Undo.RegisterCreatedObjectUndo(model, UndoName);
                }
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
