# Precision attack collisions

[Back to the README](../README.md)

[Precision](https://www.nexusmods.com/skyrimspecialedition/mods/72347) replaces Skyrim's melee hit detection with capsules that an attack clip creates and removes through `Collision_*` annotations. Getting those annotations right normally means loading the game and turning on Precision's debug view. The previewer draws the same capsules from the clip alone.

Tick **Show Precision attack collisions** in the PRECISION group.

![The attack capsule along a battleaxe](images/precision.png)

Red is the attack capsule and blue the recoil capsule, the colours Precision's own debug view uses. Each capsule rides its bone and the root motion, appears at its `Collision_Add` and goes at whatever ends it. The label under the tick box lists every collision with its time span and size, and the **Annotations & effects** tab lists the whole timeline with a note for anything that was ignored.

## Rules

The rules come from the Precision 2.0.4 source, not from its documentation. Where the two disagree, the previewer follows the code.

- `Collision_AttackStart` (or `Collision_Start`) opens the attack. `Add`, `Remove` and `ClearTargets` before it are ignored.
- `Collision_Add` needs `Node()`. Event and parameter names ignore case.
- `Collision_Remove.Node()` removes the first collision on that node. This comparison is case sensitive.
- `Collision_AttackEnd`, `Collision_Cancel`, `attackStop` and `staggerStart` remove every collision and close the attack. A `Collision_Add` after that is ignored until a new `Collision_AttackStart`, and the note names the annotation that closed the attack.
- `Duration(x)` ends a collision x seconds after it starts. `Duration(0)` uses Precision's default lifetime, stretched for a power attack. A clip counts as a power attack when its file name contains "power".
- `hitFrame` removes the first remaining recoil capsule when that Precision setting is on. `NoRecoil` and `WeaponTip` collisions get no recoil capsule.

One quirk is reproduced on purpose. `Collision_Remove.ID()` is documented as removing every collision with that ID. The code removes all of them only when another collision comes after them in Precision's list. Two `ID(1)` collisions at the end of the list lose one and keep the other. The Annotations tab says when a clip hits this.

## Sizes

All sizes are in game units.

- **WEAPON.** Precision measures the weapon model's bounding sphere to get its reach. The previewer reads the same spheres from the weapon NIF. Length is that reach, or `Length()`, times the configured multipliers and `Scale`. Radius is the configured capsule radius, or `Radius()`. The capsule runs from the weapon node along the blade.
- **SHIELD** uses the minimum weapon length, because the previewer loads no left-hand model.
- **Any other node** uses the capsule on that bone in the skeleton NIF, or Precision's built-in hand capsule when the bone has none.

The multipliers are Precision's defaults. Changes you made in Precision's own settings are not read.

## Typical use

1. Select the attack clip and tick the box.
2. Step through the swing. The capsule should appear as the weapon starts to move with intent and go before the recovery.
3. If no capsule appears, open **Annotations & effects**. The Precision section says why, for example an `Add` that came before `Collision_AttackStart`.
