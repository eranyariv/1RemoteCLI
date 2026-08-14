import { auth } from '../auth/impl'

/**
 * The signed-out screen.
 *
 * It says what the product is before asking for an identity, because "sign in with
 * Microsoft" on a bare page is a request without a reason.
 */
export function SignInScreen({ busy }: { busy: boolean }) {
  return (
    <div className="flex min-h-dvh flex-col items-center justify-center gap-8 px-6 text-center">
      <div>
        <h1 className="text-2xl font-semibold text-slate-100">1RemoteCLI</h1>
        <p className="mx-auto mt-3 max-w-xs text-sm leading-relaxed text-slate-400">
          Attach to the terminal sessions already running on your machines. Read the output, answer
          the prompt, press Ctrl+C — from wherever you are.
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

      <p className="max-w-xs text-xs leading-relaxed text-slate-600">
        Use the same Microsoft account you signed in with on the machine. Your sessions are only
        ever visible to that account.
      </p>
    </div>
  )
}
