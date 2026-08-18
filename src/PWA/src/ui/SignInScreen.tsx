import { auth } from '../auth/impl'
import { VersionLine } from './Chrome'

/**
 * The mark, sized to be the first thing seen.
 *
 * The artwork is a green mark on an opaque black square and the page is slate-950,
 * which is not black, so an ordinary image would put a visibly darker tile on the
 * page. `screen` resolves every black pixel to the backdrop exactly — the blend is
 * `1 - (1 - a)(1 - b)`, so a zero channel returns the backdrop untouched — while the
 * green survives being screened over a near-black page. Colour-keying the black to
 * transparent instead would leave a dark fringe on every antialiased edge.
 *
 * The halo behind it is the same trick in reverse: it stops the mark ending at a
 * square boundary, which is the other thing that would give the tile away.
 */
function Logo() {
  return (
    <div className="relative flex size-40 items-center justify-center sm:size-48">
      <div
        aria-hidden
        className="pointer-events-none absolute inset-0 rounded-full bg-[radial-gradient(circle,rgba(34,197,94,0.16),transparent_70%)] blur-xl"
      />
      <img
        src="/icon-512.png"
        alt="1RemoteCLI"
        width={512}
        height={512}
        className="relative size-full mix-blend-screen"
      />
    </div>
  )
}

/**
 * The signed-out screen.
 *
 * It says what the product is before asking for an identity, because "sign in with
 * Microsoft" on a bare page is a request without a reason.
 */
export function SignInScreen({ busy }: { busy: boolean }) {
  return (
    <div className="flex min-h-dvh flex-col items-center justify-center gap-8 px-6 text-center">
      <div className="flex flex-col items-center">
        <Logo />
        <h1 className="mt-2 text-2xl font-semibold text-slate-100">1RemoteCLI</h1>
        <p className="mx-auto mt-3 max-w-xs text-sm leading-relaxed text-slate-400">
          Attach to the terminal sessions already running on your machines. Read the output, answer
          the prompt, press Esc, from wherever you are.
        </p>
      </div>

      <button
        type="button"
        disabled={busy}
        onClick={() => void auth.signIn()}
        className="min-h-12 w-full max-w-xs rounded-xl bg-slate-100 px-5 text-[15px] font-medium text-slate-900 transition active:bg-slate-300 disabled:opacity-50"
      >
        {busy ? 'Signing in…' : 'Sign in with Microsoft'}
      </button>

      <div className="flex max-w-xs flex-col gap-3">
        <p className="text-xs leading-relaxed text-slate-600">
          Use the same Microsoft account you signed in with on the machine. Your sessions are only
          ever visible to that account.
        </p>

        {/*
          Deliberately reachable before signing in. The button above is a dead end for
          exactly the people this link is for: an account that is not on the allowlist
          signs in successfully and is then refused, so "what is this and how do I set
          up the Windows side" has to be answerable without handing over an identity
          first. A plain anchor, not a route: the page is a static document the service
          worker and the router both leave alone. It opens in a new tab because a
          standalone install has no back button, and navigating away in place would
          strand somebody in a document with no way back to the app.
        */}
        <p className="text-xs text-slate-500">
          <a
            href="/readme.html"
            target="_blank"
            rel="noopener"
            className="underline decoration-slate-700 underline-offset-2 transition hover:text-slate-300"
          >
            New here? Read what this is and how to install it
          </a>
        </p>
      </div>

      <VersionLine />
    </div>
  )
}
