# DEAD MILES
### Godot 4 / C# | Game concept and implementation brief

> **One-sentence pitch:** A lonely first-person road-survival sandbox where your battered vehicle is your home, and every kilometer of a procedurally generated countryside offers a reason to stop.

## Directive to the developer (Sage)

Build a **playable game**, not a tech demo or a chain of disconnected prototypes. Take ownership of technical and artistic decisions, use available asset packs when suitable, and replace unavailable assets with clean, visually coherent placeholders. Do not stop after scaffolding or ask for approval at each step. Implement what is feasible, test it, and make sure it runs. Prioritize feel, atmosphere, and a complete gameplay loop over feature quantity.

This is a **new, independent Godot project**. Don't import other game projects or assume pre-existing systems. Keep the code modular, readable, and documented where non-obvious. Aim for Windows desktop first.

## Fantasy and tone

You are driving through a vast, sparsely populated countryside after the collapse of normal life. No zombies, no supernatural explanation required, no mandatory storyline, no quest markers dragging you along. The environment should feel quiet, beautiful, and slightly unsettling: sun through pine forests, rain drumming on the roof, headlights cutting through fog, the relief of discovering a working gas pump.

Reference points (inspiration, not imitation): **Jalopy** for vehicle-as-companion, **Project Zomboid** for scavenging and consequence, **DayZ** for uncertainty and encounters, and **The Long Dark** for environmental atmosphere.

## Core loop

**Drive → notice something → pull over → explore/scavenge/hunt → manage risk → maintain car and yourself → drive on.**

The road is the spine of the game; the countryside beside it is explorable, not a corridor. Nothing forces constant travel. Players can park, sleep, camp, repair the car, and spend time in a place they like.

## World

- **Procedural, seed-based generation** of rolling terrain, forests, fields, lakes/streams where feasible, scattered clearings, and roads that follow reasonable grades. Use streamed chunks around the player with deterministic placement and unloading.
- **A believable connected road network**, with one navigable main route and occasional branch roads, dirt tracks, turnoffs, and dead ends. Avoid impossible inclines, floating roads, and random object scatter. Roadside landmarks are gameplay signals.
- **Points of interest:** gas station, abandoned farmhouse, roadside diner, small workshop, cabin, checkpoint, campsite, and occasional tiny settlement. Begin with a small handcrafted set that can be placed procedurally; variety comes from layout, condition, contents, and surroundings.
- **Environmental storytelling:** wrecked cars, empty campsites, shuttered shops, improvised barricades. Use signs, props, and lighting rather than lengthy text logs.
- **Traversal quality:** sensible vehicle collision; reliable foot collision; no holes in terrain; no pop-in directly in front of the car. Far scenery may use LOD, fog, and simplified silhouettes.

## Player

- First-person on foot with walking, sprinting, crouching if useful, jumping, interaction, inventory, and flashlight.
- Core survival needs: **health, hunger, thirst, stamina**. Keep tuning forgiving enough that exploration is enjoyable; do not create busywork meters for every bodily function.
- Equipment: flashlight, basic melee tool, simple firearm or hunting weapon if practical, food, water, fuel can, repair parts, medical supplies.
- Scavenging should be tactile and fast: approach containers, inspect/search, take useful items, leave. Inventory is limited by capacity, but avoid elaborate grid-Tetris for the first playable version.

## The vehicle: the heart of the game

- One drivable, physically convincing **old car / station wagon / small van** that the player can enter and exit. Functional steering, throttle, brakes, reverse, handbrake, headlights, engine start/stop, and a followable first-person cockpit view; optional third-person driving camera for usability.
- **Persistent condition:** fuel, engine health, tires (simplify to an overall tire condition if necessary), and storage. Engine damage affects performance; running out of fuel requires scavenging or walking.
- **Interaction:** refuel, repair with parts, open/use trunk storage, transfer items between inventory and vehicle. Park, switch off the engine, and shelter from rain.
- Good-feeling driving matters more than precise car engineering. Tune for keyboard/mouse; support controllers where straightforward.
- Do not make the player lose the car because of a minor collision or a tiny physics glitch. Provide a recovery mechanism for overturned or trapped vehicles.

## Encounters and risk

- **No zombies.** Humans are the unpredictable threat: occasional solitary scavenger, two-person bandit group, or an occupied roadside stop. Use a small number of readable behaviors: idle/patrol, notice, approach/warn, flee, attack, disengage.
- **Wildlife:** deer or boar to hunt; ambient birds and small animals as visual/audio life. Simple animation is enough. Avoid large bespoke character animation requirements.
- Some encounters are peaceful or empty. Silence is important. Encounters should be infrequent enough that a distant figure or parked car matters.
- Make combat functional but secondary. Survival, driving, and atmosphere are the main attraction.

## Weather and atmosphere

High visual priority, with practical performance in Godot:

- Full day/night cycle with soft sunrise/sunset, moonlight, stars, moving clouds, and coherent lighting changes.
- Clear, overcast, drizzle, heavy rain, fog, wind, and occasional thunderstorms. Smooth transitions, with weather reflected in sky, terrain wetness, vegetation motion, ambience, visibility, and driving.
- Rain audible against the car; windshield rain/wipers if affordable, otherwise a convincing simpler effect. Headlights and fog should make night travel moody but readable.
- Localized ambient sound: wind in trees, distant crows, rain on metal, engine idle, tires on gravel/asphalt, footsteps. Keep audio dynamic and non-repetitive.
- Do not chase expensive volumetrics at the cost of frame rate. Layered fog, particles, screen effects, and good lighting are valid solutions.

## UX and game flow

- **Main menu:** New Game (seed optional), Continue, Settings, Quit.
- Spawn near a small roadside shelter with the vehicle, some fuel, a flashlight, and minimal supplies. Let the player understand driving, looting, and survival through play rather than a long tutorial.
- Small, legible HUD: health/needs when relevant, contextual interaction prompt, vehicle fuel/condition while driving, compass or unobtrusive navigation aid. No huge objective list.
- Inventory and vehicle storage interfaces, pause menu, keybinds, graphics/display settings, audio settings.
- **Save/load:** persistent player state, car position/condition/cargo, time/weather, world seed, looted containers, and important altered world objects. Autosave plus manual save if practical.

## Implementation guidance

- **Engine:** Godot 4.x, C#, Forward+ renderer on capable PCs. Organize scenes and scripts by feature, not as one giant controller.
- Recommended systems: `WorldGenerator`, `ChunkStreamer`, `RoadNetwork`, `POIPlacer`, `WeatherDirector`, `DayNightCycle`, `PlayerController`, `VehicleController`, `InteractionSystem`, `InventoryService`, `LootSpawner`, `EncounterDirector`, `SaveService`, `AudioDirector`.
- Use deterministic noise and seeded RNG for world/loot placement. Persist deltas to the seed-generated baseline instead of serializing the entire world.
- Road placement and roadside POIs must validate slopes, access, spacing, and collisions; choose logic that makes visual and physical sense.
- Use MultiMesh/instancing, LOD/culling, chunk reuse, and restrained shadow distances for vegetation; keep collision primitives cheap.
- Avoid loading distant AI. Encounter simulation should activate near the player and deactivate safely at distance.
- Provide descriptive logs and graceful handling of missing assets. Do not silently swallow failures or fill the console with errors.

## What counts as a playable result

A player should be able to:

1. Launch the game, create/load a world, and spawn beside their vehicle.
2. Drive along a convincing road through changing terrain and weather.
3. Spot a roadside location, park, exit, and search it for useful supplies.
4. Find fuel or a repair item, return to the car, and use it.
5. Experience at least one simple wildlife or human encounter.
6. Survive long enough to see day turn into night, then save and resume.

These are **acceptance checks, not development phases**. Build the strongest end-to-end version possible. If a mechanic cannot be finished robustly, prefer a smaller complete implementation rather than a broken promise.

## Creative guardrails

- **Not** a zombie game, MMO, mission-driven RPG, or intricate crafting simulator.
- **Not** an empty highway with randomly sprinkled props.
- **Not** a showcase of procedural generation with no meaningful player decisions.
- Keep the world grounded, atmospheric, and explorable. The car should feel like a trusted companion.
- Focus on **a world to live in**, one road trip at a time.

**Working title:** DEAD MILES. Change it later if a stronger original name emerges; do not block production on naming.
