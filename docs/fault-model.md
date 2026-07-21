# Fault model — apply crash-consistency (v0.4)

Normative failure model for the Stage apply lifecycle. It exists to make the
first failure mode in the README — the half-applied change — structurally
impossible, not merely unlikely. Implementations and adapters MUST conform.

## 1. Execution model: prepare-all → atomic flip

An apply executes in exactly two stages:

1. **Prepare.** Every facet's new state (schema, data, UI artifacts) is
   completed in a staging area — the branch the changeset was simulated on,
   or one of equal fidelity. Prepare NEVER touches live state. It may fail,
   be retried, or be discarded at any point with zero live effect.
2. **Flip.** After all facets are confirmed prepared, the new state is
   activated by a single atomic pointer swap, provided by the backend
   adapter. Flip either succeeds completely or has no effect.

There is no third stage and no API that mutates live state outside flip.
Rollback is the same primitive: a re-flip to the previous pointer.

## 2. Partial-failure matrix

Every crash point MUST reduce to one of two cells: **prepare-failure (no
live effect)** or **flip-atomicity (adapter contract)**. If an
implementation exhibits a failure that fits neither cell, the design is in
violation of this model.

| # | Crash point | Live state | Resolution |
| --- | --- | --- | --- |
| F1 | during branch creation | untouched | discard the incomplete branch |
| F2 | during prepare, any facet | untouched | retry prepare or discard staging |
| F3 | after prepare, before flip | untouched | resume (flip) or discard staging |
| F4 | during flip | **old or new, never mixed** — guaranteed by the adapter's atomic primitive | recovery reads the active pointer; anything mixed is an adapter contract violation |
| F5 | after flip, before ledger confirmation | new | recovery reconciles the ledger from the active state (see §3) |
| F6 | during rollback (re-flip) | same as F4 | same as F4 |

## 3. Ledger ordering (write-ahead)

The release ledger records two entries per apply: `apply-started` (before
flip: changeset fingerprint, branch fidelity declaration, actor) and
`apply-completed` (after flip). Rollback mirrors the pair (`rollback-started`
/ `rollback-completed`). Recovery after F5/F6 finds a started-without-
completed entry and reconciles by reading which state is active — the
active state decides, the ledger never guesses. Entries are append-only;
reconciliation appends, never rewrites.

Reconciliation is total over exactly this truth table (pending kind ×
active state ref):

| Pending | Active == new ref | Active == previous ref | Active == neither | Active unreadable |
| --- | --- | --- | --- | --- |
| `apply-started` | `apply-completed` | `apply-aborted` | **unresolved** | **unresolved** |
| `rollback-started` | `rollback-completed` | `rollback-aborted` | **unresolved** | **unresolved** |

The verdict returned to the caller (`RecoveryOutcome`) reports this table on
its own axes rather than as a single opaque label: the **row** is
`PendingOperation` (`apply` | `rollback`), the **cell** is `Resolution`
(`completed` | `aborted` | `unresolved`), and the **column** is `Reason`
(`active-matches-new` | `active-matches-previous` | `active-matches-neither` |
`active-state-unreadable`). For the resolved cells the appended entry kind is
exactly `{PendingOperation}-{Resolution}`. A consumer therefore reads the table
straight off the outcome — it never has to re-read the ledger to learn which
row it was on.

An aborted rollback MUST be recorded as `rollback-aborted`, never
`apply-aborted` — the apply is still in effect, and the audit trail must say
so. **Unresolved appends nothing** and the pending entry stays visible until
an operator resolves it; appending a guess would forge the audit trail.

The two unresolved cells are distinct situations and MUST be reported
distinctly (`active-matches-neither` vs `active-state-unreadable`), because
they call for different intervention: the first says the target moved
out-of-band, the second that the adapter cannot account for the target at all
(state lost, partially restored, renamed — adapters MUST throw rather than
invent a pointer, see adapter-api §Error taxonomy).

**Recovery is per-target and total.** A target whose active pointer cannot be
read yields a verdict, not an exception: one unaccountable target MUST NOT
abort reconciliation of the others. Two things still propagate rather than
becoming verdicts — caller cancellation (which is not a judgement about any
target) and a failed ledger append (which means the audit trail itself is
broken, so no verdict about anything is trustworthy).

## 4. Adapter contract

An adapter MUST do one of the following, and MUST declare which,
machine-readably:

- **Provide the atomic swap primitive.** Flip is a single operation that is
  atomic and durable with respect to crashes, and idempotent under a supplied
  apply token (so recovery can safely re-issue it).
- **Declare degraded fidelity.** If the backend cannot provide an atomic
  swap, the adapter publishes a degradation declaration describing the
  non-atomic window's characteristics. Stage refuses to apply through a
  degraded adapter unless the host's policy explicitly accepts the declared
  degradation. Honesty over pretense — the same rule the README's principle 5
  applies to simulation.

## 5. Out of scope

- Concurrent applies to one target (v0 serializes per target).
- Cross-target transactions (one changeset, one target).
- The fidelity declaration schema — specified with the adapter API.
