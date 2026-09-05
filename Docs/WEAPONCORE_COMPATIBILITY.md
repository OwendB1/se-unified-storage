# WeaponCore compatibility

Unified Storage reads WeaponCore's mod API on both the client plugin and Magnetar companion. No additional world mod, assembly reference, configuration switch, or profile migration is required.

`GetCoreWeapons` identifies weapons by full block definition ID, before vanilla class-based discovery. This includes weapons implemented as conveyor sorters or other non-weapon block classes. Their inventories appear in the Weapons family with the Ammunition role.

`GetAllWeaponMagazines` supplies the physical magazine IDs for each definition. The adapter unions all weapon parts and ammo variants, removes duplicates, and excludes empty/`Energy` magazine names, the `Energy` placeholder definition, unresolved IDs, and non-magazine items. The API tuple's boolean is `SkipAimChecks`, not an energy flag. Ammo compatibility does not depend on the weapon's currently selected round.

Every offered magazine must also pass the destination's live inventory constraint. Known weapons without physical magazine mappings remain weapons but accept no guessed vanilla ammunition. Existing loadout target resolution, item choices, supply planning, and per-member quantities consume these shared roles. To fill one weapon definition, select it with a Block Definition inventory group, then create an Ammunition loadout for that group. Conveyor, access, working-state, capacity, and conflict checks still apply.

The adapter registers after the world is ready and requests the API through channel `67549756549`. It retries discovery and refreshes mappings every 300 plugin updates to handle delayed initialization. Changed sets invalidate cached client inventory views; changes in API enumeration order do not. API query exceptions discard stale/partial ammo while retaining known weapon ownership and emit one warning per world. Empty endpoint broadcasts and world/plugin unload clear API state; world unload also removes the message handler. Worlds without the API use normal discovery.

## Validation

Run:

```sh
dotnet build ClientPlugin/ClientPlugin.csproj -c Release -p:RunPostBuildEvent=Never
dotnet build ServerPlugin/ServerPlugin.csproj -c Release -p:RunPostBuildEvent=Never
dotnet run --project Tests/UnifiedStorage.CoreTests.csproj -c Release
```

The executable checks exercise the production adapter with minimal game-boundary doubles: absent/delayed API, full-ID matching, multi-part union, energy and invalid ammo, reordered data, late mappings, endpoint failures/recovery, throttled retries, unload, and session replacement. Builds verify real client and server API signatures. These checks do not launch SE or perform real inventory transfers.

In-game acceptance remains pending:

1. Load a WeaponCore world with a sorter-based gun, a converted vanilla gun, a multi-ammo weapon, and an energy-only weapon. Verify Weapons membership for each inventory-bearing block, including empty inventories.
2. Create a Block Definition group for each gun. Verify loadout choices contain its physical magazines only; energy-only weapons offer no physical ammo. Verify unrelated definitions sharing a subtype string do not match.
3. Fill each weapon type from cargo, including a multi-part weapon. Confirm requested per-member quantities, no incompatible magazine transfers, and rejection when live constraints or conveyor routes disallow delivery.
4. Repeat with companion-owned loadouts on a dedicated server. Confirm client and server resolve the same targets and magazines.
5. Keep the terminal open across delayed API availability; confirm groups refresh without reopening. Leave the world, join a vanilla world, then rejoin WeaponCore. Confirm no stale classification, ammo, or duplicate handlers.

API contract inspected at WeaponCore revision `f587bfcb2ebb191db8aba057c10e06aa89beb264`: [ApiBackend.cs](https://github.com/Ash-LikeSnow/WeaponCore/blob/f587bfcb2ebb191db8aba057c10e06aa89beb264/Data/Scripts/CoreSystems/Api/ApiBackend.cs), [ApiServer.cs](https://github.com/Ash-LikeSnow/WeaponCore/blob/f587bfcb2ebb191db8aba057c10e06aa89beb264/Data/Scripts/CoreSystems/Api/ApiServer.cs), and [CoreStructure.cs](https://github.com/Ash-LikeSnow/WeaponCore/blob/f587bfcb2ebb191db8aba057c10e06aa89beb264/Data/Scripts/CoreSystems/Definitions/CoreStructure.cs).
