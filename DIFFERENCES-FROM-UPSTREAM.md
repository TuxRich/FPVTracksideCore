# BIRD build: differences from upstream

This branch (`server-client`, **2.79.1.938-BIRD**, commit `a1765987`) compared with upstream master ([uewepuep/FPVTracksideCore](https://github.com/uewepuep/FPVTracksideCore), **2.79.1.919**, commit `88f4a847`), on 3 November 2026. The two split after upstream merged pull request #83 (lap editor, `838558e2`). Both are version 2.79.1. The build number counts commits since then, plus an offset (915 on both). Upstream's commits up to `88f4a847` are all here, except the ones listed under *Only in upstream*.

## Only in this version

**Timing**
- **racetimer gate timer**: two-receiver gates, through their timer API. It syncs to the gate's clock, catches up on missed detections after a reconnect, and has a *Count laps* setting: through the gate, seen by both receivers, or seen by either.
- **On/off switch for timers**: a timer can be switched off and keep its settings. It isn't connected or used, and its status icon shows a dimmed "Off". Right-click the icon to switch it on or off.
- **RSSI Analyser**: only connected timers' levels are shown, so a split timer that isn't connected no longer covers the primary's levels. Hidden on review PCs.
- **LapRF**: no errors before its first connect, and failed connects no longer log unobserved socket errors.

**Multi-PC review**
- A second PC can review races (replays, laps, results) while the main PC runs the event. A race that's open on one PC is locked on the other, changes are picked up by the other PC, and nothing is saved over another PC's changes. The review PC can't change the schedule or start races.

**Races and results**
- **Sector times**: edit and disqualify sector times in the lap editor, and add or move them in the replay.
- **Pasted results**: a stage can use a range of positions from results pasted in, e.g. 17–32 of a 48-pilot result.
- **Chase the Ace** counts wins, so it ends once someone has won twice.
- **Final Results** is a tab of its own (upstream combines all the result types in one Results tab). It can't be changed from a review PC.
- **Round generation**: a stage's next round is the stage's own, not a round of another type with the same number, and sheet stages no longer get stray races.

**Video and replay**
- Recordings cut short by a crash are repaired when their replay is opened.
- Faster frame-by-frame stepping in replays.
- Shortcuts to toggle fullscreen for a channel group's pilot, live or in replay. The hide-channel shortcuts work again.

**Interface, stability and builds**
- The auto runner shows the time left on its side button.
- Startup is about twice as fast, because only the active theme is loaded.
- A drawing error no longer freezes the screen for 5 s or leaves images and rounds blank.
- Stability fixes from event logs:
  - the RotorHazard status error storm is fixed;
  - speech recovers after audio device errors;
  - the web server stays up when its port is taken;
  - uncaught errors are written to `Crash.log`;
  - repeated errors are logged once a minute, with a count.
- Builds are labelled BIRD (e.g. 2.79.1.925-BIRD), and `Installer\build-msi.ps1` makes an MSI like the official one.

## Only in upstream

**Not taken**
- REST API (`/api/v1`) with a console and sandbox. It includes web users and roles, and remote control of the whole window.
- Seeder, formerly Brackets: a bracket engine and web app, with a Node.js build.
- Stages as a status icon instead of a tab.
- All result types in one Results tab.
- Upstream's own auto-runner time display (this version has its own).
- Empty round and stage work.

## Taken from upstream with changes

- *Final Results!* (`9a682e28`): as its own tab, without restoring deleted stages, and with the review-PC rules.
- *bug fixes + empty stage* (`29c40c56`): the sheet pilot-name fix and RotorHazard lean mode only.
- *fix for all rounds in one column* (`173b96b5`): the sheet part only.
- *fixes for sheets!* (`ef3aa862`), *Bug fixes from logs* (`7b936d05`) and the seek clamp (`c9f388b2`): adapted to this version's code.
- *More shortcut keys* (`b9a6d536`): the tab shortcuts go to this version's separate result tabs, and on a review PC the ones for tabs it hides do nothing.
- *Next Race* number (`5960fd61`): the Next button stays hidden on a review PC.
- ArUco marshalling (`7620c8ce`): its marshal editor keeps this version's sector times.
- *Big Translation update* (`098febfc`): without the strings for the upstream screens left out (empty round options, Seeder).
- Upstream's `8d355658` (user settings overwrite) and `c2ce8580` (PR 86) are already here in another form.
