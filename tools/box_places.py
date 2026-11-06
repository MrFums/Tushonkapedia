"""Where every container stands, what every key opens and where every quest sends you, read from map scenes,
for files/box-places.json, files/key-places.json and files/zone-places.json.

SPT's loot tables name each container (container_custom_DesignStuff_00063) but leave its position at 0, 0, 0. A map
scene holds the container itself: a MonoBehaviour carrying that name as its id, on a GameObject whose Transform chain
gives the world position. Scenes are read from the game (EscapeFromTarkov_Data/level*) and from every bundle under
BepInEx/plugins, so a client mod's map (a new one, a backported one, or one that adds rooms) is read the same way,
without knowing which mod it is. Nothing is changed in the install.

Which ids to look for: the server's live tables (api/boxids: every map after every mod has loaded, so the CLEAN
server must be running with the map mods in) and SPT's own database files (the stock maps, for a server without
those mods).

Output: map -> {types: container id -> container type, sets: source -> container id -> [x, y, z]}. A source is
"game" or a BepInEx/plugins folder. The server shows, per map, the set that holds the most of its own containers, so
each server gets the map it actually serves; another whole set (the SPT map beside a 1.0 backport) can be picked on
the page.

    pip install UnityPy
    python tools/box_places.py <game folder> [server url] [out]
      server url  default https://127.0.0.1:6969
      out         default files/box-places.json

Run it again after a game update, or after adding or updating a map mod. Takes a few minutes.
"""
import collections
import json
import os
import re
import ssl
import sys
import time
import urllib.request

import UnityPy

STR = re.compile(rb'([\x05-\x7f])\x00\x00\x00([A-Za-z0-9_\-]{5,127})')
# a key's id on a door or lock: 24 hex digits after their length
KEY = re.compile(rb'\x18\x00\x00\x00([0-9a-f]{24})')
# an extract's name: any printable text ("Dorms V-Ex", "Railroad To Port")
TEXT = re.compile(rb'([\x03-\x7f])\x00\x00\x00([ -~]{2,127})')


def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw*bx + ax*bw + ay*bz - az*by, aw*by - ax*bz + ay*bw + az*bx,
            aw*bz + ax*by - ay*bx + az*bw, aw*bw - ax*bx - ay*by - az*bz)


def rot(q, v):
    x, y, z, w = q
    tx, ty, tz = 2*(y*v[2] - z*v[1]), 2*(z*v[0] - x*v[2]), 2*(x*v[1] - y*v[0])
    return (v[0] + w*tx + y*tz - z*ty, v[1] + w*ty + z*tx - x*tz, v[2] + w*tz + x*ty - y*tx)


def world(t):
    """A Transform's world position: its parents' positions, turns and scales applied top down."""
    chain = []
    while t is not None:
        chain.append(t)
        p = t.m_Father
        t = p.read() if p and p.path_id else None
    P, R, S = (0.0, 0.0, 0.0), (0.0, 0.0, 0.0, 1.0), (1.0, 1.0, 1.0)
    for n in reversed(chain):
        lp, lr, ls = n.m_LocalPosition, n.m_LocalRotation, n.m_LocalScale
        v = rot(R, (lp.x*S[0], lp.y*S[1], lp.z*S[2]))
        P = (P[0]+v[0], P[1]+v[1], P[2]+v[2])
        R = qmul(R, (lr.x, lr.y, lr.z, lr.w))
        S = (S[0]*ls.x, S[1]*ls.y, S[2]*ls.z)
    return P


def containers(env, want, keys, zones, exits=frozenset()):
    """('box', id, position, file) for each container in a loaded file or bundle, ('lock', key id, position, file)
    for each door or lock that a key opens, and ('zone', id, position, file) for each place a quest sends you."""
    for o in env.objects:
        if o.type.name != 'MonoBehaviour':
            continue
        raw = o.get_raw_data()
        # a container holds its own id and no other (lists of them, such as bot patrol data, hold many); a door or
        # lock names the one key that opens it
        found = []
        named = {m.group(2).decode() for m in STR.finditer(raw) if m.group(1)[0] == len(m.group(2))}
        boxes = named & want
        if len(boxes) == 1:
            found.append(('box', boxes.pop()))
        # a quest's trigger carries its place's id (and a place can have more than one trigger)
        places = named & zones
        if len(places) == 1:
            found.append(('zone', places.pop()))
        # an extract: the game's extract objects only (their settings can name other extracts too), by the first
        # extract name in them, which is their own
        texts = [m.group(2).decode() for m in TEXT.finditer(raw) if m.group(1)[0] == len(m.group(2))] if exits else []
        if any(t in exits for t in texts):
            try:
                script = o.read(check_read=False).m_Script.read().m_ClassName
            except Exception:
                script = ''
            if 'ExfiltrationPoint' in script:
                first = next((t for t in texts if t in exits), None)
                if first:
                    found.append(('exit', first))
        locks = {m.group(1).decode() for m in KEY.finditer(raw)} & keys
        if len(locks) == 1:
            found.append(('lock', locks.pop()))
        if not found:
            continue
        try:
            go = o.read(check_read=False).m_GameObject.read()
            t = go.m_Transform.read() if getattr(go, 'm_Transform', None) else next(
                p for p in (c.component.deref() for c in go.m_Component) if p.type.name in ('Transform', 'RectTransform')).read()
            at = world(t)
        except Exception:
            continue
        for kind, i in found:
            yield kind, i, at, o.assets_file.name


def main(game, server='https://127.0.0.1:6969', out=os.path.join('files', 'box-places.json')):
    data = os.path.join(game, 'EscapeFromTarkov_Data')
    # map id (lower case) -> its container ids; container id -> type
    ids = collections.defaultdict(set)
    types = {}
    insecure = ssl.create_default_context()
    insecure.check_hostname, insecure.verify_mode = False, ssl.CERT_NONE
    with urllib.request.urlopen(server.rstrip('/') + '/wiki/api/boxids', context=insecure, timeout=60) as r:
        for m, boxes in json.load(r).items():
            ids[m.lower()].update(boxes)
            types.update(boxes)
    stock = next((os.path.join(root, 'locations') for root, dirs, _ in os.walk(os.path.join(game, 'SPT_Runtime', 'SPT_Data'))
                  if 'locations' in dirs and root.endswith('database')), None)
    for folder in os.listdir(stock) if stock else []:
        f = os.path.join(stock, folder, 'staticContainers.json')
        if os.path.exists(f):
            for c in json.load(open(f, encoding='utf-8'))['staticContainers']:
                ids[folder.lower()].add(c['template']['Id'])
                types.setdefault(c['template']['Id'], c['template']['Items'][0]['_tpl'])
    want = set().union(*ids.values())
    with urllib.request.urlopen(server.rstrip('/') + '/wiki/api/keyids', context=insecure, timeout=60) as r:
        keys = set(json.load(r))
    with urllib.request.urlopen(server.rstrip('/') + '/wiki/api/zoneids', context=insecure, timeout=60) as r:
        zones = set(json.load(r))
    # every extract's name, from SPT's own files (all of them, the Scavs' too)
    exits = set()
    for folder in os.listdir(stock) if stock else []:
        f = os.path.join(stock, folder, 'allExtracts.json')
        if os.path.exists(f):
            exits.update(e['Name'] for e in json.load(open(f, encoding='utf-8')) if e.get('Name'))
    print(f'{len(ids)} maps, {len(want)} container ids, {len(keys)} keys, {len(zones)} quest places, {len(exits)} extracts', flush=True)
    # kind (lock, zone or exit) -> source -> id -> [(position, scene)]
    placed = {k: collections.defaultdict(lambda: collections.defaultdict(list)) for k in ('lock', 'zone', 'exit')}

    # source -> id -> [(position, scene)]
    found = collections.defaultdict(lambda: collections.defaultdict(list))
    t0 = time.time()
    gg = UnityPy.load(os.path.join(data, 'globalgamemanagers'))
    scenes = next(o.read_typetree()['scenes'] for o in gg.objects if o.type.name == 'BuildSettings')
    for i, scene in enumerate(scenes):
        parts = scene.split('/')
        if len(parts) >= 5 and parts[2] == 'Locations' and os.path.exists(os.path.join(data, f'level{i}')):
            for kind, cid, at, _ in containers(UnityPy.load(os.path.join(data, f'level{i}')), want, keys, zones, exits):
                # the scene's folder (Assets/Content/Locations/<folder>/...) tells one map's copy from another's
                (found if kind == 'box' else placed[kind])['game'][cid].append((at, parts[3]))
    print(f'game: {len(found["game"])} found ({time.time() - t0:.0f}s)', flush=True)

    plugins = os.path.join(game, 'BepInEx', 'plugins')
    for mod in sorted(os.listdir(plugins)) if os.path.isdir(plugins) else []:
        for root, _, files in os.walk(os.path.join(plugins, mod)):
            for name in files:
                if not name.endswith('.bundle'):
                    continue
                try:
                    env = UnityPy.load(os.path.join(root, name))
                except Exception:
                    continue
                for kind, cid, at, scene in containers(env, want, keys, zones, exits):
                    (found if kind == 'box' else placed[kind])[mod][cid].append((at, scene))
                del env
        if found.get(mod):
            print(f'{mod}: {len(found[mod])} found ({time.time() - t0:.0f}s)', flush=True)

    # a mod's scenes are of the map(s) they hold the most ids of; its few hits on other maps are names those maps
    # share with it (and on a map with the same ids, such as Factory day and night, it counts for both)
    own = {}
    for source, places in found.items():
        if source != 'game':
            counts = {m: sum(1 for i in ids[m] if i in places) for m in ids}
            top = max(counts.values(), default=0)
            own[source] = {m for m, n in counts.items() if n == top and n > 0}

    result = {}
    homes = collections.defaultdict(set)
    for m in sorted(ids):
        sets = {}
        for source, places in found.items():
            hits = [i for i in ids[m] if i in places]
            # the game's scenes: a few stray hits are another map's containers that share a name with this one's
            if (len(hits) < 5) if source == 'game' else (m not in own[source]):
                continue
            # the scene most of this map's ids are in, so the same id in another map's scene is left out
            home = collections.Counter(s for i in hits for _, s in places[i]).most_common(1)[0][0] if source == 'game' else None
            if home:
                homes[home].add(m)
            chosen = {}
            for i in hits:
                at = [p for p, s in places[i] if home is None or s == home]
                if at:
                    chosen[i] = [round(at[0][0], 2), round(at[0][1], 2), round(at[0][2], 2)]
            if chosen:
                sets[source] = chosen
        if sets:
            result[m] = {'types': {i: types[i] for s in sets.values() for i in s if i in types}, 'sets': sets}
            print(f'{m}: ' + ', '.join(f'{s} {len(c)}' for s, c in sets.items()) + f' of {len(ids[m])}', flush=True)

    with open(out, 'w', encoding='utf-8') as f:
        json.dump(result, f, separators=(',', ':'), sort_keys=True)

    # each lock or quest place goes to the map(s) of its scenes: the game's by their folder, a mod's to the maps it's of
    for kind, name in (('lock', 'key-places.json'), ('zone', 'zone-places.json'), ('exit', 'exit-places.json')):
        spots = collections.defaultdict(lambda: collections.defaultdict(dict))
        for source, places in placed[kind].items():
            for pid, at in places.items():
                for p, scene in at:
                    for m in (homes.get(scene, set()) if source == 'game' else own.get(source, set())):
                        spot = [round(p[0], 2), round(p[1], 2), round(p[2], 2)]
                        here = spots[pid][m].setdefault(source, [])
                        if spot not in here:
                            here.append(spot)
        with open(os.path.join(os.path.dirname(out) or '.', name), 'w', encoding='utf-8') as f:
            json.dump(spots, f, separators=(',', ':'), sort_keys=True)
        print(f'{name}: {len(spots)} placed on the maps', flush=True)


if __name__ == '__main__':
    if len(sys.argv) < 2 or len(sys.argv) > 4:
        sys.exit(__doc__)
    main(*sys.argv[1:])
