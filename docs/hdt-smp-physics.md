# HDT-SMP physics

[Back to the README](../README.md)

The PHYSICS group simulates HDT-SMP items with Bullet, the engine [FSMP](https://www.nexusmods.com/skyrimspecialedition/mods/57339) uses. Bones, constraints and collision are built from the physics XML the way the FSMP source builds them, so you can test an XML and its collision meshes without starting the game.

Tick **Simulate HDT-SMP physics**. It is off by default.

## What gets simulated

- Armor, clothing, wigs and head parts whose NIF root holds the string "HDT Skinned Mesh Physics Object". The previewer reads the XML that string names.
- Weapons, through a carrier armor. FSMP reads the physics link from armor and head parts only, so a weapon's strap or chain needs an armor NIF whose XML names bones inside the weapon NIF. Equip that armor, or leave it off and the previewer uses any NIF in the weapon's folder whose XML names a weapon bone. A link string on the weapon NIF itself does nothing in game, and Diagnostics says so.
- Models with no link, through `defaultBBPs.xml` when that file is mounted.

## How it runs

The simulation runs on wall-clock time, as the game does. It keeps running while playback is paused and ignores the Speed box, so a paused pose settles under gravity.

When a looping clip wraps, the whole simulation moves with the actor, so root motion does not tear the cloth. After a seek it resets the simulated bones the way an FSMP reset does.

## Controls

| Control | What it does |
|---|---|
| **Simulate HDT-SMP physics** | Switches the simulation on and off. Off returns every bone to the animation alone. |
| **Show collision shapes** | Draws each per-vertex shape as spheres and each per-triangle shape as triangle outlines. A stub at each triangle's centre shows the margin, and in red the penetration depth. |
| **Show bones and constraints** | Draws the simulated bones and a line for each constraint |
| **Reload meshes and physics XML** | Rebuilds the scene from disk |

The line under the button shows systems, bones, constraints, collision shapes, current contacts and the time one physics step takes.

| Overlay colour | Meaning |
|---|---|
| Blue axes | Kinematic bone, which follows the animation |
| Orange axes with a ring | Simulated bone |
| Magenta line | Constraint between two bones |
| Cyan sphere, green triangle | Collider carried only by kinematic bones |
| Yellow sphere, yellow-green triangle | Collider with weight on a simulated bone |
| Red stub behind a triangle | Penetration depth |
| Red line with a cross | Contact found in the last physics step |

## Diagnostics worth acting on

Lines starting with "HDT-SMP:" in the Diagnostics tab list each system with its counts and everything the XML reader skipped. These ones usually mean a real fault in the asset:

- A shape named in the XML is not a skinned shape in the model. The XML binds shapes by name, so a renamed shape loses its physics in game with no error.
- A bone the XML uses is not on the actor.
- A skin bone has no `<bone>` line. The message names the default it inherited and whether that made it kinematic or simulated.
- A constraint joins two kinematic bones and is skipped.

## Typical use

Edit the XML in a text editor, save, press **Reload meshes and physics XML**, and watch the result on a looping clip. With **Show collision shapes** on you can see whether a skirt's collider reaches the thigh, and where it is penetrating.

## Limits

The solver settings, XML rules and collision code follow FSMP, but the result has not been compared with the game frame by frame. Treat it as a close guide for tuning, then confirm in game.
