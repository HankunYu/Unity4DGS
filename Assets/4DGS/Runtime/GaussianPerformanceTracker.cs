// SPDX-License-Identifier: MIT
// Performance tracker for 4DGS/3DGS benchmarking.
// Works both in-Editor (manual) and as a standalone build (automated CI).
//
// Standalone CLI args (passed to the built player):
//   -benchmark-duration <seconds>   override benchmarkDuration
//   -benchmark-warmup   <seconds>   override warmupDuration
//   -benchmark-output   <path>      override CSV export path
//   -benchmark-quit                 quit the player after benchmark (auto-enabled in batch/headless)

using System;
using System.Collections.Generic;
using System.IO;
using Unity.Profiling;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    public class GaussianPerformanceTracker : MonoBehaviour
    {
        [Header("References")]
        public GaussianPlayer playerRef;

        [Header("Benchmark Settings")]
        [Tooltip("Seconds to run (0 = manual stop). Overridable via -benchmark-duration")]
        public float benchmarkDuration = 30f;
        [Tooltip("Warm-up seconds before recording. Overridable via -benchmark-warmup")]
        public float warmupDuration = 3f;
        public float rollingWindowSec = 1f;

        [Header("HUD")]
        public bool showHUD = true;
        public int hudFontSize = 18;
        public Color hudColor = Color.yellow;

        [Header("Export & Automation")]
        public bool autoExportCSV = true;
        [Tooltip("CSV output directory. Overridable via -benchmark-output")]
        public string exportPath = "";
        [Tooltip("Quit the application after benchmark. Auto-set when -benchmark-quit flag is present.")]
        public bool quitWhenDone = false;

        // ── Internal ──────────────────────────────────────────────────────────────

        enum Phase { Warmup, Benchmarking, Done }
        Phase m_Phase = Phase.Warmup;
        float m_PhaseTimer = 0f;

        readonly Queue<float> m_RollingFrameTimes = new();
        float m_RollingSum = 0f;

        readonly List<float> m_FrameTimes = new();
        readonly List<int>   m_GsFrameIndices = new();
        readonly List<float> m_FrameSwitchLatencies = new();

        int   m_LastGsFrame = -1;
        float m_LastGsFrameTime = 0f;

        ProfilerRecorder m_RecDraw, m_RecCompose, m_RecCalcView, m_RecSort;

        BenchmarkResult m_Result;

        // ── Lifecycle ─────────────────────────────────────────────────────────────

        void Awake()
        {
            ParseCommandLineArgs();
        }

        void OnEnable()
        {
            m_RecDraw     = ProfilerRecorder.StartNew(new ProfilerCategory("Render"), "GaussianSplat.Draw",     15);
            m_RecCompose  = ProfilerRecorder.StartNew(new ProfilerCategory("Render"), "GaussianSplat.Compose",  15);
            m_RecCalcView = ProfilerRecorder.StartNew(new ProfilerCategory("Render"), "GaussianSplat.CalcView", 15);
            m_RecSort     = ProfilerRecorder.StartNew(new ProfilerCategory("Render"), "GaussianSplat.Sort",     15);
            ResetStats();
            Debug.Log("[GaussianPerf] Started. Warm-up phase.");
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

            if (m_Phase == Phase.Done) return;

            m_FrameTimes.Add(dt);

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

        // ── HUD ───────────────────────────────────────────────────────────────────

        void OnGUI()
        {
            if (!showHUD) return;

            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = hudFontSize,
                normal   = { textColor = hudColor }
            };

            float rollingFPS = m_RollingSum > 0f ? m_RollingFrameTimes.Count / m_RollingSum : 0f;
            float instFPS    = Time.unscaledDeltaTime > 0f ? 1f / Time.unscaledDeltaTime : 0f;

            string phase = m_Phase switch
            {
                Phase.Warmup       => $"WARM-UP ({warmupDuration - m_PhaseTimer:F1}s)",
                Phase.Benchmarking => $"BENCHMARKING ({(benchmarkDuration > 0 ? $"{benchmarkDuration - m_PhaseTimer:F1}s left" : "inf")})",
                Phase.Done         => "DONE",
                _                  => ""
            };

            string gsInfo   = playerRef != null ? $"\n4DGS Frame: {playerRef.currentFrame}  Splats: {GetSplatCount()}" : "";
            string drawMs   = m_RecDraw.Valid     ? $"{m_RecDraw.LastValue     / 1e6f:F2}ms" : "n/a";
            string compMs   = m_RecCompose.Valid   ? $"{m_RecCompose.LastValue  / 1e6f:F2}ms" : "n/a";
            string calcMs   = m_RecCalcView.Valid  ? $"{m_RecCalcView.LastValue / 1e6f:F2}ms" : "n/a";
            string sortMs   = m_RecSort.Valid      ? $"{m_RecSort.LastValue     / 1e6f:F2}ms" : "n/a";

            GUI.Label(new Rect(10, 10, 820, 200), style: style,
                text: $"[GaussianPerf] {phase}\n" +
                      $"FPS (instant): {instFPS:F1}  FPS ({rollingWindowSec:F0}s avg): {rollingFPS:F1}\n" +
                      $"Draw: {drawMs}  Compose: {compMs}  CalcView: {calcMs}  Sort: {sortMs}" +
                      gsInfo);

            if (m_Phase == Phase.Benchmarking && benchmarkDuration <= 0f)
                if (GUI.Button(new Rect(10, 220, 160, 30), "Finish Benchmark"))
                    FinishBenchmark();
        }

        // ── Finish ────────────────────────────────────────────────────────────────

        void FinishBenchmark()
        {
            m_Phase  = Phase.Done;
            m_Result = ComputeResult();
            PrintResult(m_Result);

            if (autoExportCSV)
                ExportCSV();

            if (quitWhenDone)
            {
                Debug.Log("[GaussianPerf] Quitting application.");
                Application.Quit(0);
            }
        }

        // ── Compute ───────────────────────────────────────────────────────────────

        BenchmarkResult ComputeResult()
        {
            if (m_FrameTimes.Count == 0) return new BenchmarkResult();

            var sorted = new List<float>(m_FrameTimes);
            sorted.Sort();
            int n = sorted.Count;

            float totalTime = 0f;
            foreach (var t in sorted) totalTime += t;

            float avgFT = totalTime / n;
            float p50   = sorted[Mathf.Clamp((int)(n * 0.50f), 0, n - 1)];
            float p95   = sorted[Mathf.Clamp((int)(n * 0.95f), 0, n - 1)];
            float p99   = sorted[Mathf.Clamp((int)(n * 0.99f), 0, n - 1)];

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
                $"[GaussianPerf] === BENCHMARK RESULT ===\n" +
                $"  Frames    : {r.TotalFrames}  ({r.TotalTimeSec:F2}s)\n" +
                $"  Avg FPS   : {r.AvgFPS:F2}\n" +
                $"  Min FPS   : {r.MinFPS:F2}\n" +
                $"  Max FPS   : {r.MaxFPS:F2}\n" +
                $"  p50 ms    : {r.P50FrameMs:F2}\n" +
                $"  p95 ms    : {r.P95FrameMs:F2}\n" +
                $"  p99 ms    : {r.P99FrameMs:F2}\n" +
                $"  4D switch : {r.Switch4DCount} switches, avg {r.Avg4DSwitchMs:F2}ms"
            );
        }

        // ── CSV ───────────────────────────────────────────────────────────────────

        [ContextMenu("Export CSV Now")]
        public void ExportCSV()
        {
            string dir = string.IsNullOrEmpty(exportPath)
                ? Application.persistentDataPath : exportPath;
            Directory.CreateDirectory(dir);

            string ts   = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string path = Path.Combine(dir, $"gaussian_perf_{ts}.csv");

            using var sw = new StreamWriter(path);
            sw.WriteLine("# GaussianPerf Benchmark");
            sw.WriteLine($"# Date,{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sw.WriteLine($"# AvgFPS,{m_Result.AvgFPS:F2}");
            sw.WriteLine($"# MinFPS,{m_Result.MinFPS:F2}");
            sw.WriteLine($"# MaxFPS,{m_Result.MaxFPS:F2}");
            sw.WriteLine($"# P50_ms,{m_Result.P50FrameMs:F2}");
            sw.WriteLine($"# P95_ms,{m_Result.P95FrameMs:F2}");
            sw.WriteLine($"# P99_ms,{m_Result.P99FrameMs:F2}");
            sw.WriteLine($"# 4DGS_Switches,{m_Result.Switch4DCount}");
            sw.WriteLine($"# 4DGS_AvgSwitchMs,{m_Result.Avg4DSwitchMs:F2}");
            sw.WriteLine();
            sw.WriteLine("frame_index,frame_time_ms,gs_frame_index");
            for (int i = 0; i < m_FrameTimes.Count; i++)
            {
                string gs = i < m_GsFrameIndices.Count ? m_GsFrameIndices[i].ToString() : "";
                sw.WriteLine($"{i},{m_FrameTimes[i] * 1000f:F3},{gs}");
            }

            Debug.Log($"[GaussianPerf] CSV saved: {path}");
        }

        // ── CLI args ──────────────────────────────────────────────────────────────

        void ParseCommandLineArgs()
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-benchmark-duration" when i + 1 < args.Length:
                        if (float.TryParse(args[++i], out var dur))
                            benchmarkDuration = dur;
                        break;
                    case "-benchmark-warmup" when i + 1 < args.Length:
                        if (float.TryParse(args[++i], out var wu))
                            warmupDuration = wu;
                        break;
                    case "-benchmark-output" when i + 1 < args.Length:
                        exportPath = args[++i];
                        break;
                    case "-benchmark-quit":
                        quitWhenDone = true;
                        break;
                }
            }

            Debug.Log($"[GaussianPerf] Config — duration:{benchmarkDuration}s  warmup:{warmupDuration}s  output:{exportPath}  quit:{quitWhenDone}");
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        void ResetStats()
        {
            m_PhaseTimer = 0f;
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
            var r = playerRef?.GetComponent<GaussianSplatRenderer>();
            return r != null ? r.splatCount : 0;
        }

        // ── Data struct ───────────────────────────────────────────────────────────

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
