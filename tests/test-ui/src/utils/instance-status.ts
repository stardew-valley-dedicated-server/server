/** CSS classes for the instance status indicator dot.
 *  `stopped` covers both run-ended containers and mid-run retained containers.
 *  Poison always wins visually, even when stopped, so a torn-down poisoned container
 *  keeps surfacing the red dot while draining for recording extraction. */
export function instanceStatusDotClass(
  status: string,
  connected: boolean,
  stopped: boolean,
  size: 'sm' | 'md',
): string {
  const s = size === 'sm' ? 'w-1.5 h-1.5' : 'w-2 h-2'
  if (status === 'poisoned') return `${s} rounded-full bg-error flex-none`
  if (stopped) return `${s} rounded-full bg-base-content/20 flex-none`
  if (status === 'starting') return `${s} rounded-full bg-info animate-pulse gpu-accel flex-none`
  if (status === 'in_use' && connected) return `${s} rounded-full bg-success animate-pulse gpu-accel flex-none`
  if (status === 'in_use') return `${s} rounded-full bg-warning animate-pulse gpu-accel flex-none`
  return `${s} rounded-full bg-base-content/20 flex-none`
}

/** Human-readable label for the instance status. Poison wins over stopped
 *  so a torn-down poisoned container still reads "Poisoned". */
export function instanceStatusLabel(
  status: string,
  connected: boolean,
  stopped: boolean,
  showStoppedLabel: boolean,
): string {
  if (status === 'poisoned') return 'Poisoned'
  if (stopped && showStoppedLabel) return 'Stopped'
  if (status === 'starting') return 'Starting'
  if (status === 'in_use' && connected) return 'Connected'
  if (status === 'in_use') return 'In Use'
  return 'Idle'
}

/** DaisyUI badge class for instance status. */
export function instanceStatusBadgeClass(status: string): string {
  if (status === 'poisoned') return 'badge-error'
  if (status === 'starting') return 'badge-info'
  if (status === 'in_use') return 'badge-success'
  return 'badge-ghost'
}
