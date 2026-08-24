# Contributing to Praxis

Thank you for helping improve Praxis.

## Before opening a change

1. Search existing issues and describe the user-visible problem first.
2. Keep Bluetooth UUIDs, Central/Peripheral roles and the 12-byte Praxis Link
   frame format compatible unless an issue explicitly approves a protocol
   revision.
3. Do not add extracted application logos, Apple-exported system-symbol images,
   signing files, device identifiers or local user configuration.
4. Add or update tests for behavior changes.

## Validation

On Windows:

```powershell
.\scripts\test.ps1
```

To test the end-user installer after installing Inno Setup 6:

```powershell
.\scripts\build-installer.ps1 -Version 1.2.0
```

On macOS:

```bash
swift test --package-path src/Praxis.Panel.iOS
xcodebuild \
  -project src/Praxis.Panel.iOS/PraxisPanelIOS.xcodeproj \
  -scheme PraxisPanelIOS \
  -destination 'generic/platform=iOS' \
  CODE_SIGNING_ALLOWED=NO build
```

## Pull requests

Use a focused branch, explain the behavior before and after the change, list
the validation performed, and call out any compatibility or security impact.
By contributing, you agree that your contribution is licensed under the MIT
License of this repository.
