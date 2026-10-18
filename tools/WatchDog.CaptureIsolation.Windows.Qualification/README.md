# Windows capture-isolation qualification fixture

This is an unregistered, explicit-opt-in native fixture for the managed Windows Job Object
adapter. It is deliberately absent from `WatchDog.slnx`, normal tests, pre-push, CI, application
composition, packaging, and release artifacts. Running the apphost without the exact opt-in token
and a valid role exits with usage code `2` before creating a job or process.

## Build separately

From the repository root, restore and build only this standalone project:

```powershell
dotnet restore .\tools\WatchDog.CaptureIsolation.Windows.Qualification\WatchDog.CaptureIsolation.Windows.Qualification.csproj --use-lock-file
dotnet build .\tools\WatchDog.CaptureIsolation.Windows.Qualification\WatchDog.CaptureIsolation.Windows.Qualification.csproj -c Release --no-restore
```

The first authorized restore may generate `packages.lock.json`; it should be reviewed like any
other generated dependency receipt. The fixture targets `net10.0-windows`, `win-x64`, and emits a
headless Windows-subsystem apphost (`WinExe`). Before launching any scenario role, the runner
parses that exact apphost's PE header and requires the `WindowsGui` subsystem.

## Run native qualification separately

Building does not run native qualification. A fresh, explicit authorization is represented by:

```powershell
.\scripts\qualify-windows-isolation.ps1 -RunNative
```

The separate fixture-only rename experiment is opt-in with
`.\scripts\qualify-windows-isolation.ps1 -RunNative -PublicationOnly`.
This runs only `AtomicPublicationProbe`; the default nine scenarios are unchanged.
The distinguishable two-directory experiment uses
`.\scripts\qualify-windows-isolation.ps1 -RunNative -DistinctRootOnly`.
It runs only `DistinctRootRenameProbe` with the same initial access and sharing
profiles. The two opt-in switches cannot be combined.
For either rename probe, the launcher sets the process working directory to its
fresh artifact root, and the probe verifies that directory before the call. This
confines a bare-name resolution fallback to fixture-owned artifacts in the
non-adversarial test; it is not a hostile path-race guarantee.

The script never builds, restores, installs, or searches for processes by name. It resolves the
already-built Release apphost at its literal path, launches one retained runner process, enforces a
210-second outer timeout, and prints the preserved artifact directory and exit code. Calling the
script without `-RunNative` launches nothing and exits `2`.

Artifacts are preserved below
`%TEMP%\WatchDog-Isolation-Qualification\<UTC>-<run-guid>`. They include a manifest, per-role
JSONL events, bounded coordination records, `result.json`, `report.md`, and redirected runner
output. Coordination records are nonce-bound fixture synchronization. The bootstrap scenarios
provide evidence only for the exact unregistered fixture/binary set; they are not package or
publisher evidence and do not establish production registration.

The manifest preserves the apphost hash passed by the launcher and requires it to match a fresh
runner-computed SHA-256. It also records fresh SHA-256 hashes of the exact loaded/output
`WatchDog.CaptureIsolation.Windows.Qualification.dll`,
`WatchDog.CaptureIsolation.Windows.dll`, `WatchDog.CaptureIsolation.dll`, and
`WatchDog.CaptureProtocol.dll`. Missing files, loaded/output path mismatch, PE inspection failure,
or apphost-hash mismatch fail before scenario launch.

## Scenarios and limits

The nine default scenarios qualify:

1. explicit inner-job termination of a resumed helper and its one inherited descendant;
2. helper/descendant termination when the sole inner-job owner dies while the runner retains only
   independently verified process handles and never receives the inner job handle;
3. post-create cancellation retaining exact suspended ownership through termination and reaping.
4. a restrictive local message-mode bootstrap with exact bidirectional process identity,
   authenticated correlation/limits, explicit inherited-handle allowlisting, and denial of an
   inheritable sentinel handle that was not allowlisted;
5. rejection of deliberately corrupted per-launch bootstrap secret material, requiring an
   authentication-tag failure at descriptor decode, followed by exact independent job
   termination and terminal reaping.
6. rejection of a second same-user process that receives the same allowlisted material but does
   not match the parent's exact retained helper PID/creation identity; both independently owned
   jobs are terminally reaped.
7. fail-closed cleanup when the exact helper breaks the pipe after its authenticated hello but
   before completing the mutual handshake.
8. create-new, bounded write/flush/readback and no-overwrite behavior through a child handle
   opened relative to the exact retained fixed-local root handle;
9. real mount-point mutation between root revalidation and relative create, requiring either
   fail-closed rejection or exact-handle confinement to the retained root, unchanged outside
   canary data, exact confined bytes when created, and confirmed cleanup.

Each process-launch scenario uses a fresh outer safety job. The media-root scenarios execute in
the retained runner and use fixture-owned scratch directories, exact-handle cleanup, outside
canaries, and a run-level deadline. Cleanup uncertainty overrides the original failure.
Fixed fixture deadlines are 10 seconds for readiness, 10 seconds for terminal assertions, 15
seconds for cleanup, 40 seconds per scenario, 180 seconds per run, 210 seconds in the launcher,
and 120 seconds for child emergency expiry.
The bootstrap-only fixture limits are a 10-second handshake, 5-second drain, one in-flight queue
slot, 4096-byte frame cap, 2048-byte payload cap, 8 pending commands, and 32 telemetry samples.
These are qualification values, not production defaults.
The partial-handshake receipt observes a child abort after Hello and a broken
parent transport; it does not uniquely prove that the abort caused the parent's
transport error if an unrelated I/O break occurred at the same time.

Exit codes are stable: `0` pass, `2` usage/prerequisite, `3` assertion failure with confirmed
cleanup, `4` timeout with confirmed cleanup, `5` cleanup incomplete or ownership uncertain, and
`6` infrastructure failure with confirmed cleanup.

A passing receipt qualifies only the exact Windows nested-job environment and the headless
Windows-subsystem fixture/binary set recorded for that run, including its bounded unregistered
bootstrap and handle-relative media-root scenarios. It does not qualify a console helper, a
production subsystem decision, package authenticity, production registration, media-root
evidence/admission, mapped-network roots, atomic publication, OBS, UI, devices, production
timing, packaging, release readiness, or shipment.

The optional `AtomicPublicationProbe` uses the legacy Win32 `FileRenameInfo` class
under a fixture-owned local scratch root. A passing run proves only its exact
same-directory no-replace success and existing-file collision observations
for that source/binary/environment. Historical null-root controls returned
success without a verified destination, and Win32 permits current-directory
resolution, so this scenario does not prove that `RootDirectory` determines
the destination. It does not qualify `FileRenameInfoEx`, the
NT API, broader rights/share/reparse/hard-link races, process-kill outcomes,
power-loss durability, a managed transaction, or any production/release gate.

The separate `DistinctRootRenameProbe` opens fixture-owned source and destination
directories on the same volume and passes the destination handle as
`RootDirectory`. A pass requires a retained-root-relative observation of the
final file only in that destination, with the original file identity/bytes.
A failed rooted call is verified unchanged; no null-root or path-based fallback
is attempted. It does not qualify collision races,
adversarial path mutation, power-loss durability, the managed publication
transaction, production admission, OBS, or release readiness.

Both atomic-publication native modes are currently **not admitted**. The launcher
rejects `-PublicationOnly` and `-DistinctRootOnly` before starting an apphost,
and the fixture's rename helper independently rejects a native call. The
preflight now uses a retained-root-relative `NtCreateFile(FILE_OPEN)` observer
for source, destination, working-directory, and scratch-root outside entries.
Source, destination, and outside canary directory entries are matched against
retained handle identities and bytes. Fixture cleanup retains handles if
delete marking or explicit close fails, and managed fake-handle tests cover
retry and serialization. These changes are built and managed-tested only; the
native rename modes have not been run after them. The observer's exact NT
status/information/returned-handle decision now has managed matrix tests, and
fake-handle tests cover observer-close retry and shared-gate ordering. These do
not inject faults into `NtCreateFile` or prove native rename-versus-close order.
Adversarial process-global working-directory mutation and native launch review
remain open before re-enablement. The supported launcher now hashes its apphost
and four fixed sibling DLLs from retained read-only, no-delete streams held
through runner completion. Before any scenario, the runner compares the four
expected hashes to same-stream output-file hashes and requires each file's PE
MVID to match the loaded assembly's MVID and expected output path. Managed tests
cover hash parsing and disk/MVID mismatch; the revised launcher has not been
executed. This is bounded disk/loaded-module consistency evidence, not a
cryptographic attestation of in-memory bytes or protection against hostile
path redirection. Git/source hashes in the manifest are launcher-reported.
Before its first artifact write, the apphost now requires a pre-existing,
nonce-named run directory under the qualification temp namespace. A runner
admits only an empty root or the launcher's empty stdout/stderr redirects; a
child requires the matching scenario subdirectory, parent manifest nonce, and
runner scenario-start log. Admission rejection writes no artifact and returns
prerequisite code 2. Artifact writes no longer create missing directories.
This guards against accidental direct-runner reuse or arbitrary pre-populated
roots, but the path/manifest checks are not caller authentication or retained
directory identity. A crafted direct child invocation or hostile rename,
junction, working-directory, or path swap can still redirect later operations;
the launcher creates redirect files before apphost admission. Direct apphost
invocation lacks the launcher's retained-binary guard and is unsupported with
untrusted arguments or filesystem actors. None of these checks qualifies native
publication.
