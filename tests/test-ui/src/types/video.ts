export interface VideoStatsPoint {
  /** Seconds from video start */
  offsetSec: number
  tps: number | null
}

export interface VideoItem {
  source: string
  path: string
  timelineOffset: number
  wallClockDuration: number
  thumbnailUrls?: string[]
  /** TPS stats aligned to this video's time window */
  statsPoints?: VideoStatsPoint[]
  /** Target TPS for threshold coloring (from env/game config) */
  targetTps?: number | null
  /** Container instance ID for navigation and display (e.g. "server-abc123") */
  instanceId?: string
  /** Display label from the container instance (e.g. "server-1", "client-2") */
  label?: string
}
