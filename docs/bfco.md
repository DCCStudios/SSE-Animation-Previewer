# BFCO clips

[Back to the README](../README.md)

The previewer reads clips made for the BFCO attack behavior framework as BFCO plays them. That covers the next-attack annotations, BFCO's combo windows, its attack speed rule and its clip names. MCO clips are unaffected, and both kinds can sit in one queue.

The rules on this page come from BFCO's own Nemesis behavior patch and plugin source. How authors use the annotations was surveyed in the BFCO clips of five mods from one mod list: the framework's own clips, CEC Resource, TK Dodge Redux, a SCAR battleaxe moveset and Fishermen Fish. Where the previewer infers something, the page says so.

## Which clips count as BFCO

The file name decides first, because that is what decides it in game: BFCO's behavior plays `BFCO_*` files and MCO's plays `mco_*` files.

- A file named `BFCO_…`, for example `BFCO_Attack1.hkx` or `BFCO_PowerAttack3.hkx`, is a BFCO clip.
- A file named `mco_…` is an MCO clip, whatever it carries. Many movesets ship MCO clips with BFCO annotations in them as well.
- Any other file is a BFCO clip when it carries one of BFCO's chain annotations: `BFCO_NextIs…`, `BFCO_NextWinStart`, `BFCO_NextPowerWinStart`, `BFCO_DIY_…` or `BFCO_DefAttackStage…`.

A `BFCO_AttackSpeed` annotation on its own does not make a clip a BFCO clip. Neither does a folder named BFCO.

## How a BFCO combo chains

BFCO picks the next clip by an annotation's name, not by a variable.

| Annotation | What it does in game |
|---|---|
| `BFCO_NextIsAttackN` | Opens the input buffer and names the next light clip, `BFCO_AttackN.hkx` |
| `BFCO_NextIsPowerAttackN` | The same for the next power clip, `BFCO_PowerAttackN.hkx` |
| `BFCO_NextWinStart` | The earliest moment the light transition may begin. Without it the transition never begins. |
| `BFCO_NextPowerWinStart` | The same for the power transition |
| `BFCO_DIY_EndLoop` | Closes the buffer and both windows. A clip need not carry it. Without it they stay open to the end of the clip, and the graph sends it on entering the next one. |
| `BFCO_DIY_recovery`, `MCO_Recovery` | From here, movement cancels the attack |

There is no `BFCO_NextWinEnd`. BFCO has one closer for everything, and many clips leave it out.

BFCO still runs MCO's annotations. A clip that carries `MCO_WinOpen` and the rest chains through those, and many BFCO clips ship both sets at the same times.

## In the queue

The hand-over drop-down on the **Queue** tab reads "MCO_WinOpen / BFCO_NextWinStart" and "MCO_PowerWinOpen / BFCO_NextPowerWinStart". Each clip uses the annotation that fits it:

- a BFCO clip hands over at `BFCO_NextWinStart` (or `BFCO_NextPowerWinStart`) when it also carries the next-attack annotation that arms that window
- a numbered BFCO clip without that annotation hands over at its MCO window, since only the MCO path can fire for it in game
- any other clip hands over at MCO's window

Either kind falls back to the other framework's window when it lacks its own.

Each row says which annotation it used, for example "next at BFCO_NextWinStart 0.83 s". A clip with neither plays to its end.

The previewer hands over at the window annotation, which is the earliest a transition can start. In game the transition starts there only if the attack was already pressed. A later press starts it later.

## Attack speed

BFCO clips play at `BFCO_AttackSpeed`. While that is exactly 1, they play at `MCO_AttackSpeed` instead. This is the behavior patch's own formula, and it is why most BFCO clips write both annotations with the same value.

Two things differ from MCO:

- **`BFCO_AttackSpeed` starts again at 1 in each numbered attack.** The graph resets it on entering those states, so a value set in one clip does not carry into the next. `MCO_AttackSpeed` does carry, as it does in MCO.
- **The hold loop, its outro and the charge clips are entered without that reset.** A `BFCO_AttackSpeed` set in `BFCO_PowerAttackN` therefore carries through the loop into `BFCO_PowerAttackOutroN`.
- **Hold loops and NPC power attacks ignore speed.** `BFCO_PowerAttackLoopN` and `BFCO_NpcPowerAttackN` are not bound to either variable and play at 1.00x. Annotations inside them still set the variables for the clip after.

For a BFCO clip with speed annotations, the note beside the Speed box starts "BFCO speed". In a clip that is not BFCO, a `BFCO_AttackSpeed` annotation is listed and ignored, and the note says so.

## In the Annotations tab

When you select a single BFCO clip, a **BFCO** section appears under its annotation list. A playing queue does not show it. It gives:

- the next light and power clip, read from the `BFCO_NextIs…` names (a range clip's next clip is `BFCO_RangeAttackN`), and when the buffer and each window open
- where `BFCO_DIY_EndLoop` closes them, or that they stay open to the end of the clip
- where movement can cancel the attack
- warnings for mistakes BFCO does not report

The warnings:

- **A window with no next-attack annotation.** `BFCO_NextWinStart` in a numbered clip with no `BFCO_NextIsAttackN` leads to no numbered clip. The same goes for the power pair. Swim and other special clips use a bare window and get no warning.
- **A next-attack annotation with no window.** `BFCO_NextIsAttackN` without `BFCO_NextWinStart` never chains, because BFCO starts the transition only from the window.
- **A next-attack number outside 1 to 20.** BFCO registers no such event.
- **`BFCO_DIY_Rcovery`.** This misspelling of `BFCO_DIY_recovery` is in published clips, including some of the framework's own. BFCO registers no event by that name, so it does nothing.
- **A numbered clip that names no follow-up.** It has no next-attack annotation with a window, and no MCO window. The graph's own exits, such as dual-wield, sprint and directional attacks, still apply.

The active-annotation highlight follows BFCO's rules as well. The next-attack annotations and the two windows stay highlighted until `BFCO_DIY_EndLoop`, or to the end of the clip when there is none. `BFCO_DIY_recovery` stays highlighted to the end. In a queue these end where the clip hands over, as the graph closes them on entering the next clip. BFCO's start and close pairs (`BFCO_ForbidRotationStart` and `Close`, `BFCO_0GravityStart` and `End`, `BFCO_bsprintingStart` and `Stop`, the `AMF_…MagstimStart` and `Close` pairs) and its charge stages last from one to the next.

## Clip names

For reference, the file names BFCO's behavior plays:

| File | Use |
|---|---|
| `BFCO_Attack1` to `20` | Light attacks |
| `BFCO_PowerAttack1` to `20` | Power attacks |
| `BFCO_PowerAttackLoop1` to `10`, `BFCO_PowerAttackOutro1` to `10` | Hold loop and its release |
| `BFCO_PowerAttack_Charge1` to `3` | Charge levels |
| `BFCO_PowerAttackA`, `B`, `L`, `R` | Directional power attacks (A forward, B backward) |
| `BFCO_PowerAttackComb` | Combo-key attack |
| `BFCO_SprintAttack`, `BFCO_SprintAttackPower`, `BFCO_JumpAttack`, `BFCO_JumpAttackPower` | Sprint and jump |
| `BFCO_SpecialAttack`, `BFCO_SpecialAttackPower` | Dual-wield |
| `BFCO_SwimAttack1` to `3`, `BFCO_SwimAttackPower` | Swimming |
| `BFCO_RangeAttack1` to `10`, `BFCO_BowBash`, `BFCO_BowPowerBash` | Bow and crossbow |
| `BFCO_BlockBash`, `BFCO_BlockPowerBash`, `BFCO_ShdBash`, `BFCO_ShdPowerBash` | Bashes |
| `BFCO_NpcPowerAttack1` to `3` | NPC power attacks |

For Precision's power-attack timing the previewer counts a clip as a power attack when its path contains "power", which covers these names.

## What is not modelled

- The previewer does not run the behavior graph. It lists and highlights the hold loop, charge stages and the default chain (`BFCO_DefAttackStageN`), but it does not branch on them or time the windows the graph times itself.
- The graph's own blend for a combo step is 0.2 seconds. The queue uses your Blend setting, which defaults to 0.30.
- In game the graph sends `attackStop` shortly before most clips end: 0.06 seconds for the numbered attacks and outros, 0.01 for the special ones. The previewer does not add it.
- The BFCO plugins' input handling, NPC attack choice and stamina rules do not change what a clip looks like, so the previewer leaves them out.
- BFCO's files do not show whether the game matches annotation names case-sensitively. The previewer ignores case.
