"""Builds pack/: everything the page would otherwise fetch from the internet, so the mod works offline from the
first start. Run it with the server running, after it has saved what it needs in data/ (open every map, item and bot once, or
let the fill scripts do it):

    python tools/build_pack.py <installed mod folder> <SPT_Data folder> [out=pack] [--server=https://127.0.0.1:6969]

The pack holds the base game and the mods named in files/pack-mods.txt (one mod folder name a line), nothing else.
Other mods' items are drawn on each player's PC by the game plugin. It copies, at the original quality (PNGs as
lossless WebP, everything else untouched):
  - item pictures of base-game items and of those mods' items, the game's own drawings (by the plugin) over
    tarkov.dev's; each drawing gets a .print file beside it (the server's api/icons/prints), so when a mod changes
    the item, the plugin draws it again
  - map tiles (tarkov.dev, CC BY-NC-SA 4.0), map drawings (SVG) and their layout (maps.json)
  - bot portraits and posters (mod bots' too: shown only when the server has the bot), and the list that names them
  - the quest keys list (tarkov.dev)
Empty tile files (tarkov.dev has no tile there) are kept as empty markers.
"""
import json, os, shutil, ssl, sys, urllib.request
from PIL import Image

args = [a for a in sys.argv[1:] if not a.startswith('--')]
mod, spt_data = args[0], args[1]
out = args[2] if len(args) > 2 else 'pack'
server = next((a.split('=', 1)[1] for a in sys.argv[1:] if a.startswith('--server=')), 'https://127.0.0.1:6969')
data = os.path.join(mod, 'data')
database = os.path.join(spt_data, 'database')
base = set(json.load(open(os.path.join(database, 'templates', 'items.json'), encoding='utf-8')).keys())
# guns are pictured as sold, with their default parts: those pictures are saved under the preset's id
globals_json = os.path.join(database, 'globals.json')
if os.path.exists(globals_json):
    base |= set(json.load(open(globals_json, encoding='utf-8')).get('ItemPresets', {}).keys())


def ask(route):
    # the server's certificate is its own, made on first start
    context = ssl.create_default_context()
    context.check_hostname = False
    context.verify_mode = ssl.CERT_NONE
    with urllib.request.urlopen(server + '/wiki/' + route, context=context, timeout=120) as response:
        return json.load(response)


# the mods shipped with: files/pack-mods.txt; which mod added an item, and its print, from the running server
listed = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'files', 'pack-mods.txt')
shipped_mods = {line.strip() for line in open(listed, encoding='utf-8') if line.strip() and not line.startswith('#')} if os.path.exists(listed) else set()
catalog = ask('api/catalog?lang=en')
prints = ask('api/icons/prints')
modded = {i['id'] for i in catalog['items'] if i.get('mod') in shipped_mods}
print('mods', sorted(shipped_mods), len(modded), 'items')

def webp(src, dst, quality=None, max_side=None, lossy=None):
    """The picture, unchanged in what you see: a PNG becomes a lossless WebP (same pixels, smaller file); a WebP
    or JPEG is copied as it is (re-saving it would lose detail). quality and max_side are no longer used."""
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    if os.path.getsize(src) == 0:
        open(dst, 'wb').close()
        return
    ext = os.path.splitext(src)[1].lower()
    if ext in ('.webp', '.jpg', '.jpeg'):
        shutil.copyfile(src, os.path.splitext(dst)[0] + ext)
        return
    with Image.open(src) as im:
        im = im.convert('RGBA') if im.mode in ('P', 'LA', 'RGBA') or 'transparency' in im.info else im.convert('RGB')
        if lossy:
            # map tiles: quality 95 looks the same and is about half the size
            im.save(dst, 'WEBP', quality=lossy, method=4)
        else:
            im.save(dst, 'WEBP', lossless=True, quality=100, method=6, exact=True)


# --maps-only: keep the item pictures already in out/ (slow to redo, lossless) and redo the rest
maps_only = '--maps-only' in sys.argv
if os.path.isdir(out) and not maps_only:
    shutil.rmtree(out)
elif maps_only and os.path.isdir(os.path.join(out, 'maps')):
    shutil.rmtree(os.path.join(out, 'maps'))

# item pictures: the game's own drawing (the plugin's .png) wins over tarkov.dev's (.webp), so it's copied last and
# gets a .print beside it (what the item looked like when drawn; a changed item is drawn again)
n = 0
names = [] if maps_only else sorted(os.listdir(os.path.join(data, 'icons')), key=lambda f: (f.endswith('.png'), f))
for name in names:
    stem, ext = os.path.splitext(name)
    if (stem in base or stem in modded) and ext in ('.png', '.webp'):
        webp(os.path.join(data, 'icons', name), os.path.join(out, 'icons', stem + '.webp'), 88)
        if ext == '.png' and prints.get(stem):
            with open(os.path.join(out, 'icons', stem + '.print'), 'w', encoding='utf-8') as f:
                f.write(prints[stem])
        n += 1
print('icons', n)

# maps: layout, drawings, tiles
maps = os.path.join(data, 'maps')
n = 0
for root, _, files in os.walk(maps):
    for name in files:
        src = os.path.join(root, name)
        rel = os.path.relpath(src, maps)
        if name.endswith('.png'):
            webp(src, os.path.join(out, 'maps', os.path.splitext(rel)[0] + '.webp'), lossy=95)
            n += 1
        elif name.endswith(('.svg', '.json')):
            os.makedirs(os.path.dirname(os.path.join(out, 'maps', rel)), exist_ok=True)
            shutil.copyfile(src, os.path.join(out, 'maps', rel))
print('map files', n)

# bots: every bot picture saved, mod bots' too (the page shows a bot only when the server has it)
bots = os.path.join(data, 'bots')
os.makedirs(os.path.join(out, 'bots'), exist_ok=True)
for name in os.listdir(bots):
    src = os.path.join(bots, name)
    stem, ext = os.path.splitext(name)
    if ext == '.json':
        shutil.copyfile(src, os.path.join(out, 'bots', name))
    else:
        webp(src, os.path.join(out, 'bots', stem + '.webp'), 82, 1000 if 'poster' in stem else 400)
# hand-made bot pictures (x-*) and chosen.json, which picks them: kept in files/bot-art so the rmtree above cannot lose them
art = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'files', 'bot-art')
if os.path.isdir(art):
    for name in os.listdir(art):
        shutil.copyfile(os.path.join(art, name), os.path.join(out, 'bots', name))

# the quest keys list
if os.path.exists(os.path.join(data, 'tarkovdev-keys.json')):
    shutil.copyfile(os.path.join(data, 'tarkovdev-keys.json'), os.path.join(out, 'tarkovdev-keys.json'))

size = sum(os.path.getsize(os.path.join(r, f)) for r, _, fs in os.walk(out) for f in fs)
print(f'pack: {size / 1e6:.1f} MB')
