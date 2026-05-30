/**
 * Incrementally-maintained status counts: O(1) updates instead of O(n) tree walks.
 * This is the single source of truth for test counts by status.
 */
import { reactive } from 'vue'
import type { CollectionSnapshot } from '../types/state'

export interface StatusCountMap {
  pending: number
  queued: number
  running: number
  passed: number
  failed: number
  canceled: number
  skipped: number
  notDispatched: number
  aborted: number
  [key: string]: number
}

export interface StatusCounts {
  statusCounts: StatusCountMap
  resetStatusCounts: () => void
  /** Transition a single test's status in the counts map. */
  transitionStatus: (oldStatus: string | null, newStatus: string) => void
  /** Recompute statusCounts from the full tree (used after snapshot hydration). */
  rebuildStatusCounts: (collections: CollectionSnapshot[]) => void
}

export function useStatusCounts(): StatusCounts {
  const statusCounts = reactive<StatusCountMap>({
    pending: 0, queued: 0, running: 0, passed: 0,
    failed: 0, canceled: 0, skipped: 0, notDispatched: 0, aborted: 0
  })

  function resetStatusCounts() {
    for (const key in statusCounts) statusCounts[key] = 0
  }

  function transitionStatus(oldStatus: string | null, newStatus: string) {
    if (oldStatus && oldStatus in statusCounts) statusCounts[oldStatus]--
    if (newStatus in statusCounts) statusCounts[newStatus]++
  }

  function rebuildStatusCounts(collections: CollectionSnapshot[]) {
    resetStatusCounts()
    for (const col of collections)
      for (const cls of col.classes)
        for (const test of cls.tests)
          if (test.status in statusCounts) statusCounts[test.status]++
  }

  return {
    statusCounts,
    resetStatusCounts,
    transitionStatus,
    rebuildStatusCounts,
  }
}
