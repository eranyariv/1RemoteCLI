import type { ReactNode } from 'react'
import {
  closestCenter,
  DndContext,
  KeyboardSensor,
  PointerSensor,
  useSensor,
  useSensors,
  type DragEndEvent,
} from '@dnd-kit/core'
import {
  SortableContext,
  sortableKeyboardCoordinates,
  useSortable,
  verticalListSortingStrategy,
} from '@dnd-kit/sortable'

export function SortableList({
  ids,
  onMove,
  children,
}: {
  ids: readonly string[]
  onMove(activeId: string, overId: string): void
  children: ReactNode
}) {
  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 6 } }),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
  )

  const dragEnded = (event: DragEndEvent) => {
    if (!event.over || event.active.id === event.over.id) return
    onMove(String(event.active.id), String(event.over.id))
  }

  return (
    <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={dragEnded}>
      <SortableContext items={[...ids]} strategy={verticalListSortingStrategy}>
        {children}
      </SortableContext>
    </DndContext>
  )
}

type SortableResult = ReturnType<typeof useSortable>

export function SortableGrip({
  sortable,
  label,
}: {
  sortable: Pick<SortableResult, 'attributes' | 'listeners'>
  label: string
}) {
  return (
    <button
      type="button"
      {...sortable.attributes}
      {...sortable.listeners}
      aria-label={label}
      className="flex min-h-10 w-9 shrink-0 touch-none cursor-grab items-center justify-center rounded-lg text-slate-600 transition hover:text-slate-300 active:cursor-grabbing active:bg-slate-800"
    >
      <svg viewBox="0 0 16 20" className="h-5 w-4" fill="currentColor" aria-hidden>
        <circle cx="5" cy="4" r="1.5" />
        <circle cx="11" cy="4" r="1.5" />
        <circle cx="5" cy="10" r="1.5" />
        <circle cx="11" cy="10" r="1.5" />
        <circle cx="5" cy="16" r="1.5" />
        <circle cx="11" cy="16" r="1.5" />
      </svg>
    </button>
  )
}
