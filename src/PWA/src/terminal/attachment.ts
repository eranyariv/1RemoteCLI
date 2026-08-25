import type { CliType } from '../protocol/wire'

/** Mirrors `TerminalUploadLimits` in the shared C# protocol. */
export const MAX_TERMINAL_UPLOAD_BYTES = 25 * 1024 * 1024
export const TERMINAL_UPLOAD_CHUNK_BYTES = 64 * 1024

/**
 * Uses xterm's paste path rather than emitting bytes by hand. Xterm tracks DECSET
 * 2004 from remote output and adds bracketed-paste markers only while that mode is on.
 */
export function pasteClipboardText(
  terminal: Pick<import('@xterm/xterm').Terminal, 'paste'>,
  text: string,
): void {
  terminal.paste(text)
}

/**
 * Quotes a path without submitting it.
 *
 * PowerShell treats single quotes as literal. Cmd and the interactive CLI inputs use
 * Windows-style double-quoted arguments. The agent removes quote characters from the
 * generated leaf name, but escaping here keeps the browser safe against a future root
 * path that contains one.
 */
export function quoteTerminalPath(path: string, cliType: CliType): string {
  return cliType === 'PowerShell'
    ? `'${path.replaceAll("'", "''")}'`
    : `"${path.replaceAll('"', '""')}"`
}
