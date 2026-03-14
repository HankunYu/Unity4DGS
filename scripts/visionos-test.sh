#!/usr/bin/env bash
# visionOS Simulator build-deploy-test pipeline
# Usage:
#   ./scripts/visionos-test.sh              # full pipeline
#   ./scripts/visionos-test.sh unity        # Unity export only
#   ./scripts/visionos-test.sh xcode        # Xcode build only
#   ./scripts/visionos-test.sh deploy       # install + run + logs
#   ./scripts/visionos-test.sh logs         # capture logs only
#   ./scripts/visionos-test.sh xcode-deploy # skip Unity, build Xcode + deploy
set -euo pipefail

# ─── Configuration ──────────────────────────────────────────────────
UNITY="/Applications/Unity/Hub/Editor/6000.3.8f1/Unity.app/Contents/MacOS/Unity"
DREAMCORE_PROJECT="/Users/hankun/GitHub/DreamCore"
XCODE_PROJECT="$DREAMCORE_PROJECT/Build/Unity-VisionOS.xcodeproj"
XCODE_SCHEME="Unity-VisionOS"
DERIVED_DATA="$DREAMCORE_PROJECT/Build/DerivedData"
BUNDLE_ID="com.hankun.DreamCore"
SIM_DEVICE="Apple Vision Pro"
LOG_TIMEOUT=20  # seconds to capture runtime logs

LOG_DIR="/tmp/visionos-build"
UNITY_LOG="$LOG_DIR/unity-build.log"
XCODE_LOG="$LOG_DIR/xcode-build.log"
RUNTIME_LOG="$LOG_DIR/runtime.log"
SHADER_ERRORS="$LOG_DIR/shader-errors.log"
SUMMARY="$LOG_DIR/summary.log"

mkdir -p "$LOG_DIR"

# ─── Helpers ────────────────────────────────────────────────────────
timestamp() { date "+%H:%M:%S"; }

log()   { echo "[$(timestamp)] $*"; }
ok()    { echo "[$(timestamp)] ✓ $*"; }
fail()  { echo "[$(timestamp)] ✗ $*"; }
warn()  { echo "[$(timestamp)] ! $*"; }
separator() { echo "════════════════════════════════════════════════════════════"; }

# ─── Phase 1: Unity Export ──────────────────────────────────────────
phase_unity() {
    separator
    log "PHASE 1: Unity → Xcode project export"
    log "Project: $DREAMCORE_PROJECT"

    "$UNITY" \
        -batchmode -quit -nographics \
        -projectPath "$DREAMCORE_PROJECT" \
        -executeMethod "GaussianSplatting.Editor.VisionOSBuilder.Build" \
        -buildOutput "Build" \
        -logFile "$UNITY_LOG" \
        2>&1 || true

    # Check exit and extract key info
    if grep -q "BUILD SUCCEEDED" "$UNITY_LOG" 2>/dev/null; then
        ok "Unity build succeeded"
    else
        fail "Unity build FAILED"
        # Extract errors
        grep -E "error|Error|FAILED" "$UNITY_LOG" | tail -20
        return 1
    fi

    # Extract shader errors
    grep -i "shader error\|Shader error" "$UNITY_LOG" > "$SHADER_ERRORS" 2>/dev/null || true
    local shader_err_count
    shader_err_count=$(wc -l < "$SHADER_ERRORS" | tr -d ' ')
    if [ "$shader_err_count" -gt 0 ]; then
        warn "$shader_err_count shader error(s) found:"
        cat "$SHADER_ERRORS"
    else
        ok "No shader errors"
    fi
}

# ─── Phase 2: Xcode Build ──────────────────────────────────────────
phase_xcode() {
    separator
    log "PHASE 2: Xcode build for visionOS Simulator"

    if [ ! -d "$XCODE_PROJECT" ]; then
        fail "Xcode project not found: $XCODE_PROJECT"
        fail "Run 'unity' phase first"
        return 1
    fi

    xcodebuild \
        -project "$XCODE_PROJECT" \
        -scheme "$XCODE_SCHEME" \
        -configuration Debug \
        -destination "platform=visionOS Simulator,name=$SIM_DEVICE" \
        -derivedDataPath "$DERIVED_DATA" \
        build \
        2>&1 | tee "$XCODE_LOG" | grep -E "^(Build |Compil|Link|error:|warning:.*error|✗|✓|\*\*)" || true

    if grep -q "BUILD SUCCEEDED" "$XCODE_LOG" 2>/dev/null; then
        ok "Xcode build succeeded"
    elif grep -q "** BUILD SUCCEEDED **" "$XCODE_LOG" 2>/dev/null; then
        ok "Xcode build succeeded"
    else
        fail "Xcode build FAILED"
        grep -E "^error:|: error:" "$XCODE_LOG" | tail -20
        return 1
    fi
}

# ─── Phase 3: Deploy & Test ────────────────────────────────────────
phase_deploy() {
    separator
    log "PHASE 3: Deploy to simulator & capture logs"

    # Find the built .app
    local app_path
    app_path=$(find "$DERIVED_DATA" -name "*.app" -path "*Debug-xrsimulator*" -not -name "*Tests*" 2>/dev/null | head -1)

    if [ -z "$app_path" ]; then
        # Fallback: search broader
        app_path=$(find "$DERIVED_DATA" -name "*.app" -path "*Debug*" -not -name "*Tests*" 2>/dev/null | head -1)
    fi

    if [ -z "$app_path" ]; then
        fail "Built .app not found in $DERIVED_DATA"
        fail "Run 'xcode' phase first"
        return 1
    fi
    log "App: $app_path"

    # Ensure simulator is booted
    local sim_state
    sim_state=$(xcrun simctl list devices | grep "$SIM_DEVICE" | grep -o "(Booted)" || echo "")
    if [ -z "$sim_state" ]; then
        log "Booting simulator..."
        xcrun simctl boot "$SIM_DEVICE" 2>/dev/null || true
        sleep 3
    fi
    ok "Simulator ready"

    # Terminate previous instance
    xcrun simctl terminate booted "$BUNDLE_ID" 2>/dev/null || true
    sleep 1

    # Install
    log "Installing app..."
    xcrun simctl install booted "$app_path"
    ok "App installed"

    # Launch with --console-pty to capture Unity Debug.Log output
    log "Launching app with console capture (${LOG_TIMEOUT}s)..."
    > "$RUNTIME_LOG"
    timeout "$LOG_TIMEOUT" xcrun simctl launch --console-pty booted "$BUNDLE_ID" \
        2>&1 | tee "$RUNTIME_LOG" || true
    ok "Log capture complete"

    separator
    analyze_logs
}

# ─── Phase 4: Log Capture & Analysis ───────────────────────────────
phase_logs() {
    separator
    log "Capturing runtime logs for ${LOG_TIMEOUT}s..."

    # Terminate previous instance first
    xcrun simctl terminate booted "$BUNDLE_ID" 2>/dev/null || true
    sleep 1

    # Launch with --console-pty to capture Unity Debug.Log output
    > "$RUNTIME_LOG"
    timeout "$LOG_TIMEOUT" xcrun simctl launch --console-pty booted "$BUNDLE_ID" \
        2>&1 | tee "$RUNTIME_LOG" || true

    separator
    analyze_logs
}

# ─── Log Analysis ──────────────────────────────────────────────────
analyze_logs() {
    log "=== LOG ANALYSIS ==="

    if [ ! -s "$RUNTIME_LOG" ]; then
        warn "No matching log entries captured (app may not have produced [GaussianSplat] logs)"
        return 0
    fi

    # Summary file
    > "$SUMMARY"

    # Config status
    echo "--- Config ---" >> "$SUMMARY"
    grep "Config.*LoadResources" "$RUNTIME_LOG" >> "$SUMMARY" 2>/dev/null || echo "  (no config log)" >> "$SUMMARY"

    # Renderer status
    echo "--- Renderer ---" >> "$SUMMARY"
    grep "Renderer.*OnEnable" "$RUNTIME_LOG" >> "$SUMMARY" 2>/dev/null || echo "  (no renderer log)" >> "$SUMMARY"

    # Kernel support
    echo "--- Kernel ---" >> "$SUMMARY"
    grep "Kernel" "$RUNTIME_LOG" >> "$SUMMARY" 2>/dev/null || echo "  (no kernel issues)" >> "$SUMMARY"

    # Stereo detection
    echo "--- Stereo ---" >> "$SUMMARY"
    grep "Stereo detection" "$RUNTIME_LOG" >> "$SUMMARY" 2>/dev/null || echo "  (no stereo log)" >> "$SUMMARY"

    # Draw results
    echo "--- Draw ---" >> "$SUMMARY"
    grep -E "CalcViewData|Draw.*FAILED|Gather" "$RUNTIME_LOG" >> "$SUMMARY" 2>/dev/null || echo "  (no draw issues)" >> "$SUMMARY"

    # Errors
    echo "--- Errors ---" >> "$SUMMARY"
    grep -iE "error|exception|failed|missing" "$RUNTIME_LOG" >> "$SUMMARY" 2>/dev/null || echo "  (no errors)" >> "$SUMMARY"

    cat "$SUMMARY"

    # Verdict
    separator
    if grep -q "CalcViewData FAILED\|MISSING\|Exception\|IsSupported=false" "$RUNTIME_LOG" 2>/dev/null; then
        fail "ISSUES DETECTED — see above"
        return 1
    elif grep -q "Render.*Executing\|Draw.*OK\|Gather.*OK" "$RUNTIME_LOG" 2>/dev/null; then
        ok "Rendering pipeline appears to be running"
        return 0
    else
        warn "Inconclusive — check logs manually: $RUNTIME_LOG"
        return 0
    fi
}

# ─── Main ───────────────────────────────────────────────────────────
main() {
    local phase="${1:-full}"
    separator
    log "visionOS Simulator Test Pipeline — phase: $phase"
    log "Logs: $LOG_DIR/"
    separator

    case "$phase" in
        unity)
            phase_unity
            ;;
        xcode)
            phase_xcode
            ;;
        deploy)
            phase_deploy
            ;;
        logs)
            phase_logs
            ;;
        xcode-deploy)
            phase_xcode && phase_deploy
            ;;
        full)
            phase_unity && phase_xcode && phase_deploy
            ;;
        analyze)
            analyze_logs
            ;;
        *)
            echo "Usage: $0 {full|unity|xcode|deploy|xcode-deploy|logs|analyze}"
            exit 1
            ;;
    esac
}

main "$@"
