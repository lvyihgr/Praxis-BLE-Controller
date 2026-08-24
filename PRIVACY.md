# Privacy

Praxis Bluetooth is designed for local, direct communication between a Windows
PC and an iPad over Bluetooth Low Energy.

- Praxis does not include analytics, advertising, telemetry or cloud upload.
- Control commands, application state, system summaries, PowerPoint titles,
  recent-file paths and speaker notes are not uploaded by Praxis; they travel
  only over the local BLE link between the PC and iPad. See `SECURITY.md` for
  the current authentication and confidentiality boundary.
- Application icons are extracted locally from applications installed on the
  user's Windows PC, transferred to the paired iPad and cached on that iPad.
- Pairing records and local configuration are stored only on the corresponding
  device unless the user copies them.

Windows and iPadOS may collect diagnostics according to the user's operating
system settings and the applicable Microsoft or Apple privacy policies. Such
collection is outside Praxis.

Please report an unexpected data flow privately using the process in
`SECURITY.md`.
