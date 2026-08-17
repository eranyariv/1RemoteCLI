/**
 * Where feedback goes, and what it is called when it arrives.
 *
 * A mail link rather than a form or an issue tracker: the person who has something to
 * say is holding a phone, has just been surprised by something, and will not sign up
 * for anything. Mail is the one channel already installed.
 *
 * The version is in the subject because it is the fact the reply always needs and the
 * one the sender is least likely to include. Kept identical to the agent's tray link
 * — see src/Protocol/Feedback.cs — so both arrive looking like the same product.
 */
export const FEEDBACK_ADDRESS = 'eran@yariv.org'

export function feedbackSubject(version: string): string {
  return `Feedback on 1RemoteCLI, version ${version}`
}

export function feedbackMailto(version: string): string {
  // Encoded: the subject has a comma and spaces in it, and an unencoded query is a
  // mail client's licence to keep whatever part of it it feels like.
  return `mailto:${FEEDBACK_ADDRESS}?subject=${encodeURIComponent(feedbackSubject(version))}`
}
