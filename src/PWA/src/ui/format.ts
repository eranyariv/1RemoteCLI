/**
 * Human-readable time, phrased for a glance rather than for precision.
 *
 * "3h" is what someone wants to know about a session that has been running since
 * this morning; the exact minute it started is never the question.
 */
export function uptime(since: Date, now: Date = new Date()): string {
  const seconds = Math.max(0, Math.round((now.getTime() - since.getTime()) / 1000))

  if (seconds < 60) return `${seconds}s`

  const minutes = Math.floor(seconds / 60)
  if (minutes < 60) return `${minutes}m`

  const hours = Math.floor(minutes / 60)
  if (hours < 24) {
    const rest = minutes % 60
    return rest === 0 ? `${hours}h` : `${hours}h ${rest}m`
  }

  const days = Math.floor(hours / 24)
  const restHours = hours % 24
  return restHours === 0 ? `${days}d` : `${days}d ${restHours}h`
}

/**
 * Shortens a working directory to its last couple of segments.
 *
 * A full Windows path does not fit on a phone and its interesting end is the part
 * that gets truncated, so the head goes rather than the tail.
 */
export function shortPath(path: string, segments = 2): string {
  const parts = path.split(/[\\/]/).filter(Boolean)

  if (parts.length <= segments) return path

  return `…\\${parts.slice(-segments).join('\\')}`
}

/** "Windows 11" out of "Microsoft Windows 10.0.26100", which is not for reading. */
export function shortOs(os: string): string {
  const windows = /Windows (\d+)\.(\d+)\.(\d+)/.exec(os)

  if (windows) {
    const build = Number(windows[3])
    // Windows 11 kept the 10.0 kernel version and is distinguished only by build.
    return build >= 22000 ? 'Windows 11' : 'Windows 10'
  }

  return os.replace(/^Microsoft /, '')
}
