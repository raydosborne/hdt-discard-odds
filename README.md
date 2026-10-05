# Discard Odds: target-card odds for Hearthstone Deck Tracker

An HDT plugin that shows live odds for the cards **you** care about in **any deck**. It shows **numbers only, never advice**, and uses no HSReplay data: everything is computed from HDT's live view of your deck and hand.

1. **Target cards, per deck.** Pick your deck's target cards (payoffs, combo pieces, win conditions) in *Plugins → Discard Odds → Choose target cards for this deck…*: a window lists the active deck's cards with checkboxes. Each deck keeps its own list in `targets.json` (see [Target cards](#target-cards-targetsjson)).
2. **Next draw.** The first line of the overlay box: `Next draw (N/M)  33% / 67%`, followed on the same line by the targets in the active deck list with copies left, e.g. `Soul Barrage 1/2 · Walking Dead 2/2`.
3. **"If you play it" hit/miss** for every draw or discard card in your hand:
   - **Any card whose text draws cards** ("Draw a card", "Draw 2 cards", from HearthstoneJSON text via HearthDb) gets one line `Card name  hit% / miss%` for drawing at least one target. Conditional draws (Deathrattle, "If …", Combo, Choose One, "for each" …) are marked `≈`.
   - **Card-specific models** cover discard and special-draw cards: Wicked Whispers (lowest Cost), Expired Merchant and Chronoclaws (highest Cost), Soulfire and Darkshire Librarian (random), Ocular Occultist and Gemstone Hoarder (choose), Chamber of Viscidus (look at 3), Platysaur (draw, then discard it on death), The Soularium, Hand of Gul'dan, Cursed Catacombs (Discover from deck), Sketch Artist (Shadow spell). For example: `Wicked Whispers  50% / 50%  → Walking Dead / Party Fiend` (every tied card is listed; targets in bold).
   - What a Platysaur on your board is holding (from the game's own link enchantment), Duke of Below's current size (2/2 + 2/2 per card discarded this game), and, when something in hand could discard Hand of Gul'dan, the chance its 3 draws find a target.
4. **Lethal check** on your turn: `Face damage: X vs Y health`, bold green when it's lethal, red when short (see [Lethal check](#lethal-check-your-turn)).
5. **Opening one-drop odds** during the mulligan and your turn 1 only (see [One-drop odds](#one-drop-odds-mulligan--turn-1)).

### Reading the widget

One line per card, kept short so it doesn't cover your mana, hand or the opponent's cards. The colors are fixed:

| Color | Means |
|---|---|
| gold | card name at the start of a line |
| **bold green** | hit % |
| red | miss % |
| orange | the card a *highest Cost* discard (Expired Merchant, Chronoclaws) would hit |
| blue | the card a *lowest Cost* discard (Wicked Whispers) would hit |
| dim grey | details |

The reasons behind each number (e.g. `lowest cost 3: …`, `8 targets in 24 cards, draw 3`) and a small color legend are hidden by default; turn them on with *Plugins → Discard Odds → Show details*. Update status is never shown on the overlay, only as the first item of the plugin menu (e.g. `Up to date (v0.1.1)` or `Update v0.1.2 ready: restart HDT to finish`).

M (cards left) is HDT's deck count. "Casts When Drawn" cards such as Shreds of Time are left out, because drawing one replaces itself, and they are shown as a separate note.

A **Discard Warlock preset** ships as an example in `targets.json` (Soul Barrage, Walking Dead, Hand of Gul'dan, Disposable Acolytes, Silverware Golem). It is applied automatically only to decks that have no list of their own and contain 3 or more of those cards. Nothing about any particular deck is hard-coded.

### What is generic and what is still Discard-specific

| Generic (any deck) | Still Discard Warlock-specific |
|---|---|
| Per-deck target lists, the targets window, `targets.json` | The card-specific models listed above (other discard/Discover/filtered-draw cards are not modelled) |
| Next-draw line and target list | The M0 probes and their logs (`[TEMP] [DUKE] [TIE] [MULL] [PLATY]`); `[ODDS]` and `[DECK]` work for any deck |
| Hit/miss for any plain "draw N cards" text | Type-filtered draws ("Draw a minion/spell") are skipped, except Sketch Artist |

The plugin also **logs evidence** (M0 probes) so the math can be checked against the actual game:

| Log tag | Question it settles |
|---|---|
| `[ODDS]` | For every odds card you play: the predicted hit % at that moment, then the real result (cards drawn, Catacombs offers, card discarded). |
| `[TEMP]` | How Temporary cards (Soularium, Catacombs, Sketch Artist) are marked on hand cards: attached `GBL_999e` enchantment, or GameTag 785 (`GHOSTLY`), or both. Also whether every end-of-turn burn was marked. |
| `[DUKE]` | Where the "cards discarded this game" count lives. Compares `Player.EntitiesDiscardedFromHand` with any player tag containing DISCARD, logs which tags change on each discard, and compares Duke of Below's live attack/health with `2 + 2 × discards`. |
| `[TIE]` | Which card Wicked Whispers (lowest Cost), Expired Merchant and Chronoclaws (highest Cost) discard when costs tie, and whether the discarded card was in the tie set. |
| `[DECK]` | What `PlayerCardList` contains under HDT's deck-display settings. Compares `DeckCount` with the rows (Count = 0 rows, created cards, unknown cards). |
| `[MULL]` | Mulligan redraw: whether a tossed card (or a same-name copy) can come straight back as its own replacement. |
| `[PLATY]` | Platysaur: payoff odds before its draw, which card it drew, how the game links Platysaur to that card (`TLC_603e`/`e2`/`e3` enchantments and tags), and the outcome (card played first, or discarded when Platysaur died). |

Card rules come from the real HearthstoneJSON card text (2026-10-04). For example, Platysaur (TLC_603): *"Battlecry: Draw a card. Deathrattle: Discard it."* The full table is in `src/OddsEngine.cs`. "≈" marks the models that M0 is meant to verify (Catacombs offers, Chamber's 3 cards, Sketch Artist's Shadow spell pool) and conditional generic draws.

---

## Quick install (no build needed)

1. Close HDT.
2. Download `DiscardOdds.dll` from the [latest release](https://github.com/raydosborne/hdt-discard-odds/releases/latest). Optionally check it against the `DiscardOdds.dll.sha256` file attached to that release: `(Get-FileHash .\DiscardOdds.dll -Algorithm SHA256).Hash`.
3. Put it in `%AppData%\HearthstoneDeckTracker\Plugins\DiscardOdds\` (create the folder). Windows may mark downloaded DLLs as blocked: right-click → Properties → tick **Unblock** if that box is shown.
4. Start HDT, go to **Options → Tracker → Plugins**, select **"Discard Odds (M0 probe)"** and tick **Enabled**.

From then on the plugin keeps itself up to date (see [Updates](#updates)). The steps below are only for building it yourself.

## What you need on Windows (to build from source)

- Hearthstone Deck Tracker installed and working.
- **One** of these:
  - **A) .NET SDK 8** (simplest). In PowerShell: `winget install Microsoft.DotNet.SDK.8`. You do **not** need the .NET Framework 4.7.2 Developer Pack: the project pulls the 4.7.2 reference assemblies from NuGet on the first build, so you need an internet connection once.
  - **B) Visual Studio 2022** (Community), or **Build Tools for Visual Studio 2022** with the ".NET desktop build tools" workload.

## Step 1: find your HDT folder (for the build-time reference)

The plugin compiles against HDT's own `HearthstoneDeckTracker.exe` and `HearthDb.dll`. They are **only used while compiling**: the project never copies them into the output, and you never ship them.

The normal installer puts HDT here (the version folder changes with updates):
```
%LocalAppData%\HearthstoneDeckTracker\app-<version>\
```
Run this in PowerShell to find it:
```powershell
Get-ChildItem "$env:LOCALAPPDATA\HearthstoneDeckTracker" -Directory -Filter "app-*" | Sort-Object Name | Select-Object -Last 1 -ExpandProperty FullName
```
The folder must contain `HearthstoneDeckTracker.exe` (or `Hearthstone Deck Tracker.exe` for the portable zip; either works) and `HearthDb.dll`.

## Step 2: build

Clone this repository (or download it as a zip and extract it), open PowerShell **in the folder with `DiscardOdds.csproj`**, then:

**A) .NET SDK**
```powershell
$hdt = Get-ChildItem "$env:LOCALAPPDATA\HearthstoneDeckTracker" -Directory -Filter "app-*" | Sort-Object Name | Select-Object -Last 1 -ExpandProperty FullName
dotnet build -c Release -p:HdtDir="$hdt"
```

**B) Visual Studio / Build Tools** (from the "Developer PowerShell for VS 2022"):
```powershell
msbuild DiscardOdds.csproj -restore -p:Configuration=Release -p:HdtDir="$hdt"
```
In the Visual Studio IDE, either copy `HearthstoneDeckTracker.exe` and `HearthDb.dll` into `lib\hdt\` (the default `HdtDir`, which is git-ignored), or pass the path as above. Then open `DiscardOdds.csproj` and build Release.

The output is `bin\Release\DiscardOdds.dll`, the only file you need. If the build says *"HearthstoneDeckTracker.exe not found"*, `HdtDir` points to the wrong folder.

## Step 3: install into HDT

HDT loads plugins from **`%AppData%\HearthstoneDeckTracker\Plugins`**. Don't use the `Plugins` folder inside the install directory: HDT syncs from AppData on startup and deletes anything that isn't in AppData.

1. Close HDT.
2. Copy the DLL:
   ```powershell
   $dst = "$env:APPDATA\HearthstoneDeckTracker\Plugins\DiscardOdds"
   New-Item -ItemType Directory -Force $dst | Out-Null
   Copy-Item .\bin\Release\DiscardOdds.dll $dst -Force
   ```
   (Shortcut: `dotnet build -c Release -p:HdtDir="$hdt" -p:DeployToHdt=true` builds and copies in one step.)
3. Start HDT, go to **Options → Tracker → Plugins**, select **"Discard Odds (M0 probe)"** and tick **Enabled**.
4. HDT's main window now has a **Plugins → Discard Odds** menu with: *Choose target cards for this deck…*, *Reload targets.json*, *Unlock widget (drag to move)*, *Show widget*, *Show details (reasons + color legend)*, *Show lethal check (your turn)*, *Show one-drop odds (mulligan + turn 1)*, *Reset widget position*, *Write deck snapshot to log now*, *Open log folder*, and the update status (first item) and options (*Check for updates when HDT starts*, *Auto-update*, *Check for updates now*, *Open releases page*). The plugin's button in Options → Tracker → Plugins also opens the log folder.

## Target cards (targets.json)

**Easiest:** select your deck in HDT, then *Plugins → Discard Odds → Choose target cards for this deck…*. Tick cards, or pick a preset and click *Tick preset cards*, then *Save*. *Use preset / auto* forgets the deck's own list so an auto-matching preset (if any) applies again. An empty saved list means "no targets for this deck".

**By hand:** the file is `%AppData%\HearthstoneDeckTracker\DiscardOdds\targets.json` (created with the default preset on first run). Edit it, then use *Reload targets.json*. If the file has a JSON error, the plugin logs it, uses the built-in preset, and leaves your file alone. Saving from the window keeps the previous file as `targets.json.bak`.

```json
{
  "version": 1,
  "decks": [
    {
      "deckId": "00000000-0000-0000-0000-000000000000",
      "deckName": "My Deck",
      "targets": [ { "id": "RLK_534", "name": "Soul Barrage" }, "RLK_532" ]
    }
  ],
  "presets": [
    {
      "name": "Discard Warlock payoffs",
      "autoApplyMinMatches": 3,
      "targets": [ { "id": "RLK_534", "name": "Soul Barrage" }, { "id": "RLK_532", "name": "Walking Dead" } ]
    }
  ]
}
```
- Targets are HearthstoneJSON card ids (hover a card in the window to see its id). `name` is only a label, and a plain id string works too.
- A deck matches by `deckId` (HDT's deck id) first, then by `deckName`, so a re-imported deck still finds its list.
- A preset with `autoApplyMinMatches` > 0 applies to decks without their own list that contain at least that many of its cards. Set it to 0 to make a preset manual-only.

**Moving the widget:** Plugins → Discard Odds → tick *Unlock widget*. The border turns gold. Drag it with the left mouse button over the Hearthstone window, then untick *Unlock widget*. The position is saved as a fraction of the overlay size, so it survives resolution changes. By default the widget starts lower-center-left of the board, just above your hand, so it does not cover either HDT deck list; once you drag it, your saved position is used instead. *Reset widget position* goes back to the default.

## Lethal check (your turn)

On your turn the widget adds a factual count of the damage that can reach the enemy hero right now, for example **`Face damage: 14 vs 13 health → LETHAL (all face)`** (highlighted) or `Face damage: 9 vs 13 health (4 short)`. With *Show details* a dim second line breaks it down: `board 8 + hero 3 + hand 3 (Soulfire)`, and lists your characters that can't hit face this turn with the reason (`not counted: Felbeast 1/1 (played this turn)`). It is a count, not a suggestion of what to play.

- **Board:** minions and your hero that can still attack, following HDT's own board-damage rules: a minion played or summoned this turn counts only with Charge (Rush can't go face), even when the game hasn't sent its *exhausted* flag yet; frozen, dormant, *can't attack* and Titans with abilities left are skipped; Windfury counts twice. If the enemy has **any Taunt** minion, all attack damage is shown as blocked (`enemy Taunt ×1: 10 attack damage can't go face`), and only damage from your hand counts.
- **Hand:** the best set of cards you can afford with your current mana. That covers "Deal N damage" spells and battlecries that can target the face (Spell Damage added), Charge minions, and a weapon (it replaces your hero's current attack).
- **≈** marks damage that might not all reach the face: random splits while the enemy has minions (Soul Barrage, Arcane Missiles), "...instead" upgrades (base value counted), Combo / If conditions, conditional Charge. Then "LETHAL?" is shown instead of "LETHAL".
- Not counted (first version): hero powers, buffs, cost reductions from playing cards, board-space limits, enemy secrets, armor gain or healing.
- Opponent health includes armor; an Immune hero is never shown as lethal. Turn it off with *Show lethal check* or `ShowLethalCheck=False` in `settings.ini`.

## One-drop odds (mulligan + turn 1)

Uses the 1-Cost cards in the active deck list, so it works for any deck. Shown only until your turn 1 is over; turn it off with *Show one-drop odds* or `ShowOneDrop=False`.

- **Mulligan, no 1-drop in hand:** two lines, `1-drop by T1 · keep` (only the turn-1 draw) and `1-drop by T1 · toss N` (replace every card, then the turn-1 draw). Exact hypergeometric: the tossed cards are shuffled back only after the replacements are drawn, so they can't come back as their own replacements. Going first you keep 3 cards, on the coin 4 (the Coin is not a 1-drop).
- **1-drop in hand:** `One-drop: in hand ✓`.
- **After the mulligan, before the turn-1 draw:** `1-drop on T1 draw` hit/miss; if you tossed cards and still have none, a note `Chance of this (no 1-drop after full mulligan): X%`.
- **Turn 1 after the draw:** `One-drop: in hand ✓`, or `One-drop: none (missed)` with the chance of that.

For a 30-card list with 10 one-drops and no 1-drop in the opening hand: going first, keeping gives 37.0% and a full mulligan 85.4%; on the coin, 38.5% and 92.5%.

## Updates

When HDT starts, the plugin asks GitHub once for this repository's latest release (`https://api.github.com/repos/raydosborne/hdt-discard-odds/releases/latest`, HTTPS, no other site, no data about you or your games sent). It then compares that release's tag (e.g. `v0.2.0`) with the running version:

- **Up to date:** the first Plugins-menu item reads `Up to date (v0.1.1)`. **Offline:** nothing changes; network errors are only written to the plugin log.
- **Newer, Auto-update on (default):** it downloads the release's `DiscardOdds.dll` and `DiscardOdds.dll.sha256` and checks three things: the SHA-256 matches, the DLL really is `DiscardOdds` with the tag's version, and the download URLs belong to this repository's release assets. If all three pass, it replaces the copy in `%AppData%\HearthstoneDeckTracker\Plugins\…` and the first Plugins-menu item reads **"Update vX.Y.Z ready: restart HDT to finish"** (nothing is shown on the overlay). HDT locks the DLL it is running from (its local `Plugins` folder, not the AppData one), and on the next start it copies the newer AppData file over automatically. The previous DLL is kept as `DiscardOdds\update\DiscardOdds.previous.dll`. If any check fails, nothing is replaced and the notice says so.
- **Newer, Auto-update off:** the first menu item reads "Update vX.Y.Z available (auto-update off): open release page"; click it to open the release page.

Turn either part off in **Plugins → Discard Odds** or in `settings.ini` (`CheckForUpdates=False` stops the check entirely; `AutoUpdate=False` keeps the check but never downloads). Pre-releases and drafts are ignored, and a release is never installed over a newer version.

The checksum guards against corrupted or truncated downloads. It is published in the same release as the DLL, so it cannot protect against someone who controls this GitHub repository; turn updates off if you'd rather review every version yourself.

## Where the logs go

```
%AppData%\HearthstoneDeckTracker\DiscardOdds\
    settings.ini                 widget position / toggles (WidgetPositionSaved=True once you move it), ShowLethalCheck, ShowDetails, ShowOneDrop, CheckForUpdates / AutoUpdate
    update\                      last downloaded update (DiscardOdds.dll) and the DLL it replaced (DiscardOdds.previous.dll)
    targets.json                 your per-deck target cards and presets (targets.json.bak = previous version)
    logs\m0_YYYY-MM-DD.log       readable probe log (one per day, games separated by ===== lines)
    logs\m0_YYYY-MM-DD.jsonl     the same findings as JSON lines (for offline analysis)
```
The log files are not held open: lines are appended in short bursts (about 10 times a second), so you can open, copy or tail them while HDT is running. HDT's own log (`%AppData%\HearthstoneDeckTracker\Logs\hdt_log.txt`) shows load errors, if any.

## Test protocol (about 6–10 games settles everything)

1. Select your Discard Warlock deck as the active deck in HDT (the preset applies automatically, or choose targets yourself), then play Practice or Casual games normally.
2. Try to cover:
   - **Soularium** and **Cursed Catacombs** plays. Let some Temporary cards burn at end of turn.
   - **Wicked Whispers / Expired Merchant** with **cost ties** in hand. The Coin and Catacombs both cost 0, which makes an easy tie.
   - At least one **Duke of Below** in hand or on board while discarding.
   - **Platysaur**: sometimes play the drawn card before Platysaur dies, sometimes let it be discarded.
   - Mulligans where you **toss a 2-copy card**.
3. Deck-display settings (Options → Overlay → Player deck; HDT's labels are roughly "Highlight cards in hand", "Remove cards from deck" and "Include created cards"): play at least one game with each box changed from your current setup. The `[DECK]` lines record which settings were active.
4. The two files in `logs\` hold the results. They stay on your machine (the plugin never uploads anything); review them before sharing, since they include your deck and game details.

## Safety / scope

- Only reads HDT's game state. It never sends input to Hearthstone and never reads game memory itself.
- Network: only the update check described in [Updates](#updates) (GitHub, this repository, HTTPS), and it can be switched off. Nothing about your games, decks or logs is ever uploaded.
- The global mouse hook (HDT's `User32.MouseInput`, the same one the DrawPool plugin uses) is active **only while the widget is unlocked**.
- If anything throws, the error goes to the plugin log and the plugin keeps running.

## Project layout

```
DiscardOdds.csproj        net472, x64, HDT refs Private=false, NuGet reference assemblies
src/DiscardOddsPlugin.cs  IPlugin: events, menu, widget refresh, per-deck target resolution
src/OddsEngine.cs         pure hit/miss math, card-specific rules, generic draw-text parser (no HDT types; unit-tested)
src/TargetConfig.cs       targets.json model, presets, tiny JSON reader (no HDT types; unit-tested)
src/TargetsWindow.cs      "Choose target cards" window (code-only WPF)
src/GameReader.cs         reads HDT state into snapshots / the counting rule
src/Probes.cs             the M0 probes ([ODDS] [TEMP] [DUKE] [TIE] [DECK] [MULL] [PLATY])
src/PayoffWidget.cs       overlay box, drag via User32.MouseInput
src/ProbeLog.cs           log files (not held open), tiny JSON writer, settings.ini
src/LethalEngine.cs       lethal check: face-damage count, burn/Charge/weapon parsing from card text (no HDT types; unit-tested)
src/UpdateLogic.cs        update rules: tag/version compare, release parsing, asset URL pinning, SHA-256 (no HDT types; unit-tested)
src/Updater.cs            update check + download + verify + swap into HDT's AppData plugin folder
.github/workflows/release.yml  builds the DLL on a version tag and publishes it + checksum as a GitHub Release
tests/OddsEngine.Tests    `dotnet run -c Release`: math vs closed-form hypergeometric values, draw-text parsing, targets.json rules and I/O, widget position rules, lethal check, update rules, log file sharing
```

## Releasing a new version (maintainer)

1. Make sure the tests pass: `dotnet run --project tests/OddsEngine.Tests -c Release`.
2. Tag and push: `git tag v0.2.0` then `git push origin v0.2.0`. Or, without git: GitHub → **Actions → Release → Run workflow**, enter `v0.2.0` (the tag is created on the chosen branch).
3. The workflow downloads a pinned HDT release (only to compile against; its SHA-256 is checked and nothing from it is published), runs the tests, then builds with the tag as the assembly version. It attaches `DiscardOdds.dll` and `DiscardOdds.dll.sha256` to a release named after the tag. The tag must be `vMAJOR.MINOR.PATCH`.
4. Running plugins pick it up the next time HDT starts.

When HDT's plugin API changes, update `HDT_VERSION` and `HDT_ZIP_SHA256` in the workflow. The `<Version>` in `DiscardOdds.csproj` is only the default for local builds; release builds take their version from the tag.

License: MIT (see `LICENSE`). Not affiliated with Blizzard Entertainment or HearthSim. Hearthstone is a trademark of Blizzard Entertainment.
