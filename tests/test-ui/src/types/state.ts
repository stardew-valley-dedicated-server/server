import type { VideoItem } from './video'
import type { RunMetadataPayload, FlakyTestEntry } from './events'

/** Mirrors C# TestRunState snapshot. Properties use camelCase to match JSON. */

/** Severity for a single annotation entry. Mirrors C# `Schema.Events.AnnotationLevel`. */
export type AnnotationLevel = 'info' | 'success' | 'warning' | 'error' | 'detail' | 'trace' | 'section'

/** Producer of an annotation. Mirrors C# `Schema.Events.AnnotationSource`. */
export type AnnotationSource = 'body' | 'broker' | 'recording' | 'mod' | 'setup'

/** A single entry in a test's or setup step's output log. */
export type OutputEntry =
  | { type: 'annotation'; ts: string; level: AnnotationLevel; source: AnnotationSource; message: string }
  | { type: 'screenshot'; ts: string; source: string; path: string }

export interface RunMetadataSnapshot {
  runDir: string
  data: RunMetadataPayload
}

export interface StateSnapshot {
  type: 'snapshot'
  status: 'pending' | 'running' | 'finished' | 'aborted'
  runStartTime: string | null
  runEndTime: string | null
  totalTests: number
  passed: number
  failed: number
  skipped: number
  notDispatched: number
  durationMs: number | null
  setupPhases: SetupPhaseSnapshot[]
  collections: CollectionSnapshot[]
  errors: ErrorSnapshot[]
  instances?: InstanceSnapshot[]
  runMetadata?: RunMetadataSnapshot | null
  flakyTests?: FlakyTestEntry[] | null
  /** Reason the run aborted, sourced from summary.json after WebSocket loss
   *  or a defensive re-fetch on run_finished. Null when status !== 'aborted'
   *  or when the artifact has not yet been read. */
  abortReason: string | null
  /** Reactive scrub-target (run-relative milliseconds), set by InfrastructureTimeline
   *  click-to-scrub. Consumed by InstanceInspect to scroll its connection history
   *  to the closest entry. Null when no scrub is active. */
  currentRunMs: number | null
}

export interface InstanceSnapshot {
  instanceId: string
  /**
   * Docker host this instance runs on (`local`, `vps-1`, etc.). Set by the
   * producer (broker) via `HostPool.Place` and threaded through unchanged.
   * Empty string when the event payload did not carry `hostId`. Current
   * producers always set it; the empty case is a defensive fallback from the
   * JSON converter for malformed/future events.
   */
  hostId?: string
  instanceType: 'server' | 'client'
  serverKey: string
  vncUrl: string | null
  label: string
  /**
   * Combined health + lease state. Known partial conflation per
   * `.claude/rules/universal/orthogonal-fields.md`: `poisoned` is health,
   * `idle`/`in_use` is lease, `starting` is lifecycle. The orthogonal `disposed`
   * + `connected` fields cover the other two dimensions. Splitting further is
   * deferred until a concrete bug requires it; in the meantime, treat `poisoned`
   * as overriding any prior `idle`/`in_use`.
   */
  status: 'starting' | 'idle' | 'in_use' | 'poisoned'
  connected: boolean
  /** True once the container has been torn down. Orthogonal to status so a
   *  poisoned container draining for recording extraction keeps its "poisoned"
   *  health signal while this flag gates the retained-for-recording overlay. */
  disposed: boolean
  currentTest: string | null
  /** Reason a poisoned instance was poisoned (health-check failure, etc.).
   *  Null for non-poisoned instances. */
  poisonReason: string | null
  connectedServerId: string | null
  setupSteps: SetupStepSnapshot[]
  setupStatus: 'pending' | 'running' | 'completed' | 'failed' | null
  history: InstanceHistoryEntry[]
  statsHistory?: StatsSnapshotEntry[]
  recordingPath?: string | null
}

export interface StatsSnapshotEntry {
  timestamp: string
  cpuPercent: number
  memoryMb: number
  cpuCount: number
  totalMemoryMb: number
  fps: number | null
  tps: number | null
  avgTickMs: number | null
  gameMemoryMb: number | null
  targetTps: number | null
  targetFps: number | null
  gcRate: number | null
  pendingActions: number | null
  gameThreadWaitMs: number | null
  netRxBytesPerSec: number | null
  netTxBytesPerSec: number | null
  blkReadBytesPerSec: number | null
  blkWriteBytesPerSec: number | null
  memoryLimitMb: number
}

export interface InstanceHistoryEntry {
  timestamp: string
  event: string
  testName: string | null
  reason: string | null
  serverInstanceId: string | null
  clientInstanceId: string | null
}

export interface CollectionSnapshot {
  name: string
  classes: ClassSnapshot[]
}

export interface ClassSnapshot {
  name: string
  tests: TestSnapshot[]
}

export interface TestSnapshot {
  // Collection is implicit in the tree hierarchy; methodName can be derived from
  // displayName when needed. Both removed from the wire format.
  className: string
  displayName: string
  status: 'pending' | 'queued' | 'running' | 'passed' | 'failed' | 'canceled' | 'skipped' | 'notDispatched' | 'aborted'
  durationMs: number | null
  queueDurationMs: number | null
  output: OutputEntry[] | null
  errorMessage: string | null
  errorType: string | null
  stackTrace: string | null
  recordings: VideoItem[] | null
  /**
   * Per-source skip reasons. Key is the source slug (`'server'`, the un-indexed
   * `'client'`, or an indexed `'client_2'`/`'client_3'`/…); value is the
   * snake_case `RecordingSkipReason`. Populated by `recording_skipped` events
   * and the `BuildSnapshot` projection from the runner. null when no source has
   * been skipped.
   */
  recordingSkipReasons: Record<string, string> | null
  lifecycle: {
    testMs: number
    cleanupMs: number
    artifactsMs: number
    lastKeepDisposeMs: number
    leaseReleaseMs: number
  } | null
  skipReason: string | null
  discoveryOrder: number | null
  executionOrder: number | null
  startTime: string | null
  runningStartTime: string | null
  usedInstances: string[] | null
  // Enrichment fields populated by test_enrichment event (arrives slightly
  // after the xUnit-native test_passed/failed). null until enrichment lands.
  failureCategory: string | null
  errorPreview: string | null
  phase: string | null
  reproCommand: string | null
  serverKey: string | null
  serverInstanceId: string | null
  /** Latest FailureContext.DumpAsync result, or null if no dump captured. */
  failureContext: Record<string, unknown> | null
}

export interface SetupPhaseSnapshot {
  category: string
  phase: string
  collectionName: string | null
  status: 'running' | 'completed' | 'failed' | 'aborted'
  startTime: string | null
  endTime: string | null
  error: string | null
  steps: SetupStepSnapshot[]
}

export interface SetupStepSnapshot {
  step: string
  status: 'started' | 'in_progress' | 'completed' | 'failed' | 'aborted' | 'warning'
  details: string | null
  output: OutputEntry[] | null
  timestamp: string
}

export interface ErrorSnapshot {
  message: string
  stackTrace: string | null
  timestamp: string
}
