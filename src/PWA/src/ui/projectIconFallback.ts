import type { ProjectInfo } from '../protocol/wire'

const APP_ICON = '/icon-192.png'
const GENERAL_PROJECT_ICON = '/general-project.png'

/** Returns the built-in fallback for a project that has no uploaded icon. */
export function defaultProjectIconUrl(
  project: Pick<ProjectInfo, 'isGeneral'> | null | undefined,
): string {
  return project?.isGeneral ? GENERAL_PROJECT_ICON : APP_ICON
}
