import { ref } from 'vue'

/** Snap to 10ms grid for grouping near-simultaneous breakpoints (markers, hover). */
export function snapBreakpointSec(sec: number): number { return Math.round(sec * 100) / 100 }

/** Shared video-timeline state bridging SyncedVideos (playback) and OutputPanel (display + breakpoints). */
const _timelinePos = ref(0)
const _playing = ref(false)
/** Line number → timeline-second. Keyed by line number so duplicate timestamps stay independent. */
const _breakpoints = ref<Map<number, number>>(new Map())
const _hitBreakpointLine = ref<number | null>(null)
/** Timeline-second of the breakpoint marker being hovered in the timeline ruler, or null. */
const _hoveredBreakpointSec = ref<number | null>(null)

export function useVideoTimeline() {
  return {
    timelinePos: _timelinePos,
    playing: _playing,
    breakpoints: _breakpoints,
    hitBreakpointLine: _hitBreakpointLine,
    hoveredBreakpointSec: _hoveredBreakpointSec,
  }
}
