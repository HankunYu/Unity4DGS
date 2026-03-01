#!/usr/bin/env bash
# run_benchmark.sh — Build & run 4DGS benchmark without opening Unity Editor.
#
# Usage: ./run_benchmark.sh [--unity-path <path>] [--duration <sec>]
#        [--warmup <sec>] [--output-dir <dir>] [--build-only] [--run-only]

set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_PATH="$SCRIPT_DIR"

UNITY_PATH=""; DURATION=30; WARMUP=3
OUTPUT_DIR="$SCRIPT_DIR/benchmark_results"
BUILD_DIR="$SCRIPT_DIR/Build/Benchmark"
BUILD_ONLY=false; RUN_ONLY=false

while [[ $# -gt 0 ]]; do
  case "$1" in
    --unity-path) UNITY_PATH="$2"; shift 2 ;;
    --duration)   DURATION="$2";   shift 2 ;;
    --warmup)     WARMUP="$2";     shift 2 ;;
    --output-dir) OUTPUT_DIR="$2"; shift 2 ;;
    --build-only) BUILD_ONLY=true; shift ;;
    --run-only)   RUN_ONLY=true;   shift ;;
    *) echo "Unknown option: $1"; exit 1 ;;
  esac
done

if [[ -z "$UNITY_PATH" ]]; then
  VER=$(grep "m_EditorVersion:" "$PROJECT_PATH/ProjectSettings/ProjectVersion.txt" | awk '{print $2}')
  echo "Unity version: $VER"
  CANDIDATES=(
    "/Applications/Unity/Hub/Editor/$VER/Unity.app/Contents/MacOS/Unity"
    "/Applications/Unity $VER/Unity.app/Contents/MacOS/Unity"
  )
  for c in "${CANDIDATES[@]}"; do
    [[ -f "$c" ]] && { UNITY_PATH="$c"; break; }
  done
  [[ -z "$UNITY_PATH" ]] && { echo "Unity not found. Use --unity-path."; exit 1; }
fi

echo "Unity: $UNITY_PATH"
mkdir -p "$OUTPUT_DIR" "$BUILD_DIR"

if [[ "$RUN_ONLY" == false ]]; then
  LOG_BUILD="$OUTPUT_DIR/build_$(date +%Y%m%d_%H%M%S).log"
  echo "Building... log: $LOG_BUILD"
  "$UNITY_PATH" -batchmode -quit \
    -projectPath "$PROJECT_PATH" \
    -executeMethod GaussianSplatting.Editor.BenchmarkBuildScript.Build \
    -logFile "$LOG_BUILD" 2>&1 || true

  if grep -q "Build SUCCESS" "$LOG_BUILD"; then
    echo "Build OK."
  else
    echo "Build FAILED. Log:"; tail -30 "$LOG_BUILD"; exit 1
  fi
fi

[[ "$BUILD_ONLY" == true ]] && exit 0

if [[ "$(uname)" == "Darwin" ]]; then
  APP=$(find "$BUILD_DIR" -name "*.app" -maxdepth 1 | head -1)
  EXECUTABLE="$APP/Contents/MacOS/$(basename "${APP%.app}")"
else
  EXECUTABLE=$(find "$BUILD_DIR" -maxdepth 1 -type f -executable | head -1)
fi

[[ ! -f "$EXECUTABLE" ]] && { echo "Executable not found in $BUILD_DIR"; exit 1; }

echo "Running: $(basename "$EXECUTABLE")  duration=${DURATION}s warmup=${WARMUP}s"
LOG_RUN="$OUTPUT_DIR/run_$(date +%Y%m%d_%H%M%S).log"

"$EXECUTABLE" \
  -benchmark-duration "$DURATION" \
  -benchmark-warmup   "$WARMUP" \
  -benchmark-output   "$OUTPUT_DIR" \
  -benchmark-quit \
  2>&1 | tee "$LOG_RUN" || true

echo ""
echo "=== SUMMARY ==="
grep -E "Avg FPS|Min FPS|Max FPS|p50|p95|p99|4D switch" "$LOG_RUN" 2>/dev/null || true

CSV=$(find "$OUTPUT_DIR" -name "gaussian_perf_*.csv" | sort | tail -1)
[[ -n "$CSV" ]] && { echo "CSV: $CSV"; head -12 "$CSV"; }
