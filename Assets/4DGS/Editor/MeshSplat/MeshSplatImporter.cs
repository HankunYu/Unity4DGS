using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GaussianSplatting.Editor.Import;
using GaussianSplatting.Runtime;
using Unity.Collections;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace GaussianSplatting.Editor.MeshSplat
{
    // A .meshsplat file turns a model asset into a GaussianSplatAsset: the
    // settings live in the .meta, the splats are import artifacts, and the
    // import reruns when the model is reimported. The file itself holds no
    // data.
    [ScriptedImporter(Version, Extension)]
    public class MeshSplatImporter : ScriptedImporter
    {
        public const string Extension = "meshsplat";
        // Bump whenever the output for the same model and settings changes
        public const int Version = 6;

        [SerializeField] private GameObject model;
        [SerializeField] private MeshSplatSettings settings = new();

        public GameObject Model
        {
            get => model;
            set => model = value;
        }

        public MeshSplatSettings Settings => settings;

        public override void OnImportAsset(AssetImportContext ctx)
        {
            string name = Path.GetFileNameWithoutExtension(ctx.assetPath);
            if (model == null)
            {
                ctx.LogImportWarning("No model set; pick one in the inspector.");
                return;
            }
            ctx.DependsOnArtifact(AssetDatabase.GetAssetPath(model));

            try
            {
                var meshes = MeshSplatBaker.CollectMeshes(model);
                if (meshes.Count == 0)
                    throw new InvalidOperationException($"{model.name} has no meshes");
                AddColorDependencies(ctx, meshes);
                if (settings.output == MeshSplatOutput.Sequence)
                    ImportSequence(ctx, name, meshes);
                else
                    ImportAsset(ctx, name, meshes);
            }
            catch (Exception e)
            {
                ctx.LogImportError($"Mesh splat import failed: {e.Message}");
            }
        }

        // The rest pose asset as the main object, with its binding
        private void ImportAsset(AssetImportContext ctx, string name, List<SurfaceMesh> meshes)
        {
            var samples = settings.Sampler.Sample(meshes);
            GaussianSplatAsset asset = BuildAsset(ctx, name, meshes, samples, "", out NativeArray<int> sourceIndices);
            using (sourceIndices)
                AddBinding(ctx, name, meshes, samples, sourceIndices, asset);
            ctx.SetMainObject(asset);
        }

        // One asset per frame of the clip, in a sequence as the main object
        private void ImportSequence(AssetImportContext ctx, string name, List<SurfaceMesh> meshes)
        {
            MeshSplatSequenceSettings sequenceSettings = settings.sequence;
            AnimationClip clip = sequenceSettings.clip != null ? sequenceSettings.clip : ModelClip();
            if (clip == null)
                throw new InvalidOperationException($"{model.name} has no animation; pick a clip");
            AddDependency(ctx, clip);
            float frameRate = sequenceSettings.frameRate;
            int first = sequenceSettings.firstFrame;
            int last = sequenceSettings.lastFrame >= 0 ? sequenceSettings.lastFrame : LastClipFrame(clip, frameRate);
            if (last < first)
                throw new InvalidOperationException($"no frames between {first} and {last}");

            var instance = Instantiate(model);
            instance.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                // The clip alone poses it
                foreach (Animator animator in instance.GetComponentsInChildren<Animator>())
                    animator.enabled = false;
                var poser = new MeshPoser(instance, meshes);
                List<SurfaceSample> restSamples = sequenceSettings.resampleEveryFrame ? null : settings.Sampler.Sample(meshes);
                var frames = new GaussianSplatAsset[last - first + 1];
                for (int f = first; f <= last; f++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("Baking mesh splats", $"{name}: frame {f} of {first}-{last}",
                            (f - first) / (float)frames.Length))
                        throw new OperationCanceledException("baking was cancelled");
                    clip.SampleAnimation(instance, f / frameRate);
                    List<SurfaceMesh> posed = poser.Pose();
                    List<SurfaceSample> samples = restSamples ?? settings.Sampler.Sample(posed);
                    frames[f - first] = BuildAsset(ctx, $"{name}_{f:D4}", posed, samples, $"f{f}_", out NativeArray<int> sourceIndices);
                    sourceIndices.Dispose();
                }

                var sequence = ScriptableObject.CreateInstance<GaussianSplatSequence>();
                sequence.name = name;
                sequence.frames = frames;
                sequence.frameRate = frameRate;
                ctx.AddObjectToAsset("sequence", sequence);
                ctx.SetMainObject(sequence);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                DestroyImmediate(instance);
            }
        }

        // An empty idPrefix gives the main asset's identifiers (see
        // GaussianSplatImportOutput.Add). The caller owns sourceIndices.
        private GaussianSplatAsset BuildAsset(AssetImportContext ctx, string name, List<SurfaceMesh> meshes,
            List<SurfaceSample> samples, string idPrefix, out NativeArray<int> sourceIndices)
        {
            NativeArray<InputSplatData> splats = default;
            GaussianSplatBuildResult built = null;
            try
            {
                splats = MeshSplatBaker.BuildSplats(meshes, samples, settings);
                built = GaussianSplatAssetBuilder.Build(ref splats, 0, settings.BuildSettings(), null);
                GaussianSplatAsset asset = GaussianSplatImportOutput.Add(ctx, name, built, idPrefix);
                sourceIndices = built.sourceIndices;
                built.sourceIndices = default;
                return asset;
            }
            finally
            {
                if (splats.IsCreated)
                    splats.Dispose();
                built?.Dispose();
            }
        }

        private AnimationClip ModelClip()
        {
            return AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(model)).OfType<AnimationClip>()
                .FirstOrDefault(c => !c.name.StartsWith("__preview__"));
        }

        public static int LastClipFrame(AnimationClip clip, float frameRate) =>
            Math.Max(0, (int)Math.Floor(clip.length * frameRate + 1e-3));

        // Materials and textures outside the model file (the model's own
        // are covered by depending on it)
        private void AddColorDependencies(AssetImportContext ctx, List<SurfaceMesh> meshes)
        {
            if (settings.color != SplatColorKind.Material)
                return;
            var objects = new HashSet<UnityEngine.Object>();
            foreach (SurfaceMesh mesh in meshes)
            {
                foreach (Material material in mesh.materials)
                {
                    if (material != null)
                        objects.Add(material);
                }
            }
            foreach (Texture texture in settings.materialColor.Textures(meshes))
                objects.Add(texture);
            foreach (UnityEngine.Object obj in objects)
                AddDependency(ctx, obj);
        }

        // Native assets (materials, .anim clips) count by their file, imported
        // ones by their import result, so their import settings count too
        private static void AddDependency(AssetImportContext ctx, UnityEngine.Object obj)
        {
            string path = AssetDatabase.GetAssetPath(obj);
            // Built-in resources have nothing to depend on
            if (!path.StartsWith("Assets/") && !path.StartsWith("Packages/"))
                return;
            if (AssetDatabase.IsNativeAsset(obj))
                ctx.DependsOnSourceAsset(path);
            else
                ctx.DependsOnArtifact(path);
        }

        private void AddBinding(AssetImportContext ctx, string name, List<SurfaceMesh> meshes,
            List<SurfaceSample> samples, NativeArray<int> sourceIndices, GaussianSplatAsset asset)
        {
            MeshSplatBaker.BuildBinding(meshes, samples, sourceIndices, out var meshInfos,
                out var entries, out var vertices, out var bindposes);
            using (entries)
            using (vertices)
            {
                var binding = ScriptableObject.CreateInstance<GaussianMeshBinding>();
                binding.name = $"{name} Binding";
                binding.model = model;
                binding.meshes = meshInfos;
                binding.bindposes = bindposes;
                binding.entryData = AddBlock(ctx, $"{name}_bnd", entries.Reinterpret<byte>(GaussianMeshBinding.EntrySize));
                binding.vertexData = AddBlock(ctx, $"{name}_vtx", vertices.Reinterpret<byte>(GaussianMeshBinding.VertexSize));
                binding.assetDataHash = asset.dataHash;
                binding.splatCount = asset.splatCount;
                ctx.AddObjectToAsset("binding", binding);
            }
        }

        private static TextAsset AddBlock(AssetImportContext ctx, string id, NativeArray<byte> data)
        {
            var block = new TextAsset(data.AsReadOnlySpan()) { name = id };
            ctx.AddObjectToAsset(id, block);
            return block;
        }
    }
}
