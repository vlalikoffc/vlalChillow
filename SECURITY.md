# Security policy

## Supported

Only the latest commit on `main` receives fixes. The server targets StandChillow
**2.06 OBT F1**; other client builds are untested and unsupported.

## Reporting a vulnerability

Do not open a public issue for a security problem.

Use GitHub's private vulnerability reporting on this repository:
**Security → Report a vulnerability**.

Please include the affected component (discovery, lobby, match host, plugin
loader), the client build, and reproduction steps. Expect an initial response
within a few days.

## Scope

In scope:

- Remote crash or hang of the dedicated host triggered by a LAN peer
- Unauthenticated state corruption through the lobby or match channels
- Plugin loader escaping its sidecar boundary (a plugin gaining wire access)
- Accidental disclosure of secrets or local reverse-engineering material in the
  published tree

Out of scope:

- Anything requiring physical access to the host machine
- Cheating or client-side manipulation by players on a LAN you control — this is
  a LAN tool with no anti-cheat and makes no such guarantee
- Denial of service by flooding a LAN you already have access to

## Operational note

This server binds UDP 5056, 7778 and 7777 and answers unauthenticated LAN peers
by design. Run it only on networks you control. Do not expose these ports to the
internet.
