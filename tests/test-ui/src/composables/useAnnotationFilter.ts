import { reactive, watch } from 'vue'
import type { AnnotationSource } from '../types/state'

const LS_KEY = 'output-panel-source-filter'

const ALL_SOURCES: AnnotationSource[] = ['body', 'broker', 'recording', 'mod', 'setup']

type FilterState = Record<AnnotationSource, boolean>

function defaultFilter(): FilterState {
  return { body: true, broker: true, recording: true, mod: true, setup: true }
}

function loadInitial(): FilterState {
  const state = defaultFilter()
  try {
    const raw = localStorage.getItem(LS_KEY)
    if (raw) {
      const parsed = JSON.parse(raw) as Partial<FilterState>
      for (const source of ALL_SOURCES) {
        if (typeof parsed[source] === 'boolean') state[source] = parsed[source]!
      }
    }
  } catch { /* ignore corrupt data */ }
  return state
}

const filter = reactive<FilterState>(loadInitial())

watch(filter, (val) => {
  try { localStorage.setItem(LS_KEY, JSON.stringify(val)) }
  catch { /* ignore quota / disabled storage */ }
}, { deep: true })

/**
 * Filter chip state for the per-test output panel. Single shared instance so
 * the chip bar in OutputPanel and any future callers see the same toggles.
 * Persisted across reloads in localStorage.
 */
export function useAnnotationFilter() {
  return {
    filter,
    sources: ALL_SOURCES,
    toggle(source: AnnotationSource) { filter[source] = !filter[source] },
    isEnabled(source: AnnotationSource) { return filter[source] },
    reset() {
      const defaults = defaultFilter()
      for (const source of ALL_SOURCES) filter[source] = defaults[source]
    },
    /** True when at least one source is currently disabled. Used to gate the
     *  reset button's visibility. */
    isFiltered() { return ALL_SOURCES.some(s => !filter[s]) },
  }
}
