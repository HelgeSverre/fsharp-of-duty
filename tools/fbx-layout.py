"""Dump a binary FBX's object layout: every model's name, bounds and transform.

How the Rust map's dimensions were derived. A reference FBX is far too dense to
use as geometry — our levels are brushes and procedural meshes, not imported
triangles — but its object list is exactly the right thing to author against:
what is where, how big, and at what angle.

    python3 tools/fbx-layout.py MODEL.fbx > layout.json

Only vertex arrays are inflated; every other bulk property is skipped by
offset, so a 350 MB file parses in a few seconds. Output is world-space AABBs
in the file's own units, plus each model's translation, rotation and scale.

Only the geometry bounds and the model transforms are decoded; every other
array property is skipped without inflating it, so a 350 MB file parses in
seconds and a few hundred MB of RAM.
"""
import struct, sys, zlib, json

f = open(sys.argv[1], 'rb')
data = f.read()
f.close()
version = struct.unpack_from('<I', data, 23)[0]
wide = version >= 7500
off_fmt, off_size = ('<Q', 8) if wide else ('<I', 4)

geometries = {}   # id -> (min,max) tuple
models = {}       # id -> dict(name, translation, rotation, scale)
connections = []  # (child_id, parent_id)


def read_props(pos, count):
    """Return (values, new_pos). Big arrays collapse to a bounds summary."""
    out = []
    for _ in range(count):
        t = chr(data[pos]); pos += 1
        if t == 'Y': out.append(struct.unpack_from('<h', data, pos)[0]); pos += 2
        elif t == 'C': out.append(data[pos] != 0); pos += 1
        elif t == 'I': out.append(struct.unpack_from('<i', data, pos)[0]); pos += 4
        elif t == 'F': out.append(struct.unpack_from('<f', data, pos)[0]); pos += 4
        elif t == 'D': out.append(struct.unpack_from('<d', data, pos)[0]); pos += 8
        elif t == 'L': out.append(struct.unpack_from('<q', data, pos)[0]); pos += 8
        elif t in 'SR':
            n = struct.unpack_from('<I', data, pos)[0]; pos += 4
            out.append(data[pos:pos + n]); pos += n
        elif t in 'fdlib':
            length, encoding, clen = struct.unpack_from('<III', data, pos); pos += 12
            out.append(('ARRAY', t, length, encoding, pos, clen)); pos += clen
        else:
            raise ValueError(f'unknown property type {t!r} at {pos}')
    return out, pos


def decode_doubles(spec):
    _, t, length, encoding, pos, clen = spec
    raw = data[pos:pos + clen]
    if encoding == 1:
        raw = zlib.decompress(raw)
    return struct.unpack(f'<{length}{"d" if t == "d" else "f"}', raw)


def walk(pos, end, path):
    while pos < end:
        node_end = struct.unpack_from(off_fmt, data, pos)[0]
        if node_end == 0:
            return pos + off_size * 3 + 1
        nprops = struct.unpack_from(off_fmt, data, pos + off_size)[0]
        proplen = struct.unpack_from(off_fmt, data, pos + off_size * 2)[0]
        namelen = data[pos + off_size * 3]
        name = data[pos + off_size * 3 + 1:pos + off_size * 3 + 1 + namelen].decode('utf8', 'replace')
        props_start = pos + off_size * 3 + 1 + namelen
        # Vertex arrays are the only bulk data worth inflating.
        want = name in ('Vertices',) or nprops <= 8
        if want:
            props, after = read_props(props_start, nprops)
        else:
            props, after = [], props_start + proplen
        handle(name, props, path)
        nested_start = props_start + proplen
        if nested_start < node_end:
            walk(nested_start, node_end, path + [(name, props)])
        pos = node_end
    return pos


current = {}


def handle(name, props, path):
    parent = path[-1][0] if path else ''
    if name == 'Geometry' and len(props) >= 2:
        current['geometry'] = props[0]
    elif name == 'Model' and len(props) >= 2:
        ident = props[0]
        label = props[1].decode('utf8', 'replace').split('\x00')[0]
        models[ident] = {'name': label, 't': [0.0, 0.0, 0.0], 'r': [0.0, 0.0, 0.0], 's': [1.0, 1.0, 1.0]}
        current['model'] = ident
    elif name == 'Vertices' and props and isinstance(props[0], tuple):
        values = decode_doubles(props[0])
        xs, ys, zs = values[0::3], values[1::3], values[2::3]
        if xs:
            geometries[current.get('geometry')] = (
                (min(xs), min(ys), min(zs)), (max(xs), max(ys), max(zs)), len(xs))
    elif name == 'P' and props and parent == 'Properties70':
        key = props[0].decode('utf8', 'replace') if isinstance(props[0], bytes) else ''
        target = models.get(current.get('model'))
        if target and key in ('Lcl Translation', 'Lcl Rotation', 'Lcl Scaling'):
            nums = [p for p in props if isinstance(p, float)]
            if len(nums) >= 3:
                target[{'Lcl Translation': 't', 'Lcl Rotation': 'r', 'Lcl Scaling': 's'}[key]] = nums[:3]
    elif name == 'C' and len(props) >= 3:
        connections.append((props[1], props[2]))


walk(27, len(data), [])

geometry_of = {}
for child, parent in connections:
    if child in geometries and parent in models:
        geometry_of[parent] = child

rows = []
for ident, model in models.items():
    geo = geometry_of.get(ident)
    if geo is None:
        continue
    (lo, hi, count) = geometries[geo]
    tx, ty, tz = model['t']
    sx, sy, sz = model['s']
    rows.append({
        'name': model['name'],
        'verts': count,
        'lo': list(lo), 'hi': list(hi),
        't': model['t'], 'r': model['r'], 's': model['s'],
    })

print(json.dumps(rows, indent=1))
print(f'# {len(models)} models, {len(geometries)} geometries, {len(rows)} matched', file=sys.stderr)
