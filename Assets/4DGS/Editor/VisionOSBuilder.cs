using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    /// <summary>
    /// Batch build helper for visionOS Simulator.
    /// Invoked from CLI: Unity -executeMethod GaussianSplatting.Editor.VisionOSBuilder.Build
    /// Lives in 4DGS package but works when loaded by the host project (DreamCore).
    /// </summary>
    public static class VisionOSBuilder
    {
        private const string DefaultOutputPath = "Build";

        [MenuItem("Build/visionOS Simulator (Append)")]
        public static void BuildMenuItem()
        {
            Build();
        }

        public static void Build()
        {
            string outputPath = GetArgValue("-buildOutput") ?? DefaultOutputPath;
            bool clean = HasArg("-cleanBuild");

            var scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();

            if (scenes.Length == 0)
            {
                Debug.LogError("[VisionOSBuilder] No enabled scenes in Build Settings.");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

            var options = BuildOptions.Development;
            if (!clean && System.IO.Directory.Exists(outputPath))
                options |= BuildOptions.AcceptExternalModificationsToPlayer;

            Debug.Log($"[VisionOSBuilder] Target: visionOS Simulator");
            Debug.Log($"[VisionOSBuilder] Output: {outputPath}");
            Debug.Log($"[VisionOSBuilder] Scenes: {string.Join(", ", scenes)}");
            Debug.Log($"[VisionOSBuilder] Options: {options}");

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outputPath,
                target = BuildTarget.VisionOS,
                options = options,
                subtarget = (int)StandaloneBuildSubtarget.Default,
            });

            // Always dump shader errors regardless of overall result
            int shaderErrors = 0;
            foreach (var step in report.steps)
            {
                foreach (var msg in step.messages)
                {
                    if (msg.content.IndexOf("Shader error", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        shaderErrors++;
                        Debug.LogWarning($"[VisionOSBuilder][ShaderError] {msg.content}");
                    }
                }
            }

            if (report.summary.result == BuildResult.Succeeded)
            {
                Debug.Log($"[VisionOSBuilder] BUILD SUCCEEDED in {report.summary.totalTime}" +
                          (shaderErrors > 0 ? $" ({shaderErrors} shader warnings)" : ""));
                if (Application.isBatchMode) EditorApplication.Exit(0);
            }
            else
            {
                Debug.LogError($"[VisionOSBuilder] BUILD FAILED: {report.summary.result}");
                foreach (var step in report.steps)
                {
                    foreach (var msg in step.messages)
                    {
                        if (msg.type == LogType.Error)
                            Debug.LogError($"[VisionOSBuilder] {msg.content}");
                    }
                }
                if (Application.isBatchMode) EditorApplication.Exit(1);
            }
        }

        private static string GetArgValue(string argName)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == argName)
                    return args[i + 1];
            }
            return null;
        }

        private static bool HasArg(string argName)
        {
            return Environment.GetCommandLineArgs().Contains(argName);
        }
    }
}
