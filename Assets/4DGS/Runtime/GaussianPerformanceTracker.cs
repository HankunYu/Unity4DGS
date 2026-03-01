// SPDX-License-Identifier: MIT
// Performance tracker for 4DGS/3DGS benchmarking.
// Attach to any GameObject in the scene to measure FPS and frame-switch timing.

using System;
using System.Collections.Generic;
using System.IO;
using Unity.Profiling;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Tracks and reports FPS, frame-switch latency, and GPU upload costs.
    ///
    /// Usage:
    ///   1. Attach this component to any GameObject.
    ///   2. (Optional) Assign the GaussianPlayer to playerRef.
    ///   3. Set benchmarkDuration and hit Play — results auto-logged after the run.
    ///   4. Use the on-screen HUD (showHUD = true) for live monitoring.
    ///   5. Call ExportCSV() or use the context-menu item to save a CSV report.
    /// </summary>
    public class GaussianPerformanceTracker : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("The GaussianPlayer driving the 4DGS sequence (optional, enables frame-switch metrics)")]
        public GaussianPlayer playerRef;

        [Header("Benchmark Settings")]
        [Tooltip("How many seconds to run the benchmark (0 = run indefinitely)")]
        public float benchmarkDuration = 30f;

        [Tooltip("Warm-up seconds before recording starts")]
        public float warmupDuration = 3f;

        [Tooltip("Sample window for rolling-average FPS display (seconds)")]
        public float rollingWindowSec = 1f;

        [Header("HUD")]
        public bool showHUD = true;
        public int hudFontSize = 18;
        public Color hudColor = Color.yellow;

        [Header("Export")]
        [Tooltip("Auto-export CSV when benchmark finishes")]
        public bool autoExportCSV = true;
        public string exportPath = ""; // empty = Application.persistentDataPath

        // -- Internal state --

        enum Phase { Warmup, Benchmarking, Done }
        Phase m_Phase = Phase.Warmup;

        float m_PhaseTimer = 0f;
        int m_TotalFrames = 0;

        // Rolling window
        readonly Queue<float> m_RollingFrameTimes = new();
        float m_RollingSum = 0f;

        // Per-frame stats (recorded during benchmark)
        readonly List<float> m_FrameTimes = new();
        readonly List<int> m_GsFrameIndices = new();
        readonly List<float> m_FrameSwitchLatencies = new();

        // Frame-switch detection
        int m_LastGsFrame = -1;
        float m_LastGsFrameTime = 0f;

        // ProfilerRecorder
        ProfilerRecorder m_RecDraw;
        ProfilerRecorder m_RecCompose;
        ProfilerRecorder m_RecCalcView;
        ProfilerRecorder m_RecSort;

        BenchmarkResult m_Result;

        // -- Lifecycle --

        void OnEnable()
        {
            m_RecDraw     = ProfilerRecorder.StartNew(new ProfilerCategory("Render"), "GaussianSplat.Draw",     15);
            m_RecCompose  = ProfilerRecorder.StartNew(new ProfilerCategory("Render"), "GaussianSplat.Compose",  15);
            m_RecCalcView = ProfilerRecorder.StartNew(new ProfilerCategory("Render"), "GaussianSplat.CalcView", 15);
            m_RecSort     = ProfilerRecorder.StartNew(new ProfilerCategory("Render"), "GaussianSplat.Sort",     15);

            ResetStats();
            Debug.Log("[GaussianPerf] Tracker enabled. Warm-up phase started.");
        }

        void OnDisable()
        {
            m_RecDraw.Dispose();
            m_RecCompose.Dispose();
            m_RecCalcView.Dispose();
            m_RecSort.Dispose();
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            m_PhaseTimer += dt;

            // Rolling window (always update)
            m_RollingFrameTimes.Enqueue(dt);
            m_RollingSum += dt;
            while (m_RollingSum - m_RollingFrameTimes.Peek() > rollingWindowSec)
                m_RollingSum -= m_RollingFrameTimes.Dequeue();

            if (m_Phase == Phase.Warmup)
            {
                if (m_PhaseTimer >= warmupDuration)
                {
                    m_Phase = Phase.Benchmarking;
                    m_PhaseTimer = 0f;
                    ResetBenchmarkData();
                    Debug.Log("[GaussianPerf] Warm-up done. Benchmark started.");
                }
                return;
            }

            if (m_Phase == Phase.Done)
                return;

            // Record frame
            m_TotalFrames++;
            m_FrameTimes.Add(dt);

            // 4DGS frame-switch tracking
            if (playerRef != null)
            {
                int gsFrame = playerRef.currentFrame;
                m_GsFrameIndices.Add(gsFrame);

                if (gsFrame != m_LastGsFrame)
                {
                    if (m_LastGsFrame >= 0)
                        m_FrameSwitchLatencies.Add(Time.unscaledTime - m_LastGsFrameTime);
                    m_LastGsFrame = gsFrame;
                    m_LastGsFrameTime = Time.unscaledTime;
                }
            }

            if (benchmarkDuration > 0f && m_PhaseTimer >= benchmarkDuration)
                FinishBenchmark();
        }

        // -- HUD --

        void OnGUI()
        {
            if (!showHUD) return;

            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = hudFontSize,
                normal = { textColor = hudColor }
            };

            float rollingFPS = m_RollingSum > 0f
                ? m_RollingFrameTimes.Count / m_RollingSum : 0f;
            float instFPS = Time.unscaledDeltaTime > 0f
                ? 1f / Time.unscaledDeltaTime : 0f;

            string phaseLabel = m_Phase switch
            {
                Phase.Warmup       => $"WARM-UP ({warmupDuration - m_PhaseTimer:F1}s)",
                Phase.Benchmarking => $"BENCHMARKING ({(benchmarkDuration > 0 ? $"{benchmarkDuration - m_PhaseTimer:F1}s left" : "inf")})",
                Phase.Done         => "DONE",
                _                  => ""
            };

            string gsInfo = playerRef != null
                ? $"\n4DGS Frame: {playerRef.currentFrame}  |  Splats: {GetSplatCount()}"
                : "";

            string drawMs   = m_RecDraw.Valid     ? $"{m_RecDraw.LastValue / 1e6f:F2}ms"     : "n/a";
            string compMs   = m_RecCompose.Valid   ? $"{m_RecCompose.LastValue / 1e6f:F2}ms"  : "n/a";
            string calcMs   = m_RecCalcView.Valid  ? $"{m_RecCalcView.LastValue / 1e6f:F2}ms" : "n/a";
            string sortMs   = m_RecSort.Valid      ? $"{m_RecSort.LastValue / 1e6f:F2}ms"     : "n/a";

            string text =
                $"[GaussianPerf] {phaseLabel}\n" +
                $"FPS (instant): {instFPS:F1}  |  FPS ({rollingWindowSec:F0}s avg): {rollingFPS:F1}\n" +
                $"Draw: {drawMs}  Compose: {compMs}  CalcView: {calcMs}  Sort: {sortMs}" +
                gsInfo;

            GUI.Label(new Rect(10, 10, 800, 200), text, style);

            if (m_Phase == Phase.Benchmarking && benchmarkDuration <= 0f)
                if (GUI.Button(new Rect(10, 220, 160, 30), "Finish Benchmark"))
                    FinishBenchmark();
        }

        // -- Finish & report --

        void FinishBenchmark()
        {
            m_Phase = Phase.Done;
            m_Result = ComputeResult();
            PrintResult(m_Result);
            if (autoExportCSV)
                ExportCSV();
        }

        BenchmarkResult ComputeResult()
        {
            if (m_FrameTimes.Count == 0) return new BenchmarkResult();

            var sorted = new List<float>(m_FrameTimes);
            sorted.Sort();
            int n = sorted.Count;

            float totalTime = 0f;
            foreach (var t in sorted) totalTime += t;
            float avgFT = totalTime / n;
            float p50 = sorted[Mathf.Clamp((int)(n * 0.50f), 0, n - 1)];
            float p95 = sorted[Mathf.Clamp((int)(n * 0.95f), 0, n - 1)];
            float p99 = sorted[Mathf.Clamp((int)(n * 0.99f), 0, n - 1)];

            float avgSwitch = 0f;
            foreach (var s in m_FrameSwitchLatencies) avgSwitch += s;
            if (m_FrameSwitchLatencies.Count > 0) avgSwitch /= m_FrameSwitchLatencies.Count;

            return new BenchmarkResult
            {
                TotalFrames   = n,
                TotalTimeSec  = totalTime,
                AvgFPS        = 1f / avgFT,
                MinFPS        = 1f / sorted[n - 1],
                MaxFPS        = 1f / sorted[0],
                P50FrameMs    = p50 * 1000f,
                P95FrameMs    = p95 * 1000f,
                P99FrameMs    = p99 * 1000f,
                Avg4DSwitchMs = avgSwitch * 1000f,
                Switch4DCount = m_FrameSwitchLatencies.Count,
            };
        }

        void PrintResult(BenchmarkResult r)
        {
            Debug.Log(
                $"[GaussianPerf] == BENCHMARK RESULT ==\n" +
                $"  Frames recorded : {r.TotalFrames}\n" +
                $"  Total time      : {r.TotalTimeSec:F2}s\n" +
                $"  Avg FPS         : {r.AvgFPS:F2}\n" +
                $"  Min FPS         : {r.MinFPS:F2}\n" +
                $"  Max FPS         : {r.MaxFPS:F2}\n" +
                $"  Frame time p50  : {r.P50FrameMs:F2} ms\n" +
                $"  Frame time p95  : {r.P95FrameMs:F2} ms\n" +
                $"  Frame time p99  : {r.P99FrameMs:F2} ms\n" +
                $"  4DGS switches   : {r.Switch4DCount}\n" +
                $"  Avg switch lat  : {r.Avg4DSwitchMs:F2} ms"
            );
        }

        // -- CSV export --

        [ContextMenu("Export CSV Now")]
        public void ExportCSV()
        {
            string dir = string.IsNullOrEmpty(exportPath)
                ? Application.persistentDataPath : exportPath;
            string ts   = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string path = Path.Combine(dir, $"gaussian_perf_{ts}.csv");

            using var sw = new StreamWriter(path);
            sw.WriteLine("# GaussianPerf Benchmark Summary");
            sw.WriteLine($"# Date,{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sw.WriteLine($"# AvgFPS,{m_Result.AvgFPS:F2}");
            sw.WriteLine($"# MinFPS,{m_Result.MinFPS:F2}");
            sw.WriteLine($"# MaxFPS,{m_Result.MaxFPS:F2}");
            sw.WriteLine($"# P50_ms,{m_Result.P50FrameMs:F2}");
            sw.WriteLine($"# P95_ms,{m_Result.P95FrameMs:F2}");
            sw.WriteLine($"# P99_ms,{m_Result.P99FrameMs:F2}");
            sw.WriteLine($"# 4DGS_SwitchCount,{m_Result.Switch4DCount}");
            sw.WriteLine($"# 4DGS_AvgSwitchLatency_ms,{m_Result.Avg4DSwitchMs:F2}");
            sw.WriteLine();
            sw.WriteLine("frame_index,frame_time_ms,gs_frame_index");
            for (int i = 0; i < m_FrameTimes.Count; i++)
            {
                string gsFrame = i < m_GsFrameIndices.Count ? m_GsFrameIndices[i].ToString() : "";
                sw.WriteLine($"{i},{m_FrameTimes[i] * 1000f:F3},{gsFrame}");
            }

            Debug.Log($"[GaussianPerf] CSV exported -> {path}");
        }

        // -- Helpers --

        void ResetStats()
        {
            m_TotalFrames = 0;
            m_PhaseTimer  = 0f;
            m_RollingFrameTimes.Clear();
            m_RollingSum = 0f;
            ResetBenchmarkData();
        }

        void ResetBenchmarkData()
        {
            m_FrameTimes.Clear();
            m_GsFrameIndices.Clear();
            m_FrameSwitchLatencies.Clear();
            m_LastGsFrame     = -1;
            m_LastGsFrameTime = 0f;
        }

        int GetSplatCount()
        {
            if (playerRef == null) return 0;
            var r = playerRef.GetComponent<GaussianSplatRenderer>();
            return r != null ? r.splatCount : 0;
        }

        struct BenchmarkResult
        {
            public int   TotalFrames;
            public float TotalTimeSec;
            public float AvgFPS, MinFPS, MaxFPS;
            public float P50FrameMs, P95FrameMs, P99FrameMs;
            public float Avg4DSwitchMs;
            public int   Switch4DCount;
        }
    }
}
