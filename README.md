# Resource Map

**Resource Map** marks useful resources on your world map and minimap so you can find clay, peat, good soil and ores without digging around blind. Every resource type gets its own colour.

### What it shows

- **Clay**: blue, red, fire clay and clays from other mods
- **Soil**: peat, high-fertility soil and compost
- **Ores**: surface ore nuggets and ore exposed in cliffs and slopes

Clay and soil show as coloured areas. Ores show as hexagon markers, drawn underneath creature and player markers so they work alongside mods like Creature Map Markers and Far Player Markers. Clay, soil and ores each have their own layer you can turn on or off on the map. Hover over a spot to see what it is and where.

### How it works

The server scans chunks as they load and rescans around players now and then. Results are saved with the world, and everything found so far is sent to a player when they join, so the map fills in as people explore.

### Finding a resource

Type `.resourcemap find fire clay` (or any resource name). Chat lists the 5 nearest spots with distance and direction. Add `waypoint` to the end to drop a waypoint on the nearest one. While a search is on, matching markers are drawn bigger and everything else fades. `.resourcemap clear` turns it off.

### Client commands

- `.resourcemap`: show the colour legend
- `.resourcemap opacity <0-1>` and `.resourcemap markersize <size>`
- `.resourcemap hide <resource>` and `.resourcemap show <resource>`
- `.resourcemap color <resource> <#hex|default>`
- `.resourcemap settings` and `.resourcemap reset`

### Server commands (admins)

- `/resourcemap rescan`, `reload`, `stats`, `settings`
- `/resourcemap set <setting> <value>`: change any server setting
- `/resourcemap speed slow|normal|fast|max`: how fast the scan runs

### Install

Install on both the server and the clients. Needs Vintage Story 1.22.0 or newer.

## Download

ModDB (1-click install): https://mods.vintagestory.at/resourcemap

## Report a problem

Please report bugs on the [Issues](../../issues) tab. Security problems: see [SECURITY.md](SECURITY.md).
