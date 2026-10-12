# Scope and authorization

## Goal
A working private Celtic Heroes server, for non-commercial use, that a pinned copy of the original client can connect to.

## Authorization
- Stated by the project owner on 2026-10-12: "We are authorized."
- Authorizing party: **_(owner to fill in)_**
- Scope of the authorization: **_(owner to fill in)_**

## Ground rules
1. **The Steam install is never modified.** All runs and changes use a pinned copy at `~/CelticHeroesAnalysis/client/<buildid>/CelticHeroes.app`. Its hashes are checked after every session (`scripts/inventory.sh --verify`).
2. **The private server never contacts DECA, Steam or Firebase back-ends.** Tokens the client sends (Steam ticket, Firebase ID token) are accepted without validation, never stored, and logged as `[REDACTED]`.
3. No other players' data, and no capture or decryption of live-server traffic.
4. Anti-cheat and other outside services are stripped **only from the pinned copy**, and that copy also has every live endpoint removed. It can never connect to production. Checked by the production isolation check (see README).
5. Server-side game data and logic (mob stats, drop tables, spawns, combat and quest resolution) aren't in the client. Anything not found in shipped client data has to be written by us or approximated, and labelled as such.
6. The server listens on localhost by default. Opening it to the LAN or internet is a separate decision, recorded in `decisions.md` before it happens.
7. Git holds only our notes, scripts, server code, hashes and protocol fixtures. Client binaries, patched binaries, decompiled code, asset dumps and client config files (`google-services*.json`) stay out of git and are never distributed.
8. Every claim in these docs is labelled **observed**, **statically inferred**, **experimentally confirmed** or **unknown**.
