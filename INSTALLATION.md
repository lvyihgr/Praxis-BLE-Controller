# Installing Praxis Bluetooth

Praxis Bluetooth has two required applications: the Windows Core and the iPad
Panel. They communicate directly over Bluetooth Low Energy.

## Windows: install from a release

1. Open the repository's **Releases** page.
2. Download `Praxis-Bluetooth-Setup-<version>-win-x64.exe`.
3. Run the installer. No .NET SDK or separate runtime is required.
4. Optionally enable launch at Windows sign-in.
5. Start Praxis Bluetooth from the Start menu. Its icon appears in the
   notification area; right-click it to see status, pairing code and Exit.

The installer is per-user and does not require administrator access. Until the
project has a commercial Windows code-signing certificate, Windows SmartScreen
may show an unrecognized-publisher warning. Verify that the download comes from
the official GitHub Release and compare its published SHA-256 checksum.

Uninstalling Praxis Bluetooth removes its local pairing records, logs and
configuration backups. The iPad's matching Keychain record becomes harmless;
the next installed Core can request a fresh pairing.

Developers can generate both the portable build and installer locally:

```powershell
.\scripts\test.ps1
.\scripts\build-installer.ps1 -Version 1.2.0
```

This requires the .NET 10 SDK and Inno Setup 6. The resulting files are placed
under `artifacts/`, which is intentionally excluded from source control.

## iPad: install from source with Xcode

Apple requires every iPad app installed on a physical device to be signed for
that developer account and device. A universal unsigned IPA from GitHub is not
installable, so the open-source installation path is:

1. Clone or download this repository on a Mac.
2. Open `src/Praxis.Panel.iOS/PraxisPanelIOS.xcodeproj` in Xcode.
3. Select the `PraxisPanelIOS` target, open **Signing & Capabilities**, choose
   your Team and change the Bundle Identifier if Xcode reports a conflict.
4. Connect and trust the iPad, keep Deployment Target at iOS 12.0, select the
   device and press **Run**.
5. Allow Bluetooth when prompted.

An Apple Account signed into Xcode can use a Personal Team for personal-device
testing. Apple requires Personal Team installations to be reprovisioned
periodically. A paid Apple Developer Program account is required for TestFlight,
registered-device distribution and App Store distribution.

## First pairing

1. Start Praxis Bluetooth on Windows and Praxis Panel on the iPad.
2. Open the Windows notification-area menu.
3. Enter its six-digit pairing code on the iPad.
4. After the first successful pairing, both devices retain a local trust record
   and normally reconnect without another code.

## Distribution for non-developers

For ordinary iPad users, the supported long-term routes are TestFlight during
beta testing and the App Store for a public release. Both require the project
owner's Apple Developer Program membership, signing identity and App Store
Connect setup; those private credentials must never be committed to Git.
