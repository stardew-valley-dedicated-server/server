import { ref, onUnmounted } from 'vue'
import { createLogger } from '../utils/logger'

const log = createLogger('WS')

export interface UseWebSocketOptions {
  /** Called for every message. Caller routes by content (snapshot vs event). */
  onMessage: (data: string) => void
  /** Fires on every disconnect after a successful connection. */
  onDisconnect?: (wasClean: boolean) => void
  /** Fires when reconnection gives up after maxReconnectAttempts. */
  onReconnectFailed?: () => void
  /** Max reconnect attempts before giving up (default: 3). */
  maxReconnectAttempts?: number
}

export function useWebSocket(options: UseWebSocketOptions) {
  const isConnected = ref(false)
  const connectionError = ref<string | null>(null)

  let ws: WebSocket | null = null
  let reconnectTimer: ReturnType<typeof setTimeout> | null = null
  let reconnectDelay = 1000
  let reconnectAttempts = 0
  let disposed = false
  const maxAttempts = options.maxReconnectAttempts ?? 3

  function connect() {
    if (disposed) return

    // Close previous socket
    if (ws) {
      try { ws.close() } catch { /* ignore */ }
      ws = null
    }

    const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:'
    const url = `${protocol}//${location.host}/ws`

    // Track whether this socket ever connected successfully.
    // Unlike isConnected (a reactive ref), this is a plain boolean that
    // onerror cannot clear, ensuring onDisconnect fires even when
    // onerror sets isConnected=false before onclose reads it.
    let hasConnected = false

    try {
      ws = new WebSocket(url)
    } catch (err) {
      connectionError.value = `Failed to connect: ${err}`
      scheduleReconnect()
      return
    }

    ws.onopen = () => {
      hasConnected = true
      isConnected.value = true
      connectionError.value = null
      reconnectDelay = 1000
      reconnectAttempts = 0
    }

    ws.onmessage = (event) => {
      try {
        options.onMessage(event.data)
      } catch (err) {
        log.warn('Failed to process message:', err)
      }
    }

    ws.onclose = (ev: CloseEvent) => {
      isConnected.value = false
      if (hasConnected) {
        options.onDisconnect?.(ev.wasClean)
      }
      // Only reconnect on unclean close (network failure, not server-initiated)
      if (!disposed && !ev.wasClean) {
        scheduleReconnect()
      }
    }

    ws.onerror = () => {
      connectionError.value = 'WebSocket error'
      isConnected.value = false
    }
  }

  function scheduleReconnect() {
    if (disposed || reconnectTimer) return
    reconnectAttempts++
    if (reconnectAttempts > maxAttempts) {
      options.onReconnectFailed?.()
      return
    }
    reconnectTimer = setTimeout(() => {
      reconnectTimer = null
      reconnectDelay = Math.min(reconnectDelay * 2, 10000)
      connect()
    }, reconnectDelay)
  }

  function disconnect() {
    disposed = true
    if (reconnectTimer) {
      clearTimeout(reconnectTimer)
      reconnectTimer = null
    }
    if (ws) {
      try { ws.close() } catch { /* ignore */ }
      ws = null
    }
    isConnected.value = false
  }

  /**
   * Send a text frame. Returns true on success, false if the socket isn't open
   * or the send threw — callers (sendCommand) use the boolean to decide
   * whether to fall back to the REST endpoint.
   */
  function send(data: string): boolean {
    if (!ws || ws.readyState !== WebSocket.OPEN) return false
    try { ws.send(data); return true } catch { return false }
  }

  // Auto-cleanup on component unmount
  onUnmounted(disconnect)

  return {
    isConnected,
    connectionError,
    connect,
    disconnect,
    send,
  }
}
