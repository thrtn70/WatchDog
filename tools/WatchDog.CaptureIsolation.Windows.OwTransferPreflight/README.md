# O/W cross-process handle-transfer preflight

This is a standalone, unregistered Windows x64 feasibility harness. It has no
project reference, solution registration, production route, B1 write release,
journal write, or durability claim.

O creates a fresh run under
`Documents\WatchDog.CaptureIsolation.Windows.OwTransferPreflight`, opens an
independent sibling custody file before W starts, and launches W as the same
binary. The peers bind a `CurrentUserOnly` named pipe to their exact observed
PIDs. O then transfers a rights-reduced trusted-parent directory handle to W.
After ACK, W creates one private directory and two empty files through relative
`NtCreateFile` calls. O adopts rights-reduced read handles from the retained W
process, records intent and success before each ACK, rechecks the handles after
W exits, and removes only the exact known files and empty directories.

The final JSON receipt deliberately leaves native authority, native durability,
trusted-base admission, complete handle inventory, extra-inheritance proof, and
B1 release flags false. It lists the measurements this preflight omits.

W now enters an unnamed kill-on-close job at suspended native birth with
`bInheritHandles=false`. O retains the original process and primary thread
handles, checks their returned IDs and creation time, then resumes exactly once.
Local native result slots are allocated before handle-producing calls. Source
offers bind a lease ID and generation; the writer's pinned close gate is checked
before each duplicate and its originals remain open through ACK.

## Adversarial scenarios

Pass `--run --scenario <name>` to the built executable:

| Name | Expected result |
|---|---|
| `success` | Four transfers, exact ACKs, three post-W-exit metadata observations. |
| `birth-failure` | Query fails after original native process/thread slots are captured, before resume; exact job/process reap precedes cleanup. |
| `source-reuse` | W closes/reopens the same frames file; same numeric coordinate and object ID, changed lease/generation; O rejects before duplication/ACK. |
| `store-failure-<resource>` | Custody append throws after native success is captured; no ACK for that resource; exact reap/cleanup. |
| `stop-after-success-<resource>` | Stop gate closes before synchronous duplicate returns; returned capability is retained for cleanup; no ACK. |
| `writer-death-<resource>` | W/job terminated after native success, before ACK; local read metadata checked after death. |
| `job-close` | Live W enters an infinite noncooperative wait; closing O's job handle signals exact W. |
| `owner-death` | P adopts exact W/process and cleanup handles, kills exact O while W waits noncooperatively, and observes W death without holding W's job handle. |
| `owner-death-<resource>` | P arms before W resumes, adopts separate cleanup custody, then kills O after the selected native success and receiver adoption, before its ordinary ACK. |

Resources are `trusted-parent`, `private-root`, `frames`, and `anchor`.
`--verify-early-contract` runs fixture-free prefix/stage rejection checks.
`--verify-inventory-contract` runs synthetic packet and reconciliation checks
without collecting native handles or creating fixture objects. It uses a trusted
expected context separately from candidate packets, preserves unknown observations
as incomplete, and gives known violations precedence over collection errors.
The inventory route keeps collection, authority, durability, complete inventory,
inheritance, admission and B1 write/release proof flags false. Its synthetic
observations do not qualify the existing native death scenarios.
`--verify-collector-model` checks a pure Windows x64 PSS byte decoder and a
synthetic observer-resource lifecycle model. It reads supplied bytes and name
buffers, never native pointers. It models ownership, operation completion,
walk exhaustion, release failures and timeout without drain. It performs no
PSS calls, process launches or fixture operations. Raw basic access data remains
ambiguous, reserved creation time is not process identity, and attributes do
not establish inheritance. Its modeled release eligibility grants no native
resume/death or capture admission. The accepted inventory reconciler retains
its P-only collector rule; this separate model does not integrate O-collected
packets. Native API access, runtime support, bounded calls, exact file/job
identity and observer effects remain unresolved.
The mutable lifecycle model takes serialized events from one caller; it does
not synchronize simultaneous CLR-thread calls. Releases bind the exact
descriptor coordinate, owner and acquisition operation. Bounded records retain
raw status codes and return tuples, and the synthetic gate requires fresh
liveness for all three P/O/W roles after both resources have been released.

`--verify-runtime-boundary-reducer` checks a separate, fixture-free metadata
reducer using supplied P/O observations and clocks. P commitment, local O
admission, missing entry, immutable first returns and publication are separate.
Finite borrowed-use tickets require exact completion and a distinct drain;
cancellation, timeout or publication cannot grant disposal eligibility. Reserved
terminal records preserve settlement and an immutable cutoff report after
ordinary-event saturation. Descriptor release and fresh aborted cleanup are
Unsupported; W remains Unrepresented. The route performs no callbacks, runtime
scheduling, transport, native work or OS capability operations. All native,
authentication, inventory, production, B1 and release proof flags remain false.

`--verify-in-memory-supervision-driver` checks a deterministic, fixture-free
driver that composes the frozen supervision proposal and diagnostic codec.
Supplied P clocks and queued events advance its deadlines, nonrenewable cleanup
grace and immutable report mailbox. Separate validation/publication ledgers
share one bounded driver accountant; auxiliary counters grant no extra capacity.
The route performs no real transport, timer, scheduler, process or native work.
O termination remains supplied diagnostic evidence; W cleanup is unrepresented.
Native, authentication, inventory, production, B1 and release proof flags remain
false. These checks do not qualify runtime scheduling or native cleanup.

`--verify-diagnostic-envelope-codec` checks a separate, fixture-free diagnostic
wire codec and in-memory transcript with supplied context and result records.
The prospective frame limit is 65,536 bytes including its four-byte prefix;
actual encoded bytes and cleanup/report reservations are checked before
publication. The checks cover strict parsing, immutable first returns, replay
and context rejection, overflow and reserved-capacity protection. They perform
no real transport, timer, process or native operation. Supplied cohort and actor
metadata does not establish peer authentication or native custody; all native,
inventory, production, B1 and release proof flags remain false.
The final report's optional termination field describes exact collector O only,
independent of the sampled O/W target. Separate W cleanup evidence remains
unrepresented and unqualified; an O exit report cannot establish completed O/W
recovery, PSS release or witness validity.

`--verify-supervision-proposal` checks a separate, fixture-free O/P diagnostic
supervision proposal using supplied synthetic identities, capability custody,
monotonic events and process-exit observations. O is the actual collector and
descriptor owner; P is the command issuer and independent supervisor. It composes
the existing observer-resource model and leaves the accepted P-only inventory
contract unchanged. The proposal performs no native calls, launches, transport,
fixture operations, or production registration. It separates deadline expiry,
late cleanup, termination requests, exact exit/reap and unresolved descriptors;
process exit never proves PSS release or unaffected job/DeleteOnClose witnesses.
Application call/event/output budgets reserve resource-free and final-report
capacity. Clock observations and payload sizes are supplied synthetic inputs;
these checks do not qualify a running deadline supervisor or real transport
serialization. They do not bound native call duration or allocation. All native
proof, admission, B1 journal/write/release and release-approval flags remain false.
The standalone project remains unregistered. G1/G3 native topology/support and
G2/G4/G5/G6/G7 access, observer, identity, export and barrier gates remain pending.
Run only the pure verification route for a pure-model qualification; the native
`--run` routes require separate authorization.

A passing expected fault exits 0 with `succeeded=true`; inspect `scenario`,
`expectedFault`, custody events, cleanup and measurement flags. `source-reuse`
fails if the host does not actually reuse the numeric handle coordinate.
The `owner-death` case covers the late barrier after all read adoptions. The four
resource variants cover selected post-success/pre-ACK edges. Actual asynchronous
native cancellation/callback drain and death at other protocol edges remain open.
For early edges, P ACKs the three retained O directories and exact W process
before W resumes. The P-held W process capability adds `PROCESS_DUP_HANDLE`
for direct W-to-P cleanup pulls; its actual mask and noninheritance are queried.
Each P cleanup read binds W as creator/source holder and P as invoking duplicator
and target, with the original source coordinate and lease generation. These reads
do not count as O adoptions or satisfy W's ordinary O ACKs. The final witness
requires an unchanged acknowledged custody prefix and the selected resource's
native success/receiver adoption before its ordinary ACK. The parent edge has
three cleanup objects and no W-created files; read edges require all six objects.
Injected append failure is a controlled IOException, not a disk failure or
crash-recovery qualification. The custody file remains DeleteOnClose.
The witness channel is bounded to 512 KiB and uses PID/nonce binding, not a
production cryptographic bootstrap. W source manifests used for fault cleanup
are trusted fixture metadata, not adopted O read capabilities or authority proof.

Run after a root-owned build:

```powershell
& .\tools\WatchDog.CaptureIsolation.Windows.OwTransferPreflight\bin\Release\net10.0-windows\win-x64\WatchDog.CaptureIsolation.Windows.OwTransferPreflight.exe --run
```

Or invoke through the SDK when an explicit restore/build is acceptable:

```powershell
dotnet run --project .\tools\WatchDog.CaptureIsolation.Windows.OwTransferPreflight\WatchDog.CaptureIsolation.Windows.OwTransferPreflight.csproj -c Release -- --run
```
