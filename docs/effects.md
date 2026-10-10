# Effects

[Back to the README](../README.md)

Attack clips often spawn visual effects through annotations. The previewer resolves those annotations to NIF files and plays them on the timeline with the clip. It can also open any effect NIF on its own.

Effects are the least exact part of the program. Placement and timing follow the annotations and records. The particle look is a simulation of the NIF's modifiers and has not been matched against the game image by image. The Diagnostics tab names every block it could not handle.

## Effects from a clip

With **Effects** ticked, these are resolved when a clip is selected:

- Payload Interpreter annotations (`PIE.@...`), including aliases and delayed payloads. Spell casts are followed to their magic effects, projectiles, casting art and hit art. Particle spawns keep their body-part index and offsets.
- Dynamic Animation Casting event configurations, when **Preview conditional DAC triggers** is ticked.
- Key-triggered spells from Dtry's Key Utils configurations, listed under **EFFECT TRIGGERS** so you can fire them by hand.

The bottom of the **Annotations & effects** tab lists every resolved effect with its start time and the annotation it came from.

Actor conditions on these triggers are assumed to pass. Gameplay scripts are not run.

## Opening an effect NIF directly

**Preview effect NIF…** in the right column opens a NIF and plays it alone on the timeline.

![A vanilla fireball explosion](images/effect.png)

**Effect sequence** picks one of the NIF's controller sequences, or follows Start to Loop automatically.

**Add effect overlay…** attaches an effect NIF to the scene alongside the character, and **Clear effect overlays** removes them.

## Effects defined in plugins

- **Plugin visual effects…** lists the visual effect and reference effect records in the loaded plugins and plays one at a chosen target.
- **Impacts / explosions...** lists impact data and explosion records, with their art and duration.

## What the particle simulator covers

`NiParticleSystem` runs at a fixed 120 Hz, so a seek always lands on the same result.

- Box, sphere, cylinder and mesh-surface emitters, including emitters that follow animated bones
- Lifetime, spawn-on-death generations, growth and fade, colour, rotation
- Gravity, drag, bomb and turbulence forces
- Planar and spherical colliders with bounce, die-on-collide and spawn-on-collide
- Flipbook sub-texture sheets, read in the game's (u, width, v, height) layout
- Strip particles, mesh particles and refraction layers

Lag bone controllers, billboard nodes and visibility controllers on effect meshes are also evaluated.

## Custom effect annotations

The previewer also plays `PIE.@FF7VFX` annotations, an instruction used by the FF7 Limit Gauge SKSE plugin to spawn a NIF at the actor root or along the weapon blade:

```
PIE.@FF7VFX|<nif under meshes>|root, follow or weapon|<seconds>|<x>|<y>|<z>[|nine rotation numbers]
PIE.@FF7VFXEND|<nif under meshes>
```

`root` spawns once in the world at the actor root plus the offset. `follow` does the same but stays on the actor. `weapon` rides the `WEAPON` node with the NIF's +Z along the blade and respawns every `<seconds>` until the matching end annotation.

## Limits

- Projectile origins, hit events and spell art lifetimes still need comparison with the game.
- Some modifiers and shader effects are approximate.
- Dense effects cost frame time. The Diagnostics tab reports the particle budget in use.

[Technical notes](technical-notes.md) records what each modifier does, which engine version it was checked against and what remains unverified.
