#!/usr/bin/env python3
"""Stage 2 of the Dead Miles map importer: stage-1 arrays -> streamable tile packs.

    python3 build.py <workdir> <outdir> [--name mazowsze]

Writes <outdir>/<name>.idx (header + tile index), <name>.NN.pak (zlib tile blobs, each file
kept under 48 MB so it can live in a normal git repository) and <name>.places.json.

Game space: X = east, Z = south (north is -Z), metres from the origin in common.py.
The binary layout here is read by scripts/world/map/MapData.cs; keep the two in step.
"""
import sys
import os
import json
import math
import time
import zlib
import struct
import heapq
import argparse
from collections import defaultdict
from multiprocessing import Pool

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common as C  # noqa: E402

TILE = 1536.0            # metres; 16 terrain chunks of 96 m
LAND_RES = 384           # land-cover cells per tile side (4 m)
LAND_CELL = TILE / LAND_RES
WD_RES = 32              # water-distance cells per tile side (48 m); stored as (WD_RES+1)^2 corners
WD_CELL = TILE / WD_RES
WD_UNIT = 4.0            # metres per stored unit
PAK_LIMIT = 48 * 1024 * 1024

F_BRIDGE, F_TUNNEL, F_ONEWAY, F_UNPAVED, F_LIT, F_RAIL, F_STARTCAP, F_ENDCAP = 1, 2, 4, 8, 16, 32, 64, 128
BF_GHOST, BF_TRIS, BF_PART = 1, 2, 4

RAMP_SLOPE = 0.05        # embankment grade leading up to a bridge
DECK_CLEAR = 6.0         # lift of a bridge that passes over another road or a railway
DECK_LOW = 1.4           # lift of a bridge that only spans water or a ditch

t0 = time.time()


def log(msg):
    print(f'[{time.time() - t0:6.0f}s] {msg}', flush=True)


def tile_of(v):
    return int(math.floor(v / TILE))


# =================================================================================================
# roads
# =================================================================================================

def compute_lift(roads):
    """Per road vertex: how far above the ground the carriageway rides (bridges and their ramps)."""
    import shapely
    xy = roads['xy'].astype(np.float64) / 100.0
    off = roads['off']
    attrs = roads['attrs']
    nodes = roads['nodes']
    nways = len(attrs)
    flags = attrs['flags']
    layer = attrs['layer']
    is_bridge = ((flags & F_BRIDGE) != 0) & ((flags & F_TUNNEL) == 0)
    bridge_idx = np.nonzero(is_bridge)[0]
    log(f'{len(bridge_idx)} bridge ways')

    counts = np.diff(off)
    way_of_pt = np.repeat(np.arange(nways), counts)
    lines = shapely.linestrings(xy, indices=way_of_pt)
    tree = shapely.STRtree(lines)
    pairs = tree.query(lines[bridge_idx], predicate='intersects')
    crosses = np.zeros(nways, dtype=bool)
    for bi, other in zip(pairs[0], pairs[1]):
        b = bridge_idx[bi]
        if other == b or (flags[other] & F_TUNNEL):
            continue
        if is_bridge[other] and layer[other] >= layer[b]:
            continue
        nb = nodes[off[b]:off[b + 1]]
        no = nodes[off[other]:off[other + 1]]
        if np.intersect1d(nb, no).size:
            continue  # joined at a junction, not passing over
        crosses[b] = True
    del lines, tree

    uniq, inv = np.unique(nodes, return_inverse=True)
    inv = inv.astype(np.int64)
    lift = np.zeros(len(uniq), dtype=np.float32)
    for b in bridge_idx:
        lay = max(1, int(layer[b]))
        target = DECK_CLEAR * lay if (crosses[b] or lay >= 2) else DECK_LOW
        sl = inv[off[b]:off[b + 1]]
        np.maximum.at(lift, sl, target)

    # adjacency along every non-tunnel way
    seg_a = np.arange(len(nodes) - 1)
    last = np.zeros(len(nodes), dtype=bool)
    last[off[1:] - 1] = True
    seg_a = seg_a[~last[:-1]]
    tunnel_pt = np.repeat((flags & F_TUNNEL) != 0, counts)
    seg_a = seg_a[~tunnel_pt[seg_a]]
    u = inv[seg_a]
    v = inv[seg_a + 1]
    d = np.hypot(xy[seg_a + 1, 0] - xy[seg_a, 0], xy[seg_a + 1, 1] - xy[seg_a, 1]).astype(np.float32)
    uu = np.concatenate([u, v])
    vv = np.concatenate([v, u])
    dd = np.concatenate([d, d])
    order = np.argsort(uu, kind='stable')
    uu, vv, dd = uu[order], vv[order], dd[order]
    start = np.searchsorted(uu, np.arange(len(uniq) + 1))

    heap = [(-float(lift[i]), int(i)) for i in np.nonzero(lift > 0)[0]]
    heapq.heapify(heap)
    while heap:
        neg, i = heapq.heappop(heap)
        cur = -neg
        if cur < lift[i] - 1e-4:
            continue
        for k in range(start[i], start[i + 1]):
            j = int(vv[k])
            cand = cur - RAMP_SLOPE * float(dd[k])
            if cand > lift[j] + 0.02:
                lift[j] = cand
                heapq.heappush(heap, (-cand, j))
    log(f'{int((lift > 0.05).sum())} road nodes ride above the ground')
    return lift[inv]


def split_way(px, pz, pl):
    """Cut a polyline at tile borders. Yields (tx, tz, x[], z[], lift[], start_index_distance)."""
    n = len(px)
    xs, zs, ls = [px[0]], [pz[0]], [pl[0]]
    for i in range(n - 1):
        x0, z0, l0 = px[i], pz[i], pl[i]
        x1, z1, l1 = px[i + 1], pz[i + 1], pl[i + 1]
        ts = []
        a, b = sorted((x0, x1))
        k = math.floor(a / TILE) + 1
        while k * TILE < b:
            ts.append((k * TILE - x0) / (x1 - x0))
            k += 1
        a, b = sorted((z0, z1))
        k = math.floor(a / TILE) + 1
        while k * TILE < b:
            ts.append((k * TILE - z0) / (z1 - z0))
            k += 1
        for t in sorted(ts):
            if 1e-6 < t < 1 - 1e-6:
                xs.append(x0 + (x1 - x0) * t)
                zs.append(z0 + (z1 - z0) * t)
                ls.append(l0 + (l1 - l0) * t)
        xs.append(x1)
        zs.append(z1)
        ls.append(l1)
    # group runs of segments by the tile of their midpoint
    pieces = []
    cur_tile = None
    start = 0
    for i in range(len(xs) - 1):
        t = (tile_of((xs[i] + xs[i + 1]) * 0.5), tile_of((zs[i] + zs[i + 1]) * 0.5))
        if t != cur_tile:
            if cur_tile is not None:
                pieces.append((cur_tile, start, i))
            cur_tile = t
            start = i
    pieces.append((cur_tile, start, len(xs) - 1))
    return xs, zs, ls, pieces


def build_roads(work):
    roads = np.load(os.path.join(work, 'roads.npz'))
    lift_pt = compute_lift(roads)
    xy = roads['xy']
    off = roads['off']
    attrs = roads['attrs']
    gx = xy[:, 0].astype(np.float64) / 100.0
    gz = -xy[:, 1].astype(np.float64) / 100.0
    per_tile = defaultdict(list)
    nways = len(attrs)
    for w in range(nways):
        a, b = off[w], off[w + 1]
        px, pz, pl = gx[a:b], gz[a:b], lift_pt[a:b]
        # drop repeated points
        keep = np.ones(b - a, dtype=bool)
        keep[1:] = (np.abs(np.diff(px)) + np.abs(np.diff(pz))) > 0.02
        if keep.sum() < 2:
            continue
        px, pz, pl = px[keep], pz[keep], pl[keep]
        cls, flags, width_dm, layer, _lanes = attrs[w]
        flags = int(flags)
        tx0, tx1 = tile_of(px.min()), tile_of(px.max())
        tz0, tz1 = tile_of(pz.min()), tile_of(pz.max())
        if tx0 == tx1 and tz0 == tz1:
            xs, zs, ls = px.tolist(), pz.tolist(), pl.tolist()
            pieces = [((tx0, tz0), 0, len(xs) - 1)]
        else:
            xs, zs, ls, pieces = split_way(px.tolist(), pz.tolist(), pl.tolist())
        seglen = [math.hypot(xs[i + 1] - xs[i], zs[i + 1] - zs[i]) for i in range(len(xs) - 1)]
        cum = [0.0]
        for s in seglen:
            cum.append(cum[-1] + s)
        for (tx, tz), i0, i1 in pieces:
            f = flags
            if i0 == 0:
                f |= F_STARTCAP
            if i1 == len(xs) - 1:
                f |= F_ENDCAP
            ox, oz = tx * TILE, tz * TILE
            n = i1 - i0 + 1
            rec = bytearray(struct.pack('<BBBbfH', int(cls), f, int(width_dm), int(layer), cum[i0] % 4096.0, n))
            for i in range(i0, i1 + 1):
                rec += struct.pack('<hhB',
                                   int(round((xs[i] - ox) * 10)), int(round((zs[i] - oz) * 10)),
                                   int(min(255, max(0, round(ls[i] * 10)))))
            per_tile[(tx, tz)].append(bytes(rec))
        if w % 300000 == 0:
            log(f'roads {w}/{nways}')
    log(f'roads cut into {sum(len(v) for v in per_tile.values())} pieces over {len(per_tile)} tiles')
    return per_tile


# =================================================================================================
# buildings
# =================================================================================================

def ring_area(x, z):
    return 0.5 * float(np.dot(x, np.roll(z, -1)) - np.dot(z, np.roll(x, -1)))


def point_in_ring(px, pz, x, z):
    inside = False
    n = len(x)
    j = n - 1
    for i in range(n):
        if (z[i] > pz) != (z[j] > pz) and px < (x[j] - x[i]) * (pz - z[i]) / (z[j] - z[i]) + x[i]:
            inside = not inside
        j = i
    return inside


def build_buildings(work):
    import mapbox_earcut as earcut
    d = np.load(os.path.join(work, 'buildings.npz'))
    xy = d['xy']
    ring_off = d['ring_off']
    ring_inner = d['ring_inner']
    feat_ring = d['feat_ring']
    attrs = d['attrs']
    nfeat = len(attrs)
    gx = xy[:, 0].astype(np.float64) / 100.0
    gz = -xy[:, 1].astype(np.float64) / 100.0

    # centroid (of the outer ring's vertices) for every feature
    first_ring = feat_ring[:-1]
    a = ring_off[first_ring]
    b = ring_off[first_ring + 1]
    csx = np.concatenate([[0.0], np.cumsum(gx)])
    csz = np.concatenate([[0.0], np.cumsum(gz)])
    cnt = np.maximum(1, b - a)
    cx = (csx[b] - csx[a]) / cnt
    cz = (csz[b] - csz[a]) / cnt

    # Outlines that are fully described by building:part pieces are dropped in favour of the parts.
    is_part = attrs['part'] == 1
    part_cells = defaultdict(list)
    for i in np.nonzero(is_part)[0]:
        part_cells[(int(cx[i] // 64), int(cz[i] // 64))].append(i)
    has_parts = np.zeros(nfeat, dtype=bool)
    if part_cells:
        for i in np.nonzero(~is_part)[0]:
            x = gx[a[i]:b[i]]
            z = gz[a[i]:b[i]]
            x0, x1, z0, z1 = x.min(), x.max(), z.min(), z.max()
            found = False
            for kx in range(int(x0 // 64), int(x1 // 64) + 1):
                for kz in range(int(z0 // 64), int(z1 // 64) + 1):
                    for p in part_cells.get((kx, kz), ()):
                        if x0 <= cx[p] <= x1 and z0 <= cz[p] <= z1 and point_in_ring(cx[p], cz[p], x, z):
                            found = True
                            break
                    if found:
                        break
                if found:
                    break
            has_parts[i] = found
    log(f'{int(is_part.sum())} building parts; {int(has_parts.sum())} outlines replaced by their parts')

    per_tile = defaultdict(list)
    kept = 0
    for i in range(nfeat):
        if has_parts[i]:
            continue
        btype, part, roof, height_dm, min_dm, roof_dm, has_height = attrs[i]
        btype = int(btype)
        rings = []
        area = 0.0
        ok = True
        for r in range(feat_ring[i], feat_ring[i + 1]):
            x = gx[ring_off[r]:ring_off[r + 1] - 1]   # drop the closing duplicate
            z = gz[ring_off[r]:ring_off[r + 1] - 1]
            if len(x) < 3:
                ok = ring_inner[r] == 1
                if not ok:
                    break
                continue
            ar = ring_area(x, z)
            inner = ring_inner[r] == 1
            if not inner:
                area = abs(ar)
            # outer rings positive (shoelace in X,Z), holes negative
            if (ar < 0) != inner:
                x, z = x[::-1], z[::-1]
            if len(x) > 600:
                ok = False
                break
            rings.append((x, z))
        if not ok or not rings or area < 4.0:
            continue
        height = height_dm / 10.0
        if not has_height:
            if btype == C.B_GENERIC:
                height = 2.8 if area < 30 else 6.5 if area < 260 else 8.0
            elif btype == C.B_HOUSE and area < 45:
                height = 4.5
            elif btype == C.B_APARTMENTS and area > 900:
                height = 18.0
            else:
                height = C.BUILDING_DEFAULT_HEIGHT[btype]
        height = max(2.2, height)
        roof = int(roof)
        if roof == 0:
            # sensible default when the map does not say
            if btype in (C.B_HOUSE, C.B_FARM, C.B_CHURCH) or (btype == C.B_GENERIC and 30 <= area < 260):
                roof = 2 if len(rings) == 1 and len(rings[0][0]) <= 8 else 3
            else:
                roof = 1
        roof_h = roof_dm / 10.0
        if roof >= 2 and roof_h <= 0:
            roof_h = min(height * 0.42, 4.5) if roof in (2, 3) else height * 0.5
        min_h = min(min_dm / 10.0, max(0.0, height - 0.5))
        roof_h = min(roof_h, height - min_h)

        tx, tz = tile_of(cx[i]), tile_of(cz[i])
        ox, oz = tx * TILE, tz * TILE
        flags = BF_PART if part else 0
        tri = None
        if len(rings) > 1:
            verts = np.concatenate([np.stack([x, z], axis=1) for x, z in rings])
            ends = np.cumsum([len(x) for x, _ in rings]).astype(np.uint32)
            tri = earcut.triangulate_float64(verts, ends)
            if len(tri) == 0 or len(tri) > 60000:
                rings = rings[:1]
                tri = None
            else:
                flags |= BF_TRIS

        def encode(ox, oz, fl):
            rec = bytearray(struct.pack('<BBBHHHB', btype, fl, roof, int(round(height * 10)),
                                        int(round(min_h * 10)), int(round(roof_h * 10)), len(rings)))
            for x, z in rings:
                rec += struct.pack('<H', len(x))
                q = np.empty((len(x), 2), dtype='<i2')
                q[:, 0] = np.clip(np.round((x - ox) * 10), -32768, 32767)
                q[:, 1] = np.clip(np.round((z - oz) * 10), -32768, 32767)
                rec += q.tobytes()
            if fl & BF_TRIS:
                rec += struct.pack('<H', len(tri)) + tri.astype('<u2').tobytes()
            return bytes(rec)

        per_tile[(tx, tz)].append(encode(ox, oz, flags))
        kept += 1
        # Ghost copies let neighbouring tiles keep trees and grass out of an overhanging building.
        x, z = rings[0]
        for gtx in range(tile_of(x.min()), tile_of(x.max()) + 1):
            for gtz in range(tile_of(z.min()), tile_of(z.max()) + 1):
                if (gtx, gtz) != (tx, tz):
                    per_tile[(gtx, gtz)].append(encode(gtx * TILE, gtz * TILE, (flags | BF_GHOST) & ~BF_TRIS))
        if i % 500000 == 0:
            log(f'buildings {i}/{nfeat}')
    log(f'{kept} buildings over {len(per_tile)} tiles')
    return per_tile


# =================================================================================================
# land cover
# =================================================================================================

_land = {}


def _init_land(work):
    d = np.load(os.path.join(work, 'land.npz'))
    _land['gx'] = d['xy'][:, 0].astype(np.float32) / 100.0
    _land['gz'] = -d['xy'][:, 1].astype(np.float32) / 100.0
    _land['ring_off'] = d['ring_off']
    _land['ring_inner'] = d['ring_inner']
    _land['feat_ring'] = d['feat_ring']
    _land['cls'] = d['attrs']['cls']
    w = np.load(os.path.join(work, 'waterlines.npz'))
    _land['wx'] = w['xy'][:, 0].astype(np.float32) / 100.0
    _land['wz'] = -w['xy'][:, 1].astype(np.float32) / 100.0
    _land['woff'] = w['off']
    _land['wwidth'] = w['attrs']['width_dm'].astype(np.float32) / 10.0


def _raster_tile(job):
    from PIL import Image, ImageDraw
    (tx, tz), feats, lines = job
    ox, oz = tx * TILE, tz * TILE
    gx, gz = _land['gx'], _land['gz']
    ring_off, ring_inner, feat_ring = _land['ring_off'], _land['ring_inner'], _land['feat_ring']
    img = Image.new('L', (LAND_RES, LAND_RES), C.L_OPEN)
    draw = ImageDraw.Draw(img)
    s = 1.0 / LAND_CELL

    def pix(a, b):
        p = np.empty((b - a, 2), dtype=np.float32)
        p[:, 0] = (gx[a:b] - ox) * s
        p[:, 1] = (gz[a:b] - oz) * s
        return p.ravel().tolist()

    for cls, f in feats:
        r0, r1 = feat_ring[f], feat_ring[f + 1]
        if r1 - r0 == 1:
            draw.polygon(pix(ring_off[r0], ring_off[r0 + 1]), fill=int(cls))
        else:
            mask = Image.new('1', (LAND_RES, LAND_RES), 0)
            md = ImageDraw.Draw(mask)
            md.polygon(pix(ring_off[r0], ring_off[r0 + 1]), fill=1)
            for r in range(r0 + 1, r1):
                md.polygon(pix(ring_off[r], ring_off[r + 1]), fill=0)
            img.paste(int(cls), (0, 0), mask)
    wx, wz, woff, wwidth = _land['wx'], _land['wz'], _land['woff'], _land['wwidth']
    for li in lines:
        a, b = woff[li], woff[li + 1]
        p = np.empty((b - a, 2), dtype=np.float32)
        p[:, 0] = (wx[a:b] - ox) * s
        p[:, 1] = (wz[a:b] - oz) * s
        draw.line(p.ravel().tolist(), fill=C.L_WATER, width=max(1, int(round(wwidth[li] * s))))
    return (tx, tz), img.tobytes()


def build_land(work, tiles_needed):
    d = np.load(os.path.join(work, 'land.npz'))
    gx = d['xy'][:, 0].astype(np.float64) / 100.0
    gz = -d['xy'][:, 1].astype(np.float64) / 100.0
    ring_off, feat_ring = d['ring_off'], d['feat_ring']
    cls = d['attrs']['cls']
    prio = {c: i for i, c in enumerate(C.LAND_ORDER)}
    nfeat = len(cls)
    first = feat_ring[:-1]
    a, b = ring_off[first], ring_off[first + 1]
    per_tile = defaultdict(list)
    for f in range(nfeat):
        x, z = gx[a[f]:b[f]], gz[a[f]:b[f]]
        c = int(cls[f])
        for tx in range(tile_of(x.min()), tile_of(x.max()) + 1):
            for tz in range(tile_of(z.min()), tile_of(z.max()) + 1):
                per_tile[(tx, tz)].append((prio[c], c, f))
    w = np.load(os.path.join(work, 'waterlines.npz'))
    wx = w['xy'][:, 0].astype(np.float64) / 100.0
    wz = -w['xy'][:, 1].astype(np.float64) / 100.0
    woff = w['off']
    lines_per_tile = defaultdict(list)
    for li in range(len(woff) - 1):
        x, z = wx[woff[li]:woff[li + 1]], wz[woff[li]:woff[li + 1]]
        for tx in range(tile_of(x.min() - 10), tile_of(x.max() + 10) + 1):
            for tz in range(tile_of(z.min() - 10), tile_of(z.max() + 10) + 1):
                lines_per_tile[(tx, tz)].append(li)
    all_tiles = set(per_tile) | set(lines_per_tile) | set(tiles_needed)
    jobs = []
    for t in all_tiles:
        feats = sorted(per_tile.get(t, ()))
        jobs.append((t, [(c, f) for _, c, f in feats], lines_per_tile.get(t, [])))
    log(f'rasterising land cover for {len(jobs)} tiles')
    out = {}
    with Pool(4, initializer=_init_land, initargs=(work,)) as pool:
        for n, (t, data) in enumerate(pool.imap_unordered(_raster_tile, jobs, chunksize=64)):
            out[t] = data
            if n % 5000 == 0:
                log(f'land {n}/{len(jobs)}')
    return out


def build_water_distance(land, bounds):
    """Distance to the nearest open water on a coarse grid, used to flatten the ground towards shores."""
    from scipy import ndimage
    tx0, tx1, tz0, tz1 = bounds
    k = LAND_RES // WD_RES
    W = (tx1 - tx0 + 1) * WD_RES
    H = (tz1 - tz0 + 1) * WD_RES
    water = np.zeros((H, W), dtype=bool)
    for (tx, tz), data in land.items():
        arr = np.frombuffer(data, dtype=np.uint8).reshape(LAND_RES, LAND_RES)
        if not (arr == C.L_WATER).any():
            continue
        blk = (arr == C.L_WATER).reshape(WD_RES, k, WD_RES, k).any(axis=(1, 3))
        water[(tz - tz0) * WD_RES:(tz - tz0 + 1) * WD_RES, (tx - tx0) * WD_RES:(tx - tx0 + 1) * WD_RES] = blk
    dist = ndimage.distance_transform_edt(~water).astype(np.float32) * WD_CELL
    # cell-centre values -> corner values (average of the four cells around each corner)
    pad = np.pad(dist, 1, mode='edge')
    corner = (pad[:-1, :-1] + pad[1:, :-1] + pad[:-1, 1:] + pad[1:, 1:]) * 0.25
    q = np.clip(np.round((corner - WD_CELL * 0.5) / WD_UNIT), 0, 255).astype(np.uint8)
    out = {}
    for (tx, tz) in land:
        r0, c0 = (tz - tz0) * WD_RES, (tx - tx0) * WD_RES
        out[(tx, tz)] = q[r0:r0 + WD_RES + 1, c0:c0 + WD_RES + 1].tobytes()
    return out


# =================================================================================================
# points
# =================================================================================================

def build_pois(work):
    p = np.load(os.path.join(work, 'pois.npy'))
    with open(os.path.join(work, 'poi_names.txt'), encoding='utf-8') as f:
        names = f.read().split('\n')
    per_tile = defaultdict(list)
    seen = set()
    for i in range(len(p)):
        x, z = p['x'][i] / 100.0, -p['y'][i] / 100.0
        key = (int(p['kind'][i]), int(x // 25), int(z // 25))
        if key in seen:      # the same place mapped as both a node and a building
            continue
        seen.add(key)
        tx, tz = tile_of(x), tile_of(z)
        name = (names[i] if i < len(names) else '').encode('utf-8')[:60]
        while True:
            try:
                name.decode('utf-8')
                break
            except UnicodeDecodeError:
                name = name[:-1]
        per_tile[(tx, tz)].append(struct.pack('<BHHB', int(p['kind'][i]),
                                              int(round((x - tx * TILE) * 10)), int(round((z - tz * TILE) * 10)),
                                              len(name)) + name)
    log(f'{len(seen)} points of interest')
    return per_tile


def build_places(work):
    p = np.load(os.path.join(work, 'places.npy'))
    with open(os.path.join(work, 'place_names.txt'), encoding='utf-8') as f:
        names = f.read().split('\n')
    out = []
    seen = set()
    for i in range(len(p)):
        rank = int(p['rank'][i])
        if rank > 2:
            continue
        key = (names[i], int(p['x'][i] // 200000), int(p['y'][i] // 200000))
        if key in seen:
            continue
        seen.add(key)
        out.append({'n': names[i], 'r': rank, 'x': round(p['x'][i] / 100.0, 1), 'z': round(-p['y'][i] / 100.0, 1),
                    'p': int(p['pop'][i])})
    out.sort(key=lambda e: (e['r'], -e['p'], e['n']))
    return out


# =================================================================================================
# pack
# =================================================================================================

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('work')
    ap.add_argument('out')
    ap.add_argument('--name', default='mazowsze')
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)

    roads = build_roads(args.work)
    buildings = build_buildings(args.work)
    pois = build_pois(args.work)
    land = build_land(args.work, set(roads) | set(buildings))
    txs = [t[0] for t in land]
    tzs = [t[1] for t in land]
    bounds = (min(txs), max(txs), min(tzs), max(tzs))
    wd = build_water_distance(land, bounds)
    log(f'tile bounds x {bounds[0]}..{bounds[1]}, z {bounds[2]}..{bounds[3]}')

    index = []
    pak_no = 0
    pak = open(os.path.join(args.out, f'{args.name}.{pak_no:02d}.pak'), 'wb')
    pak_size = 0
    total = 0
    for t in sorted(land):
        r = roads.get(t, [])
        b = buildings.get(t, [])
        p = pois.get(t, [])
        blob = bytearray(b'DMT1')
        blob += struct.pack('<I', len(r)) + b''.join(r)
        blob += struct.pack('<I', len(b)) + b''.join(b)
        blob += struct.pack('<H', len(p)) + b''.join(p)
        blob += land[t]
        blob += wd[t]
        comp = zlib.compress(bytes(blob), 9)
        if pak_size + len(comp) > PAK_LIMIT:
            pak.close()
            pak_no += 1
            pak = open(os.path.join(args.out, f'{args.name}.{pak_no:02d}.pak'), 'wb')
            pak_size = 0
        index.append((t[0], t[1], pak_no, pak_size, len(comp), len(blob)))
        pak.write(comp)
        pak_size += len(comp)
        total += len(comp)
    pak.close()

    with open(os.path.join(args.out, f'{args.name}.idx'), 'wb') as f:
        f.write(b'DMIX')
        f.write(struct.pack('<IfIIfddI', 1, TILE, LAND_RES, WD_RES, WD_UNIT, C.ORIGIN_LAT, C.ORIGIN_LON, len(index)))
        for tx, tz, pn, o, n, raw in index:
            f.write(struct.pack('<hhHIII', tx, tz, pn, o, n, raw))
    places = build_places(args.work)
    with open(os.path.join(args.out, f'{args.name}.places.json'), 'w', encoding='utf-8') as f:
        json.dump(places, f, ensure_ascii=False, separators=(',', ':'))
    log(f'wrote {len(index)} tiles, {total / 1e6:.1f} MB in {pak_no + 1} pak file(s), {len(places)} places')


if __name__ == '__main__':
    main()
