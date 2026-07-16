# Fault model — apply crash-consistency (v0.1)

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
`apply-completed` (after flip). Recovery after F5 finds a started-without-
completed entry and reconciles by reading which state is active — the
active state's fingerprint decides, the ledger never guesses. Entries are
append-only; reconciliation appends, never rewrites.

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

## 5. Out of scope (v0.1)

- Concurrent applies to one target (v0 serializes per target).
- Cross-target transactions (one changeset, one target).
- The fidelity declaration schema — specified with the adapter API.
