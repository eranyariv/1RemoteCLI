import { describe, expect, it } from 'vitest'

import { feedbackMailto, feedbackSubject } from './feedback'

describe('the feedback link', () => {
  it('names the product and the version in the subject', () => {
    expect(feedbackSubject('0.01')).toBe('Feedback on 1RemoteCLI, version 0.01')
  })

  it('goes to the address the agent uses', () => {
    expect(feedbackMailto('0.01')).toMatch(/^mailto:eran@yariv\.org\?subject=/)
  })

  /**
   * The comma and the spaces are the point. A mailto whose query is not encoded is
   * handled differently by every mail client, and the one that drops the subject drops
   * the version with it — which is the only reason the link exists.
   */
  it('encodes the subject', () => {
    const link = feedbackMailto('0.01')

    expect(link).not.toContain(' ')
    expect(link).toContain('Feedback%20on%201RemoteCLI%2C%20version%200.01')
  })
})
