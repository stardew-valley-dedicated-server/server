import { reactive, ref, shallowRef, triggerRef } from 'vue'
import type { Ref, ShallowRef } from 'vue'
import type { StateSnapshot, CollectionSnapshot, TestSnapshot, SetupPhaseSnapshot, SetupStepSnapshot, ErrorSnapshot, StatsSnapshotEntry, InstanceSnapshot } from '../types/state'
import type { TestEvent } from '../types/events'
import { useWebSocket } from './useWebSocket'
import { useElapsedTimer } from './useElapsedTimer'
import { useSelectionState } from './useSelectionState'
import { useStatusCounts } from './useStatusCounts'
import { useScreenshotCache } from './useScreenshotCache'
import { createLogger } from '../utils/logger'

const log = createLogger('Store')
import type { StatusCountMap } from './useStatusCounts'

export type { StatusCountMap } from './useStatusCounts'

export interface TestStore {
  state: StateSnapshot
  /**
   * The test collections tree as a shallow ref. Vue does NOT track mutations
   * to objects inside this array; only replacement of the array itself or
   * explicit triggerRef() calls cause dependents to re-evaluate. This is the
   * core performance optimization: high-frequency mutations to test.output,
   * test.errorMessage etc. don't trigger tree re-renders.
   */
  collections: ShallowRef<CollectionSnapshot[]>
  /**
   * Bumped when the selected test's content (output, error, screenshots)
   * changes. OutputPanel watches this to know when to re-read the test object.
   */
  selectedTestVersion: Ref<number>
  selectedTest: TestSnapshot | null
  selectedStep: SetupStepSnapshot | null
  selectedError: ErrorSnapshot | null
  isConnected: boolean
  connectionError: string | null
  isReportMode: boolean
  selectTest: (test: TestSnapshot | null) => void
  selectStep: (step: SetupStepSnapshot | null) => void
  selectError: (error: ErrorSnapshot | null) => void
  /** Pre-computed status counts, updated incrementally. */
  statusCounts: StatusCountMap
  /** True when the run has ended (finished or aborted). Components should stop all activity. */
  runDone: boolean
  /** Ref to the elapsed timer DOM element. StatusBar binds this for rAF-driven updates. */
  elapsedTimerRef: Ref<HTMLElement | null>
  /** Final elapsed ms (set once when run finishes). Null while running. */
  elapsedMs: number | null
  /** Ephemeral container stats (CPU/memory) and game stats, keyed by instanceId. Updated every ~1s. */
  instanceStats: Map<string, { cpuPercent: number; memoryMb: number; cpuCount: number; totalMemoryMb: number; fps: number | null; tps: number | null; avgTickMs: number | null; gameMemoryMb: number | null; targetTps: number | null; targetFps: number | null; gcRate: number | null; pendingActions: number | null; gameThreadWaitMs: number | null; netRxBytesPerSec: number | null; netTxBytesPerSec: number | null; blkReadBytesPerSec: number | null; blkWriteBytesPerSec: number | null; memoryLimitMb: number }>
  /** Stats history, keyed by instanceId. Unbounded; kept for the entire run. */
  instanceStatsHistory: Map<string, StatsSnapshotEntry[]>
  /** Monotonically increasing counter, bumped on every instance_stats event. Cheap change signal. */
  statsVersion: Ref<number>
  /**
   * Resolve a screenshot artifact path to a displayable URL.
   * Returns a cached blob URL if available, otherwise falls back to the
   * live `/artifacts/` endpoint. Screenshots are eagerly fetched and cached
   * as blob URLs when events arrive, so they survive runner shutdown.
   */
  screenshotSrc: (path: string) => string
  /** Archived instances from a finished/aborted run, inspectable but no live VNC. */
  stoppedInstances: InstanceSnapshot[]
  /** Find a global setup phase step by name (for cross-linking from instance steps). */
  findGlobalStep: (stepName: string) => SetupStepSnapshot | null
  /** Find a test by display name (for cross-linking from history). */
  findTest: (displayName: string) => TestSnapshot | null
  /** Send a runtime control command to the runner. WS-first, REST fallback. */
  sendCommand: (cmd: 'stop') => Promise<void>
}

function createEmptyState(): StateSnapshot {
  return {
    type: 'snapshot',
    status: 'pending',
    runStartTime: null,
    runEndTime: null,
    totalTests: 0,
    passed: 0,
    failed: 0,
    skipped: 0,
    notDispatched: 0,
    durationMs: null,
    setupPhases: [],
    collections: [],
    errors: [],
    runMetadata: null,
    flakyTests: null,
    abortReason: null,
    currentRunMs: null,
  }
}

/** Strip the absolute TestResults/ prefix from a runDir path, yielding the
 *  URL-relative form usable under /artifacts/. The runner emits absolute paths
 *  (Windows or POSIX); we look for the last `/runs/` or `\runs\` segment and
 *  return everything from `runs/` onward. */
export function relativeRunPath(runDir: string): string | null {
  const norm = runDir.replace(/\\/g, '/')
  const idx = norm.lastIndexOf('/runs/')
  if (idx < 0) return null
  return norm.slice(idx + 1) // drop leading '/'
}

export function useTestStore(): TestStore {
  const state = reactive<StateSnapshot>(createEmptyState())
  const { selectedTest, selectedStep, selectedError, selectedTestVersion, selectTest, selectStep, selectError } = useSelectionState()
  const isReportMode = ref(false)

  // ── Shallow collections: the core perf optimization ──
  // The collections tree is stored in a shallowRef. Vue does NOT deep-track
  // mutations to objects inside it. We call triggerRef(collections) explicitly
  // when tree-visible properties change (status, duration, structure).
  // High-frequency mutations (test.output, test.errorMessage) do NOT trigger
  // tree re-renders; only OutputPanel's selectedTestVersion tracks those.
  const collections = shallowRef<CollectionSnapshot[]>([])

  /** Signal that the tree's render-visible properties changed. */
  function notifyTreeChanged() {
    triggerRef(collections)
  }

  // ── Status counts (extracted composable) ──
  const { statusCounts, transitionStatus, rebuildStatusCounts: _rebuildCounts } = useStatusCounts()

  function rebuildStatusCounts() {
    _rebuildCounts(collections.value)
  }

  // ── Screenshot cache (extracted composable) ──
  const { screenshotSrc, cacheScreenshot, cacheScreenshotsFromOutput } = useScreenshotCache()


  // ── Stopped instances archive (survives run_finished / aborted) ──
  const stoppedInstances = reactive<InstanceSnapshot[]>([])


  let nextExecutionOrder = 0

  // ── Ephemeral container stats (CPU/memory), not part of snapshot ──
  // Includes Docker daemon limits for relative threshold computation.
  const instanceStats = reactive(new Map<string, { cpuPercent: number; memoryMb: number; cpuCount: number; totalMemoryMb: number; fps: number | null; tps: number | null; avgTickMs: number | null; gameMemoryMb: number | null; targetTps: number | null; targetFps: number | null; gcRate: number | null; pendingActions: number | null; gameThreadWaitMs: number | null; netRxBytesPerSec: number | null; netTxBytesPerSec: number | null; blkReadBytesPerSec: number | null; blkWriteBytesPerSec: number | null; memoryLimitMb: number }>())
  const instanceStatsHistory = reactive(new Map<string, StatsSnapshotEntry[]>())
  const statsVersion = ref(0)

  // ── Elapsed timer (extracted composable) ──
  const { elapsedTimerRef, elapsedMs, startElapsedTimer: _startTimer, stopElapsedTimer: _stopTimer } = useElapsedTimer()

  // ── Abort reason fetch (one-shot per run) ──
  // Aborted runs do not fire run_finished; the durable signal is summary.json on
  // disk. This is also called defensively from run_finished to cover the rare
  // race where the runner crashes between OnRunFinished and WriteRunArtifacts.
  let _summaryFetched = false
  async function fetchSummaryAndApplyAbort(): Promise<void> {
    if (_summaryFetched) return
    _summaryFetched = true
    const runDir = state.runMetadata?.runDir
    if (!runDir) return
    const relative = relativeRunPath(runDir)
    if (!relative) return
    try {
      const res = await fetch(`/artifacts/${relative}/summary.json`)
      if (!res.ok) return
      const summary = await res.json() as { aborted?: boolean; abortReason?: string | null }
      if (summary.aborted === true) {
        state.abortReason = summary.abortReason ?? null
        if (state.status !== 'aborted') state.status = 'aborted'
      }
    } catch {
      // Artifact may not yet exist (timing) or runner is fully gone; harmless.
    }
  }

  function hydrateFromSnapshot(snapshot: StateSnapshot) {
    // Assign scalar properties directly. Vue's reactive proxy tracks these.
    state.type = snapshot.type
    state.status = snapshot.status
    state.runStartTime = snapshot.runStartTime
    state.runEndTime = snapshot.runEndTime
    state.totalTests = snapshot.totalTests
    state.passed = snapshot.passed
    state.failed = snapshot.failed
    state.skipped = snapshot.skipped
    state.notDispatched = snapshot.notDispatched ?? 0
    state.durationMs = snapshot.durationMs

    // For arrays, mutate in-place (splice+push) rather than replacing the reference.
    // This ensures Vue's reactivity system properly tracks the deep changes.
    state.setupPhases.splice(0, state.setupPhases.length, ...snapshot.setupPhases)

    // Collections use shallowRef. Replace the array to trigger dependents
    collections.value = snapshot.collections
    // Build className -> collection name mapping for collection name resolution
    for (const col of collections.value) {
      for (const cls of col.classes) {
        if (!(cls.name in classToCollection)) classToCollection[cls.name] = col.name
      }
    }
    state.errors.splice(0, state.errors.length, ...snapshot.errors)

    if (snapshot.instances) {
      if (!state.instances) state.instances = []
      // Ensure history and used_instances fields exist (missing in older snapshots/mock data)
      for (const inst of snapshot.instances) {
        if (!inst.history) inst.history = []
        if (inst.disposed === undefined) inst.disposed = false
      }

      // When hydrating a finished/aborted run, all instances are past containers.
      // put them in stoppedInstances so the UI shows them correctly (not as live VNC).
      // For live runs, clear stoppedInstances to prevent duplicates on WebSocket reconnect
      // (stopped instances from the previous connection would otherwise persist).
      if (state.status === 'finished' || state.status === 'aborted') {
        state.instances.splice(0, state.instances.length)
        stoppedInstances.splice(0, stoppedInstances.length, ...snapshot.instances)
      } else {
        // Live run: disposed-and-already-recorded instances belong in Past Containers;
        // disposed-but-still-finalizing and live ones stay on the main grid.
        const live: typeof snapshot.instances = []
        const past: typeof snapshot.instances = []
        for (const inst of snapshot.instances) {
          if (inst.disposed && inst.recordingPath) past.push(inst)
          else live.push(inst)
        }
        state.instances.splice(0, state.instances.length, ...live)
        stoppedInstances.splice(0, stoppedInstances.length, ...past)
      }

      // Hydrate stats history from snapshot so charts show full history
      // even when loading mid-run
      for (const inst of snapshot.instances) {
        if (inst.statsHistory && inst.statsHistory.length > 0) {
          instanceStatsHistory.set(inst.instanceId, [...inst.statsHistory])
        }
      }
    }

    // Sync nextExecutionOrder from snapshot so new events get correct ordering
    let maxExecOrder = -1
    for (const col of collections.value)
      for (const cls of col.classes)
        for (const test of cls.tests) {
          if (test.executionOrder != null && test.executionOrder > maxExecOrder)
            maxExecOrder = test.executionOrder
          cacheScreenshotsFromOutput(test.output)
        }
    nextExecutionOrder = maxExecOrder + 1

    // Rebuild status counts from the full tree
    rebuildStatusCounts()

    // Start elapsed timer if running
    if (state.status === 'running' && state.runStartTime) {
      startElapsedTimer()
    } else if (state.durationMs != null) {
      stopElapsedTimer(state.durationMs)
    }

    // Hydration onto a finished/aborted run: pull summary.json to populate
    // abortReason. Cheap (single 304 on subsequent reconnects).
    if (state.status === 'aborted' || state.status === 'finished') {
      void fetchSummaryAndApplyAbort()
    }

    // Auto-select a sensible test on a fresh hydrate so the OutputPanel shows
    // content immediately instead of an empty state. Only acts when nothing is
    // selected — preserves user's selection across reconnect snapshots.
    // Priority: failed > running > most-recently-finished.
    if (!selectedTest.value) {
      const candidate = findInitialSelection()
      if (candidate) selectTest(candidate)
    }

    // Sync browser tab title with current state
    updateTitleFromState()
  }

  function findInitialSelection(): TestSnapshot | null {
    let firstFailed: TestSnapshot | null = null
    let firstRunning: TestSnapshot | null = null
    let mostRecent: TestSnapshot | null = null
    let mostRecentOrder = -1
    for (const col of collections.value) {
      for (const cls of col.classes) {
        for (const test of cls.tests) {
          if (!firstFailed && test.status === 'failed') firstFailed = test
          if (!firstRunning && test.status === 'running') firstRunning = test
          if (test.executionOrder != null && test.executionOrder > mostRecentOrder) {
            mostRecentOrder = test.executionOrder
            mostRecent = test
          }
        }
      }
    }
    return firstFailed ?? firstRunning ?? mostRecent
  }

  function updateTitleFromState() {
    switch (state.status) {
      case 'pending': {
        // Show the most recent running setup phase, or generic "Setting up"
        const runningPhase = [...state.setupPhases].reverse().find(p => p.status === 'running')
        if (runningPhase) {
          document.title = `\u25CC ${runningPhase.phase} \u2013 Test Runner`
        } else if (state.totalTests > 0) {
          document.title = `\u25CC ${state.totalTests} tests ready \u2013 Test Runner`
        } else {
          document.title = `\u25CC Setting up \u2013 Test Runner`
        }
        break
      }
      case 'running':
        // Title spinner is handled by the elapsed timer's rAF loop
        document.title = `\u25CC Running \u2013 Test Runner`
        break
      case 'finished': {
        if (statusCounts.failed > 0) {
          document.title = `\u2718 ${statusCounts.failed} failed \u2013 Test Runner`
        } else if (statusCounts.canceled > 0) {
          document.title = `\u26A0 Canceled \u2013 Test Runner`
        } else if (statusCounts.passed > 0) {
          document.title = `\u2714 ${statusCounts.passed} passed \u2013 Test Runner`
        } else {
          document.title = `\u26A0 No tests ran \u2013 Test Runner`
        }
        break
      }
      case 'aborted':
        document.title = `\u26A0 Disconnected \u2013 Test Runner`
        break
    }
  }

  function startElapsedTimer() {
    if (state.runStartTime) _startTimer(state.runStartTime)
  }

  function stopElapsedTimer(finalMs?: number) {
    _stopTimer(finalMs)
  }

  function makePhaseKey(category: string, phase: string, collection?: string | null): string {
    return collection ? `${collection}:${category}:${phase}` : `${category}:${phase}`
  }

  function applyEvent(event: TestEvent) {
    switch (event.event) {
      case 'populate_tests':
        state.totalTests = event.testCount
        // Hydrate the full test tree from the event so the UI shows tests
        // immediately, even if the client connected after PopulateTests ran
        // and the initial snapshot was empty.
        if (event.collections && collections.value.length === 0) {
          for (const col of event.collections) {
            const colSnapshot = { name: col.name, classes: [] as typeof collections.value[0]['classes'] }
            for (const cls of col.classes) {
              if (!(cls.name in classToCollection)) classToCollection[cls.name] = col.name
              colSnapshot.classes.push({
                name: cls.name,
                tests: cls.tests.map(t => ({
                  className: t.className,
                  displayName: t.displayName,
                  status: (t.status as 'pending'),
                  durationMs: null,
                  queueDurationMs: null,
                  output: null,
                  errorMessage: null,
                  errorType: null,
                  stackTrace: null,
                  recordings: null,
                  recordingSkipReasons: null,
                  lifecycle: null,
                  skipReason: null,
                  discoveryOrder: t.discoveryOrder ?? null,
                  executionOrder: null,
                  startTime: null,
                  runningStartTime: null,
                  usedInstances: null,
                  failureCategory: null,
                  errorPreview: null,
                  phase: null,
                  reproCommand: null,
                  serverKey: null,
                  serverInstanceId: null,
                  failureContext: null,
                }))
              })
            }
            collections.value = [...collections.value, colSnapshot]
          }
          rebuildStatusCounts()
          markTreeDirty()
        }
        updateTitleFromState()
        break

      case 'run_started':
        state.status = 'running'
        state.runStartTime = event.timestamp
        state.totalTests = event.testCount
        state.abortReason = null
        state.currentRunMs = null
        _summaryFetched = false
        stoppedInstances.splice(0, stoppedInstances.length)
        startElapsedTimer()
        updateTitleFromState()
        break

      case 'discovery_complete':
        // Don't overwrite total_tests. populate_tests already set the correct
        // expanded count. Discovery reports unexpanded method count.
        break

      case 'run_finished':
        state.status = 'finished'
        state.runEndTime = event.timestamp
        state.durationMs = event.durationMs
        stopElapsedTimer(event.durationMs)

        // Keep WebSocket open after run_finished. Recording extraction events
        // (from broker disposal in TestSummaryFixture) arrive after this point.
        // The connection closes naturally when the test runner process exits.
        // onDisconnect checks state.status and stops reconnecting if already finished.

        // Mark remaining pending/queued tests as notDispatched (matching backend
        // semantics — they were enumerated but never dispatched). Running tests
        // become canceled (test started, didn't terminalize).
        for (const col of collections.value) {
          for (const cls of col.classes) {
            for (const test of cls.tests) {
              if (test.status === 'pending' || test.status === 'queued') {
                transitionStatus(test.status, 'notDispatched')
                test.status = 'notDispatched'
                test.skipReason = 'Not dispatched'
              } else if (test.status === 'running') {
                transitionStatus('running', 'canceled')
                test.status = 'canceled'
                test.errorMessage = 'Test was interrupted when the run ended'
              }
            }
          }
        }

        // Sync scalar counts from the authoritative statusCounts map
        state.passed = statusCounts.passed
        state.failed = statusCounts.failed
        state.skipped = statusCounts.skipped
        state.notDispatched = statusCounts.notDispatched

        // Auto-select first failed test if nothing failed is currently selected
        if (state.failed > 0 && selectedTest.value?.status !== 'failed') {
          outer: for (const col of collections.value) {
            for (const cls of col.classes) {
              for (const test of cls.tests) {
                if (test.status === 'failed') {
                  selectTest(test)
                  break outer
                }
              }
            }
          }
        }

        // Keep the larger of execution total and discovered total
        if (event.totalTests > state.totalTests)
          state.totalTests = event.totalTests

        // Archive remaining live instances for post-run inspection (without VNC iframes)
        // Append to stoppedInstances (some may already be there from mid-run disposals)
        if (state.instances?.length) {
          stoppedInstances.push(...state.instances)
          state.instances.splice(0, state.instances.length)
        }
        // Keep stats/history; stoppedInstances need them for inspect modal

        // Finalize any still-running setup phases and steps
        for (const phase of state.setupPhases) {
          if (phase.status === 'running') {
            phase.status = state.failed > 0 ? 'failed' : 'completed'
            phase.endTime = event.timestamp
            for (const step of phase.steps) {
              if (step.status === 'started' || step.status === 'in_progress') {
                step.status = 'failed'
              }
            }
          }
        }

        markTreeDirty()
        updateTitleFromState()
        // Defensive: if the runner crashed between OnRunFinished and
        // WriteRunArtifacts, summary.json may carry aborted=true even though
        // run_finished arrived. Fire-and-forget; ignored on graceful runs.
        void fetchSummaryAndApplyAbort()
        break

      case 'run_aborted':
        // Live abort signal from the runner (Ctrl+C in terminal, or any
        // setup-phase failure). The runner broadcasts this immediately so
        // the UI doesn't have to wait for the WebSocket reconnect-retry
        // exhaustion path (onReconnectFailed → markAborted) — which can
        // take several seconds while Kestrel shuts down. markAborted is
        // idempotent and a no-op if the run already ended.
        markAborted()
        markTreeDirty()
        break

      case 'test_annotation': {
        const test = findOrCreateTest(event.testCollection, event.testClass, event.displayName)
        if (test) {
          test.output = test.output || []
          test.output.push({
            type: 'annotation',
            ts: event.timestamp,
            level: event.level,
            source: event.source,
            message: event.message,
          })
          markSelectedContentDirty(test)
        }
        break
      }

      case 'test_started': {
        const test = findOrCreateTest(event.testCollection, event.testClass, event.displayName)
        if (test) {
          const oldStatus = test.status
          test.status = 'queued'
          test.startTime = event.timestamp
          transitionStatus(oldStatus, 'queued')
          markTreeDirty()
          markSelectedContentDirty(test)
        }
        break
      }

      case 'test_running': {
        const test = findOrCreateTest(event.testCollection, event.testClass, event.displayName)
        if (test) {
          const oldStatus = test.status
          test.status = 'running'
          test.runningStartTime = event.timestamp
          test.executionOrder = event.executionOrder ?? nextExecutionOrder++
          transitionStatus(oldStatus, 'running')
          markTreeDirty()
          markSelectedContentDirty(test)
          // Auto-select first running test if nothing selected
          if (!selectedTest.value) {
            selectTest(test)
          }
        }
        break
      }

      case 'test_passed': {
        const test = findOrCreateTest(event.testCollection, event.testClass, event.displayName)
        if (test) {
          const oldStatus = test.status
          test.status = 'passed'
          test.durationMs = event.durationMs
          test.queueDurationMs = event.queueDurationMs ?? null
          // Pipe-accumulated output has timestamps; prefer it over event.output.
          // Fall back to event.output for reconnecting clients that missed pipe events.
          if (event.output && !test.output) test.output = event.output
          cacheScreenshotsFromOutput(test.output)
          transitionStatus(oldStatus, 'passed')
          markTreeDirty()
          markSelectedContentDirty(test)
        }
        break
      }

      case 'test_failed': {
        const test = findOrCreateTest(event.testCollection, event.testClass, event.displayName)
        const isCanceled = event.exceptionType === 'System.OperationCanceledException'
          || event.exceptionType === 'System.Threading.Tasks.TaskCanceledException'
        if (test) {
          const oldStatus = test.status
          const newStatus = isCanceled ? 'canceled' : 'failed'
          test.status = newStatus
          test.durationMs = event.durationMs
          test.queueDurationMs = event.queueDurationMs ?? null
          // Pipe-accumulated output has timestamps; prefer it over event.output.
          // Fall back to event.output for reconnecting clients that missed pipe events.
          if (event.output && !test.output) test.output = event.output
          cacheScreenshotsFromOutput(test.output)
          test.errorMessage = event.error
          test.errorType = event.exceptionType
          test.stackTrace = event.stackTrace
          // Screenshots arrive separately via the screenshot event and are
          // appended to test.output. The xUnit-native test_failed.screenshotPath
          // is cached here so the eager screenshot-cache populates before the
          // separate event arrives, but the snapshot itself only stores it in output.
          if (event.screenshotPath) cacheScreenshot(event.screenshotPath)
          transitionStatus(oldStatus, newStatus)
          markTreeDirty()
          markSelectedContentDirty(test)
          // Auto-select failed test (but not canceled ones)
          if (!isCanceled) selectTest(test)
        }
        break
      }

      case 'test_skipped': {
        const test = findOrCreateTest(event.testCollection, event.testClass, event.displayName)
        if (test) {
          const oldStatus = test.status
          test.status = 'skipped'
          test.skipReason = event.reason
          transitionStatus(oldStatus, 'skipped')
          markTreeDirty()
          markSelectedContentDirty(test)
        }
        break
      }

      case 'setup_phase_started': {
        // Use collection as the identity key (matches backend's MakePhaseKey)
        const key = makePhaseKey(event.category, event.phase, event.collection)

        const existingPhase = state.setupPhases.find(p =>
          makePhaseKey(p.category, p.phase, p.collectionName) === key
        )

        if (existingPhase) {
          // Same phase restarting (e.g., shared phase re-run). Reset it.
          // Clear selectedStep if it pointed at one of the old steps.
          if (selectedStep.value && existingPhase.steps.includes(selectedStep.value)) {
            selectedStep.value = null
          }
          existingPhase.status = 'running'
          existingPhase.startTime = event.timestamp
          existingPhase.endTime = null
          existingPhase.error = null
          existingPhase.steps = []
        } else {
          state.setupPhases.push({
            category: event.category,
            phase: event.phase,
            collectionName: event.collection ?? null,
            status: 'running',
            startTime: event.timestamp,
            endTime: null,
            error: null,
            steps: []
          })
        }

        updateTitleFromState()
        break
      }

      case 'setup_phase_completed': {
        const key = makePhaseKey(event.category, event.phase, event.collection)

        const phase = state.setupPhases.find(p =>
          makePhaseKey(p.category, p.phase, p.collectionName) === key
        )

        if (phase) {
          phase.status = event.success ? 'completed' : 'failed'
          phase.endTime = event.timestamp
          phase.error = event.error

          // When the phase failed, finalize any still-running steps
          if (!event.success) {
            for (const step of phase.steps) {
              if (step.status === 'started' || step.status === 'in_progress') {
                step.status = 'failed'
              }
            }
          }
        }
        // Update matching instances' setup_status
        if (event.collection && state.instances) {
          for (const inst of state.instances) {
            if (inst.serverKey === event.collection || inst.instanceId === event.collection) {
              inst.setupStatus = event.success ? 'completed' : 'failed'
            }
          }
        }
        break
      }

      case 'setup_step': {
        const phase = findActiveSetupPhase(event.category, event.collection)
        if (phase) {
          const existing = phase.steps.find(s => s.step === event.step)
          if (existing) {
            existing.status = event.status
            existing.details = event.details
            existing.timestamp = event.timestamp
            if (event.status === 'in_progress' && event.details) {
              existing.output = existing.output || []
              existing.output.push({ type: 'annotation', ts: event.timestamp, level: 'info', source: 'setup', message: event.details })
            }
            markSelectedStepDirty(existing)
          } else {
            const newStep: SetupStepSnapshot = {
              step: event.step,
              status: event.status,
              details: event.details,
              output: null,
              timestamp: event.timestamp
            }
            phase.steps.push(newStep)
          }
        }
        // Also route to matching instances by server_key or instance_id === event.collection
        if (event.collection && state.instances) {
          for (const inst of state.instances) {
            if (inst.serverKey !== event.collection && inst.instanceId !== event.collection) continue
            if (inst.setupStatus === null) inst.setupStatus = 'running'
            const existingStep = inst.setupSteps.find(s => s.step === event.step)
            if (existingStep) {
              existingStep.status = event.status
              existingStep.details = event.details
              existingStep.timestamp = event.timestamp
              if (event.status === 'in_progress' && event.details) {
                existingStep.output = existingStep.output || []
                existingStep.output.push({ type: 'annotation', ts: event.timestamp, level: 'info', source: 'setup', message: event.details })
              }
            } else {
              inst.setupSteps.push({
                step: event.step,
                status: event.status,
                details: event.details,
                output: null,
                timestamp: event.timestamp
              })
            }
          }
        }
        break
      }

      case 'screenshot': {
        cacheScreenshot(event.screenshotPath)
        const test = findOrCreateTest(event.testCollection, event.testClass, event.displayName)
        if (test) {
          test.output = test.output || []
          test.output.push({ type: 'screenshot', ts: event.timestamp, source: event.source, path: event.screenshotPath })
          markSelectedContentDirty(test)
        }
        break
      }

      case 'recording': {
        cacheScreenshot(event.recordingPath)
        const test = findOrCreateTest(event.testCollection, event.testClass, event.displayName)
        if (test) {
          test.recordings = test.recordings || []
          test.recordings.push({
            source: event.source,
            path: event.recordingPath,
            timelineOffset: event.timelineOffset,
            wallClockDuration: event.wallClockDuration,
          })
          markSelectedContentDirty(test)
        }
        break
      }

      case 'recording_skipped': {
        const test = findOrCreateTest(event.testCollection, event.testClass, event.displayName)
        if (test) {
          test.recordingSkipReasons = test.recordingSkipReasons || {}
          test.recordingSkipReasons[event.source] = event.reason
          markSelectedContentDirty(test)
        }
        break
      }

      case 'run_metadata': {
        state.runMetadata = { runDir: event.runDir, data: event.data }
        break
      }

      case 'flaky_tests': {
        state.flakyTests = event.tests
        break
      }

      case 'test_enrichment': {
        const test = findOrCreateTest(event.testCollection, event.testClass, event.displayName)
        if (test) {
          test.failureCategory = event.failureCategory
          test.errorPreview = event.errorPreview
          test.phase = event.phase
          test.reproCommand = event.reproCommand
          test.serverKey = event.serverKey
          test.serverInstanceId = event.serverInstanceId
          test.failureContext = event.failureContext
          // Eagerly cache an enrichment-side screenshot path; the snapshot only
          // stores screenshots in test.output via the dedicated screenshot event.
          if (event.screenshotPath) cacheScreenshot(event.screenshotPath)
          test.lifecycle = {
            testMs: event.testBodyMs,
            cleanupMs: event.cleanupMs,
            artifactsMs: event.artifactsMs,
            lastKeepDisposeMs: event.lastKeepDisposeMs,
            leaseReleaseMs: event.leaseReleaseMs,
          }
          markSelectedContentDirty(test)
        }
        break
      }

      case 'instance_created': {
        if (!state.instances) state.instances = []
        // Deduplicate: if an instance with the same ID already exists, preserve history/setup
        const existingIdx = state.instances.findIndex(i => i.instanceId === event.instanceId)
        const existing = existingIdx >= 0 ? state.instances[existingIdx] : null
        const newInst: import('../types/state').InstanceSnapshot = {
          instanceId: event.instanceId,
          hostId: event.hostId,
          instanceType: event.instanceType,
          serverKey: event.serverKey,
          vncUrl: event.vncUrl,
          label: event.label ?? '',
          status: (event.vncUrl ? 'idle' : 'starting') as 'starting' | 'idle',
          connected: false,
          disposed: existing?.disposed ?? false,
          currentTest: null,
          poisonReason: null,
          connectedServerId: null,
          setupSteps: existing?.setupSteps ?? [],
          setupStatus: existing?.setupStatus ?? null,
          history: existing?.history ?? [{ timestamp: event.timestamp, event: 'created', testName: null, reason: null, serverInstanceId: null, clientInstanceId: null }],
          recordingPath: existing?.recordingPath ?? null
        }
        if (existingIdx >= 0) {
          state.instances[existingIdx] = newInst
        } else {
          state.instances.push(newInst)
        }
        break
      }

      case 'instance_leased': {
        const inst = state.instances?.find(i => i.instanceId === event.instanceId)
        if (inst) {
          inst.status = 'in_use'
          inst.currentTest = event.testName ?? null
          inst.connectedServerId = event.serverInstanceId ?? null
          inst.history.push({ timestamp: event.timestamp, event: 'leased', testName: event.testName ?? null, reason: null, serverInstanceId: event.serverInstanceId ?? null, clientInstanceId: null })
        }
        // Track which instances ran this test
        if (event.testName) {
          const test = findTestByDisplayName(event.testName)
          if (test) {
            if (!test.usedInstances) test.usedInstances = []
            if (!test.usedInstances.includes(event.instanceId))
              test.usedInstances.push(event.instanceId)
            markSelectedContentDirty(test)
          }
        }
        break
      }

      case 'instance_returned': {
        const inst = state.instances?.find(i => i.instanceId === event.instanceId)
        if (inst) {
          inst.history.push({ timestamp: event.timestamp, event: 'returned', testName: null, reason: null, serverInstanceId: null, clientInstanceId: null })
          inst.status = 'idle'
          inst.connected = false
          inst.currentTest = null
          inst.connectedServerId = null
        }
        break
      }

      case 'instance_disposed': {
        if (state.status !== 'finished' && state.status !== 'aborted') {
          // Live run: flag as disposed but keep on the main grid so VncFrame can
          // show the "finalizing recording" overlay until instance_recording arrives.
          // Status is left alone (poisoned containers stay poisoned through the drain window).
          // run_finished archival (above) sweeps any stragglers into stoppedInstances.
          const inst = state.instances?.find(i => i.instanceId === event.instanceId)
          if (inst) {
            inst.history.push({ timestamp: event.timestamp, event: 'disposed', testName: null, reason: null, serverInstanceId: null, clientInstanceId: null })
            inst.disposed = true
            inst.connected = false
            inst.currentTest = null
            inst.connectedServerId = null
          }
        }
        // Post-run: ignore, instance already archived in stoppedInstances
        break
      }

      case 'instance_recording': {
        // Instance may be in active or stopped list (recording event arrives after disposal)
        if (state.instances) {
          const idx = state.instances.findIndex(i => i.instanceId === event.instanceId)
          if (idx >= 0) {
            const inst = state.instances[idx]
            inst.recordingPath = event.recordingPath
            // Migrate disposed-and-retained instances into Past Containers now that the recording is available.
            if (inst.disposed) {
              const [removed] = state.instances.splice(idx, 1)
              stoppedInstances.push(removed)
            }
            break
          }
        }
        const stopped = stoppedInstances.find(i => i.instanceId === event.instanceId)
        if (stopped) {
          stopped.recordingPath = event.recordingPath
        }
        break
      }

      case 'instance_poisoned': {
        const inst = state.instances?.find(i => i.instanceId === event.instanceId)
        if (inst) {
          inst.history.push({ timestamp: event.timestamp, event: 'poisoned', testName: null, reason: event.reason ?? null, serverInstanceId: null, clientInstanceId: null })
          inst.status = 'poisoned'
          // Poisoned containers are no longer running a test — clear currentTest
          // and surface the reason in its own field instead of overloading currentTest.
          inst.poisonReason = event.reason ?? null
          inst.currentTest = null
        }
        break
      }

      case 'instance_connected': {
        const inst = state.instances?.find(i => i.instanceId === event.instanceId)
        if (inst) {
          inst.history.push({ timestamp: event.timestamp, event: 'connected', testName: null, reason: null, serverInstanceId: null, clientInstanceId: null })
          inst.connected = true
        }
        break
      }

      case 'instance_disconnected': {
        const inst = state.instances?.find(i => i.instanceId === event.instanceId)
        if (inst) {
          inst.history.push({ timestamp: event.timestamp, event: 'disconnected', testName: null, reason: null, serverInstanceId: null, clientInstanceId: null })
          inst.connected = false
        }
        break
      }

      case 'instance_client_attached': {
        const inst = state.instances?.find(i => i.instanceId === event.instanceId)
        if (inst) {
          inst.history.push({ timestamp: event.timestamp, event: 'client_attached', testName: null, reason: null, serverInstanceId: null, clientInstanceId: event.clientInstanceId ?? null })
        }
        break
      }

      case 'instance_stats': {
        const statsEntry = {
          cpuPercent: event.cpuPercent, memoryMb: event.memoryMb,
          cpuCount: event.cpuCount, totalMemoryMb: event.totalMemoryMb,
          fps: event.fps ?? null, tps: event.tps ?? null,
          avgTickMs: event.avgTickMs ?? null,
          gameMemoryMb: event.gameMemoryMb ?? null,
          targetTps: event.targetTps ?? null,
          targetFps: event.targetFps ?? null,
          gcRate: event.gcRate ?? null,
          pendingActions: event.pendingActions ?? null,
          gameThreadWaitMs: event.gameThreadWaitMs ?? null,
          netRxBytesPerSec: event.netRxBytesPerSec ?? null,
          netTxBytesPerSec: event.netTxBytesPerSec ?? null,
          blkReadBytesPerSec: event.blkReadBytesPerSec ?? null,
          blkWriteBytesPerSec: event.blkWriteBytesPerSec ?? null,
          memoryLimitMb: event.memoryLimitMb ?? 0,
        }
        instanceStats.set(event.instanceId, statsEntry)
        let history = instanceStatsHistory.get(event.instanceId)
        if (!history) {
          history = []
          instanceStatsHistory.set(event.instanceId, history)
        }
        history.push({ timestamp: event.timestamp, ...statsEntry })
        statsVersion.value++
        break
      }

      case 'diagnostic':
      case 'error':
        // Stored in event log but not tracked in state model
        if (event.event === 'error') {
          state.errors.push({
            message: event.message,
            stackTrace: event.stackTrace,
            timestamp: event.timestamp
          })
        }
        break

      default:
        log.warn('Unknown event type:', (event as { event: string }).event)
    }
  }

  function markAborted() {
    // Flush any queued messages. A run_finished event may be in the buffer
    flushPendingMessages()

    if (state.status === 'finished' || state.status === 'aborted') return
    if (state.status !== 'running' && state.status !== 'pending') return

    state.status = 'aborted'
    stopElapsedTimer()
    updateTitleFromState()

    for (const col of collections.value) {
      for (const cls of col.classes) {
        for (const test of cls.tests) {
          if (test.status === 'running') {
            transitionStatus('running', 'aborted')
            test.status = 'aborted'
          } else if (test.status === 'pending' || test.status === 'queued') {
            transitionStatus(test.status, 'skipped')
            test.status = 'skipped'
            test.skipReason = 'Run aborted'
          }
        }
      }
    }
    for (const phase of state.setupPhases) {
      if (phase.status === 'running') {
        phase.status = 'aborted'
        for (const step of phase.steps) {
          if (step.status === 'started' || step.status === 'in_progress') {
            step.status = 'aborted'
          }
        }
      }
    }

    // Archive remaining live instances for post-run inspection (without VNC iframes)
    if (state.instances?.length) {
      stoppedInstances.push(...state.instances)
      state.instances.splice(0, state.instances.length)
    }

    notifyTreeChanged()

    // Read summary.json for the abort reason. Fire-and-forget; the artifact is
    // always written before WebSocket close (Program.cs:113 / WebRenderer.DisposeAsync).
    void fetchSummaryAndApplyAbort()
  }


  function findOrCreateTest(collection: string, className: string, displayName: string): TestSnapshot | null {
    // Resolve xUnit v3's verbose runtime collection name back to the short
    // name from populate_tests. e.g. "Test collection for Namespace.Class (id: hash)"
    // becomes "Test collection for Class".
    collection = resolveCollectionName(collection, className)

    for (const col of collections.value) {
      if (col.name !== collection) continue
      for (const cls of col.classes) {
        if (cls.name !== className) continue
        const test = cls.tests.find(t => t.displayName === displayName)
        if (test) return test
      }
    }

    // Test not found in pre-populated tree. Create it dynamically.
    // Note: collections is a shallowRef, so we must replace the array reference
    // (not mutate in-place) so Vue dependents are notified via triggerRef.
    let col = collections.value.find(c => c.name === collection)
    if (!col) {
      col = { name: collection, classes: [] }
      collections.value = [...collections.value, col]
    }
    let cls = col.classes.find(c => c.name === className)
    if (!cls) {
      cls = { name: className, tests: [] }
      col.classes.push(cls) // classes array is inside a collection object, not a shallowRef, push is fine
    }
    const newTest: TestSnapshot = {
      className,
      displayName,
      status: 'pending',
      durationMs: null,
      queueDurationMs: null,
      output: null,
      errorMessage: null,
      errorType: null,
      stackTrace: null,
      recordings: null,
      recordingSkipReasons: null,
      lifecycle: null,
      skipReason: null,
      discoveryOrder: null,
      executionOrder: null,
      startTime: null,
      runningStartTime: null,
      usedInstances: null,
      failureCategory: null,
      errorPreview: null,
      phase: null,
      reproCommand: null,
      serverKey: null,
      serverInstanceId: null,
      failureContext: null,
    }
    cls.tests.push(newTest)
    statusCounts.pending++
    return newTest
  }

  /** Find a test by its display_name across all collections. */
  function findTestByDisplayName(displayName: string): TestSnapshot | null {
    for (const col of collections.value)
      for (const cls of col.classes) {
        const test = cls.tests.find(t => t.displayName === displayName)
        if (test) return test
      }
    return null
  }

  // Maps className -> pre-populated collection name (built from populate_tests event)
  const classToCollection: Record<string, string> = {}

  function resolveCollectionName(runtimeName: string, className: string): string {
    if (collections.value.some(c => c.name === runtimeName)) return runtimeName
    return classToCollection[className] ?? runtimeName
  }

  function findActiveSetupPhase(category: string, collection?: string | null): SetupPhaseSnapshot | undefined {
    // Prefer matching collection (for parallel server setups)
    if (collection) {
      const match = state.setupPhases.find(
        p => p.category === category && p.status === 'running' && p.collectionName === collection
      )
      if (match) return match
    }
    // Fallback: any running phase in this category
    return state.setupPhases.find(p => p.category === category && p.status === 'running')
  }

  // Check for static report mode
  const reportDataEl = document.getElementById('test-report-data')
  if (reportDataEl?.textContent) {
    try {
      const snapshot = JSON.parse(reportDataEl.textContent) as StateSnapshot
      hydrateFromSnapshot(snapshot)
      isReportMode.value = true
    } catch (err) {
      log.error('Failed to parse report data:', err)
    }
  }

  // ── Event batching: queue incoming WS messages and flush once per frame ──
  // This coalesces multiple WebSocket messages into a single Vue reactivity
  // flush, preventing per-event re-renders of the entire tree.
  let pendingMessages: string[] = []
  let flushScheduled = false
  // Tracks whether any tree-visible property changed during the current batch.
  // Set by applyEvent handlers; read+reset by flushPendingMessages.
  let treeDirty = false
  let selectedContentDirty = false

  function scheduleFlush() {
    if (flushScheduled) return
    flushScheduled = true
    requestAnimationFrame(flushPendingMessages)
  }

  function flushPendingMessages() {
    flushScheduled = false
    const batch = pendingMessages
    pendingMessages = []
    // Reset dirty flags before processing so handlers can set them
    treeDirty = false
    selectedContentDirty = false

    for (const data of batch) {
      try {
        const parsed = JSON.parse(data)
        if (parsed.type === 'snapshot') {
          hydrateFromSnapshot(parsed as StateSnapshot)
        } else if (parsed.event) {
          applyEvent(parsed as TestEvent)
        }
      } catch (err) {
        log.warn('Failed to parse message:', err)
      }
    }

    // Trigger exactly one shallow-ref notification per frame (if needed)
    if (treeDirty) notifyTreeChanged()
    if (selectedContentDirty) selectedTestVersion.value++
  }

  /** Queue incoming message for batch processing on next animation frame. */
  function handleMessage(data: string) {
    pendingMessages.push(data)
    scheduleFlush()
  }

  /** Mark the tree as needing a re-render (called from event handlers). */
  function markTreeDirty() { treeDirty = true }

  /** Mark the selected test's content as changed (called from event handlers). */
  function markSelectedContentDirty(test: TestSnapshot) {
    if (selectedTest.value && selectedTest.value.displayName === test.displayName) {
      selectedContentDirty = true
    }
  }

  /** Mark the selected step's content as changed (called from event handlers). */
  function markSelectedStepDirty(step: SetupStepSnapshot) {
    if (selectedStep.value && selectedStep.value === step) {
      selectedContentDirty = true
    }
  }

  // Set up WebSocket if not in report mode
  let ws: ReturnType<typeof useWebSocket> | null = null

  if (!isReportMode.value) {
    // In Vite dev mode, try mock data first so the UI can be developed without a running backend.
    // In production builds (make test-web), always connect to the real WebSocket backend.
    if (import.meta.env.DEV) {
      initDevMode()
    } else {
      ws = connectBackend()
    }
  }

  async function initDevMode() {
    try {
      const response = await fetch('/mock-artifacts/mock-data.json')
      if (response.ok) {
        const snapshot = await response.json() as StateSnapshot
        hydrateFromSnapshot(snapshot)
        // In dev mode the snapshot is from a finished run, so archive instances
        if ((snapshot.status === 'finished' || snapshot.status === 'aborted') && state.instances?.length) {
          stoppedInstances.splice(0, stoppedInstances.length, ...state.instances)
          state.instances.splice(0, state.instances.length)
        }
        return // Mock loaded, no backend needed
      }
    } catch { /* no mock data */ }
    // Mock not available, fall through to real backend
    ws = connectBackend()
  }

  function connectBackend(): ReturnType<typeof useWebSocket> {
    const socket = useWebSocket({
      onMessage: handleMessage,
      onDisconnect(wasClean) {
        // Flush pending messages so run_finished is applied if it arrived same tick
        flushPendingMessages()
        if (wasClean || state.status === 'finished' || state.status === 'aborted') {
          // Clean close or run already done. Server shut down intentionally.
          // Stop reconnecting to avoid connect/timeout/disconnect spam.
          socket.disconnect()
        }
        // Unclean close during running → useWebSocket retries automatically
      },
      onReconnectFailed() {
        // All retries exhausted on an unclean disconnect. Server is truly gone
        markAborted()
      },
    })
    socket.connect()
    fetchState()
    return socket
  }

  async function fetchState() {
    try {
      const res = await fetch('/api/state')
      if (res.ok) {
        const snapshot = await res.json() as StateSnapshot
        hydrateFromSnapshot(snapshot)
      }
    } catch {
      // Server not ready yet. WebSocket will deliver the snapshot when it connects
    }
  }

  /**
   * Send a runtime control command to the runner. Tries the WebSocket first;
   * if the socket is mid-reconnect or send throws, falls back to POST
   * /api/command with the same JSON payload.
   */
  async function sendCommand(cmd: 'stop'): Promise<void> {
    const payload = JSON.stringify({ cmd })
    if (ws?.send(payload)) return
    try {
      await fetch('/api/command', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: payload,
      })
    } catch (err) {
      log.warn('sendCommand failed:', cmd, err)
    }
  }

  return {
    state,
    collections,
    selectedTestVersion,
    get selectedTest() { return selectedTest.value },
    set selectedTest(v) { selectedTest.value = v },
    get selectedStep() { return selectedStep.value },
    set selectedStep(v) { selectedStep.value = v },
    get selectedError() { return selectedError.value },
    set selectedError(v) { selectedError.value = v },
    get isConnected() { return ws?.isConnected.value ?? false },
    get connectionError() { return ws?.connectionError.value ?? null },
    get isReportMode() { return isReportMode.value },
    get runDone() { return state.status === 'finished' || state.status === 'aborted' },
    selectTest,
    selectStep,
    selectError,
    statusCounts,
    elapsedTimerRef,
    get elapsedMs() { return elapsedMs.value },
    instanceStats,
    instanceStatsHistory,
    statsVersion,
    screenshotSrc,
    stoppedInstances,
    findGlobalStep(stepName: string): SetupStepSnapshot | null {
      for (const phase of state.setupPhases) {
        const match = phase.steps.find(s => s.step === stepName)
        if (match) return match
      }
      return null
    },
    findTest(displayName: string): TestSnapshot | null {
      return findTestByDisplayName(displayName)
    },
    sendCommand,
  }
}
