# BFCO clips

[Back to the README](../README.md)

The previewer reads clips made for the BFCO attack behavior framework as BFCO plays them. That covers the next-attack annotations, BFCO's combo windows, its attack speed rule and its clip names. MCO clips are unaffected, and both kinds can sit in one queue.

The rules on this page come from BFCO's own Nemesis behavior patch and plugin source, checked against 306 BFCO clips from five mods. Where the previewer infers something, the page says so.

## Which clips count as BFCO

A clip is treated as a BFCO clip when either is true:

- its file name starts with `BFCO_`, for example `BFCO_Attack1.hkx` or `BFCO_PowerAttack3.hkx`
- it carries one of BFCO's chain annotations: `BFCO_NextIs…`, `BFCO_NextWinStart`, `BFCO_NextPowerWinStart`, `BFCO_DIY_…` or `BFCO_DefAttackStage…`

A `BFCO_AttackSpeed` annotation on its own does not make a clip a BFCO clip. Neither does a folder named BFCO.

## How a BFCO combo chains

BFCO picks the next clip by an annotation's name, not by a variable.

| Annotation | What it does in game |
|---|---|
| `BFCO_NextIsAttackN` | Opens the input buffer and names the next light clip, `BFCO_AttackN.hkx` |
| `BFCO_NextIsPowerAttackN` | The same for the next power clip, `BFCO_PowerAttackN.hkx` |
| `BFCO_NextWinStart` | The earliest moment the light transition may begin |
| `BFCO_NextPowerWinStart` | The same for the power transition |
| `BFCO_DIY_EndLoop` | Closes the buffer and both windows. It is optional. Without it they stay open to the end of the clip. |
| `BFCO_DIY_recovery`, `MCO_Recovery` | From here, movement cancels the attack |

There is no `BFCO_NextWinEnd`. BFCO has one closer for everything, and many clips leave it out.

BFCO still runs MCO's annotations. A clip that carries `MCO_WinOpen` and the rest chains through those, and many BFCO clips ship both sets at the same times.

## In the queue

The hand-over drop-down on the **Queue** tab reads "MCO_WinOpen / BFCO_NextWinStart" and "MCO_PowerWinOpen / BFCO_NextPowerWinStart". Each clip uses the annotation that fits it:

- a BFCO clip hands over at `BFCO_NextWinStart` (or `BFCO_NextPowerWinStart`), falling back to MCO's window if it has no BFCO one
- any other clip hands over at MCO's window

Each row says which annotation it used, for example "next at BFCO_NextWinStart 0.83 s". A clip with neither plays to its end.

The previewer hands over at the window annotation, which is the earliest a transition can start. In game the transition starts there only if the attack was already pressed. A later press starts it later.

## Attack speed

BFCO clips play at `BFCO_AttackSpeed`. While that is exactly 1, they play at `MCO_AttackSpeed` instead. This is the behavior patch's own formula, and it is why most BFCO clips write both annotations with the same value.

Two things differ from MCO:

- **`BFCO_AttackSpeed` starts again at 1 in every clip.** The graph resets it on entering each attack state, so a value set in one clip does not carry into the next. `MCO_AttackSpeed` does carry, as it does in MCO.
- **Hold loops ignore it.** `BFCO_PowerAttackLoopN` clips are not bound to either variable and play at 1.00x.

The note beside the Speed box reads "BFCO speed" for a BFCO clip. In a clip that is not BFCO, a `BFCO_AttackSpeed` annotation is listed and ignored, and the note says so.

## In the Annotations tab

A BFCO clip gets a **BFCO** section under its annotation list. It gives:

- the next light and power clip, read from the `BFCO_NextIs…` names, and when the buffer and each window open
- where `BFCO_DIY_EndLoop` closes them, or that they stay open to the end of the clip
- where movement can cancel the attack
- warnings for mistakes BFCO does not report

The warnings:

- **A window with no next-attack annotation.** `BFCO_NextWinStart` without a `BFCO_NextIsAttackN` before it leads to no clip.
- **`BFCO_DIY_Rcovery`.** This misspelling of `BFCO_DIY_recovery` appears in many published clips. BFCO registers no event by that name, so it does nothing.
- **A clip that cannot chain.** It has no next-attack annotation and no window of either framework.

The active-annotation highlight follows BFCO's rules as well. The next-attack annotations and the two windows stay highlighted until `BFCO_DIY_EndLoop`, or to the end of the clip when there is none. `BFCO_DIY_recovery` stays highlighted to the end. BFCO's start and close pairs (`BFCO_ForbidRotationStart` and `Close`, `BFCO_0GravityStart` and `End`, `BFCO_bsprintingStart` and `Stop`, the `AMF_…MagstimStart` and `Close` pairs) and its charge stages last from one to the next.

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

For Precision's power-attack timing the previewer counts a clip as a power attack when its path contains "power", which covers these names.

## What is not modelled

- The behavior graph is not run. The hold loop, charge stages and the default chain (`BFCO_DefAttackStageN`) are listed and highlighted, but the previewer does not branch on them or time the windows the graph times itself.
- The graph's own blend for a combo step is 0.2 seconds. The queue uses your Blend setting, which defaults to 0.30.
- In game the graph sends `attackStop` 0.06 seconds before a clip ends. The previewer does not add it.
- The BFCO plugins' input handling, NPC attack choice and stamina rules do not change what a clip looks like and are not simulated.
- Whether the game matches annotation names case-sensitively is not known. The previewer ignores case.
