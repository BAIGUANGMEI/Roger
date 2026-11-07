# Official PawnIO installer 2.2.0

Roger Setup bundles the unmodified official signed `PawnIO_setup.exe`, not separately extracted driver files.

- Release: https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0
- Exact download: https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe
- SHA-256: `1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032`
- Windows Authenticode publisher: `namazso.eu` / `namazso`.
- Product version: `2.2.0.0`.
- Official project: https://pawnio.eu/

`LICENSE.txt` transcribes the redistribution and warranty notice embedded in that exact installer. The original installer explicitly permits redistribution without modification. Roger keeps the installer intact, verifies its hash and Windows signature during packaging, and verifies its hash again before installation. It invokes only the normal signed edition with `-install -silent`, never `-unrestricted`.

The publicly available driver source at https://github.com/namazso/PawnIO has its own GPL-2.0-or-later licensing and interface exception. Those source terms must not be substituted for the distinct notice supplied with this official signed binary distribution. Roger does not relicense, patch, or separately redistribute the extracted driver.

The official release notes document Windows error-code returns and the reboot-required result in silent mode. The command-line usage embedded in the same verified installer documents `-install` and `-silent`.
