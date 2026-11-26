WotS Multiplayer {VERSION} — two-player campaign mod for War on the Sea
=======================================================================

Play the Solomons campaign as two people: one commands the Allies, the other
Japan, on a shared campaign map in real time, and fight the battles live
against each other. Free and open source (MIT). An unofficial fan project, not
affiliated with Killerfish Games. Beta: expect rough edges and keep backups of
your saves.

Both players need: War on the Sea on Steam (the same game version — the mod
checks), Windows, and THIS SAME MOD VERSION.


1. INSTALL (both players)
-------------------------
 1. Close the game.
 2. Find the game folder: Steam → Library → right-click War on the Sea →
    Manage → Browse local files. (Usually
    C:\Program Files (x86)\Steam\steamapps\common\War on the Sea)
 3. Copy EVERYTHING inside the "Game folder" folder of this download into the
    game folder (next to WarOnTheSea.exe). Windows may ask for permission to
    copy into Program Files: allow it.
    You should then have: winhttp.dll, doorstop_config.ini, .doorstop_version
    and a BepInEx folder next to WarOnTheSea.exe.
 4. Start the game. The first start takes a little longer. In the main menu you
    now see a MULTIPLAYER button under CAMPAIGN, and a small "WotS MP {VERSION}"
    line at the top left.

Your single-player game keeps working. Multiplayer saves get their own names
("Multiplayer (Host) …"), so your single-player saves are never overwritten.


2. PLAY
-------
Host (the player whose PC runs the campaign):
 1. MULTIPLAYER → Create a Room. Type your name.
 2. Pick the address that goes into the room code:
     - Internet: your public IP (see "Connecting over the Internet" below),
     - a local network or VPN address (Tailscale, ZeroTier, Radmin VPN…) if
       you both use the same network/VPN — the easiest way,
     - This PC only: for testing two copies on one PC.
 3. Create the room. You get a code like ABCD-EFGH-JKLM-NPQR (already copied
    to the clipboard). Send it to your friend.
Friend:
 4. MULTIPLAYER → Join a Room → paste the code → Join.
Both:
 5. The setup screen opens on both PCs. The host picks which side they play
    and the economy for each side (command points, supplies, income, upgrade
    costs…); the friend sees the values live and presses Ready.
 6. The host presses Start campaign. Both campaigns load; the game starts
    paused — one player proposes Resume in the speed window, the other accepts.

Continue later: the host saves the campaign as usual (Esc → Save, or the
campaign summary); next time, after both are connected, the setup screen lists
the host's saves under "Or continue a saved multiplayer campaign" (the friend
presses Ready, the host presses Continue). The autosave made at the start of
every battle is listed too.

If the connection drops: the host's campaign pauses and the room stays open.
The friend goes back to the main menu and joins the room again with the same
code; the host's game sends the campaign as it is now.

More than two players (3 to 6): the host chooses the number of players under
Create a Room (or [Multiplayer] MaxPlayers). Players 3 and up join with the
same room code and pick a team under Join a Room (Auto, the host's team or
Player 2's team). They are subordinates of their team's commander (the host or
Player 2):
 - they wait at the main menu, where the setup screen shows the settings; when
   the campaign starts or is continued, their team's map loads by itself.
   Someone who joins later gets the campaign as it is once the host is on the
   map;
 - they give campaign orders to their team's forces, and in battle they help
   command their team's ships and planes (helm, guns, damage control);
 - they don't vote on speed or encounters; Start and retreating in battle are
   the commanders' decisions;
 - the force or ship their commander has selected takes no orders from them,
   nor anything the commander has locked: the commander selects it and presses
   K (or the button in the speed window). Locked forces show "LOCKED".
The host's player list (F11, or the setup screen) can move a subordinate to
the other team before the campaign starts, or remove one. Anyone who drops
during a campaign joins again with the same code and gets their place back:
Player 2's slot is kept for Player 2 (the host can let someone else take it).


3. THE RULES IN SHORT
---------------------
Time (speed window, top right; keys: Space pause, "," slower, "." faster,
"/" fastest; F9 = battle speed):
 - Slowing down is instant. Speeding up and pausing are proposals the other
   player must accept: their speed window lights up amber with big ACCEPT /
   DECLINE buttons, a 20 s countdown bar and a soft chime.
 - Chat: type in the box at the bottom of the speed window (Enter sends;
   click the map or press Esc to give the keys back to the game).
 - Each player has 3 "Force" tokens per battle and 3 per in-game week on the
   campaign map: a forced speed-up skips the vote and holds for 60 seconds.
Encounters:
 - When your forces meet, both players get the encounter panel with a 15 s
   bar. Engage or Ignore: if BOTH choose Ignore, there is no battle; if either
   engages, the battle starts. No answer counts as Ignore.
Battles:
 - Fought live: the host's PC runs the battle, the friend commands their own
   ships and planes as usual. The battle starts when both press Start.
 - The fleets start a little closer than in the original game (3/4 of the
   distance at which they met, never closer than 5 km).
 - Nearby task forces join: besides air groups within 50 km (as in the
   original game), every other ship task force within 20 km of its side's
   fleet in the encounter joins the battle at its real position on the map
   (up to 3 ship task forces per side), even if they have just fought
   another battle.
 - Both players can classify enemy ships with the recognition manual; a
   correct class gives the usual fire-control bonus.
 - At the end both players get the after-action report. Each earns command
   points for the enemy ships they sank (the same rule for both sides).
 - After a battle the enemy's surviving forces stay on the map and fade out
   over time instead of vanishing at once.
 - Retreat: press RETREAT (objectives panel, ≡ icon) to disengage — your ships
   stop firing. If both players disengage, or the other player presses
   "Allow retreat" on your bar, the battle ends at once. Otherwise wait out the
   game's disengage countdown, then 30 s (game time) without a new hit from the
   enemy; then you can leave, which ends the battle for both. Firing cancels a
   retreat; a new hit restarts the 30 s.
 - Scuttling: counter-flood the ship's compartments in the damage panel.
Campaign:
 - Both players can rearm/resupply a task force (fleet panel → second order
   list → Resupply Ships) at a friendly port or from their own supply ships,
   as in the original game.
 - Scout circle patterns and Repeat course (patrols) work for both players.
 - A task force sails only as fast as its slowest ship, including battle
   damage (lost propellers or engines, flooding). The fleet panel shows, next
   to Rename, which ship holds it back and how fast it would be without it.
 - The TACTICAL view of a single fleet is switched off in multiplayer.
 - Combat difficulty is neutral (1.0) for both players; the campaign economy
   from the setup screen is the only handicap.


4. CONNECTING OVER THE INTERNET
-------------------------------
The friend connects directly to the host's PC on TCP port 7777.
 - Easiest: both install the same free VPN (Tailscale, ZeroTier, Radmin VPN…)
   and the host picks the VPN address when creating the room. No router setup.
 - Otherwise the host forwards TCP port 7777 on the router to their PC, and
   picks "Internet" with their public IP (search "what is my IP"). The code
   changes when the public IP changes.
 - Windows Firewall: the first time you host, Windows asks whether War on the
   Sea may use the network — allow it (private networks; also public if you
   connect through the Internet). If you clicked "Cancel", allow
   WarOnTheSea.exe in Windows Security → Firewall → Allow an app.

Safety:
 - The room code contains the host's address and the room's password: share
   it only with your friend, and make a new room if it got out.
 - The mod's connection is not encrypted. Over the Internet a VPN (Tailscale,
   ZeroTier…) encrypts it and keeps the port closed to everyone else; with
   port forwarding, close the forward when you're not playing.
 - Built-in protection: only one player gets in; an address that tries 5
   wrong room codes is blocked for 15 minutes (the host sees a message);
   connections that don't check in properly are dropped; oversized or
   malformed messages are refused. When the room uses a VPN or local-network
   address, the game listens on that network only.
 - The mod never runs code sent by the other PC; it only exchanges game data
   (positions, orders, the campaign state, which Player 2's game keeps as its
   "Multiplayer (Player 2)" save).


5. TROUBLESHOOTING
------------------
 - No MULTIPLAYER button: the files aren't next to WarOnTheSea.exe, or a
   copy was blocked. Check that BepInEx\plugins\WotSMultiplayer\
   WotSMultiplayer.dll exists, and that winhttp.dll is in the game folder.
 - "Network protocol mismatch" / "mod version mismatch": both players need
   the same mod version. "Game files differ": both need the same game version
   (let Steam update the game, or verify the game files).
 - "Wrong room code": a new room makes a new code; ask the host for the
   current one.
 - Can't connect: check the firewall prompt on the host, the port forwarding,
   or use a VPN (section 4).
 - F11 opens the mod's panel (status, chat, connect by address).
 - Reporting a bug: send BepInEx\LogOutput.log from both PCs (and say what
   happened and when). Have a look before sharing it: it can contain the
   player names you typed, IP addresses and folder paths.


6. SETTINGS
-----------
File: BepInEx\config\community.wots.multiplayer.cfg (created at first start).
Also active in single-player (set to 0 for the original game's behaviour):
 - [Battle] MaxAircraftPerSide = 60: planes per side pulled into a campaign
   battle (the original game: 24).
 - [Campaign] MinEncounterDistanceKm = 1: forces closer than this never meet
   (the original game: 6 km, so ships often slipped past each other at night).
Multiplayer only:
 - [Battle] MpSpawnDistanceFactor = 0.75: how far apart the fleets start, as a
   share of the distance at which they met (1 = the original game). The host's
   value counts.
 - [Battle] MpReinforcementRadiusKm = 20: ship task forces this close to their
   side's fleet in the encounter join the battle (0 = the original game: one
   ship task force per side). [Battle] MpMaxTaskForcesPerSide = 3: most ship
   task forces per side in one battle. The host's values count.
 - [General] ClearDayNightBar = true: the campaign map's day/night bar (bottom
   left) shows the current day or night from start to end: a sun crosses it
   by day (light yellow to evening orange), a moon by night (orange to black
   to first light); the start and end times are at its ends. Night = the
   hours the game treats as dark in battle (18:00-07:00).
 - [General] SpeedProposalSound = true: chime when the other player proposes a
   speed change.
 - [Campaign] DamagedShipsSlowTaskForces = true: damaged ships slow their task
   force (the host's value counts).
 - [Multiplayer] MaxPlayers = 2: a room for up to 6 players; players 3+ join a
   team as subordinates (see "More than two players" under 2. PLAY). Also in
   the Room window.
 - [Multiplayer] LockKey = K: commanders (Player 1 and 2) lock the selected
   task force, air group, ship or plane group for their subordinates.
Single-player options, off by default: auto-resolve fighter/scout encounters,
speed-vote test mode, the clearer day/night bar ([SinglePlayer]
ClearDayNightBar).


7. UNINSTALL
------------
Delete from the game folder: winhttp.dll, doorstop_config.ini,
.doorstop_version and the BepInEx folder (or only
BepInEx\plugins\WotSMultiplayer if you keep other BepInEx mods). Your saves
stay; multiplayer saves are the files named "Multiplayer (…)" in
%USERPROFILE%\AppData\LocalLow\Killerfish Games\War on the Sea\save\campaign.


8. KNOWN LIMITS
---------------
 - Two commanders (up to 4 more players as subordinates), Solomons theatre
   (Allies vs Japan).
 - Subordinates join a battle only if they are on the campaign map when it
   starts; teams can only be changed before the campaign starts.
 - The host's PC runs the campaign and the battles; the host should have the
   faster PC and connection.
 - A player who drops during a battle rejoins after it ends.
 - If a PC sleeps or locks, the game stalls for both: keep both PCs awake.


9. CREDITS AND LICENSES
-----------------------
WotS Multiplayer — © 2026 WotS Multiplayer contributors, MIT license
(LICENSE.txt). War on the Sea © Killerfish Games (not included).
Bundled: BepInEx (LGPL-2.1), HarmonyX, MonoMod, Mono.Cecil (MIT) — see
THIRD-PARTY-NOTICES.txt and the licenses folder.
