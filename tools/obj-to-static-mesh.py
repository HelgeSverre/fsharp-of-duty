"""Convert a Crafty OBJ export into Ironsight's compact static-mesh format.

The format is deliberately tiny and boring: a four-byte magic, triangle count,
then a packed uint16 material plus position/UV pairs for three corners. Normals
are recovered from winding when the embedded asset is loaded.

    python3 tools/obj-to-static-mesh.py de_dust2.obj dust2.mesh textures.zip atlas.png
"""

import struct
import sys
import zipfile
from io import BytesIO
from pathlib import Path

from PIL import Image


SOURCE_UNITS_PER_METRE = 40.0
SOURCE_CENTRE_X = -320.0
SOURCE_CENTRE_Y = 1120.0

# Source material number -> Ironsight Materials.all index. The reference's MTL
# names distinguish individual wall/door variants. Each texture keeps its own
# atlas layer while this table selects the collision/penetration surface.
MATERIALS = {
    0: 13, 1: 1, 2: 2, 3: 1, 4: 1, 5: 11, 6: 11, 7: 1,
    8: 1, 9: 2, 10: 13, 11: 13, 12: 2, 13: 1, 14: 1, 15: 13,
    16: 1, 17: 2, 18: 22, 19: 1, 20: 2, 21: 2, 22: 11, 23: 1,
    24: 1, 25: 11, 26: 1, 27: 1, 28: 1, 29: 1, 30: 1, 31: 2,
    32: 6, 33: 1,
}


def world(vertex):
    """Source X/Y are horizontal and Z is up; Ironsight uses Y-up."""
    x, y, z = vertex
    return (
        (SOURCE_CENTRE_X - x) / SOURCE_UNITS_PER_METRE,
        z / SOURCE_UNITS_PER_METRE,
        (y - SOURCE_CENTRE_Y) / SOURCE_UNITS_PER_METRE,
    )


source, output, texture_zip, atlas_output = map(Path, sys.argv[1:5])
vertices = []
texcoords = []
triangles = []
source_material = 1

for line in source.read_text().splitlines():
    fields = line.split()
    if not fields:
        continue
    if fields[0] == "v":
        vertices.append(tuple(map(float, fields[1:4])))
    elif fields[0] == "vt":
        texcoords.append(tuple(map(float, fields[1:3])))
    elif fields[0] == "usemtl":
        source_material = int(fields[1].rsplit("_", 1)[1])
    elif fields[0] == "f":
        polygon = []
        for field in fields[1:]:
            indices = field.split("/")
            polygon.append((int(indices[0]) - 1, int(indices[1]) - 1))
        for index in range(1, len(polygon) - 1):
            # Mirroring X and swapping Source Y/Z cancel one another's
            # handedness change, so the OBJ winding remains outward-facing.
            corners = [polygon[0], polygon[index], polygon[index + 1]]
            triangles.append((
                100 + MATERIALS[source_material] * 1000 + source_material,
                *((world(vertices[vertex]), texcoords[texcoord]) for vertex, texcoord in corners),
            ))

with output.open("wb") as stream:
    stream.write(b"D2M2")
    stream.write(struct.pack("<I", len(triangles)))
    for material, a, b, c in triangles:
        stream.write(struct.pack("<H15f", material, *a[0], *a[1], *b[0], *b[1], *c[0], *c[1]))

# One normalized tile per source material. UVs remain the OBJ's own; the
# shader selects the corresponding 256px atlas cell and repeats within it.
atlas = Image.new("RGBA", (1536, 1536))
with zipfile.ZipFile(texture_zip) as archive:
    for material in range(34):
        name = f"textures/de_dust2_material_{material}.png"
        # These GoldSrc-era textures are deliberately low-resolution. Nearest
        # preserves their texels and keeps the atlas highly compressible.
        texture = Image.open(BytesIO(archive.read(name))).convert("RGBA").resize((256, 256), Image.Resampling.NEAREST)
        atlas.paste(texture, ((material % 6) * 256, (material // 6) * 256))
atlas.save(atlas_output, optimize=True)

print(f"{source}: {len(vertices)} vertices -> {len(triangles)} textured triangles -> {output}")
print(f"{texture_zip}: 34 materials -> {atlas_output}")
