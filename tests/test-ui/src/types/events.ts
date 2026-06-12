import type { AnnotationLevel, AnnotationSource, OutputEntry } from "./state";

/** Mirrors C# event types. All properties use camelCase to match JSON serialization. */

export type TestEvent =
    | PopulateTestsEvent
    | RunStartedEvent
    | DiscoveryCompleteEvent
    | RunFinishedEvent
    | RunAbortedEvent
    | TestStartedEvent
    | TestRunningEvent
    | TestPassedEvent
    | TestFailedEvent
    | TestSkippedEvent
    | TestAnnotationEvent
    | DiagnosticEvent
    | ErrorEvent
    | SetupPhaseStartedEvent
    | SetupPhaseCompletedEvent
    | SetupStepEvent
    | ScreenshotEvent
    | RecordingEvent
    | RecordingSkippedEvent
    | TestEnrichmentEvent
    | RunMetadataEvent
    | FlakyTestsEvent
    | InstanceCreatedEvent
    | InstanceRecordingEvent
    | InstanceStatusEvent
    | InstanceStatsEvent;

export interface PopulateTestsEvent {
    event: "populate_tests";
    timestamp: string;
    testCount: number;
    collections: {
        name: string;
        classes: {
            name: string;
            tests: {
                className: string;
                displayName: string;
                status: string;
                discoveryOrder?: number | null;
            }[];
        }[];
    }[];
}

export interface RunStartedEvent {
    event: "run_started";
    timestamp: string;
    testCount: number;
}

export interface DiscoveryCompleteEvent {
    event: "discovery_complete";
    timestamp: string;
    testCasesDiscovered: number;
    testCasesToRun: number;
}

export interface RunFinishedEvent {
    event: "run_finished";
    timestamp: string;
    totalTests: number;
    passed: number;
    failed: number;
    skipped: number;
    durationMs: number;
}

export interface RunAbortedEvent {
    event: "run_aborted";
    timestamp: string;
    /** "ctrl-c", "ui_stop", "preflight", "image_build", ... — matches the runner's recorder.SetAbortReason cause */
    cause: string;
}

export interface TestStartedEvent {
    event: "test_started";
    timestamp: string;
    testCollection: string;
    testClass: string;
    testMethod: string;
    displayName: string;
}

export interface TestRunningEvent {
    event: "test_running";
    timestamp: string;
    testCollection: string;
    testClass: string;
    testMethod: string;
    displayName: string;
    executionOrder?: number | null;
}

export interface TestPassedEvent {
    event: "test_passed";
    timestamp: string;
    testCollection: string;
    testClass: string;
    displayName: string;
    durationMs: number;
    queueDurationMs?: number | null;
    output: OutputEntry[] | null;
}

export interface TestFailedEvent {
    event: "test_failed";
    timestamp: string;
    testCollection: string;
    testClass: string;
    displayName: string;
    durationMs: number;
    queueDurationMs?: number | null;
    error: string;
    exceptionType: string;
    stackTrace: string | null;
    screenshotPath: string | null;
    output: OutputEntry[] | null;
}

export interface TestSkippedEvent {
    event: "test_skipped";
    timestamp: string;
    testCollection: string;
    testClass: string;
    displayName: string;
    reason: string;
}

/**
 * Per-test annotation (Plane C). Single sink for `TestBase.Log*` calls
 * and curated infrastructure narration (broker, recording, mod-forwarded).
 */
export interface TestAnnotationEvent {
    event: "test_annotation";
    timestamp: string;
    testCollection: string;
    testClass: string;
    displayName: string;
    level: AnnotationLevel;
    source: AnnotationSource;
    message: string;
}

export interface DiagnosticEvent {
    event: "diagnostic";
    timestamp: string;
    source: string;
    level: string;
    message: string;
}

export interface ErrorEvent {
    event: "error";
    timestamp: string;
    message: string;
    stackTrace: string | null;
}

export interface SetupPhaseStartedEvent {
    event: "setup_phase_started";
    timestamp: string;
    category: string;
    phase: string;
    collection: string | null;
}

export interface SetupPhaseCompletedEvent {
    event: "setup_phase_completed";
    timestamp: string;
    category: string;
    phase: string;
    success: boolean;
    error: string | null;
    collection: string | null;
}

export interface SetupStepEvent {
    event: "setup_step";
    timestamp: string;
    category: string;
    step: string;
    status: "started" | "in_progress" | "completed" | "failed" | "aborted" | "warning";
    details: string | null;
    collection: string | null;
}

export interface ScreenshotEvent {
    event: "screenshot";
    timestamp: string;
    testCollection: string;
    testClass: string;
    displayName: string;
    screenshotPath: string;
    source: string;
}

export interface RecordingEvent {
    event: "recording";
    timestamp: string;
    testCollection: string;
    testClass: string;
    displayName: string;
    recordingPath: string;
    source: string;
    timelineOffset: number;
    wallClockDuration: number;
}

/**
 * A per-test recording was NOT produced for a given source. Mirrors C#
 * `Schema.Events.RecordingSkippedEvent`. `reason` is the snake_case wire form
 * of `RecordingSkipReason` (`artifacts_opted_out`, `retention_passed`, etc.).
 *
 * Source naming is the same as `RecordingEvent` for indexed sources, plus the
 * un-indexed `'client'` for class-level skips that apply to all client cards
 * for the test (e.g., `artifacts_opted_out`, `retention_passed`).
 */
export interface RecordingSkippedEvent {
    event: "recording_skipped";
    timestamp: string;
    testCollection: string;
    testClass: string;
    displayName: string;
    source: string;
    reason: RecordingSkipReason;
}

/** Snake-case wire form of C# `Schema.Events.RecordingSkipReason`. */
export type RecordingSkipReason =
    | "artifacts_opted_out"
    | "retention_passed"
    | "end_time_missing"
    | "recorder_never_started"
    | "recorder_missing"
    | "zero_duration"
    | "extraction_failed"
    | "finalize_deferred_failed";

/**
 * Per-test enrichment carrying fields known only to the test child process:
 * failure category/phase/preview/repro, server context, the full lifecycle
 * phase breakdown, and the latest failure-context dump (server state at failure).
 * Correlates with the xUnit-native test_passed/failed event by displayName; may
 * arrive slightly after it (same pattern as ScreenshotEvent).
 */
export interface TestEnrichmentEvent {
    event: "test_enrichment";
    timestamp: string;
    testCollection: string;
    testClass: string;
    displayName: string;
    outcome: "passed" | "failed" | "canceled";
    failureCategory: string | null;
    errorPreview: string | null;
    phase: string | null;
    reproCommand: string | null;
    serverKey: string | null;
    serverInstanceId: string | null;
    screenshotPath: string | null;
    testBodyMs: number;
    artifactsMs: number;
    cleanupMs: number;
    lastKeepDisposeMs: number;
    leaseReleaseMs: number;
    /** Latest FailureContext.DumpAsync result. Shape: { reason, ...extras, serverState?, diagnosticsError? }. */
    failureContext: Record<string, unknown> | null;
}

/**
 * Run identity announcement: runId, runDir, git, env, runtime, server-config plan.
 * Emitted once per run by the test child immediately after run-metadata.json is
 * written. Carries the same payload that's also serialized to disk; the runner
 * surfaces it to the UI for the "Run details" drawer.
 */
export interface RunMetadataEvent {
    event: "run_metadata";
    timestamp: string;
    runDir: string;
    data: RunMetadataPayload;
}

export interface RunMetadataPayload {
    schemaVersion: number;
    runId: string;
    timestamp: string;
    git?: {
        sha: string | null;
        branch: string | null;
        dirty: boolean;
    };
    env?: Record<string, string | null>;
    runtime?: {
        os: string | null;
        dotnet: string | null;
        docker: string | null;
    };
    testCount: number;
    serverConfigs?: {
        key: string;
        label: string;
        testCount: number;
        prestartedInstanceCount: number;
    }[];
}

export interface FlakyTestEntry {
    test: string;
    failRate: number;
    recentRuns: number;
}

export interface FlakyTestsEvent {
    event: "flaky_tests";
    timestamp: string;
    tests: FlakyTestEntry[];
}

export interface InstanceCreatedEvent {
    event: "instance_created";
    timestamp: string;
    instanceId: string;
    /** Docker host this instance runs on (`local`, `vps-1`, etc.). */
    hostId: string;
    instanceType: "server" | "client";
    serverKey: string;
    vncUrl: string | null;
    label: string | null;
}

export interface InstanceRecordingEvent {
    event: "instance_recording";
    timestamp: string;
    instanceId: string;
    recordingPath: string;
}

export interface InstanceStatusEvent {
    event:
        | "instance_leased"
        | "instance_returned"
        | "instance_disposed"
        | "instance_poisoned"
        | "instance_connected"
        | "instance_disconnected"
        | "instance_client_attached";
    timestamp: string;
    instanceId: string;
    testName?: string;
    reason?: string;
    serverInstanceId?: string;
    clientInstanceId?: string;
}

export interface InstanceStatsEvent {
    event: "instance_stats";
    timestamp: string;
    instanceId: string;
    /** Docker host this instance runs on. Threaded through unchanged from the producer. */
    hostId: string;
    cpuPercent: number;
    memoryMb: number;
    /** Docker daemon CPU count (e.g., 6 for 6-core). 0 if unknown. */
    cpuCount: number;
    /** Docker daemon total memory in MB. 0 if unknown. */
    totalMemoryMb: number;
    /** Game loop FPS (from /stats endpoint). Null if not yet available. */
    fps: number | null;
    /** Game ticks per second (from /stats endpoint). Null if not yet available. */
    tps: number | null;
    /** Average tick duration in ms (from /stats endpoint). Null if not yet available. */
    avgTickMs: number | null;
    /** Game process memory in MB (from /stats endpoint). Null if not yet available. */
    gameMemoryMb: number | null;
    /** Configured target TPS (from /stats endpoint). Null if not yet available. */
    targetTps: number | null;
    /** Configured target FPS (from /stats endpoint). Null if not yet available. */
    targetFps: number | null;
    /** GC collections per second (all generations). Null if unavailable. */
    gcRate: number | null;
    /** Number of actions queued for the game thread. Null if unavailable. */
    pendingActions: number | null;
    /** Rolling avg game thread wait time in ms. Null if unavailable. */
    gameThreadWaitMs: number | null;
    /** Network receive rate in bytes/sec. Null if unavailable. */
    netRxBytesPerSec: number | null;
    /** Network transmit rate in bytes/sec. Null if unavailable. */
    netTxBytesPerSec: number | null;
    /** Block I/O read rate in bytes/sec. Null if unavailable. */
    blkReadBytesPerSec: number | null;
    /** Block I/O write rate in bytes/sec. Null if unavailable. */
    blkWriteBytesPerSec: number | null;
    /** Container memory limit in MB (0 = no limit set). */
    memoryLimitMb: number;
}
