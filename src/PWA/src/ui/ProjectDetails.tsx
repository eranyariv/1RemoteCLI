import type { ProjectInfo } from '../protocol/wire'
import { ProjectIcon } from './ProjectTiles'

export function ProjectDetails({ project }: { project: ProjectInfo }) {
  return (
    <section
      aria-label="Project details"
      className="flex items-start gap-4 rounded-2xl border border-slate-800 bg-slate-900/60 p-4"
    >
      <ProjectIcon project={project} className="size-20 rounded-2xl" />

      <div className="min-w-0 flex-1">
        <h2 className="text-xl font-semibold text-slate-100">{project.name}</h2>
        <p className="mt-1 whitespace-pre-wrap text-sm leading-5 text-slate-400">
          {project.description ?? 'No description provided.'}
        </p>

        {project.siteUrl || project.repoUrl ? (
          <div className="mt-3 flex flex-wrap gap-x-4 gap-y-2 text-sm">
            {project.siteUrl ? (
              <a
                href={project.siteUrl}
                target="_blank"
                rel="noopener noreferrer"
                className="font-medium text-sky-400 underline decoration-sky-800 underline-offset-4"
              >
                Project page
              </a>
            ) : null}
            {project.repoUrl ? (
              <a
                href={project.repoUrl}
                target="_blank"
                rel="noopener noreferrer"
                className="font-medium text-sky-400 underline decoration-sky-800 underline-offset-4"
              >
                Repository
              </a>
            ) : null}
          </div>
        ) : null}
      </div>
    </section>
  )
}
