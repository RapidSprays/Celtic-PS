# Decisions

| Date | Decision | Why |
|---|---|---|
| 2026-10-12 | Target one **pinned client build** (24928510) in a copy, not the Steam install. | The live client updates often (6.15.0 today, 7.0.0 in beta). The Steam install must stay unmodified. |
| 2026-10-12 | Stage E (real client on the private server) is the project goal. The owner states the project is authorized. | Owner's direction. |
| 2026-10-12 | The private client strips anti-cheat, Firebase, telemetry, purchases and ads, and **has every live endpoint removed**. The game's own wire protocol is kept. | The owner wants a simpler private setup. Removing live endpoints means a client without anti-cheat can never reach production. Keeping the protocol is less work than rewriting the client's networking. |
| 2026-10-12 | Steam API and ownership checks stay intact. | No DRM bypass. Steam runs alongside. |
| 2026-10-12 | Server in C# / .NET 8, persistence in SQLite. | Same language and protobuf library as the client. .NET 8 SDK is already installed. |
| 2026-10-12 | No Wireshark and no contact with the live game server. | Server responses are rebuilt from the client's own parse code. |
| 2026-10-12 | The server listens on localhost only by default. | Any wider exposure is a separate decision. |
| 2026-10-12 | The private client runs in env **Test** (0) | Uses the world `ip` directly (no DNS), turns on client debug logging, and reports client exceptions to our server. |
| 2026-10-12 | The private client gets its own bundle id (P1) | The live PlayerPrefs hold the owner's real credentials; the private client must never read or send them. |
| 2026-10-12 | The server won't verify the client HMAC or password hashes, and won't embed the client's static keys | They add nothing on a private server. This keeps the client's secrets out of our code. |
| 2026-10-12 | Plan change: there's no anti-cheat to strip (no detectors run). The patch set is P1–P7 in `patch-manifest.md` | Stage B evidence. |
