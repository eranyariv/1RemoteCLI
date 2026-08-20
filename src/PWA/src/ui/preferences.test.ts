import { act, cleanup, renderHook, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { moveId, orderByPreference, reconcileOrder, useProjectOrder } from './preferences'

describe('persistent ordering', () => {
  beforeEach(() => window.localStorage.clear())
  afterEach(cleanup)

  it('keeps saved ids first and appends new ids in their source order', () => {
    expect(reconcileOrder(['machine-b', 'missing', 'machine-b'], ['machine-a', 'machine-b', 'machine-c'])).toEqual([
      'machine-b',
      'machine-a',
      'machine-c',
    ])
  })

  it('moves a dragged id to the drop target', () => {
    expect(moveId(['a', 'b', 'c'], 'c', 'a')).toEqual(['c', 'a', 'b'])
    expect(moveId(['a', 'b'], 'missing', 'a')).toEqual(['a', 'b'])
  })

  it('orders objects without losing newly discovered entries', () => {
    const items = [{ id: 'a' }, { id: 'b' }, { id: 'c' }]
    expect(orderByPreference(items, ['c', 'a'], (item) => item.id)).toEqual([
      { id: 'c' },
      { id: 'a' },
      { id: 'b' },
    ])
  })

  it('restores project order after the ordering hook remounts', async () => {
    const first = renderHook(() => useProjectOrder(['a', 'b', 'c']))

    act(() => first.result.current.move('c', 'a'))
    await waitFor(() =>
      expect(window.localStorage.getItem('1remote.project-order.v1')).toBe('["c","a","b"]'),
    )
    first.unmount()

    const second = renderHook(() => useProjectOrder(['a', 'b', 'c']))
    expect(second.result.current.order).toEqual(['c', 'a', 'b'])
  })
})
