# Risk register

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R1 | Live client updates (7.0.0 beta) change the protocol | high | low for us | Target the pinned build 24928510. Diff `enums.json` and opcode maps per build with AsmIndex before moving builds |
| R2 | The private client leaks live credentials or telemetry | medium without P1/P5 | high | P1 (separate bundle id), P5–P7 (no telemetry or IAP), isolation check after every patch |
| R3 | The private client reaches production | low after P2/P3 | high | Local env URL and defaults; `lsof` isolation check per session |
| R4 | Stock Lidgren `NetServer` is incompatible with the client fork | medium | medium | Test first in Stage D. Fallback: build the server on the fork's own semantics (protocol-level reimplementation from the observed wire format) |
| R5 | No server-side game data (mob stats, loot, spawns, AI) | certain | high for gameplay depth | The schema is known. Author data per zone, starting with the starter zone; label it approximated |
| R6 | FullCharacterData and other big readers are unmapped, so the client fails to enter the world | high | medium | Field-by-field mapping, fixture tests, and client debug logs plus ClientErrorMessage in env Test |
| R7 | Detectors hidden in Addressables scenes | low | low | The server ignores any reports; scan bundles if symptoms appear |
| R8 | Balance JSON uses `TypeNameHandling.Auto` | low | high (code execution in the client) | Only our own host serves balance; reply VersionCheck OK so no download happens |
| R9 | Steam requirement (RestartAppIfNecessary, Prod quit) | low | medium | Steam stays running; env Test avoids the Prod check |
