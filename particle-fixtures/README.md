# Particle fixtures

Small NIF files written for the previewer's particle tests, with the five textures they use. Each one isolates a single feature of `NiParticleSystem`: an emitter shape, a force, a collider, a render mode. Nothing here comes from the game or from a mod.

Open one with **Preview effect NIF…**, or mount this folder with **Add mod folder…** first so the textures resolve. In the portable build the folder is `Samples\ParticleFixtures`.

| Files | What they exercise |
|---|---|
| `fountain.nif` | A plain upward emitter with gravity |
| `collision-plane.nif`, `collision-sphere.nif`, `collision-die.nif`, `collision-spawn.nif` | Planar and spherical colliders: bounce, die on contact, spawn on contact |
| `gravity-*.nif`, `directional-drag.nif`, `bomb-*.nif` | Forces |
| `inherit-motion.nif`, `inherit-disabled.nif`, `lag-on.nif`, `lag-off.nif` | Motion inherited from a moving parent |
| `orientation-*.nif`, `rotation-*.nif`, `aspect-*.nif` | Billboard orientation, rotation and velocity stretch |
| `render-*.nif`, `color-packing.nif`, `distance-fade.nif`, `particle-lod.nif` | Alpha test modes, palettes, UV wrap, vertex colour, fade and level of detail |
| `mesh-debris.nif`, `mesh-animated.nif`, `mesh-visibility.nif` | Mesh particles |
| `crossed-strips.nif`, `strip-retention.nif` | Strip particles |
| `multiple-emitters.nif`, `multi-target-emitter.nif`, `skinned-emitter.nif` | Emitter binding, including a skinned emitter mesh |
| `spawn-on-death.nif`, `sequence-end.nif` | Generations and controller sequences |
| `billboards.nif`, `billboard-scale-*.nif` | Billboard nodes |
