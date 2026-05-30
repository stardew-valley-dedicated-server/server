import { ref, watch } from 'vue'

const LS_KEY = 'vnc-grid-prefs'

function loadInitial(): boolean {
  try {
    const raw = localStorage.getItem(LS_KEY)
    if (raw) {
      const parsed = JSON.parse(raw)
      return parsed.interactive === true
    }
  } catch { /* ignore corrupt data */ }
  return false
}

const interactive = ref(loadInitial())

watch(interactive, (val) => {
  try {
    const raw = localStorage.getItem(LS_KEY)
    const parsed = raw ? JSON.parse(raw) : {}
    parsed.interactive = val
    localStorage.setItem(LS_KEY, JSON.stringify(parsed))
  } catch { /* ignore */ }
})

export function useVncInteractive() {
  return { interactive }
}
