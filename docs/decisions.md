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
