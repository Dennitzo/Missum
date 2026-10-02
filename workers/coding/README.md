# Native Windows model runtime

Missum runs `llama-server.exe` directly on Windows for General, Coding, vision and
embedding requests, reading existing Unsloth model files in place.
The gateway and other Missum services continue to run in Docker; the gateway connects
to Windows through `http://host.docker.internal:8081`.

GPU placement is negotiated through the supervisor on port 8082 (router port
plus one), advertised with the `missum-gpu-policy:single-preferred-v1` catalog tag.
Before loading an unloaded model, Missum reads current physical GPU free memory.
Weights (all GGUF shards and any projector) plus the configured VRAM reserve
must fit before attempting one GPU. Qwen3.8-27B prefers physical GPU1; other
models choose the eligible GPU with most free memory. CUDA device indices are
mapped using PCI bus order, independently of physical NVIDIA indices.

The single-GPU trial uses the full training context, all GPU layers, one device
and no automatic CPU offloading. llama itself validates the KV/cache/compute
allocation. A confirmed allocation failure allows exactly one retry using the
existing multi-GPU fitting policy after the failed child has exited. Other
errors are not disguised as VRAM failures. No running model's placement is
changed by catalog refresh. Decisions and failures are recorded in
`%USERPROFILE%/.missum/native-runtime/gpu-placement.jsonl`.

Multi-GPU loads use an explicit `layer` split by default. Layers and their KV
cache reside on their respective GPUs; single-token decoding traverses those
layers sequentially, so both GPUs need not show equal utilization at the same
instant. Utilization alone does not establish token-generation performance.

`MISSUM_NATIVE_MULTI_GPU_SPLIT` (`layer`, `row`, `tensor`) is an optional manual
override, resolved only before an actual multi-GPU model load. The loaded split,
context configuration, cache formats, fitting and GPU-layer settings remain
fixed through catalog refresh, model reuse and session snapshot save/restore.
Changing the environment takes effect on the next model load and changes the
cache fingerprint; it cannot relabel an active model's KV snapshot. Placement
events include these resolved settings without conversation text.

Experimental `tensor` mode requires
`MISSUM_NATIVE_MULTI_GPU_CONTEXT_LIMIT`, an explicit integer token maximum from
4096 to 2147483647 chosen after measuring available VRAM and output correctness.
The effective context is the smaller of that explicit limit and the GGUF's
training maximum. Missing or invalid limits produce a configuration error before
the multi-GPU load. Tensor mode uses `fit = off`, all GPU layers and an explicit
context; unsupported llama automatic fitting is never used, and Missum does not
invent a smaller context on allocation failure. Current installed native builds
must support the model architecture and the configured `q8_0` KV cache; verify
the executable rather than assuming support from a split-mode flag. Single-GPU
trials and embedding cache/context settings remain unchanged. Historical split
measurements from other executables do not establish current tensor behavior.

A separately benchmarked configuration can be stored locally in
`<state-directory>/model-load-policy.json` (schema version 1, `profiles` array).
This optional file is shared by repository and portable launches using the same
native state directory; it is not a machine-wide environment setting and is not
created automatically. Each profile must specify `architecture: deepseek4`,
`split: layer` or `split: tensor`, `context: model-maximum` and
`cacheK/cacheV: q8_0`. Tensor profiles also require `allReduce: none`; layer
profiles leave that field unset. Exact `modelFiles` identities include every shard and the
projector; `runtimeFiles` identities include the executable and DLLs. Each file
identity is `[absolute resolved path, bytes, modification time in nanoseconds]`.
`gpuIdentities` contains exactly two PCI-ordered objects with `pciBusId`, `uuid`,
`name` and `memoryTotalMiB`; `gpuPciBusIds` repeats their ordered PCI addresses.
The catalog helpers `model_file_identity`, `runtime_file_identity` and
`gpu_profile_inventory` produce these values.

Optional profile parameters are `mmprojDevice` (`CUDA0`/`CUDA1`), `batchSize` and
`ubatchSize` (1–8192, microbatch no larger than batch). They are frozen with the
load policy and included in profiled snapshot compatibility. Profiles always use
the complete GGUF context maximum, `fit = off` and all GPU layers. Their exactly
two validated GPUs are selected explicitly as `CUDA0,CUDA1` in PCI order, with
`tensor-split = 1,1`, matching the measured equal allocation instead of selecting
a free-memory-dependent ratio. Both settings are frozen in the profile's cache
identity. Unprofiled and manual split settings retain automatic allocation.
Profiles do not
require the manual context-limit environment variable. Model aliases with the
same weights/projector can match; different models, changed files and replaced
GPUs cannot inherit the tuning. Missing, malformed or stale profiles retain the
normal placement policy and record a concise diagnostic.

The supervisor sets `GGML_CUDA_ALLREDUCE=none` only in its native child process
environment and only for an exact matching local profile. An explicit human
AllReduce or CUDA-graphs override is preserved. An incompatible AllReduce value
or any CUDA-graphs-disable override skips the automatic tensor profile; CUDA
graphs otherwise keep their native default. Adding a tensor profile after startup
may require a supervisor restart to apply its process-wide collective setting.
Layer profiles do not alter collective settings and can match on the next model
load without a process-environment restart. Existing human AllReduce/graphs
overrides remain unchanged for layer profiles. Changing a loaded profile's batch
or projector configuration takes effect only on the next load and changes its
cache identity; ordinary layer loads without a profile retain their legacy cache
identity. Profile selection remains a local, measured choice, not a claim that a
split mode is faster on every model or machine.
An explicit `MISSUM_NATIVE_MULTI_GPU_SPLIT` overrides automatic profile choice.

A confirmed tensor allocation failure or explicit unsupported tensor-mode error
allows one layer retry with the same full context, projector placement and batch
configuration. Other errors are not retried. If full-context layer allocation
also fails, loading fails visibly instead of cutting the context or retrying
indefinitely. The existing default layer/single-GPU cache identity is preserved.

Missum starts the shared native runtime when connecting to its local gateway or
refreshing the local model catalog. The portable package includes the launcher
and scanner in `Assets/NativeRuntime`, so this does not require a repository
checkout. `windows/start-ai-stack.ps1` also starts the same runtime. Its optional
`-NativeModelRoot` (also accepted as `-CodingModelRoot`) selects another existing model directory. The inspected
installation uses `C:/Users/AMD/.cache/huggingface/hub`. The native supervisor
uses Unsloth's existing executable at
`%USERPROFILE%/.unsloth/llama.cpp/build/bin/Release/llama-server.exe`. No model
files or runtime binaries are downloaded or copied.

Reasoning language is independent of the selected effort. Missum adds a German
language instruction at the model request boundary. For recognized Jinja
templates with a final `<think>` generation prefix, the native catalog writes
an external template copy under `native-runtime/templates`: known Qwen effort
instructions are localized without changing their effort semantics. Recognized
Qwen templates also prefill the neutral heading `Überlegung auf Deutsch:` in
the native thinking channel. llama includes this prefill in `reasoning_content`;
the history renderer retains it exactly once, keeping the prompt prefix stable.
This adds no invented reasoning
sentence; all analysis is still generated by the model. Language announcements
are discouraged. Templates without a recognized history form get only the
localized instructions. The
thinking-disabled branch, tool formatting, code and original GGUF remain
unchanged. Unrecognized templates retain their original form and the system
language instruction; no unknown template syntax is rewritten.

```powershell
.\windows\manage-coding-llama.ps1 -Action Start
.\windows\manage-coding-llama.ps1 -Action Status
.\windows\manage-coding-llama.ps1 -Action Stop
# Another existing model directory or binary:
.\windows\manage-coding-llama.ps1 -Action Start -ModelRoot D:\Models -BinaryPath D:\Llama\llama-server.exe
```

Start is idempotent and rejects a port occupied by an unrelated process. Stop
checks the saved supervisor PID, process start time, and command line. A Windows
Job Object binds only this supervisor's server and router children to its
lifetime. Unsloth's own processes and the separate `manage-llama-server.ps1`
installation are not stopped or reconfigured. Processes are launched hidden.

The scanner refreshes `%USERPROFILE%/.missum/native-runtime/models.ini` every ten seconds.
This user-profile path remains the same when launching Missum from a normal Windows
process or an MSIX application. A Windows restart ends the native processes;
opening Missum starts them again. No scheduled task or Windows service is installed.
Refreshing the Coding dropdown calls `/v1/models/coding`; the gateway requests
the native router's `v1/models?reload=1`. Stable path-derived IDs distinguish
quantizations and snapshots. Unloaded models load when their role is requested. The
router keeps at most one model resident. Idle sleep is explicitly disabled
(`--sleep-idle-seconds -1`, also inherited by every child preset). The installed
Unsloth build crashed with `0xC0000005` in `llama.dll` during a sleep/wake reload
triggered by native input-token counting on 2026-09-12. The manager continues to
unload models on actual model/workload switches; an idle model otherwise retains
its allocation. Changes to this process argument take effect at the next managed
runtime restart.
General and Coding share text-model IDs. Dedicated vision presets attach the
matching projector from the same snapshot; dedicated embedding presets use the
GGUF pooling metadata. Model switches remain within this single native runtime.

Only GGUF models with valid metadata are included. Tokenizer-only GGUFs, ASR,
adapters, standalone projectors, and incomplete split sets are excluded.
Unsloth's metadata-only first shard is supported when all tensor shards
exist. Safetensors are not converted or offered as llama.cpp models. Discovery
validates metadata and shard presence; llama.cpp validates tensor contents when
loading. All model accesses are reads.

Text and vision presets start from the GGUF training context maximum; native
memory fitting may reduce the allocated context to fit available GPU memory.
The loaded model's `/props` reports the actual allocation. Embedding presets
use their GGUF context maximum. Supported thinking models use the highest effort
declared by their chat template and no fixed reasoning-token cap. The runtime uses one
slot, Jinja native tool calling, prompt caching and streamed prefill progress.
Automatic GPU fitting reserves
2,048 MiB per GPU for existing workers. `-FitTargetMiB` and `-GpuLayers` adjust
these settings when starting the native supervisor. Large models may require CPU
offloading; model availability does not imply they fit in the available RAM or
generate quickly. Model load errors and the five-minute load limit are explicit.

The Coding loop emits a heartbeat and applies a 20-minute model-turn limit.
Prefill counts, text, tool selection, execution, and failures are distinct events.
No fixed random seed is sent. Complete native tool payloads are validated before
execution. Logs and ownership metadata are in `%USERPROFILE%/.missum/native-runtime`.
Ownership checks use the saved process start time and exact script/state
arguments, allowing repository and portable launchers to reuse the same process.
The app launches PowerShell without redirected output pipes, because the
long-lived supervisor can inherit those pipe handles. It passes a fresh
`-ErrorFile` path for each start; a failed launch writes the original exception
message there and exits with code 1. Normal CLI output remains available.

Coding runs default to 96 model rounds and 96 tool calls. The gateway environment
variables `MISSUM_AI_CODING_MAXIMUM_MODEL_ROUNDS` (2–120) and
`MISSUM_AI_CODING_MAXIMUM_TOOL_CALLS` (1–120) configure those budgets. Independent
reads can share one model round, while tool execution remains sequential.
The last eight rounds warn the model to finish; the last round reserves a
tool-free summary of findings, changes, validation and unfinished work. Reaching
the budget records `agent.run_limit` with that summary instead of claiming the
task was completed. Already emitted tool calls are processed before checking the
next model-round budget. The run duration includes client-tool resume periods.
Every five seconds, a bounded deadline check queues overdue unanswered client
proposals or waiting runs for the same single processor to end with `run.timeout`.
Terminal runs ignore stale queue entries, so a repeated result cannot restart a
completed task or repeat its file changes.

Validation:

Native Coding and General sessions also keep optional KV snapshots under
`%USERPROFILE%/.missum/native-runtime/session-cache`. The gateway prepares a
session before inference and checkpoints a completed response; switching to a
different session or an auxiliary General request first saves the outgoing slot.
Model unload and graceful supervisor shutdown also checkpoint tracked slots.
After a restart, llama.cpp restores the compatible snapshot and compares the
actual prompt prefix before reusing tokens. The database conversation remains
authoritative when a snapshot is missing or incompatible.

Snapshots use hashed model/session keys and model-file, native-binary, template,
and GPU-configuration fingerprints.

Stop and follow-up prompts: the DeepSeek chat template is rewritten so historical
assistant turns replay by their stored `reasoning_content`, not by the current
thinking flag. Switching the reasoning level between two runs of one session
(`none` -> `on`) therefore keeps the whole native KV prefix; only the generation
prompt changes. An interrupted save waits up to 90 s for llama to leave its
current prompt batch (`INTERRUPTED_SAVE_IDLE_SECONDS`), because large models take
tens of seconds per 2048-token batch and the slot cannot serve the follow-up earlier. Cache storage is bounded to 64 GiB with a
4 GiB free-space reserve; the oldest inactive snapshots may be evicted. Cache
failures are recorded in `session-cache/events.jsonl` and fall back to normal
prompt processing without failing the user request. Snapshotting does not load
additional models, create extra inference slots, or shorten the conversation.

General continuation restores the native transcript only when the client's
visible history still exactly matches the preceding request and its published
answer. Edited, removed, compacted or differently scoped history falls back to
the current client transcript. Model selection does not erase conversation
content, but KV tensors are never shared between different models. Returning to
a compatible model can restore that model's own snapshot.

The required cache regression matrix is:

| Scenario | Deterministic regression | Native evidence |
| --- | --- | --- |
| Successive prompts, German reminders, reasoning and tools | `CodingSessionContextTests`, `GeneralSessionContextTests`, `ModelRuntimeClientTests` | `full`: `follow-up` |
| General/Coding and workspace/session isolation | `SessionCacheIdentityTests`, `test_session_cache.py` | `full`: `return-to-session` |
| Model A to B to A | `GeneralSessionContextTests`, `test_session_cache.py` | `full --other-model`: B is content replay; returning to A must report cached tokens |
| App reconnect, persisted history | `GeneralSessionContextTests`, `test_session_cache.py` | `full`: `new-client` (native process remains alive) |
| Native runtime restart | `test_session_cache.py` | `seed-restart`, actual supervised restart, `verify-restart` |
| Cache failure, incompatible model/template, disk limit, cancellation | `test_session_cache.py`, `ModelRuntimeClientTests` | Failed scenarios make the gate exit nonzero |
| Measured reuse versus requested cache or restored file | `SessionCacheIdentityTests`, `CodingModelRuntimeTests` | Every reuse assertion reads native `usage.prompt_tokens_details.cached_tokens` |

`verify_session_cache.py` produces a JSON report and exits nonzero for missing
measurements or insufficient reuse. A snapshot restore alone is not accepted as
proof. Its native test does not validate the UI or gateway; tab-state and full
conversation-path regressions must also pass. Run the test while no Missum generation
is active. `--other-model` intentionally changes model residency.

```powershell
python workers/coding/verify_session_cache.py --model '<native-id>' --other-model '<other-native-id>' --report artifacts/cache-native.json
python workers/coding/verify_session_cache.py --model '<native-id>' --stage seed-restart --state artifacts/cache-restart-state.json --report artifacts/cache-seed.json
# Restart Missum's native runtime with the normal supervised launcher, then:
python workers/coding/verify_session_cache.py --model '<native-id>' --stage verify-restart --state artifacts/cache-restart-state.json --report artifacts/cache-restart.json
python workers/coding/verify_reasoning_language.py --model '<Qwen-native-id>' --report artifacts/reasoning-language.json
```

The language probe uses the production German rule with an English code prompt,
checks both generated reasoning turns, verifies the exact historical prefix and
requires measured reuse of the previous generated tail. Its language check is
specific to that fixed probe, not a guarantee for every future model response.

```powershell
python -m unittest discover -s workers/coding -p 'test_*.py' -v
powershell -NoProfile -File workers/coding/test_launcher.ps1
python workers/coding/catalog.py --model-root C:\Users\AMD\.cache\huggingface\hub --list-models
dotnet test tests/Missum.Ai.Server.Tests/Missum.Ai.Server.Tests.csproj --filter CodingModelRuntimeTests
```

The inspected native build is Unsloth `b10840-mix-d5c17a0`, commit `58670d128`,
Windows CUDA13 older-GPU bundle including SM75 support. Model architecture and
quantization support must be checked against the chosen installed binary.
See the [llama.cpp server protocol](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md).
