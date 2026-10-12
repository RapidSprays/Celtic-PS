# Vendored: lidgren-network-gen3

- Source: https://github.com/lidgren/lidgren-network-gen3
- Commit: f99b006d9af8a9a230ba7c5ce0320fc727ebae0c (Mon Aug 31 17:08:44 2020 +0200)
- License: MIT (see LICENSE)
- Changes: replaced the old-style project with an SDK-style net8.0 project; one source fix in
  `NetReliableSenderChannel.DestoreMessage` (null check before decrement, matching the client fork; upstream
  crashes when an ack arrives during shutdown). Marked with `// CH:`.

The Celtic Heroes client embeds a fork of this library. Wire-relevant code (message types, constants,
packet header encoding, handshake, ping/pong) was compared against the client's fork on 2026-10-12 and
matches; see docs/protocol-notes.md §1.
