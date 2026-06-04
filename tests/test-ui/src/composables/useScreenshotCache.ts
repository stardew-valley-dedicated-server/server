import type { OutputEntry } from '../types/state'

/**
 * Screenshot blob cache that eagerly fetches screenshots as blob URLs so they
 * survive runner shutdown. Key: artifact path, Value: blob URL.
 */

export interface ScreenshotCache {
  /** Resolve a screenshot path to a displayable URL (blob URL or /artifacts/ fallback). */
  screenshotSrc: (path: string) => string
  /** Trigger eager fetch and cache of a screenshot artifact. */
  cacheScreenshot: (artifactPath: string) => void
  /** Cache screenshots found in typed output entries. */
  cacheScreenshotsFromOutput: (output: OutputEntry[] | null) => void
}

export function useScreenshotCache(): ScreenshotCache {
  const blobCache = new Map<string, string>()
  const fetchPending = new Set<string>()

  function cacheScreenshot(artifactPath: string) {
    if (blobCache.has(artifactPath) || fetchPending.has(artifactPath)) return
    if (artifactPath.startsWith('data:')) return // already inline
    if (artifactPath.startsWith('mock-artifacts/')) return // static files served by Vite
    if (artifactPath.startsWith('artifacts/')) return // offline report bundle: media sits next to index.html
    fetchPending.add(artifactPath)
    fetch(`/artifacts/${artifactPath}`)
      .then(res => {
        if (!res.ok) throw new Error(`${res.status}`)
        return res.blob()
      })
      .then(blob => {
        // Revoke previous blob URL if replacing (prevents memory leak)
        const prev = blobCache.get(artifactPath)
        if (prev) URL.revokeObjectURL(prev)
        blobCache.set(artifactPath, URL.createObjectURL(blob))
      })
      .catch(() => { /* runner may already be down, keep using /artifacts/ fallback */ })
      .finally(() => { fetchPending.delete(artifactPath) })
  }

  function screenshotSrc(path: string): string {
    if (path.startsWith('data:')) return path
    if (blobCache.has(path)) return blobCache.get(path)!
    // mock-artifacts/ paths are static files served directly from public/ by Vite
    if (path.startsWith('mock-artifacts/')) return `/${path}`
    // artifacts/ paths come from the offline report bundle, where media sits
    // next to index.html — keep them document-relative so they resolve over file://
    if (path.startsWith('artifacts/')) return path
    return `/artifacts/${path}`
  }

  function cacheScreenshotsFromOutput(output: OutputEntry[] | null) {
    if (!output) return
    for (const entry of output) {
      if (entry.type === 'screenshot') cacheScreenshot(entry.path)
    }
  }

  return {
    screenshotSrc,
    cacheScreenshot,
    cacheScreenshotsFromOutput,
  }
}
