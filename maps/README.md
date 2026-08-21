# Native Counter-Strike maps

Ironsight reads GoldSrc BSP30 maps in this folder directly. The BSP supplies
world and brush-model geometry, texture projection, embedded WAD3 palettes,
player spawns, and ladder entities.

Some maps reference stock textures that are not embedded in the BSP. Put the
referenced `.wad` files beside the map and the loader will resolve them by
texture name. Missing WAD textures receive a deterministic material-coloured
fallback, so geometry and gameplay remain available without the dependency.

Current maps:

- `aim_map.bsp`
- `awp_india.bsp`
- `fy_pool_day.bsp`
- `fy_iceworld.bsp`
- `fy_snow.bsp`
- `de_rats2.bsp`
- `cs_office.bsp`

Retained map-specific dependencies:

- `de_vegas.wad` for `fy_iceworld`
- `gfx/env/snow*.tga` and `sound/de_torn/tk_windStreet.wav` for `fy_snow`
- map-supplied `.txt` and `.res` metadata

`fy_snow.res` also references Valve's `halflife.wad`, which is not included in
the downloaded archive. Copy it from an owned Half-Life/Counter-Strike
installation if its stock textures are needed.

## Known map issues

- `de_rats2`: the kitchen sink has no water/swimming behavior. Falling into it
  can softlock a match because the player currently has no way to swim or climb
  back out. Defer this until water volumes and swimming/escape behavior are
  designed together.
