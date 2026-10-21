# Long-session performance

## Display and model context are independent

The native client initially reads the newest 24 messages and at most 64 compact
tool headers per message. Tool bodies are fetched on expansion; preceding header
pages use stable persisted ordinals. Completed messages, original Markdown/LaTeX,
tool receipts and model input remain in the database. UI eviction does not change
the model prompt, project memory, compaction boundaries or KV ownership.

Live text is appended to a typed per-message projection. A text fragment never
deserializes and serializes the preceding transcript or tool array. Terminal
snapshots reconcile with the live projection and retain explicitly loaded older
headers. Continuation/subagent auxiliary receipts are separate from display pages,
so timing and delegation do not require loading every historical tool result.

Large messages are indexed into lossless source blocks outside the dispatcher.
Only the viewport and two screens of overscan on each side own native controls;
measured spacers retain scroll geometry. Stable completed text blocks are reused
while the unfinished tail changes. Source ranges own read-from positions, LaTeX
copying and image positions. Selection pins its native runs until cleared.
Markdown preparation and a cancellable single-worker formula rasterizer publish
only results matching the current source version. Hidden views receive state
updates without rebuilding their controls.

The inactive display allowance is partitioned explicitly: 32 MiB for native
inactive child projections and 32 MiB for the decoded subagent transcript cache
(16 Mi UTF-16 characters). The bitmap formula cache has its own 8 MiB bound.
Both inactive partitions evict by access recency; access does not affect tab
ordering. Transcript cache hits validate snapshot/journal length and write time.
Explicitly leased active writers keep their existing journal baseline outside
the inactive allowance, so a transcript exceeding 32 MiB does not force a full
checkpoint on every update. Terminal saves, replaced child attempts and stream
disposal release those leases; persisted `running` alone never pins a transcript.
At most two internal PDF engines are retained, shared by publication file path.
When following the live tail, more than 96 loaded messages are reduced to the
latest 72; earlier pages remain available through the database cursor.

## Storage, project knowledge and caches

Schema 56 moves tool headers and original receipts into indexed per-step rows.
Legacy messages are imported individually in an atomic transaction, after their
first display, with at most two background jobs. Original receipt JSON is retained
losslessly. Stream content, changed receipts and the SSE acknowledgement cursor
commit together; 250 ms write-behind never advances a cursor past its durable
content. Tool boundaries and terminal states flush immediately. Recovery reads
the frozen execution receipt of the resumed message separately from text history.

### Session-only model context (2026-10-21)

Automatic project snapshots, cross-session history search, idle project summaries
and shared KV prefixes have been removed. A new session starts with only its own
conversation, current task and explicitly supplied workspace information. Project
files and publication views remain available through the normal tools.

The `missum-session-v3` cache namespace binds session ID, role and canonical
workspace; native compatibility additionally validates model, runtime, template
and KV geometry. Subagents retain independent branches of their parent run.
There is no mutable shared project cache. Checkpoints without `contextScope:
session-v3` are not restored, because their summaries may contain cross-session
knowledge. The original chat history remains authoritative and is re-prepared
on first continuation. Ordinary session compaction and the model-specific window
remain unchanged. Checkpoint throttling (120 seconds and 8,192 new tokens), stop,
completion, unload and owner handoff persistence remain active.

The measurements below document the earlier project-context implementation and
are historical; they do not establish the current session-only cache behavior.

## Measured checks on 2026-10-17

The separate SQLite benchmark retained all 2,500 receipts (22,167,037 original JSON
characters) and 160,000 source characters. Initial legacy display took 140 ms;
background normalization took 327 ms. Indexed 64-header pages measured p95
0.71 ms and single-step writes p95 0.43 ms on this PC.

The real DeepSeek runtime retained its 1,048,576-token window. Both new native
branches reused all 2,175 shared prefix tokens. The public gateway path reused
7,888 tokens in each new session; independent ALPHA/BETA continuations reused
8,176/8,173 tokens. A changed tool schema was rejected and recomputed correctly.
Reports live under `artifacts/long-session-project-context-20261017/`.

The separate suites passed 1,812 client tests, all 1,263 server tests and all 190
worker tests. Subsequent targeted client checks passed 130 streaming/navigation/
shutdown/subagent cases after the cancellation and cache fixes, plus the
project-history broker test.
Cancellation tests distinguish a completely applied but unflushed event from a
partially applied successor: detachment flushes only the former; explicit Stop
first restores that durable source and finalizes it without advancing its cursor.

The real native replay of the original Bohm conversation used all 19 original
message texts (365,819 characters) with 526 compact headers from 2,479 original
receipts. Final r7 dispatcher p95 was 1.9727 ms, maximum 94.0611 ms, with no UI faults. The
separate 2,500-receipt database case processed 4,096 incremental events at p95
1.9125 ms/p99 7.3745 ms; 34 warm session opens had p95 204.1871 ms. The reopened
message realized 43 of 731 logical blocks; a 5,000-paragraph expanded reasoning
view realized 20 blocks. After complete first-use warm-up, retained managed growth
was 14,631,352 bytes (13.95 MiB), with a 3,432,648-byte (3.27 MiB) range across the
last four navigation cycles. There were no UI projection faults; two PDF engines
were retained. SQLite paging loaded 24, then 48, then 60 messages with zero scroll
anchor displacement. Actual parent and subagent tool expansion hydrated and
rendered their original details; returning to a tab restored the configured fold
default. The complete desktop Portable smoke exited successfully with code 0.
Final reports are `artifacts/portable/win-x64.native-responsiveness-validation.json`,
`win-x64.native-long-session-validation.json` and `win-x64.native-paging-validation.json`.
These measurements are
accelerated checks on this PC; they do not establish overnight stability.

The native database retained all 15 sessions and 95 messages, including their
original content and tool receipts; protected application preferences were
unchanged and integrity checks passed. Validation also exposed an existing
24-hour server cleanup that removed original run journals and evidence. That
cleanup was corrected, and the original journals and receipts were restored from
the pre-change backup. All six affected image artifacts were restored at their
original artifact paths from byte-identical recovery copies. All 12 original
artifact files passed size and SHA-256 verification; all 133,142 original server
events are unchanged. Recovery reports remain under
`artifacts/long-session-project-context-20261017/`; this does not recreate or alter
the original research content.

## Separate native replay

Run after a Portable build in the fresh, isolated profile created by
`windows/smoke.ps1`. Set `MISSUM_SMOKE_UI_RESPONSIVENESS=1` and
`MISSUM_SMOKE_UI_LONG_SESSION=1`; optionally provide a real anonymized replay via
`MISSUM_SMOKE_UI_REPLAY`. No model, Python simulation or user database is used.
There are no test invocations or test switches in `windows/build.ps1`.

The accelerated native case persists 2,500 receipts with approximately 21 MiB of
outputs, opens the real SQLite/host/dispatcher path, and verifies that only 64
compact headers reach the display. It replays 4,096 append/status events while
posting input probes from another thread, scrolls across recycled paragraphs,
checks exact LaTeX selection, explicitly opens a 5,000-paragraph reasoning detail
through the deferred tool API, warms one complete navigation cycle, and performs
two separate eight-cycle navigation series (32 further real session opens). It records
`native-long-session-validation.json` in the isolated data directory.

Acceptance: dispatcher p95 <=100 ms, p99 <=250 ms and maximum <=500 ms; no UI
projection faults; at most 256 realized blocks in this fixture; complete original
copy/read source; unchanged durable content and receipt count; warm session-open p95 <1,000 ms; post-warm-up managed
growth <=32 MiB after repeated navigation and a <=4 MiB range across the final
four cycles. Every cycle samples retained memory after finalization with the UI
dispatcher still pumping. Cold-to-final growth is reported separately from the
baseline after initial expansion and navigation. The report includes working-set and
managed-memory samples, logical/realized counts, deferred payload checks and PDF
engine count. These are measured outcomes, not inferred from a successful build.

## Overnight acceptance

An accelerated replay does not establish overnight stability. Run a standalone
8-12 hour soak after the native replay passes, against a disposable project and
separate profile. Replay realistic text/status/tool arrival rates and large tool
outputs, alternate parent and child views, periodically select/copy formulas,
scroll history and revisit the publication. Record dispatcher percentiles, event
queue age, render/layout time, control counts, decoded-cache bytes, GC allocation,
managed heap and process working set at one-minute intervals. After warm-up,
memory and realized-control counts must plateau rather than grow with elapsed
time. End the producer, drain pending events, and verify replay cursors and the
unchanged stored source. The separate real-model project-prefix checks described
above have passed; they do not replace a long-duration streaming soak.

Actual overnight acceptance remains pending until its duration report has been
produced; unit tests, accelerated replays and builds do not establish it.
