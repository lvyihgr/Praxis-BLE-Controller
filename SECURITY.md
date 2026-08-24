# Security policy

## Supported versions

Security fixes are provided for the latest release on `main`. Historical
releases are not automatically covered.

## Reporting a vulnerability

Please do not disclose a vulnerability in a public issue before a fix is
available. Use GitHub's **Report a vulnerability** private reporting feature on
this repository. Include the affected version, reproduction steps, expected
impact and any proposed mitigation.

You should receive an acknowledgement within seven days. The maintainer will
coordinate disclosure after affected users have a reasonable opportunity to
update.

## Security model

Praxis can start applications and send keyboard or media commands on the
Windows desktop. Treat access to a paired iPad as access to those controls.

- Only explicitly paired panels are authorized.
- Pairing codes are temporary and rate limited.
- Authenticated sessions reject altered or replayed messages.
- Pairing records remain local and can be revoked from the Windows tray menu.
- Bluetooth UUIDs and framing are public identifiers, not security secrets.

Praxis 1.2 authenticates application messages but does not add a separate
application-layer encryption scheme. Message contents are carried over the
local BLE link. Do not treat Bluetooth radio proximity as protection suitable
for highly confidential presentation notes; application-layer encryption is a
planned hardening area.

The project does not attempt to protect a compromised Windows account, a
compromised iPad, or an attacker with administrative access to either device.
