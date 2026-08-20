import type { CSSProperties } from 'react'
import { useSortable } from '@dnd-kit/sortable'
import { CSS } from '@dnd-kit/utilities'

export function sortableStyle(sortable: ReturnType<typeof useSortable>): CSSProperties {
  return {
    position: 'relative',
    zIndex: sortable.isDragging ? 20 : undefined,
    transform: CSS.Transform.toString(sortable.transform),
    transition: sortable.transition,
    opacity: sortable.isDragging ? 0.75 : undefined,
  }
}

export { useSortable }
