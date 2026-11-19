using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GaussianSplatting.Runtime;
using Tide;
using UnityEditor;
using UnityEditor.Presets;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GaussianSplatting.Editor
{
    /// <summary>
    /// Saves the look of a GaussianSplatRenderer as a GaussianLookPreset and
    /// applies one to a renderer.
    ///
    /// A look is the renderer's layers (its direct children holding a style
    /// volume, a particle emitter or particle links) with the styles they
    /// use, and the look settings on the renderer's own GameObject. The splat
    /// asset, the timeline and everything scene wide (config, lights, post
    /// processing) are not part of it.
    ///
    /// Saving copies the styles into the preset, so later changes to them
    /// leave it alone. Applying replaces the renderer's layers with the
    /// preset's and writes the saved values back into the style assets the
    /// preset was saved from, so a look comes back as it was; timeline tracks
    /// bound to the replaced layers are bound to the new ones by name.
    /// </summary>
    public static class GaussianLookPresets
    {
        private const string UndoApply = "Apply Look Preset";

        // Look settings of the renderer itself; its asset, visibility, order
        // and occluder stay as they are
        private static readonly string[] RendererProperties =
        {
            "splatScale", "opacityScale", "shOrder", "shOnly",
            "backfaceCulling", "backfaceFadeAngle",
            "occlusionCulling", "occlusionTolerance", "occlusionFootprintScale",
        };

        // Properties left alone on the other look components: references into
        // the scene, which a preset cannot hold
        private static readonly string[] ScenePropertyNames = { "lights" };

        // Components next to the renderer that are saved whole. A preset
        // saved without one turns it off on the renderer it is applied to.
        private static readonly Type[] OptionalComponents = { typeof(GaussianStyler), typeof(GaussianSplatLighting) };

        /// <summary>The direct children of the renderer that make up its look.</summary>
        public static List<Transform> FindLayers(GaussianSplatRenderer renderer)
        {
            var layers = new List<Transform>();
            foreach (Transform child in renderer.transform)
            {
                if (IsLayer(child))
                    layers.Add(child);
            }
            return layers;
        }

        /// <summary>
        /// Saves the renderer's look at <paramref name="assetPath"/> (an .asset
        /// path in the project; the layers go into a prefab of the same name
        /// next to it), replacing a preset already there.
        /// </summary>
        public static GaussianLookPreset Save(GaussianSplatRenderer renderer, string assetPath)
        {
            if (renderer == null)
                throw new ArgumentNullException(nameof(renderer));
            RequireEditMode();

            string prefabPath = Path.ChangeExtension(assetPath, ".prefab");
            Transform root = renderer.transform;
            List<Transform> layers = FindLayers(renderer);

            // Bound timelines hold their targets at animated values; save the values at rest
            List<TidePlayer> preview = SuspendTimelines();
            GameObject container = null;
            try
            {
                AssetDatabase.DeleteAsset(assetPath);
                AssetDatabase.DeleteAsset(prefabPath);
                var preset = ScriptableObject.CreateInstance<GaussianLookPreset>();
                AssetDatabase.CreateAsset(preset, assetPath);

                // Cloned under an inactive root, so the clones never start running
                container = new GameObject(Path.GetFileNameWithoutExtension(assetPath));
                container.SetActive(false);
                foreach (Transform layer in layers)
                {
                    GameObject clone = Object.Instantiate(layer.gameObject, container.transform, false);
                    clone.name = layer.name;
                }

                DropTransientAssets(container);
                SnapshotStyles(container, preset);
                CollectReferences(root, layers, preset);
                SnapshotComponents(renderer, preset);

                preset.Layers = PrefabUtility.SaveAsPrefabAsset(container, prefabPath);
                EditorUtility.SetDirty(preset);
                AssetDatabase.SaveAssets();
                return preset;
            }
            finally
            {
                if (container != null)
                    Object.DestroyImmediate(container);
                ResumeTimelines(preview);
            }
        }

        /// <summary>
        /// Replaces the renderer's look with the preset's. One undo step
        /// brings the old one back.
        /// </summary>
        public static void Apply(GaussianLookPreset preset, GaussianSplatRenderer renderer)
        {
            if (preset == null)
                throw new ArgumentNullException(nameof(preset));
            if (renderer == null)
                throw new ArgumentNullException(nameof(renderer));
            RequireEditMode();
            if (preset.Layers == null)
                throw new InvalidOperationException($"Look preset '{preset.name}' has lost its layers prefab.");

            Transform root = renderer.transform;
            List<Transform> oldLayers = FindLayers(renderer);
            foreach (Transform layer in oldLayers)
            {
                if (PrefabUtility.IsPartOfPrefabInstance(layer))
                    throw new InvalidOperationException($"'{layer.name}' is part of a prefab instance; unpack it before applying a look preset.");
            }

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(UndoApply);
            int undoGroup = Undo.GetCurrentGroup();

            List<TidePlayer> preview = SuspendTimelines();
            try
            {
                List<TimelineBinding> bindings = CollectTimelineBindings(root, oldLayers);
                foreach (Transform layer in oldLayers)
                    Undo.DestroyObjectImmediate(layer.gameObject);

                Dictionary<GaussianSplatStyle, GaussianSplatStyle> styles = RestoreStyles(preset);

                GameObject container = Object.Instantiate(preset.Layers);
                foreach (GaussianStyleVolume volume in container.GetComponentsInChildren<GaussianStyleVolume>(true))
                {
                    if (volume.style != null && styles.TryGetValue(volume.style, out GaussianSplatStyle style))
                        volume.style = style;
                }
                foreach (Transform layer in container.transform.Cast<Transform>().ToList())
                {
                    layer.SetParent(root, false);
                    Undo.RegisterCreatedObjectUndo(layer.gameObject, UndoApply);
                }
                Object.DestroyImmediate(container);

                RestoreReferences(root, preset);
                ApplyComponents(preset, renderer);
                RestoreTimelineBindings(root, bindings);
            }
            finally
            {
                ResumeTimelines(preview);
                Undo.CollapseUndoOperations(undoGroup);
            }
        }

        /// <summary>
        /// The style assets applying the preset writes its saved values into.
        /// A style whose source asset is gone gets a new asset next to the
        /// preset instead and is not listed.
        /// </summary>
        public static List<GaussianSplatStyle> FindStylesToRestore(GaussianLookPreset preset)
        {
            var styles = new List<GaussianSplatStyle>();
            foreach (GaussianLookStyle entry in preset.Styles)
            {
                GaussianSplatStyle source = entry.FindSource();
                if (source != null)
                    styles.Add(source);
            }
            return styles;
        }

        private static bool IsLayer(Transform child) =>
            child.GetComponentInChildren<GaussianStyleVolume>(true) != null ||
            child.GetComponentInChildren<GaussianParticleEmitter>(true) != null ||
            child.GetComponentInChildren<GaussianParticleLinks>(true) != null;

        private static void RequireEditMode()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Look presets are saved and applied in edit mode.");
        }

        // Emitters and links draw from in-memory assets they make themselves
        private static void DropTransientAssets(GameObject container)
        {
            foreach (GaussianSplatRenderer renderer in container.GetComponentsInChildren<GaussianSplatRenderer>(true))
            {
                if (renderer.splatAsset != null && !EditorUtility.IsPersistent(renderer.splatAsset))
                    renderer.splatAsset = null;
                if (renderer.nextAsset != null && !EditorUtility.IsPersistent(renderer.nextAsset))
                    renderer.nextAsset = null;
            }
        }

        private static void SnapshotStyles(GameObject container, GaussianLookPreset preset)
        {
            var snapshots = new Dictionary<GaussianSplatStyle, GaussianSplatStyle>();
            foreach (GaussianStyleVolume volume in container.GetComponentsInChildren<GaussianStyleVolume>(true))
            {
                GaussianSplatStyle style = volume.style;
                if (style == null)
                    continue;
                if (!snapshots.TryGetValue(style, out GaussianSplatStyle snapshot))
                {
                    snapshot = Object.Instantiate(style);
                    snapshot.name = style.name;
                    AssetDatabase.AddObjectToAsset(snapshot, preset);
                    snapshots.Add(style, snapshot);
                    preset.Styles.Add(new GaussianLookStyle(snapshot, style));
                }
                volume.style = snapshot;
            }
        }

        // The references from the layers to the renderer's GameObject and to
        // each other, read from the scene objects the layers were cloned from
        private static void CollectReferences(Transform root, List<Transform> layers, GaussianLookPreset preset)
        {
            foreach (Transform layer in layers)
            {
                foreach (Component component in layer.GetComponentsInChildren<Component>(true))
                {
                    if (component == null || component is Transform)
                        continue;
                    if (!GaussianLookAddress.TryCreate(root, component, out GaussianLookAddress owner))
                        continue;
                    SerializedProperty property = new SerializedObject(component).GetIterator();
                    while (property.Next(true))
                    {
                        if (property.propertyType != SerializedPropertyType.ObjectReference || property.name == "m_GameObject")
                            continue;
                        Object value = property.objectReferenceValue;
                        if (value == null || !InLook(root, layers, value))
                            continue;
                        if (GaussianLookAddress.TryCreate(root, value, out GaussianLookAddress target))
                            preset.References.Add(new GaussianLookReference(owner, property.propertyPath, target));
                    }
                }
            }
        }

        // On the renderer's own GameObject or in one of its layers
        private static bool InLook(Transform root, List<Transform> layers, Object obj)
        {
            Transform t = obj is Component component ? component.transform : (obj as GameObject)?.transform;
            if (t == null)
                return false;
            if (t == root)
                return true;
            foreach (Transform layer in layers)
            {
                if (t.IsChildOf(layer))
                    return true;
            }
            return false;
        }

        private static void RestoreReferences(Transform root, GaussianLookPreset preset)
        {
            foreach (GaussianLookReference reference in preset.References)
            {
                Object owner = reference.Owner.Resolve(root);
                Object target = reference.Target.Resolve(root);
                if (owner == null || target == null)
                {
                    Debug.LogWarning($"Look preset '{preset.name}': could not restore {reference.Owner}.{reference.Property}", preset);
                    continue;
                }
                var serialized = new SerializedObject(owner);
                SerializedProperty property = serialized.FindProperty(reference.Property);
                if (property == null)
                    continue;
                property.objectReferenceValue = target;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static void SnapshotComponents(GaussianSplatRenderer renderer, GaussianLookPreset preset)
        {
            AddComponentSnapshot(renderer, preset);
            foreach (Type type in OptionalComponents)
            {
                if (renderer.TryGetComponent(type, out Component component))
                    AddComponentSnapshot(component, preset);
            }
        }

        private static void AddComponentSnapshot(Component component, GaussianLookPreset preset)
        {
            var snapshot = new Preset(component) { name = component.GetType().Name };
            AssetDatabase.AddObjectToAsset(snapshot, preset);
            preset.Components.Add(snapshot);
        }

        private static void ApplyComponents(GaussianLookPreset preset, GaussianSplatRenderer renderer)
        {
            Preset saved = FindComponentSnapshot(preset, renderer);
            if (saved != null)
            {
                Undo.RecordObject(renderer, UndoApply);
                saved.ApplyTo(renderer, RendererProperties);
            }

            foreach (Type type in OptionalComponents)
            {
                renderer.TryGetComponent(type, out Component component);
                saved = preset.Components.FirstOrDefault(p => p != null && p.GetTargetFullTypeName() == type.FullName);
                if (saved == null)
                {
                    if (component is Behaviour behaviour && behaviour.enabled)
                    {
                        Undo.RecordObject(behaviour, UndoApply);
                        behaviour.enabled = false;
                    }
                    continue;
                }
                if (component == null)
                    component = Undo.AddComponent(renderer.gameObject, type);
                Undo.RecordObject(component, UndoApply);
                saved.ApplyTo(component, SavedPropertyNames(saved));
            }
        }

        private static Preset FindComponentSnapshot(GaussianLookPreset preset, Component component) =>
            preset.Components.FirstOrDefault(p => p != null && p.CanBeAppliedTo(component));

        // The preset's top-level properties, without those that point into a scene
        private static string[] SavedPropertyNames(Preset saved) =>
            saved.PropertyModifications
                .Select(m => m.propertyPath.Split('.')[0])
                .Distinct()
                .Where(name => !ScenePropertyNames.Contains(name))
                .ToArray();

        // Writes each saved style back into the asset it was copied from and
        // returns, per saved style, the asset the layers should use
        private static Dictionary<GaussianSplatStyle, GaussianSplatStyle> RestoreStyles(GaussianLookPreset preset)
        {
            var styles = new Dictionary<GaussianSplatStyle, GaussianSplatStyle>();
            foreach (GaussianLookStyle entry in preset.Styles)
            {
                GaussianSplatStyle snapshot = entry.Snapshot;
                if (snapshot == null)
                    continue;
                GaussianSplatStyle source = entry.FindSource();
                if (source != null)
                {
                    string name = source.name;
                    Undo.RecordObject(source, UndoApply);
                    EditorUtility.CopySerialized(snapshot, source);
                    source.name = name;
                }
                else
                {
                    // The style the look was saved from is gone: give it a new one next to the preset
                    source = Object.Instantiate(snapshot);
                    source.name = snapshot.name;
                    string folder = Path.GetDirectoryName(AssetDatabase.GetAssetPath(preset));
                    string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{snapshot.name}.asset");
                    AssetDatabase.CreateAsset(source, path);
                    entry.SetSource(source);
                    EditorUtility.SetDirty(preset);
                }
                source.NotifyChanged();
                EditorUtility.SetDirty(source);
                styles[snapshot] = source;
            }
            return styles;
        }

        private readonly struct TimelineBinding
        {
            public readonly TidePlayer Player;
            public readonly string TrackId;
            public readonly GaussianLookAddress Target;

            public TimelineBinding(TidePlayer player, string trackId, GaussianLookAddress target)
            {
                Player = player;
                TrackId = trackId;
                Target = target;
            }
        }

        private static TidePlayer[] FindTimelines() =>
            Object.FindObjectsByType<TidePlayer>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        // Takes edit-mode previews off their targets, which puts the targets
        // back at their own values
        private static List<TidePlayer> SuspendTimelines()
        {
            var bound = new List<TidePlayer>();
            foreach (TidePlayer player in FindTimelines())
            {
                if (!player.IsBound)
                    continue;
                player.Unbind();
                bound.Add(player);
            }
            return bound;
        }

        private static void ResumeTimelines(List<TidePlayer> players)
        {
            foreach (TidePlayer player in players)
            {
                if (player != null)
                    player.Seek(player.CurrentTime);
            }
        }

        // The timeline tracks bound to something in the layers
        private static List<TimelineBinding> CollectTimelineBindings(Transform root, List<Transform> layers)
        {
            var bindings = new List<TimelineBinding>();
            foreach (TidePlayer player in FindTimelines())
            {
                foreach (TideBinding binding in player.Bindings)
                {
                    if (binding.Target == null || !InLook(root, layers, binding.Target))
                        continue;
                    if (binding.Target is Component component && component.transform == root)
                        continue;
                    if (GaussianLookAddress.TryCreate(root, binding.Target, out GaussianLookAddress target))
                        bindings.Add(new TimelineBinding(player, binding.TrackId, target));
                }
            }
            return bindings;
        }

        private static void RestoreTimelineBindings(Transform root, List<TimelineBinding> bindings)
        {
            foreach (TimelineBinding binding in bindings)
            {
                TideTrack track = binding.Player.Timeline != null ? binding.Player.Timeline.FindTrack(binding.TrackId) : null;
                if (track == null)
                    continue;
                Object target = binding.Target.Resolve(root);
                if (target == null)
                {
                    Debug.LogWarning($"Track '{track.Name}' was bound to {binding.Target}, which the applied look does not have; it is left unbound.", binding.Player);
                    continue;
                }
                Undo.RecordObject(binding.Player, UndoApply);
                binding.Player.SetBinding(track, target);
                EditorUtility.SetDirty(binding.Player);
            }
        }
    }
}
