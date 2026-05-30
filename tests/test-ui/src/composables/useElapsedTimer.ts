/**
 * Interval-based elapsed timer that updates a DOM element directly (bypasses
 * Vue reactivity). Also updates the browser tab title with a rotating spinner
 * during the run.
 *
 * Uses setInterval(100ms) rather than rAF: formatDuration is second-precision,
 * so ticking at 10 Hz is more than enough, and it keeps the elapsed timer from
 * pinning the browser's refresh driver at vsync while a run is active.
 * Writes are skipped while the tab is hidden.
 */
import { ref } from 'vue'
import type { Ref } from 'vue'
import { formatDuration } from '../utils/format'

export interface ElapsedTimer {
  /** Ref to the elapsed timer DOM element. StatusBar binds this for timer-driven updates. */
  elapsedTimerRef: Ref<HTMLElement | null>
  /** Final elapsed ms (set once when run finishes). Null while running. */
  elapsedMs: Ref<number | null>
  /** Start the timer. Call when run_started or hydrating a running snapshot. */
  startElapsedTimer: (runStartTime: string) => void
  /** Stop the timer and set final elapsed ms. */
  stopElapsedTimer: (finalMs?: number) => void
}

const TICK_INTERVAL_MS = 100
const SPINNER_INTERVAL_MS = 200
const SPINNER_FRAMES = ['\u2809', '\u2818', '\u2830', '\u2824', '\u2806', '\u2803']

export function useElapsedTimer(): ElapsedTimer {
  const elapsedTimerRef = ref<HTMLElement | null>(null)
  const elapsedMs = ref<number | null>(null)

  let intervalId: ReturnType<typeof setInterval> | null = null
  let runStartMs: number | null = null
  let spinnerIndex = 0
  let lastSpinnerMs = 0
  let lastText = ''
  let lastTitle = ''
  let visibilityHandler: (() => void) | null = null

  function writeText(text: string) {
    if (text === lastText) return
    lastText = text
    const el = elapsedTimerRef.value
    if (el) el.textContent = text
  }

  function writeTitle(title: string) {
    if (title === lastTitle) return
    lastTitle = title
    document.title = title
  }

  function tick() {
    if (runStartMs == null) return
    if (document.hidden) return

    const now = performance.now()
    const elapsed = Date.now() - runStartMs
    writeText(formatDuration(elapsed))

    if (now - lastSpinnerMs >= SPINNER_INTERVAL_MS) {
      lastSpinnerMs = now
      spinnerIndex = (spinnerIndex + 1) % SPINNER_FRAMES.length
      writeTitle(`${SPINNER_FRAMES[spinnerIndex]} Running \u2013 Test Runner`)
    }
  }

  function startElapsedTimer(runStartTime: string) {
    if (intervalId != null) return
    runStartMs = new Date(runStartTime).getTime()
    lastSpinnerMs = 0
    // Immediate first tick so the UI updates without a 100ms delay.
    tick()
    intervalId = setInterval(tick, TICK_INTERVAL_MS)

    // Flush text the moment the tab becomes visible again, so the user never
    // sees a stale value after returning from another tab.
    visibilityHandler = () => { if (!document.hidden) tick() }
    document.addEventListener('visibilitychange', visibilityHandler)
  }

  function stopElapsedTimer(finalMs?: number) {
    if (intervalId != null) {
      clearInterval(intervalId)
      intervalId = null
    }
    if (visibilityHandler) {
      document.removeEventListener('visibilitychange', visibilityHandler)
      visibilityHandler = null
    }
    runStartMs = null
    if (finalMs != null) elapsedMs.value = finalMs
  }

  return {
    elapsedTimerRef,
    elapsedMs,
    startElapsedTimer,
    stopElapsedTimer,
  }
}
