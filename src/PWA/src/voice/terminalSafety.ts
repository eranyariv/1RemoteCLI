export interface TerminalRisk {
  risky: boolean
  reason: string | null
}

const HIGH_RISK: readonly [RegExp, string][] = [
  [/\b(?:rm|rmdir)\b[^\r\n]*(?:-[^\s]*r[^\s]*|--recursive|\/s)(?:\s|$)/i, 'it recursively deletes files'],
  [/\bremove-item\b[^\r\n]*-recurse(?:\s|$)/i, 'it recursively deletes files'],
  [/\b(?:del|erase)\b[^\r\n]*\s\/[sq]\b/i, 'it deletes files without an ordinary prompt'],
  [/\b(?:format|diskpart|mkfs|dd)\b/i, 'it can overwrite a disk'],
  [/\b(?:shutdown|restart-computer|stop-computer|reboot)\b/i, 'it can stop or restart a machine'],
  [/\bgit\s+reset\b[^\r\n]*--hard\b/i, 'it discards working-tree changes'],
  [/\bgit\s+clean\b[^\r\n]*-[^\s]*f/i, 'it permanently removes untracked files'],
  [/\bgit\s+push\b[^\r\n]*(?:--force|-f)\b/i, 'it force-pushes remote history'],
  [/\b(?:drop|truncate)\s+(?:database|schema|table)\b/i, 'it destroys database data'],
  [/\bterraform\s+(?:apply[^\r\n]*-auto-approve|destroy)\b/i, 'it can destroy infrastructure'],
  [/\b(?:kubectl|helm)\s+(?:delete|uninstall)\b/i, 'it removes deployed resources'],
  [/\baz\s+(?:group|resource)\s+delete\b/i, 'it removes Azure resources'],
]

const AMBIGUOUS: readonly [RegExp, string][] = [
  [/\b(?:sudo|runas)\b/i, 'it requests elevated privileges'],
  [/\b(?:invoke-expression|iex|eval)\b/i, 'it evaluates generated code'],
  [/(?:^|[^|])\|(?:[^|]|$)|&&|\|\||;|(?:^|\s)[<>]{1,2}(?:\s|$)/, 'it combines or redirects commands'],
  [/\b(?:curl|wget|irm|invoke-webrequest)\b[^\r\n]*\|\s*(?:sh|bash|pwsh|powershell)\b/i, 'it executes downloaded code'],
  [/\bset-executionpolicy\b/i, 'it changes script execution policy'],
]

// This protects voice recognition from submitting a consequential mishearing. It is
// deliberately conservative, and is not a terminal sandbox or a substitute for the
// remote session's own authorization and confirmation prompts.
export function terminalRisk(value: string): TerminalRisk {
  const text = value.trim()
  if (!text) return { risky: true, reason: 'no command was recognized' }
  if (text.length > 1_000) return { risky: true, reason: 'the command is unusually long' }
  if (/[\r\n]/.test(text)) return { risky: true, reason: 'it contains more than one line' }

  for (const [pattern, reason] of [...HIGH_RISK, ...AMBIGUOUS]) {
    if (pattern.test(text)) return { risky: true, reason }
  }

  return { risky: false, reason: null }
}
