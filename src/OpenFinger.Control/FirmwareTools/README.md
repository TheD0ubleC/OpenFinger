Release packaging notes for firmware flashing:

- Preferred release layout:
  - `OpenFinger.Control.exe`
  - `openfinger_firmware_tool.exe`
  - `FirmwarePackages\...`
  - `FirmwareTools\espflash.exe`

- `openfinger_firmware_tool.exe` resolves flashing backends in this order:
  1. Bundled `FirmwareTools\espflash.exe`
  2. Bundled `FirmwareTools\espflash\espflash.exe`
  3. Adjacent `espflash.exe`
  4. Repository-local development binary under `.codex_temp\cargo-root\bin\espflash.exe`
  5. Cargo user binary under `%USERPROFILE%\.cargo\bin\espflash.exe`
  6. Global PATH fallback

- Release builds should ship a standalone `espflash.exe` here.
- Do not depend on Python in end-user releases.
