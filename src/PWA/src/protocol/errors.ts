/**
 * Error codes the hub can send, mirroring `src/Protocol/ErrorCodes.cs`, and the
 * sentences we show a person when one arrives.
 *
 * The codes are matched as strings rather than a union type on purpose: a newer
 * hub is allowed to send a code this build has never heard of, and the right
 * response to that is a generic message, not a crash.
 */

export const ErrorCodes = {
  UnsupportedProtocolVersion: 'unsupported_protocol_version',
  AccountNotAllowed: 'account_not_allowed',
  MachineNotFound: 'machine_not_found',
  MachineOffline: 'machine_offline',
  SessionNotFound: 'session_not_found',
  NotAttached: 'not_attached',
  TokenExpired: 'token_expired',
  IdentityChanged: 'identity_changed',
  InvalidRequest: 'invalid_request',
  InternalError: 'internal_error',
} as const

/**
 * Turns a hub error into something worth reading.
 *
 * `account_not_allowed` gets the most care because it is the first thing a new
 * colleague hits, and the difference between "you are not on the list" and "the
 * connection failed" is the difference between asking the right person and
 * filing a bug.
 */
export function describeError(code: string, fallback?: string): string {
  switch (code) {
    case ErrorCodes.AccountNotAllowed:
      return 'This account is not allowed to use 1RemoteCLI. Ask whoever runs the hub to add it.'
    case ErrorCodes.UnsupportedProtocolVersion:
      return 'This app is too old for the hub. Reload the page to pick up the current version.'
    case ErrorCodes.MachineNotFound:
      return 'That machine is not registered to this account.'
    case ErrorCodes.MachineOffline:
      return 'That machine is offline. Its agent is not connected right now.'
    case ErrorCodes.SessionNotFound:
      return 'That session has ended.'
    case ErrorCodes.NotAttached:
      return 'Not attached to that session any more.'
    case ErrorCodes.TokenExpired:
    case ErrorCodes.IdentityChanged:
      return 'Your sign-in expired. Sign in again to carry on.'
    case ErrorCodes.InvalidRequest:
      return fallback ?? 'The hub rejected that request.'
    case ErrorCodes.InternalError:
      return 'The hub hit an unexpected problem. Try again in a moment.'
    default:
      return fallback ?? 'Something went wrong talking to the hub.'
  }
}
