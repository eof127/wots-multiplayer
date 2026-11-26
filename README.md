# WotS Multiplayer — a live multiplayer campaign for War on the Sea

An unofficial, free and open-source (MIT) fan mod that turns the War on the Sea Solomons campaign into a live game: one
player commands the Allies, the other Japan, on a shared campaign map in real time, and every battle between them is
fought live, each player commanding their own ships and planes. Up to four more players can join a side as subordinates
of its commander. Not affiliated with or endorsed by Killerfish Games.

**Version 0.28.0 (beta).** Every player needs War on the Sea on Steam (the same game version) and the same mod version.

## Download and install
Get `WotS_Multiplayer_v0.28.0.zip` from the **Releases** page. It contains the mod plus an unmodified copy of BepInEx
5.4.23.6 (the mod loader): copy the contents of its "Game folder" next to `WarOnTheSea.exe`, start the game and use the
new **MULTIPLAYER** button under CAMPAIGN. The full player guide (install, room codes, connecting over the Internet or a
VPN, rules, settings, uninstall, safety) is [`release/README.txt`](release/README.txt), also inside the zip.

## Patch notes — v0.28.0 compared with the base game

### New: multiplayer
- **MULTIPLAYER menu.** The host creates a room and sends a short room code; friends paste it to join. No server and no
  Steam lobby: works on a local network, with port forwarding or over a free VPN (Tailscale, ZeroTier…). 2–6 players.
- **Setup screen.** The host picks their side (Allies or Japan); the friend gets the other. Each side's economy can be
  set separately: starting command points, supplies, fuel, engineering and troops, weekly income, upgrade costs, port and
  airfield limits, the refund for released ships and the points for sinking ships. Saved multiplayer campaigns can be
  continued from the same screen; multiplayer saves get their own "Multiplayer (…)" names and never overwrite
  single-player saves.
- **Shared real-time campaign.** Both players give their own orders with the normal interface: form, split, merge and
  release task forces, plot courses, launch strikes and scouts (scout patterns and repeat courses included), CAP, land,
  strategic bombing, bombard, cargo, base upgrades and resupply. The host's game runs the campaign; the other players'
  maps follow it, each with its own fog of war.
- **Encounters for two.** When the players' forces meet, both get the encounter panel with a 15-second bar. The battle
  is skipped only if both choose Ignore; if either engages it is fought. No answer counts as Ignore.
- **Live battles on every PC.** The host's PC runs the battle; everyone commands their own ships and planes with the
  usual controls (helm, waypoints, guns, torpedoes, aircraft, damage control, sensors). The battle starts when both
  commanders press Start. Both sides can use the recognition manual for the fire-control bonus, and both get the
  after-action report and their command points.
- **Retreating by agreement.** If both players disengage, or the other player presses "Allow retreat", the battle ends
  at once. Otherwise the retreating player runs out the game's disengage countdown, then needs 30 seconds (game time)
  without a new hit before leaving; both screens show the countdown bars.
- **Speed voting.** Slowing down is instant; speeding up and pausing are proposals the other player must accept (amber
  window, big ACCEPT / DECLINE buttons, countdown, soft chime). Each player has 3 "Force" tokens per battle and 3 per
  in-game week to skip the vote.
- **Chat** in the speed window and in the F11 panel.
- **More players (3–6).** Players 3 and up join a team as subordinates of the host or Player 2. They see their team's
  map, give campaign orders, help command their team's ships and planes in battle, and chat. Commanders keep the speed
  and encounter votes, Start and retreat decisions. The force or ship a commander has selected takes no orders from
  their subordinates, nor anything the commander locks (select it and press K, or use the speed window's button); locked
  forces show "LOCKED" on the map. The host can move a subordinate to the other team before the campaign starts, or
  remove one.
- **Rejoining.** If someone drops, the campaign pauses and the room stays open; they join again with the same code and
  get their place back (Player 2's slot is kept for Player 2 unless the host releases it).

### Changed rules
Also in single-player (set the value to 0 in the config file for the original behaviour):
- **Bigger air battles:** up to 60 planes per side are pulled into a campaign battle (base game: 24).
- **Closer encounters:** forces can meet down to 1 km apart (base game: 6 km, so ships often slipped past each other at
  night).

Multiplayer only:
- **Closer battle starts:** fleets start at 3/4 of the distance at which they met, never closer than 5 km.
- **Several task forces per battle:** every other ship task force within 20 km of its side's fleet in the encounter
  joins at its real map position, up to 3 per side, even right after another battle (base game: one ship task force per
  side, plus air groups within 50 km).
- **Damage slows task forces:** a task force moves only as fast as its slowest ship, counting destroyed propellers,
  funnels and engine rooms and flooding; the fleet panel says which ship holds it back. (Single-player: optional.)
- **Fair for both sides:** both players earn command points for the ships they sink, get their weekly income every week
  and the refund for repaired ships; the campaign and combat difficulty multipliers are neutral (1.0) for both, so the
  setup screen is the only handicap.
- **No AI for human forces:** Player 2's fleets and planes never use the game's AI behaviour (no automatic courses,
  chasing, recalls, scout launches or retreats).
- **After a battle** the enemy's surviving forces stay on the map and fade out over time instead of vanishing at once.
- **Agreed speed holds:** the game's automatic pauses for notifications and menus are skipped (their messages still
  show); loading, the encounter panel and the Esc menu still pause.
- **No TACTICAL view** in multiplayer (its battles would be fought by the AI).

### Interface
- New day/night bar on the campaign map: a sun crosses it by day, a moon by night, with the start and end times at its
  ends (multiplayer default; optional in single-player).
- New windows: speed window (votes, chat, players by side, lock button), encounter time bar, retreat bars, F11 panel.
- Fleet panel note when battle damage limits a task force's speed; "LOCKED" / "P1 SELECTED" badges for subordinates.

### Single-player
Works as before. Besides the two changed rules above, everything is opt-in in the config file or the F11 panel:
auto-resolve fighter/scout encounters, the new day/night bar, damaged ships slowing task forces, speed-vote test mode.

### Not changed
- No unit stats, damage, weapons, sensors or other balance data.
- No game files: the mod patches the game in memory through BepInEx; remove it and the game is exactly as before.

## Building from source
- Requirements: Windows, War on the Sea installed (the build references its `Managed` assemblies), the .NET SDK
  (the plugin targets .NET Framework 3.5 via reference assemblies), BepInEx 5.4.23.6 in the game folder.
- Build + deploy: `powershell -ExecutionPolicy Bypass -File tools\Build.ps1` (the game must be closed).
- Logic and network tests (no game needed): `tools\Test.ps1`. Release zip: `tools\Package.ps1`.
- The repository folder is expected inside the game folder (`War on the Sea\_MultiplayerMod`), as the scripts locate
  the game from there.

## Repository layout
- `src/WotSMultiplayer/` — the BepInEx plugin (Harmony patches; host-authoritative networking over TCP).
- `tests/` — logic and loopback network tests. `tools/` — build, test and package scripts.
- `release/` — the player guide, license and third-party notices that go into the zip.

## License
The mod's own code is MIT licensed ([`release/LICENSE.txt`](release/LICENSE.txt)), © WotS Multiplayer contributors.
The bundled BepInEx, Unity Doorstop, HarmonyX, MonoMod and Mono.Cecil keep their own licenses
([`release/THIRD-PARTY-NOTICES.txt`](release/THIRD-PARTY-NOTICES.txt), `release/licenses/`). War on the Sea is
© Killerfish Games and is not included.
