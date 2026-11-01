# AiTeammate - Slay the Spire 2 two-player AI teammate mod

Lets a human play P1 while P2 is fully automated (combat, rewards, events, rest sites, shops, Neow, map route).

**Tested on game version v0.111.0 (Windows).** Status: private development, migrating to the next game version.

## Dependencies
| Mod | Notes |
|---|---|
| DualRoleAdventure (local multi-control) | Required. Declared in `mod/AiTeammate.json`. |
| CombatSolver 0.50.1 (Torch, MIT) | Required. Used by reflection for P2 combat plans. Our build uses a locally patched DLL (patch details TBD). |
| STS2-RitsuLib >= 0.6.0 | Required by CombatSolver (tested 0.6.9). |
| SpireStrategist 0.6.22 (MIT) | Used for shop relic valuation. |

None of these are redistributed in this repo. See THIRD_PARTY.md.

## Layout
- `src/` C# source (AiTeammateMod.cs, RewardBackendFlow.cs)
- `mod/` mod manifest, default settings, card score table
- `docs/HANDOFF.md` development log / directives history

## Build
Compiled manually with .NET Framework 4.0 csc.exe (C# 5 syntax), referencing the game's sts2.dll, Harmony and the dependency mods. A build script is TODO.

## Install
Copy `AiTeammate.dll` + the files in `mod/` into `<game>/mods/AiTeammate/`.