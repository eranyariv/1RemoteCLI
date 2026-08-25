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
  ProjectNotFound: 'project_not_found',
  DuplicateProjectName: 'duplicate_project_name',
  InvalidProjectSiteUrl: 'invalid_project_site_url',
  InvalidProjectRepoUrl: 'invalid_project_repo_url',
  CannotDeleteGeneralProject: 'cannot_delete_general_project',
  FileTooLarge: 'file_too_large',
  UploadNotFound: 'upload_not_found',
  UploadFailed: 'upload_failed',
  UploadCancelled: 'upload_cancelled',
  UploadUnavailable: 'upload_unavailable',
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
    case ErrorCodes.ProjectNotFound:
      return 'That project no longer exists.'
    case ErrorCodes.DuplicateProjectName:
      return 'You already have a project with that name.'
    case ErrorCodes.InvalidProjectSiteUrl:
      return 'Site URL must be a complete http:// or https:// address.'
    case ErrorCodes.InvalidProjectRepoUrl:
      return 'GitHub repo URL must be a complete http:// or https:// address.'
    case ErrorCodes.CannotDeleteGeneralProject:
      return 'The General project cannot be deleted.'
    case ErrorCodes.FileTooLarge:
      return 'That file is larger than the 25 MB upload limit.'
    case ErrorCodes.UploadNotFound:
      return 'That upload is no longer active. Choose the file again.'
    case ErrorCodes.UploadFailed:
      return fallback ?? 'The machine could not save that file.'
    case ErrorCodes.UploadCancelled:
      return 'The upload was cancelled.'
    case ErrorCodes.UploadUnavailable:
      return 'Update the agent on this machine before attaching files.'
    default:
      return fallback ?? 'Something went wrong talking to the hub.'
  }
}
