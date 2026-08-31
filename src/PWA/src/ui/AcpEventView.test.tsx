import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'

import type { ChatEvent, ChatPlanEntry } from '../protocol/wire'
import { AcpEventView } from './AcpEventView'

afterEach(cleanup)

function task(
  taskId: string,
  content: string,
  status: string,
  parentTaskId: string | null = null,
  depth = 0,
): ChatPlanEntry {
  return {
    taskId,
    content,
    status,
    parentTaskId,
    depth,
    priority: 'medium',
  }
}

function plan(entries: ChatPlanEntry[], revision = 1): ChatEvent {
  return {
    eventId: 'plan:prompt:1',
    kind: 'Plan',
    text: entries.map((entry) => entry.content).join('\n'),
    title: 'Plan',
    status: null,
    toolKind: null,
    permissionRequestId: null,
    options: [],
    content: [],
    locations: [],
    planEntries: entries,
    rawInputJson: null,
    rawOutputJson: null,
    planTurnId: 'prompt:1',
    planRevision: revision,
  }
}

describe('ACP plan view', () => {
  it('renders nested mixed-status tasks with accessible progress', () => {
    render(
      <AcpEventView
        detailLevel="summary"
        item={plan([
          task('release', 'Prepare release', 'in_progress'),
          task('tests', 'Run tests', 'completed', 'release', 1),
          task('deploy', 'Deploy production', 'failed', 'release', 1),
          task('announce', 'Announce release', 'pending'),
        ])}
      />,
    )

    const progress = screen.getByRole('progressbar', { name: 'Plan progress' })
    expect(progress.getAttribute('aria-valuenow')).toBe('1')
    expect(progress.getAttribute('aria-valuemax')).toBe('4')
    expect(screen.getByText('1 of 4 complete')).toBeTruthy()
    expect(screen.getByRole('button', { name: 'Collapse Prepare release' })).toBeTruthy()
    expect(screen.getByText('Completed')).toBeTruthy()
    expect(screen.getByText('In progress')).toBeTruthy()
    expect(screen.getByText('Failed')).toBeTruthy()
    expect(screen.getByText('Pending')).toBeTruthy()
    expect(screen.getByRole('treeitem', { name: /Prepare release/ }).getAttribute('aria-current')).toBe(
      'step',
    )
  })

  it('keeps branch collapse state while a replacement updates rows in place', () => {
    const { rerender } = render(
      <AcpEventView
        detailLevel="summary"
        item={plan([
          task('release', 'Prepare release', 'in_progress'),
          task('tests', 'Run tests', 'pending', 'release', 1),
        ])}
      />,
    )

    fireEvent.click(screen.getByRole('button', { name: 'Collapse Prepare release' }))
    expect(screen.queryByText('Run tests')).toBeNull()

    rerender(
      <AcpEventView
        detailLevel="summary"
        item={plan(
          [
            task('release', 'Prepare release', 'completed'),
            task('tests', 'Run tests', 'failed', 'release', 1),
          ],
          2,
        )}
      />,
    )

    expect(screen.queryByText('Run tests')).toBeNull()
    fireEvent.click(screen.getByRole('button', { name: 'Expand Prepare release' }))
    expect(screen.getByText('Run tests')).toBeTruthy()
    expect(screen.getByText('Failed')).toBeTruthy()
  })

  it('renders ordinary ACP plans as a flat task list and collapses the whole plan', () => {
    render(
      <AcpEventView
        detailLevel="summary"
        item={plan([
          task('inspect', 'Inspect repository', 'completed'),
          task('edit', 'Edit implementation', 'in_progress'),
        ])}
      />,
    )

    expect(screen.getAllByRole('treeitem')).toHaveLength(2)
    expect(screen.queryByRole('button', { name: /^Collapse Inspect repository$/ })).toBeNull()

    fireEvent.click(screen.getByRole('button', { name: /^Plan/ }))
    expect(screen.queryByRole('tree', { name: 'Plan tasks' })).toBeNull()
  })
})
