interface FocusableTerminal {
  focus(): void
}

export function refocusTerminalIfActive(
  host: HTMLElement | null,
  terminal: FocusableTerminal | null,
): void {
  if (host?.contains(document.activeElement)) {
    terminal?.focus()
  }
}
