# Claude Science skill v1

Claude Science selects `WorkflowProfile=claude-science-v1`. Other modes keep their
existing tool schemas, instructions and plan lifecycle. The skill is embedded in
the gateway assembly from `Skills/claude-science/`, without a skill installer or
a second planning tool. Its core and stage instructions load at run start;
verification/publication modules load only when needed. Focus messages append
only when the active stage, pending steps or an observed repeated failure changes.
Existing evaluated prompt prefixes are never rewritten during a run.

`coding.updatePlan` accepts `scienceStages` only in this profile. Each stage uses
the same ID as its ordinary plan step, plus question, strategy, dependencies,
checks, open questions, blockers and outcome (`open`, `clarified`, `refuted`).
Updates are atomic, omitted fields remain unchanged, checks merge by ID, and
required checks cannot be silently weakened. A completed stage needs a classified
outcome and genuine successful work evidence. A counterexample/refutation can be
a valid result. An unresolved question remains open, independent tasks may proceed.
Assigning a receipt already validates its tool kind and success, even for an open
stage. A rejected assignment names the field to replace and up to three actual
matching receipt IDs; it cannot receive a misleading successful review acknowledgement.
Changing a completed question/strategy requires a justified reopen. Reopening or
changed bound input hashes invalidates dependent stages and their old receipts.

Methods are selected for the claim: sources, analytic checks, Lean, numerical
validation, plots or publication. There is no mandatory universal sequence.
Lean initialization, printed success strings, successful reads and failed commands
cannot satisfy a Lean/numeric check. Lean requires KernelAccepted, a passed axiom
audit and its checked source-file hash. The mathematical relevance of a theorem,
adequacy of physical assumptions and genuine independence of a calculation still
require scientific judgement; the gate does not certify these semantically.

New, unevaluated tool responses include host-owned evidence IDs and file bindings.
Successful delegated evidence is imported from the authorized subagent's actual
completion receipts, not from claims in its prose. The parent confirms bound files
before using these receipts. Explicit `paths` bind numerical scripts/data and the
publication; checked Lean sources and read sources bind automatically.

Before a stage closes, the host refreshes only its bound paths through ordinary
readonly client operations (one displayed line plus the actual whole-file SHA).
Before the final stage closes, all checked paths are refreshed. These operations
use durable deterministic proposal IDs and a checkpoint flag, survive restart,
do not require extra model turns, and never scan the project tree. File observations
may persist even when a plan update is rejected; the plan update itself stays atomic.
Subsequent uncertain file activity prevents a false final success. Source changes
after the final snapshot remain ordinary concurrent filesystem changes, not an
atomic workspace lock.

The structured state survives context compaction and continuation of the same
session. A fully settled Science plan remains in its historical checkpoint;
the next task gets empty plan lists while retaining findings and evidence, so
successive tasks cannot exhaust the bounded stage list. Normal Codex plans retain their run-local lifecycle. Entering the new
profile from an older session changes the policy/schema prefix once and therefore
may require a normal KV prefill. No shared project context or cache is introduced.

Run completion includes optional `scientificOutcome`. Open work adds a visible
partial-completion note to the ordinary assistant message; it does not force a
reasoning/retry loop. Publication.md remains the directly edited project document
with the existing numbering/TOC/layout, readable mathematical proofs, contextual
plots and `Literatur und Daten`. Used sources belong under `Quellen/`; missing
access or downloads must be reported, not invented.

Tests remain separate from `windows/build.ps1`. Live acceptance uses the existing
native tools, a real selected model and an isolated gateway data volume.
