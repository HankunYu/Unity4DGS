// SPDX-License-Identifier: MIT
// CLI-triggered build script for automated benchmark runs.
// Usage (from project root):
//   /path/to/Unity -batchmode -quit -projectPath . \
//     -executeMethod GaussianSplatting.Editor.BenchmarkBuildScript.Build \
//     [-logFile build.log]

using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    public static class BenchmarkBuildScript
    {
        const string kScene       = "Assets/Scenes/SampleScene.unity";
        const string kOutputDir   = "Build/Benchmark";
        const string kProductName = "GaussianBenchmark";

        /// <summary>
        /// Entry point for -executeMethod.
        /// Reads optional env vars:
        ///   BENCHMARK_OUTPUT_DIR  — override output directory
        ///   BENCHMARK_DURATION    — seconds (written to PlayerPrefs via launch args)
        /// </summary>
        public static void Build()
        {
            string outputDir = Environment.GetEnvironmentVariable("BENCHMARK_OUTPUT_DIR")
                               ?? kOutputDir;

            string exeName = kProductName;
#if UNITY_EDITOR_OSX
            exeName += ".app";
#elif UNITY_EDITOR_WIN
            exeName += ".exe";
#endif
            string outputPath = Path.Combine(outputDir, exeName);
            Directory.CreateDirectory(outputDir);

            var options = new BuildPlayerOptions
            {
                scenes       = new[] { kScene },
                locationPathName = outputPath,
                target       = EditorUserBuildSettings.activeBuildTarget,
                options      = BuildOptions.None,
            };

            Debug.Log($"[BenchmarkBuild] Building to: {outputPath}");
            var report = BuildPipeline.BuildPlayer(options);

            if (report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
                Debug.Log($"[BenchmarkBuild] Build SUCCESS → {outputPath}");
            else
            {
                Debug.LogError($"[BenchmarkBuild] Build FAILED: {report.summary.result}");
                EditorApplication.Exit(1);
            }
        }
    }
}
