import struct, sys, json, glob, os

# Reads width/height for every texture in Fallout 4 DX10 archives from the archive's own entry table.
def dx10_entries(path):
    with open(path, 'rb') as f:
        d = f.read()
    magic, version, kind, count, name_off = struct.unpack_from('<4sI4sIQ', d, 0)
    if magic != b'BTDX' or kind != b'DX10':
        return {}
    p = 24
    dims = []
    for _ in range(count):
        name_hash, ext, dir_hash, unk, nchunks, chunk_hdr_len, height, width, nmips, fmt, unk16 = struct.unpack_from('<I4sIBBHHHBBH', d, p)
        p += 24 + nchunks * 24
        dims.append((width, height))
    names = []
    p = name_off
    for _ in range(count):
        n, = struct.unpack_from('<H', d, p); p += 2
        names.append(d[p:p + n].decode('latin1')); p += n
    return {names[i].replace('/', chr(92)).lower(): dims[i] for i in range(count)}

data = sys.argv[1]; catalog = sys.argv[2]
index = {}
for f in sorted(glob.glob(os.path.join(data, 'Fallout4 - Textures*.ba2'))):
    index.update(dx10_entries(f))
rows = json.load(open(catalog))
missing = 0
for r in rows:
    key = ('textures' + chr(92) + r['diffuse']).lower()
    if key in index:
        r['width'], r['height'] = index[key]
    else:
        missing += 1
json.dump(rows, open(catalog, 'w'), indent=1)
print(f"textures indexed {len(index)}, catalog rows {len(rows)}, without dims {missing}")
