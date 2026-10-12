# Confidence ledger

Labels: **O** observed · **SI** statically inferred · **EC** experimentally confirmed · **U** unknown. Confidence: high / medium / low.

| # | Claim | Label | Conf. | Evidence | Alternatives |
|---|---|---|---|---|---|
| 1 | Build 24928510 is v6.15.0 (213) on Unity 6000.3.10f1 | O | high | `inventories/build-info.json` | — |
| 2 | Scripting backend is Mono | O | high | `Data/Managed/Assembly-CSharp.dll`, `MonoBleedingEdge/`, Player.log "Mono path[0]" | — |
| 3 | The pinned copy runs from its own path with Steam running | EC | high | `lsof` txt mapping to the `client/24928510` path; Player.log Mono path | — |
| 4 | At startup the client checks a patch server at `production.ch.decagames.com:443/patchserver/` | O | high | `logs/pin-launch-Player.log` | — |
| 5 | Client patch level is 143; the server reported 60 | O | high | same log | The two numbers may measure different things. Not important. |
| 6 | Firebase starts before SteamAPI_Init and a "token" is retrieved | O | high | same log, `[SharedTech]` lines | The token may be Firebase Auth, AppCheck or a SharedTech token (U) |
| 7 | `st-services-unity.dll` is the "SharedTech" services layer | SI | low | name plus log tag | To check in Stage B |
| 8 | `text.db` holds item and quest display text keyed by id | O | high | `sqlite3 .schema` | — |
| 9 | The server refers to items and quests by these ids | SI | medium | the schema has text only | The ids could be client-local (U until Stage C) |
| 10 | Game server transport and port | U | — | No non-443 socket at the title screen | It may open after login; it may be TLS on 443 |
