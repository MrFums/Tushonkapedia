// Tushonkapedia page: the quest guide for this server. Reads the quest data from api/graph (and a profile's progress
// from api/profile), shows each trader's quests as a tree, each quest on its own page, and plans raids from a
// profile's progress. Item pictures come from the server (icon/<id>). Loaded as a classic script; the .mjs name
// is only because SPT won't load a mod folder that holds a .js file.
(() => {
  'use strict';

  // Quest Glow's colours and flag values
  // Quest Glow's colours, plus purple for quests with rewards worth having
  const COLORS = { 1: '#FFC740', 2: '#409EFF', 4: '#FF4538', 8: '#B57CFF' };
  const KAPPA = 1, LIGHTKEEPER = 2, LOCKOUT = 4, REWARD = 8;
  // the server's money, from the graph: item id -> short name, a currency tag ("USD") -> its item, and the base currency
  let ROUBLES = '';
  const currencyId = tag => data?.currencyTags?.[tag] || ROUBLES;
  // the quests that end the Kappa and Lightkeeper lines, and the Lightkeeper: SPT's own ids, sent with the graph
  let COLLECTOR = '', KNOCK_KNOCK = '', LIGHTKEEPER_TRADER = '';
  // the in-game order of the traders; others (mods) come after, by name
  const STATUS_TEXT = { done: 'Completed', ready: 'Hand in', active: 'In progress', available: 'Ready to start', locked: 'Locked', failed: 'Failed', lockedOut: 'Locked out', hidden: 'Not offered' };
  const NEED_TEXT = { key: 'Key', lock: 'Lock nearby', handover: 'Hand over', find: 'Find', place: 'Place', build: 'Build', sell: 'Sell', wear: 'Wear', weapon: 'Use weapon', mods: 'Weapon mods', caliber: 'Caliber', given: 'Given at start' };
  // a row lists this many other prerequisites by name, then counts the rest
  const ALSO_NAMED = 3;
  // a quest with more prerequisites than this lists them grouped by trader
  const GROUP_PREREQS = 8;
  // objectives that send you into a raid somewhere (the rest are handed in at the trader)
  const AT_TRADER = ['HandoverItem', 'SellItemToTrader', 'TraderLoyalty', 'TraderStanding', 'Level', 'Skill', 'WeaponAssembly'];

  const $ = id => document.getElementById(id);
  const esc = text => String(text ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);

  let data = null;
  // every quest and link the server sent; data.quests and data.edges keep only the ones this profile can get
  let allQuests = [];
  let allEdges = [];
  const byId = new Map();
  const outgoing = new Map();
  const requiredBy = new Map();
  const opensCount = new Map();
  let questItems = new Set();
  let traders = [];
  let profiles = [];
  let profile = null;
  const state = new Map();
  let view = { page: 'overview', id: '' };
  let mapFilter = '';
  // how quests under one parent are ordered: '' (level), 'opens' (most follow-ups first), 'few', 'name'
  let questSort = '';
  // which mods' quests to show (empty = all); '*' stands for the vanilla quests a mod changed
  const modFilter = new Set();
  let flagFilter = 0;
  let planMap = '';
  let keepFilter = 0;
  // item pages and "where to get it" answers from the server, kept while the page is open
  const itemCache = new Map();
  const whereCache = new Map();
  const wherePending = new Set();
  let lootReady = true;

  // ---------- settings kept in this browser ----------

  const prefs = Object.assign({ profile: '', kappa: true, lk: true, lockout: true, reward: true, hideDone: false, showLocked: false, hideUnavailable: true, hideHave: false, rewardsAll: false, folded: [], opened: [] }, (() => {
    try { return JSON.parse(localStorage.getItem('tushonkapedia') || localStorage.getItem('questmap') || '{}'); } catch { return {}; }
  })());
  const folded = new Set(prefs.folded);
  const opened = new Set(prefs.opened);

  function savePrefs() {
    prefs.folded = [...folded];
    prefs.opened = [...opened];
    try { localStorage.setItem('tushonkapedia', JSON.stringify(prefs)); localStorage.removeItem('questmap'); } catch { /* private window */ }
  }

  // ---------- data ----------

  async function getJson(url) {
    // the language picked under Options goes with every request (the page's own ?lang= still wins)
    const want = new URLSearchParams(location.search).get('lang') || prefs.lang;
    if (want && !/[?&]lang=/.test(url)) url += (url.includes('?') ? '&' : '?') + 'lang=' + encodeURIComponent(want);
    const response = await fetch(url, { cache: 'no-cache', headers: prefs.profile ? { 'X-Tushonkapedia-Key': lockKey(prefs.profile) } : {} });
    if (!response.ok) throw new Error(`${response.status} ${await response.text()}`);
    return response.json();
  }

  async function loadGraph(rebuild) {
    const params = new URLSearchParams();
    const lang = new URLSearchParams(location.search).get('lang') || prefs.lang;
    if (lang) params.set('lang', lang);
    if (rebuild) params.set('rebuild', '1');
    try {
      data = await getJson('api/graph?' + params);
      ROUBLES = data.baseCurrency;
      COLLECTOR = data.collectorQuest || ''; KNOCK_KNOCK = data.knockKnockQuest || ''; LIGHTKEEPER_TRADER = data.lightkeeperId || '';
      allQuests = data.quests;
      allEdges = data.edges;
    } catch (error) {
      $('page').innerHTML = `<div class="intro red">Couldn't load the quest data from the server: ${esc(error.message)}</div>`;
      return;
    }
    index();
    $('version').textContent = 'v' + data.version;
    footer();
  }

  function itemMod(id, item) {
    return item?.mod || catalogMod(id);
  }

  // the mod that added an item, from the item list once it's in
  let catalogMods = null;
  function catalogMod(id) {
    // the item list, asked for when it isn't in yet: the page draws again once it is
    const catalog = wiki('catalog', 'api/catalog');
    if (!catalogMods && catalog?.items) catalogMods = new Map(catalog.items.filter(i => i.mod).map(i => [i.id, i.mod]));
    return catalogMods?.get(id) || '';
  }

  // the small note by a modded item's name: which mod, when that's known
  const modMark = i => i.modded ? tagBox(itemMod(i.id, i) || 'mod') : '';
  // a small boxed note after a name ("Tracer", the mod that added it), so notes don't run into each other or the name
  const tagBox = text => ` <span class="tagbox">${esc(text)}</span>`;

  async function loadProfiles() {
    try {
      profiles = await getJson('api/profiles');
    } catch {
      profiles = [];
    }
    const select = $('profile');
    select.length = 1;
    profiles.sort((a, b) => a.name.localeCompare(b.name))
      .forEach(p => select.add(new Option(`${p.name}${p.level ? ' · Lv ' + p.level : ''}`, p.id)));
    $('profilePick').hidden = !profiles.length;
    if (!profiles.some(p => p.id === prefs.profile)) prefs.profile = '';
    select.value = prefs.profile;
  }

  async function loadProfile() {
    profile = null;
    if (prefs.profile && !await loggedIn(prefs.profile)) {
      prefs.profile = '';
      $('profile').value = '';
      savePrefs();
    }
    if (prefs.profile) {
      try {
        profile = await getJson('api/profile?id=' + encodeURIComponent(prefs.profile));
      } catch {
        profile = null;
      }
    }
    computeState();
    offered();
    if (profile && editsState(profile.id) == null) setTimeout(() => editsAsk(), 0);
  }

  // the profile again every 30 seconds while the page is in view, so what you own and your quests follow the game
  // (it saves the profile as you play); the page is drawn again only when something changed
  setInterval(async () => {
    if (!prefs.profile || !profile || document.hidden) return;
    try {
      const fresh = await getJson('api/profile?id=' + encodeURIComponent(prefs.profile));
      if (fresh.id !== prefs.profile || JSON.stringify([fresh.owned, fresh.quests, fresh.level, fresh.offered]) === JSON.stringify([profile.owned, profile.quests, profile.level, profile.offered])) return;
      const before = new Map(state);
      profile = fresh;
      computeState();
      offered();
      render();
      // the quests that moved on show up marked for a few seconds
      const moved = [...state].filter(([id, st]) => before.has(id) && before.get(id) !== st).map(([id]) => id);
      for (const id of moved) document.querySelectorAll(`.q[data-q="${id}"]`).forEach(el => el.classList.add('flash'));
      if (moved.length) setTimeout(() => document.querySelectorAll('.q.flash').forEach(el => el.classList.remove('flash')), 6000);
    } catch { /* try again next time */ }
  }, 30000);

  // Quests SPT won't offer leave the lists: event quests outside their event (or with events turned off), and the
  // other side's or another edition's quests. A quest the profile already has stays. Links to them still open.
  function offered() {
    const gone = q => profile ? statusOf(q.id) === 'hidden' : !!q.hidden;
    data.quests = allQuests.filter(q => !gone(q));
    const shown = new Set(data.quests.map(q => q.id));
    data.edges = allEdges.filter(e => shown.has(e.from) && shown.has(e.to));
    index();
  }

  function index() {
    byId.clear();
    outgoing.clear();
    requiredBy.clear();
    opensCount.clear();
    for (const quest of allQuests) {
      byId.set(quest.id, quest);
      outgoing.set(quest.id, []);
      requiredBy.set(quest.id, []);
    }
    for (const edge of data.edges) {
      outgoing.get(edge.from)?.push(edge);
      if (edge.kind === 'requires') requiredBy.get(edge.to)?.push(edge.from);
    }
    // Lightkeeper opens with Knock-Knock
    const lk = data.traders[LIGHTKEEPER_TRADER];
    if (lk && byId.has(KNOCK_KNOCK) && !lk.unlockedBy.includes(KNOCK_KNOCK)) lk.unlockedBy.push(KNOCK_KNOCK);
    traders = Object.keys(data.traders)
      .filter(id => data.quests.some(q => q.trader === id))
      .sort((a, b) => (a > b) - (a < b));
    // items picked up during the quest itself aren't worth keeping from other raids
    questItems = new Set(data.quests.flatMap(q => q.needs.filter(n => n.text.includes('(quest item)')).flatMap(n => n.ids)));
  }

  function traderName(id) {
    return data.traders[id]?.name || id;
  }

  function itemName(id) {
    return data.items[id] || id;
  }

  // how many later quests a quest opens, all the way forward
  function opens(id) {
    if (!opensCount.has(id)) opensCount.set(id, unlocked(id).size - 1);
    return opensCount.get(id);
  }

  // ---------- a profile's progress ----------

  // Has the profile got this trader yet? Lightkeeper's trader data says unlocked, but nobody can reach him before
  // Knock-Knock, so he counts as locked until it's done. Without a profile, a trader counts as locked when a quest
  // unlocks it (Fence starts "locked" in the data too, yet every profile has him).
  function traderOpen(id) {
    if (id === LIGHTKEEPER_TRADER && byId.has(KNOCK_KNOCK)) return !!profile && statusOf(KNOCK_KNOCK) === 'done';
    // a quest taken from them proves they're open, whatever the profile's flag says (the BTR driver's stays off)
    if (profile && data.quests.some(q => q.trader === id && ['active', 'ready', 'done', 'failed'].includes(statusOf(q.id)))) return true;
    const unlocked = profile?.traders?.[id]?.unlocked;
    // locked with nothing in the data that unlocks them: the flag never flips, so it means nothing
    if (unlocked === false && !data.traders[id]?.unlockedBy?.length) return true;
    if (unlocked != null) return unlocked;
    return !(data.traders[id]?.unlockedByDefault === false && data.traders[id]?.unlockedBy?.length);
  }

  function startsLocked(id) {
    return (id === LIGHTKEEPER_TRADER && byId.has(KNOCK_KNOCK))
      || (data.traders[id]?.unlockedByDefault === false && !!data.traders[id]?.unlockedBy?.length);
  }

  // What the profile says, and for quests it hasn't reached yet, whether they could be started now or can no
  // longer be done (a prerequisite failed, or a rival was finished).
  function computeState() {
    state.clear();
    if (!profile || !data) return;
    const raw = profile.quests || {};
    for (const quest of allQuests) {
      const s = raw[quest.id];
      if (s === 'Success') state.set(quest.id, 'done');
      else if (s === 'Fail' || s === 'MarkedAsFailed' || s === 'FailRestartable' || s === 'Expired') state.set(quest.id, 'failed');
      else if (s === 'AvailableForFinish') state.set(quest.id, 'ready');
      else if (s === 'Started') state.set(quest.id, 'active');
    }
    // a few passes, so "locked out" runs down a chain
    for (let pass = 0; pass < 6; pass++) {
      let changed = false;
      for (const quest of allQuests) {
        const known = state.get(quest.id);
        if (known && !['available', 'locked', 'lockedOut', 'hidden'].includes(known)) continue;
        const next = notOffered(quest) ? 'hidden' : blocked(quest) ? 'lockedOut' : missing(quest).length ? 'locked' : 'available';
        if (next !== known) { state.set(quest.id, next); changed = true; }
      }
      if (!changed) break;
    }
  }

  // Why SPT won't offer this quest (to this profile, or to anyone right now): an event quest outside its event,
  // the other side's quest, or another game edition's. Empty when it's offered.
  function notOffered(quest) {
    if (quest.hidden) return 'event';
    if (!profile) return '';
    const side = String(profile.side || '').toLowerCase();
    if (quest.onlySide && side && quest.onlySide !== side) return 'side';
    const edition = profile.gameVersion;
    if (edition && ((quest.editions && !quest.editions.includes(edition)) || quest.notEditions?.includes(edition))) return 'edition';
    return '';
  }

  function notOfferedNote(quest) {
    const why = notOffered(quest);
    if (why === 'event') return `<div class="note hidden">An event quest (${esc(quest.hidden)}). SPT only offers it while that event runs, and it isn't running on this server, so you can't take it now.</div>`;
    if (why === 'side') return `<div class="note hidden">Only ${quest.onlySide === 'bear' ? 'BEAR' : 'USEC'} players get this quest, so ${esc(profile.name)} won't.</div>`;
    if (why === 'edition') return `<div class="note hidden">${esc(profile.name)}'s game edition doesn't get this quest.</div>`;
    if (quest.onlySide) return `<div class="note">Only ${quest.onlySide === 'bear' ? 'BEAR' : 'USEC'} players get this quest.</div>`;
    return '';
  }

  function statusOf(id) {
    return state.get(id) || '';
  }

  // does a quest's state meet a link's wanted statuses?
  function linkMet(status, wanted) {
    if (!wanted.length) return status === 'done' || status === 'failed';
    return wanted.some(w => (w === 'Success' && status === 'done')
      || (w === 'Fail' && status === 'failed')
      || (w === 'Started' && ['active', 'ready', 'done'].includes(status))
      || (w === 'AvailableForFinish' && ['ready', 'done'].includes(status)));
  }

  function linkPossible(status, wanted) {
    if (linkMet(status, wanted)) return true;
    if (status === 'lockedOut' || status === 'hidden') return false;
    if (status === 'failed') return wanted.includes('Fail');
    if (status === 'done') return false;
    return true;
  }

  function blocked(quest) {
    return quest.start.some(c => c.kind === 'Quest' && c.quests.some(t => byId.has(t) && !linkPossible(statusOf(t), c.status)));
  }

  // one true/false per start condition, or nulls without a profile
  function startMet(quest) {
    return quest.start.map(c => {
      if (!profile) return null;
      if (c.kind === 'Quest') return c.quests.every(t => !byId.has(t) || linkMet(statusOf(t), c.status));
      if (c.kind === 'Level') return compare(profile.level || 0, c.compare, c.value);
      if (c.kind === 'TraderLoyalty') return compare(profile.traders?.[c.trader]?.loyalty || 1, c.compare, c.value);
      if (c.kind === 'TraderStanding') return compare(profile.traders?.[c.trader]?.standing || 0, c.compare, c.value);
      return true;
    });
  }

  // what still stands between a profile and a quest, in words: "Reach level 15 (3 to go)", "Finish X"
  function missing(quest) {
    if (!profile) return [];
    const list = [];
    if (!traderOpen(quest.trader)) {
      const by = data.traders[quest.trader]?.unlockedBy || [];
      list.push({ text: `Unlock ${esc(traderName(quest.trader))}${by.length ? ` (finish ${by.map(link).join(' or ')})` : ''}`, quest: by[0] });
    }
    const met = startMet(quest);
    quest.start.forEach((c, i) => {
      if (met[i]) return;
      if (c.kind === 'Level') list.push({ text: `Reach level ${c.value} (${c.value - (profile.level || 0)} to go)`, levels: c.value - (profile.level || 0) });
      else if (c.kind === 'Quest') {
        for (const t of c.quests.filter(t => byId.has(t) && !linkMet(statusOf(t), c.status))) {
          const st = statusOf(t);
          list.push({ text: `${c.status.includes('Success') ? 'Finish' : words(c.status)} ${link(t)}${st === 'active' || st === 'ready' ? ' <span class="sub">(in progress)</span>' : ''}`, quest: t });
        }
      } else list.push({ text: esc(c.text) });
    });
    return list;
  }

  function compare(have, how, want) {
    switch (how) {
      case '>': return have > want;
      case '<': return have < want;
      case '<=': return have <= want;
      case '=': case '==': return have === want;
      default: return have >= want;
    }
  }

  // ---------- chains ----------

  // Everything a quest needs, all the way back (same walk as the server's).
  function chain(root) {
    const seen = new Set();
    const todo = [root];
    while (todo.length) {
      const id = todo.pop();
      const quest = byId.get(id);
      if (!quest || seen.has(id)) continue;
      seen.add(id);
      for (const link of quest.start) {
        if (link.kind !== 'Quest') continue;
        for (const target of link.quests) {
          todo.push(target);
          const linked = link.status.includes('Fail') && byId.get(target);
          if (linked) linked.failsWhen.filter(c => c.kind === 'Quest').forEach(c => todo.push(...c.quests));
        }
      }
    }
    return seen;
  }

  // every quest this one opens, all the way forward
  function unlocked(root) {
    const seen = new Set();
    const todo = [root];
    while (todo.length) {
      const id = todo.pop();
      if (seen.has(id)) continue;
      seen.add(id);
      for (const edge of outgoing.get(id) || []) if (edge.kind === 'requires') todo.push(edge.to);
    }
    return seen;
  }

  // ---------- marks and pictures ----------

  // A coloured edge on the left of a quest: one colour, or the colours stacked when a quest is needed for more
  // than one thing. Written once per mix of flags, leaving out colours turned off under Options.
  function writeMarks() {
    let css = '';
    for (let flags = 1; flags < 16; flags++) {
      const colors = [KAPPA, LIGHTKEEPER, LOCKOUT, REWARD].filter(f => flags & f && shown(f)).map(f => COLORS[f]);
      if (!colors.length) continue;
      const step = 100 / colors.length;
      const bands = colors.map((c, i) => `${c} ${i * step}% ${(i + 1) * step}%`).join(', ');
      // one colour for places with no room for stripes (the quest page): the one that matters most
      const one = [LOCKOUT, KAPPA, LIGHTKEEPER, REWARD].find(f => flags & f && shown(f));
      css += `.m${flags} { --mark: linear-gradient(${bands}); --mark-one: ${COLORS[one]}; }\n`;
    }
    $('marks').textContent = css;
  }

  function shown(flag) {
    return flag === KAPPA ? prefs.kappa : flag === LIGHTKEEPER ? prefs.lk : flag === REWARD ? prefs.reward : prefs.lockout;
  }

  // the server's Kappa / Lightkeeper / lock-out flags, plus "great reward" when it named a reward worth having
  function flagsOf(quest) {
    return quest.flags | (quest.highlights.length ? REWARD : 0);
  }

  function markClass(quest) {
    const flags = [KAPPA, LIGHTKEEPER, LOCKOUT, REWARD].reduce((sum, f) => sum | (flagsOf(quest) & f && shown(f) ? f : 0), 0);
    return flags ? ` mk m${flags}` : '';
  }

  // a quest's great rewards as chips, each with the item's picture when it's about an item
  function rewardChips(quest) {
    return `<div class="why-chips">${quest.highlights.map((h, i) => `<span class="why-chip reward">${quest.highlightItems?.[i] ? icon(quest.highlightItems[i], 'sm') : ''}${linked(h, [quest.highlightItems?.[i]])}</span>`).join('')}</div>`;
  }

  // A recommended quest: its trader, where it sends you and why it's worth taking, each reason in its own colour.
  function recCard(r, i) {
    const q = r.quest;
    const maps = q.maps.filter(m => m !== 'Any');
    const kind = x => x === 'Kappa' ? 'kappa' : x === 'Lightkeeper' ? 'lk' : x.startsWith('opens') ? 'opens'
      : x.startsWith('rewards') || q.highlights.includes(x) ? 'reward' : x.includes('your best map') ? 'map' : x === 'no raid needed' ? 'raid' : '';
    return `<button class="rec${markClass(q)}" data-q="${q.id}"><span class="rec-n">${i + 1}</span>${avatar(q.trader, 'mid')}
      <span class="rec-main"><span class="rec-name">${esc(q.name)}${tags(q)}</span>
        <span class="rec-meta">${esc(traderName(q.trader))} · ${maps.length ? esc(maps.join(', ')) : 'any map'}${q.level ? ` · level ${q.level}` : ''}</span>
        <span class="rec-why">${r.reasons.filter(x => x !== 'Kappa' && x !== 'Lightkeeper').map(x => `<span class="why ${kind(x)}">${esc(x)}</span>`).join('')}</span></span></button>`;
  }

  // How to do a quest (the objectives' details, what you will need) stays hidden until you ask to see it, with one
  // button per quest, so the page doesn't tell you more than you want; "Always show quest hints" turns that off.
  const revealed = new Set();
  function reasonChips(reasons) {
    return `<div class="why-chips">${reasons.map(x => `<span class="why-chip">${esc(x)}</span>`).join('')}</div>`;
  }

  // each mod gets its own colour, the same every time
  function modColor(name) {
    let h = 0;
    for (const c of String(name)) h = (h * 31 + c.charCodeAt(0)) % 360;
    return `hsl(${h} 55% 62%)`;
  }

  function modName(quest) {
    return quest.mod ? quest.mod : 'a mod';
  }

  // A quest's marks in one strip after its name: soft coloured labels for what it counts towards (each can be
  // turned off under Options), then plain grey ones for what's worth knowing (a key, a mod, an event).
  function tags(quest) {
    const mark = (cls, text, title) => `<span class="mark ${cls}"${title ? ` title="${esc(title)}"` : ''}>${text}</span>`;
    const marks = [quest.flags & KAPPA && shown(KAPPA) && mark('kappa', 'Kappa', 'Needed for the Kappa container'),
      quest.flags & LIGHTKEEPER && shown(LIGHTKEEPER) && mark('lk', 'Lightkeeper', 'Needed to reach Lightkeeper'),
      quest.flags & LOCKOUT && shown(LOCKOUT) && mark('lockout', 'Lock-out', 'Taking it locks other quests out'),
      quest.highlights.length && shown(REWARD) && mark('reward', 'Great reward', `Great reward: ${quest.highlights.join(' · ')}`),
      quest.hidden && mark('plain', 'Event', `SPT only offers it during ${quest.hidden}`),
      quest.needs.some(n => n.kind === 'key') && mark('plain', 'Key', 'Needs a key'),
      quest.mod != null && (prefs.compactMods
        ? mark('plain', `<span class="moddot" style="background:${quest.mod ? modColor(quest.mod) : 'var(--faint)'}"></span>`, `Added by ${modName(quest)}`)
        : mark('plain', quest.mod ? `<span class="moddot" style="background:${modColor(quest.mod)}"></span>Added by ${esc(quest.mod)}` : 'Mod', `Added by ${modName(quest)}`)),
      quest.mod == null && quest.changed && mark('plain', prefs.compactMods ? '<span class="moddot hollow"></span>' : 'Changed by a mod', 'A mod on this server changes this quest from the one SPT ships')].filter(Boolean);
    return marks.length ? `<span class="marks">${marks.join('')}</span>` : '';
  }

  // an item's picture; it removes itself when the server has none (mod items, or offline and not saved yet)
  // Clicking it opens the item's page.
  // bumped when pictures change (guns now show their default preset), so browsers drop the ones they kept
  const ICON_V = 2;

  function icon(id, size = '') {
    return id ? `<img class="ico ${size}" data-item="${esc(id)}" src="icon/${esc(id)}?v=${ICON_V}" alt="" title="${esc(itemName(id))}" loading="lazy" onerror="qmNoPic(this)">` : '';
  }

  // An item with no picture yet (a mod item the game plugin hasn't drawn) gets a tile with its short name, like the
  // game shows before an icon loads.
  function shortLabel(name) {
    const word = String(name || '?').replace(/["'“”‘’]/g, '').replace(/\(.*?\)/g, '').trim().split(/\s+/)[0] || '?';
    return word.length > 9 ? word.slice(0, 8) + '…' : word;
  }

  window.qmNoPic = img => {
    const tile = document.createElement('span');
    tile.className = `${img.className} noimg`;
    tile.textContent = img.dataset.short || shortLabel(img.title || itemName(img.dataset.item || ''));
    if (img.dataset.item) tile.dataset.item = img.dataset.item;
    tile.title = img.title;
    img.replaceWith(tile);
  };

  // a card picture: catalog and stock cards, the item page
  function pic(id, short) {
    return `<img src="icon/${esc(id)}?v=${ICON_V}" alt="" title="${esc(itemName(id))}" loading="lazy"${short ? ` data-short="${esc(short)}"` : ''} onerror="qmNoPic(this)">`;
  }

  function itemLink(id, text) {
    return `<a class="il" data-item="${esc(id)}">${esc(text ?? itemName(id))}</a>`;
  }

  // Text with these items' names made into links to their pages, wherever they're mentioned (any case). Longest
  // names first, so "Golden Zibbo lighter" wins over "Zibbo lighter".
  function linked(text, ids) {
    const names = [...new Set(ids || [])].map(id => ({ id, name: itemName(id) })).filter(n => n.name && n.name !== n.id)
      .sort((a, b) => b.name.length - a.name.length);
    let rest = String(text ?? '');
    const parts = [];
    for (const { id, name } of names) {
      const at = rest.toLowerCase().indexOf(name.toLowerCase());
      if (at < 0) continue;
      parts.push({ id, at, length: name.length });
      rest = rest.slice(0, at) + '\u0000'.repeat(name.length) + rest.slice(at + name.length);
    }
    const source = String(text ?? '');
    let html = '';
    let from = 0;
    for (const p of parts.sort((a, b) => a.at - b.at)) {
      html += esc(source.slice(from, p.at)) + itemLink(p.id, source.slice(p.at, p.at + p.length));
      from = p.at + p.length;
    }
    return html + esc(source.slice(from));
  }

  // a key an objective needs: its picture and a link to it
  function keyRow(k) {
    return `<div class="objective-key"><span class="k">Key</span>${k.ids[0] ? `${icon(k.ids[0], 'sm')}${itemLink(k.ids[0], k.text)}` : esc(k.text)}</div>`;
  }

  // A price in a currency, by the short name this server's locale gives it. When that name is a real currency code
  // (RUB, USD, EUR) the browser writes it with its sign; any other (a mod's own coin) keeps its name.
  const moneyFormats = new Map();
  function money(amount, currency) {
    const code = data?.currencies?.[currency] || '';
    if (!moneyFormats.has(code)) {
      let f = null;
      try { if (/^[A-Z]{3}$/.test(code)) f = new Intl.NumberFormat(undefined, { style: 'currency', currency: code, currencyDisplay: 'narrowSymbol', maximumFractionDigits: 0 }); } catch { f = null; }
      // a code the browser only knows by its letters gains nothing from it
      if (f && f.formatToParts(1).find(p => p.type === 'currency')?.value === code) f = null;
      moneyFormats.set(code, f);
    }
    const f = moneyFormats.get(code);
    return f ? f.format(Math.round(amount)) : `${number(Math.round(amount))} ${code}`.trim();
  }

  // ---------- how much a quest is worth doing ----------

  // 0.25 for any quest, more for Kappa or Lightkeeper, for opening a long chain and for rewards worth having.
  // A dead-end quest with poor rewards stays near the bottom; it still counts, just less.
  function worth(quest) {
    return 0.25 + (quest.flags & KAPPA ? 0.6 : 0) + (quest.flags & LIGHTKEEPER ? 0.4 : 0)
      + Math.min(0.8, Math.log2(1 + opens(quest.id)) / 6) + 0.9 * (quest.rewardScore || 0);
  }

  function worthReasons(quest) {
    const reasons = [...quest.highlights];
    if (quest.flags & KAPPA) reasons.push('Kappa');
    if (quest.flags & LIGHTKEEPER) reasons.push('Lightkeeper');
    if (opens(quest.id) >= 3) reasons.push(`opens ${opens(quest.id)} later quests`);
    return reasons;
  }

  function worthWord(quest) {
    const w = worth(quest);
    return w >= 1.6 ? 'Top priority' : w >= 1.1 ? 'High priority' : w >= 0.7 ? 'Worth doing' : 'Low priority';
  }

  // ---------- where to get items ----------

  // Ask the server where these items can be had (best maps, trader offers), then draw the page again.
  function needWhere(ids) {
    const missing = [...new Set(ids)].filter(id => id && !whereCache.has(id) && !wherePending.has(id));
    if (!missing.length) return;
    missing.forEach(id => wherePending.add(id));
    const page = JSON.stringify(view);
    getJson('api/where?ids=' + missing.slice(0, 300).join(',')).then(answer => {
      lootReady = answer.lootReady;
      for (const id of missing) whereCache.set(id, answer.items[id] || { loot: [], buy: [], crafted: false });
      if (JSON.stringify(view) === page) render();
    }).catch(() => missing.forEach(id => wherePending.delete(id)));
  }

  // "Streets of Tarkov (night)" and "Ground Zero (level 21+)" are the same map for quests
  function baseMap(name) {
    return String(name).replace(/ \((night|level 21\+)\)$/, '');
  }

  // a trader offer the profile can use now
  function offerOpen(o) {
    if (!profile) return !o.quest;
    if (!traderOpen(o.trader)) return false;
    if ((profile.traders?.[o.trader]?.loyalty || 1) < o.level) return false;
    if (!o.quest) return true;
    const st = statusOf(o.quest);
    return o.questState === 'started' ? ['active', 'ready', 'done'].includes(st) : o.questState === 'fail' ? st === 'failed' : st === 'done';
  }

  function offerText(o) {
    const what = o.currency ? money(o.price, o.currency) : `barter ${o.barter.map(b => `${b.count} × ${itemLink(b.id)}`).join(' + ')}`;
    return `${esc(traderName(o.trader))} LL${o.level} · ${what}${o.quest ? ` · after ${link(o.quest)}` : ''}`;
  }

  // one line on how to get an item: buy it if you can, else its best loot spot
  function howToGet(id) {
    const where = whereCache.get(id);
    if (!where) return '<span class="sub">…</span>';
    const buy = where.buy.find(offerOpen);
    if (buy) return `${offerText(buy)}`;
    if (where.loot.length) return lootLine(where.loot);
    if (where.buy.length) return `${offerText(where.buy[0])}`;
    if (where.crafted) return 'Crafted in the hideout';
    if (where.carriedBy?.length) return `On ${esc(where.carriedBy.slice(0, 3).join(', '))}`;
    return '<span class="sub">No trader, loot spot or bot has it here</span>';
  }

  // expected copies per raid on a whole map, in words: as if every container and spot on it were searched
  function chance(perRaid) {
    if (perRaid >= 1.5) return `~${Math.round(perRaid)} a raid, map-wide`;
    if (perRaid >= 0.8) return '~1 a raid, map-wide';
    if (perRaid <= 0) return 'not as loot';
    return `1 raid in ${Math.max(2, Math.round(1 / perRaid))}, map-wide`;
  }

  // the chance one container holds it: "1 in 220"
  function oneIn(each) {
    return each >= 0.67 ? 'most' : `1 in ${Math.max(2, Math.round(1 / each))}`;
  }

  // one kind of container on a map: "1 in 220 · 63 on the map"
  function containerOdds(c) {
    if (c.id === 'forced') return 'always there';
    if (!c.each) return chance(c.perRaid);
    return `${oneIn(c.each)}${c.onMap ? ` (${c.onMap >= 2 ? Math.round(c.onMap) : c.onMap} on the map)` : ''}`;
  }

  // container kinds that share a name (three kinds of weapon box) as one
  function byName(containers) {
    const merged = new Map();
    for (const c of containers) {
      const m = merged.get(c.name);
      if (!m) { merged.set(c.name, { ...c, ids: [c.id] }); continue; }
      m.ids.push(c.id);
      m.perRaid += c.perRaid;
      m.onMap = Math.round(((m.onMap || 0) + (c.onMap || 0)) * 10) / 10;
      m.each = m.onMap ? Math.min(1, m.perRaid / m.onMap) : m.each;
    }
    return [...merged.values()];
  }

  // Where to look, led by the kind of container: a container kind holds the same things on every map, so the map
  // that "wins" is just the one with the most of them (Streets). "Technical supply crate (1 in 9 has one)", then
  // the maps with the most of them. Loose-only items, and items always in one place, keep the map first.
  function lootLine(loot) {
    const forced = loot.find(s => s.containers.some(c => c.id === 'forced'));
    if (forced) return `${esc(forced.map)} · always there`;
    const kinds = new Map();
    for (const s of loot) {
      for (const c of byName(s.containers)) {
        if (!c.each) continue;
        const k = kinds.get(c.name) || { name: c.name, ids: new Set(), each: 0, onMap: 0, maps: [] };
        c.ids.forEach(i => k.ids.add(i));
        k.each = Math.max(k.each, c.each);
        k.onMap += c.onMap || 0;
        k.maps.push({ map: s.map, onMap: c.onMap || 0, each: c.each });
        kinds.set(c.name, k);
      }
    }
    // among the containers with near the best odds, the one there are most of (a crate found on every map beats
    // a slightly likelier one only Lighthouse has five of)
    const all = [...kinds.values()];
    const top = Math.max(0, ...all.map(k => k.each));
    const box = all.filter(k => k.each >= top * 0.6).sort((a, b) => b.onMap - a.onMap || b.each - a.each)[0];
    if (!box) return `${esc(loot[0].map)} · ${bestOdds(loot[0])}`;
    // the maps where this container gives about those odds, most containers first; one name per map
    // ("Ground Zero (level 21+)" is the same ground as Ground Zero)
    const seen = new Set();
    const maps = box.maps.filter(m => m.each >= box.each * 0.7).sort((a, b) => b.onMap - a.onMap)
      .filter(m => { const base = m.map.replace(/\s*\(.*\)$/, ''); return !seen.has(base) && seen.add(base); })
      .slice(0, 4).map(m => `${esc(m.map.replace(/\s*\(.*\)$/, ''))}${m.onMap >= 1 ? ` (${Math.round(m.onMap)})` : ''}`);
    return `${boxLink([...box.ids], box.name.replace(/^./, ch => ch.toUpperCase()))} <span class="sub">(${oneIn(box.each)} has one)</span>`
      + ` <span class="sub">most on ${maps.join(', ')}</span>`;
  }

  // a kind of container, opening its page: the maps it stands on and what's in it
  function boxLink(ids, name) {
    return ids.includes('forced') ? esc(name) : `<a class="il" data-box="${esc(ids.join(','))}">${esc(name)}</a>`;
  }

  // the container on a map most likely to hold it: "dead scav (1 in 220 has one)"
  function bestOdds(spot) {
    const c = byName(spot.containers).filter(x => x.each || x.id === 'forced').sort((a, b) => (b.id === 'forced') - (a.id === 'forced') || b.each - a.each)[0];
    if (!c) return spot.points ? `loose <span class="sub">(${chance(spot.loose || spot.total)})</span>` : `<span class="sub">${chance(spot.total)}</span>`;
    if (c.id === 'forced') return 'always there';
    return `${esc(c.name.toLowerCase())} <span class="sub">(${c.each >= 0.67 ? 'most have one' : `${oneIn(c.each)} has one`})</span>`;
  }

  function rarityWord(perRaid) {
    return perRaid >= 1 ? 'Common' : perRaid >= 0.25 ? 'Uncommon' : perRaid >= 0.05 ? 'Rare' : perRaid > 0 ? 'Very rare' : 'No map spawns';
  }

  // What a profile still has to find: items its quests in progress (and those it could pick up) want handed over
  // found in raid, and the keys their objectives need. Weighted by how much the quest is worth.
  function neededItems() {
    const needs = new Map();
    const add = (id, quest, count, key, weight) => {
      if (!id || questItems.has(id)) return;
      const n = needs.get(id) || { id, count: 0, quests: new Set(), key: false, weight: 0 };
      n.count += count;
      n.quests.add(quest.id);
      n.key ||= key;
      n.weight = Math.max(n.weight, weight);
      needs.set(id, n);
    };
    for (const quest of data.quests) {
      const st = statusOf(quest.id);
      if (st !== 'active' && st !== 'available') continue;
      const scale = st === 'active' ? 1 : 0.5;
      for (const o of quest.objectives) {
        if (o.kind === 'HandoverItem' && o.fir && o.items.length && firstOfGroup(quest, o)) add(o.items[0], quest, o.count ?? 1, false, worth(quest) * scale);
      }
      for (const k of quest.needs.filter(n => n.kind === 'key')) add(k.ids[0], quest, 1, true, worth(quest) * scale);
    }
    return needs;
  }

  // ---------- rows and trees ----------

  // an objective that's one of "any one of these": only the first of its group is counted when adding up what to bring
  const firstOfGroup = (quest, o) => !o.any || quest.objectives.find(x => x.any === o.any) === o;

  // quest rows show the quest's own picture while this is on (a trader's quest list)
  let rowPic = false;

  function row(quest, extra = '', fold = '', below = '') {
    const st = statusOf(quest.id);
    const maps = quest.maps.filter(m => m !== 'Any');
    const follow = opens(quest.id);
    const meta = [quest.level ? `Level ${quest.level}` : '', maps.length ? maps.join(', ') : 'Any map', follow ? `${follow} follow-up${follow === 1 ? '' : 's'}` : '', extra].filter(Boolean).join(' · ');
    return `<div class="q${markClass(quest)}${st ? ' st-' + st : ''}" data-q="${quest.id}">
      ${fold}
      ${rowPic && quest.image ? `<img class="q-pic" src="${esc(quest.image)}" alt="" loading="lazy" onerror="this.remove()">` : ''}
      <div class="main"><div class="name">${esc(quest.name)}${tags(quest)}</div><div class="meta">${meta}</div>${below}</div>
      ${st ? `<span class="status ${st}">${STATUS_TEXT[st]}</span>` : ''}
    </div>`;
  }

  // a short row for lists on a quest's page
  function mini(id, why = '') {
    const quest = byId.get(id);
    if (!quest) return `<div class="mini"><span class="t sub">${esc(id)} (not on this server)</span></div>`;
    const st = statusOf(id);
    return `<div class="mini${markClass(quest)}" data-q="${id}"><span class="t">${esc(quest.name)}
      <span class="w">${esc(traderName(quest.trader))}${why ? ' · ' + why : ''}</span></span>
      ${st ? `<span class="status ${st}">${STATUS_TEXT[st]}</span>` : ''}</div>`;
  }

  const isLocked = id => ['locked', 'lockedOut'].includes(statusOf(id));
  // can't be taken now, so it goes to the bottom of a list
  const shutOut = id => profile && ['locked', 'lockedOut', 'hidden'].includes(statusOf(id)) ? 1 : 0;

  // A quest hangs under the prerequisite from the same list that comes latest (its longest chain). Other
  // prerequisites are named on the row (the first few, then a count), so nothing is drawn as a web of crossing
  // lines. A run of quests that each open just one more stays in one column; the tree only steps in where a
  // line branches. The button on a row folds away everything after it. With a profile, each line is folded at
  // its first locked quest until you open it.
  function tree(ids) {
    const set = new Set(ids);
    const parent = new Map();
    for (const id of ids) {
      const quest = byId.get(id);
      const best = (requiredBy.get(id) || [])
        .filter(p => set.has(p) && byId.get(p).depth < quest.depth)
        .sort((a, b) => byId.get(b).depth - byId.get(a).depth || byId.get(a).name.localeCompare(byId.get(b).name))[0];
      parent.set(id, best || '');
    }
    const kids = new Map();
    for (const id of ids) {
      const p = parent.get(id);
      if (!kids.has(p)) kids.set(p, []);
      kids.get(p).push(id);
    }
    const sort = list => list.sort((a, b) => {
      const qa = byId.get(a), qb = byId.get(b);
      const by = questSort === 'opens' ? opens(b) - opens(a) : questSort === 'few' ? opens(a) - opens(b) : questSort === 'name' ? qa.name.localeCompare(qb.name) : 0;
      return shutOut(a) - shutOut(b) || by || (qa.level ?? 0) - (qb.level ?? 0) || qa.depth - qb.depth || qa.name.localeCompare(qb.name);
    });
    const below = id => (kids.get(id) || []).reduce((n, k) => n + 1 + below(k), 0);
    const autoFold = id => !!profile && !prefs.showLocked && isLocked(id) && !(parent.get(id) && isLocked(parent.get(id)));
    const isFolded = id => folded.has(id) || (autoFold(id) && !opened.has(id));

    const line = id => {
      const quest = byId.get(id);
      const others = (requiredBy.get(id) || []).filter(p => p !== parent.get(id));
      const named = others.slice(0, ALSO_NAMED).map(p => {
        const pq = byId.get(p);
        return esc(pq.name) + (pq.trader !== quest.trader ? ` (${esc(traderName(pq.trader))})` : '');
      });
      const more = others.length > ALSO_NAMED ? ` and ${others.length - ALSO_NAMED} more` : '';
      const extra = others.length ? `<span class="also">also needs ${named.join(', ')}${more}</span>` : '';
      const hidden = below(id);
      const shut = isFolded(id);
      const fold = hidden
        ? `<button class="fold${shut ? ' shut' : ''}" data-fold="${id}" data-shut="${shut ? 1 : 0}" title="${shut ? 'Show' : 'Hide'} the ${hidden} quests after this one">${shut ? `+ ${hidden}` : '−'}</button>`
        : '<span class="fold-spacer"></span>';
      return row(quest, extra, fold);
    };
    const branch = id => {
      let html = '';
      for (let at = id; ;) {
        html += line(at);
        if (isFolded(at)) return html;
        const children = sort(kids.get(at) || []);
        if (children.length === 1) {
          html += '<div class="down"></div>';
          at = children[0];
          continue;
        }
        if (children.length > 1) html += `<ul>${children.map(c => `<li>${branch(c)}</li>`).join('')}</ul>`;
        return html;
      }
    };
    const roots = sort(kids.get('') || []);
    return { roots: roots.filter(r => below(r)), html: `<ul class="tree">${roots.map(id => `<li>${branch(id)}</li>`).join('')}</ul>` };
  }

  // unavailable: a trader's page leaves out, unless asked, the quests you can't take now (locked, locked out, or
  // not offered to you)
  function filtered(list, flags = true, unavailable = false) {
    return list.filter(q => (!mapFilter || q.maps.includes(mapFilter))
      && (!flags || !flagFilter || q.flags & flagFilter)
      && (!modFilter.size || modFilter.has(q.mod) || (modFilter.has('*') && q.changed))
      && !(prefs.hideDone && profile && statusOf(q.id) === 'done')
      && !((unavailable ? prefs.hideUnavailable : prefs.hideLockedLists) && shutOut(q.id)));
  }

  // A quest list's controls on one tidy line: the map as a drop-down (real maps only, not a mod's internal names),
  // the goal filters, the show/hide switches, and expand/collapse as plain links at the end.
  function toolbar(list, roots, flags = true, unavailable = false) {
    const known = wiki('maps', 'api/maps');
    const real = Array.isArray(known) ? new Set(known.map(m => m.name)) : null;
    const maps = [...new Set(list.flatMap(q => q.maps))].filter(m => m === 'Any' || !real || real.has(m))
      .sort((a, b) => (a === 'Any') - (b === 'Any') || a.localeCompare(b));
    const chip = (attr, value, label, on) => `<button class="chip${on ? ' on' : ''}" ${attr}="${esc(value)}">${label}</button>`;
    const locked = list.filter(q => ['locked', 'lockedOut', 'hidden'].includes(statusOf(q.id))).length;
    return `<div class="toolbar qbar">
      <label class="qbar-map">Map <select data-mapselect><option value="">All maps</option>${maps.map(m => `<option value="${esc(m)}"${mapFilter === m ? ' selected' : ''}>${m === 'Any' ? 'Any map' : esc(m)}</option>`).join('')}</select></label>
      <label class="qbar-map">Order <select data-qsort>${[['', 'By level'], ['opens', 'Most follow-ups first'], ['few', 'Fewest follow-ups first'], ['name', 'By name']].map(([v, t]) => `<option value="${v}"${questSort === v ? ' selected' : ''}>${t}</option>`).join('')}</select></label>
      ${flags ? `<span class="qbar-group">${chip('data-flag', KAPPA, '<span class="sw kappa"></span>Kappa', flagFilter === KAPPA)}${chip('data-flag', LIGHTKEEPER, '<span class="sw lk"></span>Lightkeeper', flagFilter === LIGHTKEEPER)}${chip('data-flag', LOCKOUT, '<span class="sw lockout"></span>Lock-out', flagFilter === LOCKOUT)}</span>` : ''}
      ${profile ? `<span class="qbar-group">${chip('data-hide', '1', 'Hide completed', prefs.hideDone)}${chip(unavailable ? 'data-hideunavailable' : 'data-hidelocked', '1', `Hide locked${locked ? ` (${locked})` : ''}`, unavailable ? prefs.hideUnavailable : prefs.hideLockedLists)}</span>` : ''}
      ${(() => {
        const mods = [...new Set(list.map(q => q.mod).filter(Boolean))].sort((a, b) => a.localeCompare(b));
        const changed = list.some(q => q.mod == null && q.changed);
        if (!mods.length && !changed) return '';
        return `<span class="qbar-group qbar-mods"><span class="sub">Source mod</span>${mods.map(m => chip('data-modf', m, `<span class="moddot" style="background:${modColor(m)}"></span>${esc(m)}`, modFilter.has(m))).join('')}${changed ? chip('data-modf', '*', '<span class="moddot hollow"></span>Changed by a mod', modFilter.has('*')) : ''}${chip('data-compactmods', '1', 'Compact tags', prefs.compactMods)}</span>`;
      })()}
      <span class="qbar-fold"><a data-foldall="0">Expand all</a> · <a data-foldall="${esc(roots.join(','))}">Collapse all</a></span>
    </div>`;
  }

  function avatar(id, size = '') {
    const trader = data.traders[id];
    const initials = esc((trader?.name || '?').slice(0, 2));
    return trader?.avatar
      ? `<img class="avatar ${size}" src="${esc(trader.avatar)}" alt="" loading="lazy" onerror="this.outerHTML='<span class=&quot;avatar ${size}&quot;>${initials}</span>'">`
      : `<span class="avatar ${size}">${initials}</span>`;
  }

  function progress(list) {
    const done = list.filter(q => statusOf(q.id) === 'done').length;
    return { done, total: list.length, pct: list.length ? Math.round(done * 100 / list.length) : 0 };
  }

  // "Unlocked by finishing X" for a trader that starts locked
  function traderLockNote(id) {
    if (traderOpen(id)) return '';
    const by = data.traders[id]?.unlockedBy || [];
    return `${profile ? 'Not unlocked yet' : 'Starts locked'}${by.length ? ` · unlocked by finishing ${by.map(link).join(' or ')}` : ''}`;
  }

  // ---------- raid planning ----------

  // The maps an objective sends you to: its own, or the quest's when it names just one.
  function objectiveMaps(quest, objective) {
    if (AT_TRADER.includes(objective.kind)) return [];
    if (objective.maps.length) return objective.maps;
    const maps = quest.maps.filter(m => m !== 'Any');
    return maps.length === 1 ? maps : [];
  }

  // Every map with something to do for a profile, best first. Each objective of a quest in progress counts as
  // much as the quest is worth (worth()), quests you could pick up count half, and items your quests want found
  // in raid (and keys you need) add a little where they spawn best.
  // ---------- shopping list ----------
  // Items the player wants to find in raid, per character, kept in this browser: { item id: how many }.
  let shopFind = '';
  const shopList = () => (profile && prefs.shop?.[profile.id]) || {};
  function shopSet(id, count) {
    if (!profile) return;
    prefs.shop = prefs.shop || {};
    const list = { ...shopList() };
    if (count > 0) list[id] = count; else delete list[id];
    prefs.shop[profile.id] = list;
    savePrefs();
    render();
  }

  function shopCard() {
    const list = Object.entries(shopList());
    const text = shopFind.trim().toLowerCase();
    const cat = text.length >= 2 ? wiki('catalog', 'api/catalog') : null;
    const hits = cat?.items ? cat.items.filter(i => !i.fixedPart && !i.unused && !shopList()[i.id] && i.name.toLowerCase().includes(text)).slice(0, 8) : [];
    const wish = wishState()?.list?.items || [];
    return card(`Shopping list${list.length ? ` <span class="n">${list.length}</span>` : ''}`, `
      ${list.length ? list.map(([id, count]) => `<div class="shop-row">${icon(id)}<span class="t">${itemLink(id)}</span>
        <span class="shop-n"><button class="btn" data-shopn="${esc(id)}|-1" title="One less">-</button><b>${number(count)}</b><button class="btn" data-shopn="${esc(id)}|1" title="One more">+</button></span>
        <button class="btn" data-shopn="${esc(id)}|0">Remove</button></div>`).join('')
        : '<p class="sub">Add what you want to find in raid. Maps are then ranked with it too, and the route goes past where it turns up most.</p>'}
      <div class="toolbar"><input class="filter" id="shopFind" placeholder="Add an item" value="${esc(shopFind)}" autocomplete="off"></div>
      ${text.length >= 2 && !cat ? '<p class="sub">Looking…</p>' : ''}
      ${hits.length ? `<div class="shop-hits">${hits.map(i => `<button class="shop-hit" data-shopadd="${esc(i.id)}">${icon(i.id)}<span>${esc(i.name)}</span></button>`).join('')}</div>` : ''}
      ${wish.some(w => !shopList()[w.id]) ? '<button class="btn" data-shopwish>Add everything on my wishlist</button>' : ''}`);
  }

  function mapPlan() {
    const plans = new Map();
    const plan = map => {
      map = baseMap(map);
      if (!plans.has(map)) plans.set(map, { map, score: 0, objectives: [], pickup: [], finds: [] });
      return plans.get(map);
    };
    const needs = neededItems();
    for (const [id, count] of Object.entries(shopList())) {
      const n = needs.get(id) || { id, count: 0, quests: new Set(), key: false, weight: 0 };
      n.count = Math.max(n.count, count);
      n.shop = count;
      n.weight = Math.max(n.weight, 1);
      needs.set(id, n);
    }
    needWhere([...needs.keys()]);
    for (const need of needs.values()) {
      const where = whereCache.get(need.id);
      const best = where?.loot[0];
      if (!best) continue;
      for (const spot of where.loot) {
        // only maps where it's reasonably likely, and the best one more so
        if (spot.total < 0.03 || spot.total < best.total / 3) continue;
        const p = plan(spot.map);
        if (p.finds.some(f => f.need.id === need.id)) continue;
        p.finds.push({ need, spot });
        // what you asked for counts in full; what quests want found counts a little
        p.score += (need.shop ? 1 : 0.3) * need.weight * Math.min(1, spot.total) * (need.key ? 1.5 : 1);
      }
    }
    for (const quest of data.quests) {
      const st = statusOf(quest.id);
      if (st === 'active') {
        for (const o of quest.objectives) {
          for (const map of objectiveMaps(quest, o)) {
            const p = plan(map);
            p.objectives.push({ quest, objective: o });
            p.score += worth(quest);
          }
        }
      } else if (st === 'available') {
        for (const map of new Set(quest.objectives.flatMap(o => objectiveMaps(quest, o)))) {
          const p = plan(map);
          p.pickup.push(quest);
          p.score += 0.5 * worth(quest);
        }
      }
    }
    for (const p of plans.values()) {
      p.objectives.sort((a, b) => worth(b.quest) - worth(a.quest) || a.quest.name.localeCompare(b.quest.name));
      p.finds.sort((a, b) => !!b.need.shop - !!a.need.shop || b.need.key - a.need.key || b.need.weight * b.spot.total - a.need.weight * a.spot.total);
    }
    return [...plans.values()].filter(p => p.objectives.length || p.pickup.length || p.finds.some(f => f.need.shop)).sort((a, b) => b.score - a.score || a.map.localeCompare(b.map));
  }

  // Quests to pick up next, by how much they're worth doing (worth()), a little more when they're on the map
  // you'd go to anyway or need no raid at all.
  // Quests worth starting now: those the game would list for this character to start, as SPT works them out for its
  // quest screen (without that answer, the ones whose start conditions are met).
  function recommended(bestMap) {
    const offered = profile?.offered?.length ? new Set(profile.offered) : null;
    return data.quests.filter(q => statusOf(q.id) === 'available' && (!offered || offered.has(q.id))).map(q => {
      const reasons = worthReasons(q);
      let score = worth(q);
      if (bestMap && q.objectives.some(o => objectiveMaps(q, o).includes(bestMap))) { score += 0.3; reasons.push(`on ${bestMap}, your best map`); }
      if (q.objectives.every(o => !objectiveMaps(q, o).length)) { score += 0.15; reasons.push('no raid needed'); }
      // every pick says why it's here: its rewards, when nothing above did
      if (!q.highlights.length && q.rewardValue > 0 && (q.rewardScore || 0) >= 0.3) reasons.push(`rewards worth ${money(q.rewardValue, ROUBLES)}`);
      if (!reasons.length) reasons.push('you can start it now');
      return { quest: q, score, reasons };
    }).sort((a, b) => b.score - a.score || a.quest.name.localeCompare(b.quest.name));
  }

  // available or started quests whose rewards are worth having, best first
  function greatRewards() {
    return data.quests.filter(q => q.highlights.length && ['available', 'active', 'ready'].includes(statusOf(q.id)))
      .sort((a, b) => b.rewardScore - a.rewardScore || a.name.localeCompare(b.name));
  }

  // Locked quests that are nearest to opening: one thing missing, and that thing is close (a quest you're on,
  // or a few levels).
  function unlocksSoon() {
    return data.quests.filter(q => statusOf(q.id) === 'locked').map(q => ({ quest: q, missing: missing(q) }))
      .filter(x => x.missing.length && x.missing.length <= 2)
      .map(x => {
        const closeness = x.missing.reduce((sum, m) => sum + (m.levels ?? (m.quest && ['active', 'ready'].includes(statusOf(m.quest)) ? 1 : m.quest && statusOf(m.quest) === 'available' ? 2 : 4)), 0);
        return { ...x, closeness };
      })
      .sort((a, b) => a.closeness - b.closeness || b.quest.flags - a.quest.flags || a.quest.name.localeCompare(b.quest.name));
  }

  // ---------- pages ----------

  function render() {
    if (!data) return;
    $('passwordLink').hidden = !profile;
    document.body.classList.toggle('edits-off', !editsOn());
    $('editsLink').hidden = !profile;
    $('editsLink').textContent = editsOn() ? 'Turn off character changes' : 'Turn on character changes…';
    $('logoutLink').hidden = !profile || !profileLocked;
    renderNav();
    const page = $('page');
    if (view.page === 'quest' && byId.has(view.id)) page.innerHTML = questPage(byId.get(view.id));
    else if (view.page === 'trader' && view.id) page.innerHTML = traderPage(view.id);
    // a trader's quests from the Quests menu: the same list as the trader page's first tab, kept under Quests
    else if (view.page === 'quests' && view.id && data.traders[view.id]) page.innerHTML = `${crumbs(['quests', 'Quests'], [null, traderName(view.id)])}${traderQuests(view.id, `<p class="sub"><a data-page="trader" data-id="${esc(view.id)}">${esc(traderName(view.id))}'s stock and loyalty levels</a></p>`)}`;
    else if (view.page === 'quests') page.innerHTML = questsPage();
    else if (view.page === 'traders') page.innerHTML = tradersPage();
    else if (view.page === 'maps') page.innerHTML = mapsPage();
    else if (view.page === 'map') page.innerHTML = mapPage(view.id);
    else if (view.page === 'box') page.innerHTML = boxPage(view.id);
    else if (view.page === 'hideout') page.innerHTML = hideoutPage(view.id);
    else if (view.page === 'crafts') page.innerHTML = craftsPage();
    else if (view.page === 'bots') page.innerHTML = botsPage();
    else if (view.page === 'rules') page.innerHTML = rulesPage(view.id);
    else if (view.page === 'bot') page.innerHTML = botPage(view.id);
    else if (view.page === 'keep') page.innerHTML = itemsPage();
    else if (view.page === 'kappa') page.innerHTML = goalPage(KAPPA);
    else if (view.page === 'lightkeeper') page.innerHTML = goalPage(LIGHTKEEPER);
    else if (view.page === 'lockouts') page.innerHTML = lockoutPage();
    else if (view.page === 'plan') page.innerHTML = planPage();
    else if (view.page === 'items') page.innerHTML = catalogPage(view.id);
    else if (view.page === 'item') page.innerHTML = itemPage(view.id);
    else if (view.page === 'rewards') page.innerHTML = rewardsPage();
    else if (view.page === 'goal') page.innerHTML = goalsPage(view.id);
    else if (view.page === 'player') page.innerHTML = playerPage();
    else if (view.page === 'wishlist') page.innerHTML = wishlistPage();
    else if (view.page === 'stash') page.innerHTML = stashPage();
    else if (view.page === 'kinds') page.innerHTML = kindsPage();
    else page.innerHTML = overviewPage();
    mountLootMaps();
  }

  // What the section menus list that comes from the server (item categories, maps, bots, hideout areas): asked for
  // the first time the pointer rests on that menu, and kept
  const MENU_DATA = { items: ['catalog', 'api/catalog'], maps: ['maps', 'api/maps'], bots: ['bots', 'api/bots'], hideout: ['hideout', 'api/hideout'] };
  let menuCats = null;

  function renderNav() {
    // the section the open page belongs to
    const section = { quest: 'quests', kappa: 'quests', lightkeeper: 'quests', lockouts: 'quests', rewards: 'quests', trader: 'traders',
      item: 'items', map: 'maps', box: 'maps', bot: 'bots' }[view.page] || view.page;
    document.body.dataset.section = section;
    const link = (page, text) => `<a data-page="${page}" data-id=""${section === page ? ' class="on"' : ''}>${text}</a>`;
    const a = (page, id, text) => `<a data-page="${page}" data-id="${esc(String(id))}">${text}</a>`;
    const rule = id => { const r = RULES.find(x => x[0] === id); return r ? a('rules', id, r[1]) : ''; };
    const ready = key => { const v = wikiCache.get(key); return v && !v.error ? v : null; };
    const waiting = key => [wikiCache.get(key)?.error ? '<span class="sub">Couldn\'t load</span>' : '<span class="sub">Loading…</span>'];
    const col = (title, links, wide = false) => links.length ? `<div class="dd-col${wide ? ' wide' : ''}"><h4>${title}</h4><div class="dd-links">${links.join('')}</div></div>` : '';
    // a section's link, and under it a panel of its pages that opens on hover
    const menu = (page, text, cols, load = '') => `<div class="sec"${load ? ` data-menu="${load}"` : ''}>${link(page, text)}${cols ? `<div class="dd">${cols}</div>` : ''}</div>`;

    const catalog = ready('catalog');
    if (catalog && !menuCats) {
      const used = new Set(catalog.items.filter(i => !i.fixedPart && !i.unused).map(i => i.category));
      const parent = new Map(catalog.categories.map(c => [c.id, c.parent || '']));
      // a top category counts when an item sits anywhere under it
      const top = id => { while (parent.get(id)) id = parent.get(id); return id; };
      const tops = new Set([...used].map(top));
      menuCats = catalog.categories.filter(c => !c.parent && tops.has(c.id)).sort((x, y) => x.name.localeCompare(y.name));
    }
    const maps = ready('maps');
    const bots = ready('bots');
    const hideout = ready('hideout');
    const bosses = bots ? bots.filter(b => b.kind === 'boss').sort((x, y) => botTitle(x.name).localeCompare(botTitle(y.name))) : null;

    $('nav').innerHTML = `<div class="nav-in"><div class="nav-main">${menu('overview', 'Main page', '')}
      ${menu('quests', 'Quests', col('Lists', [a('quests', '', 'All quests'), a('kappa', '', 'Kappa'), a('lightkeeper', '', 'Lightkeeper'), a('lockouts', '', 'Lock-out choices'),
        a('rewards', '', 'Great rewards'), a('keep', '', 'Items to keep')]) + col('By trader', traders.map(id => a('quests', id, esc(traderName(id)))), true))}
      ${menu('items', 'Items', col('Categories', menuCats ? menuCats.map(c => a('items', c.id, esc(c.name))) : waiting('catalog'), true)
        + col('Charts', [rule('ammo'), rule('armor'), rule('containers')]), 'items')}
      ${menu('traders', 'Traders', col('Traders', traders.map(id => a('trader', id, esc(traderName(id)))), true)
        + col('Trading', [rule('flea'), rule('insurance'), rule('repairs'), rule('karma')]))}
      ${menu('maps', 'Maps', col('Maps', maps ? oneFactory([...maps]).sort((x, y) => x.name.localeCompare(y.name)).map(m => a('map', m.id, esc(m.name))) : waiting('maps'), true)
        + col('Raids', [a('plan', '', 'Plan a raid'), rule('events')]), 'maps')}
      ${menu('bots', 'Bots', col('Kinds', bots ? botGroups(bots).map(([k, t]) => a('bots', k, esc(t))) : waiting('bots'))
        + col('Bosses', bosses ? bosses.map(b => a('bot', b.id, esc(botTitle(b.name)))) : waiting('bots'), true), 'bots')}
      ${menu('hideout', 'Hideout', col('Areas', hideout ? hideout.areas.map(x => a('hideout', x.type, esc(x.name))) : waiting('hideout'), true)
        + col('Production', [a('crafts', '', 'All crafts'), rule('circle')]), 'hideout')}
      ${menu('crafts', 'Crafts', col('By area', hideout ? hideout.areas.filter(x => x.crafts.length).map(x => a('crafts', x.type, `${esc(x.name)} <span class="sub">${x.crafts.length}</span>`)) : waiting('hideout'), true)
        + col('Also', [a('crafts', '', 'Every craft'), rule('circle'), a('goal', '', 'I want to…')]), 'hideout')}
      ${menu('rules', 'Mechanics', col('Trading', [rule('flea'), rule('insurance'), rule('repairs')]) + col('Gear', [rule('ammo'), rule('armor'), rule('containers')])
        + col('Character', [rule('levels'), rule('skills'), rule('health'), rule('prestige'), rule('achievements')])
        + col('World', [rule('karma'), rule('events'), rule('circle')]))}</div>
      <div class="nav-mine">${profile ? link('player', 'My character') + link('stash', 'My stash') + link('wishlist', 'Wishlist') : ''}${link('plan', 'Plan a raid')}${link('keep', 'Items to keep')}${link('goal', 'I want to…')}</div></div>`;
  }

  function card(title, body, n, cls = '') {
    return `<div class="card ${cls}"><h3>${title}${n != null && n !== '' ? `<span class="n">${n}</span>` : ''}</h3>${body}</div>`;
  }

  // The quest lists: Kappa, Lightkeeper, lock-outs and great rewards, with your progress on each.
  function questListsTable() {
    const lists = [['kappa', 'Kappa', 'kappa', data.quests.filter(q => q.flags & KAPPA), 'for the Collector quest'],
      ['lightkeeper', 'Lightkeeper', 'lk', data.quests.filter(q => q.flags & LIGHTKEEPER), 'to reach Lightkeeper'],
      ['lockouts', 'Lock-out choices', 'lockout', null, 'pick one, the others fail'],
      ['rewards', 'Great rewards', 'reward', data.quests.filter(q => q.highlights.length), 'worth it for what they give']];
    return `<table class="wt"><thead><tr><th>List</th><th class="num">Quests</th>${profile ? '<th class="num">Done</th>' : ''}<th>What it is</th></tr></thead><tbody>
      ${lists.map(([page, name, cls, list, text]) => `<tr data-page="${page}" data-id=""><td><a class="${cls}">${name}</a></td>
        <td class="num">${list ? list.length : lockGroups().length}</td>${profile ? `<td class="num">${list ? progress(list).done : ''}</td>` : ''}<td class="sub">${text}</td></tr>`).join('')}</tbody></table>`;
  }

  // Every trader with quests: how many, how many you've done, and whether they start locked.
  function tradersTable(big = false) {
    return `<table class="wt"><thead><tr><th colspan="2">Trader</th><th class="num">Quests</th>${profile ? '<th class="num">Done</th>' : ''}${big ? '<th>Unlock</th>' : ''}</tr></thead><tbody>
      ${traders.map(id => {
        const list = data.quests.filter(q => q.trader === id);
        return `<tr data-page="trader" data-id="${id}"><td class="pic-cell">${avatar(id, big ? 'mid' : '')}</td><td><a>${esc(traderName(id))}</a>${!big && !traderOpen(id) ? ' <span class="sub">locked</span>' : ''}</td>
          <td class="num">${list.length}</td>${profile ? `<td class="num">${progress(list).done}</td>` : ''}${big ? `<td class="sub">${traderLockNote(id)}</td>` : ''}</tr>`;
      }).join('')}</tbody></table>`;
  }

  // Every map: raid time, quests there, bosses, and with a profile how many of your objectives are there.
  // maps SPT's launcher has no picture of under their own name
  const MAP_PICTURE = { icebreaker: 'map-icebreaker.png', labyrinth: '/files/banners/67eac0afef0822d52d09f6a5.jpg' };
  // the picture SPT's launcher has for a map: the server looks it up in what SPT serves
  const mapBanner = id => MAP_PICTURE[String(id).toLowerCase()] || `api/banner?map=${encodeURIComponent(id)}`;

  function mapsTable(maps) {
    const plans = profile ? mapPlan() : [];
    return `<table class="wt"><thead><tr><th>Map</th><th class="num">Raid</th><th class="num">Quests</th>${profile ? '<th class="num">Yours</th>' : ''}<th>Bosses</th></tr></thead><tbody>
      ${oneFactory([...maps]).sort((a, b) => a.name.localeCompare(b.name)).map(m => {
        const quests = data.quests.filter(q => q.maps.some(x => baseMap(x) === baseMap(m.name))).length;
        const plan = plans.find(x => x.map === baseMap(m.name));
        return `<tr data-page="map" data-id="${esc(m.id)}"><td class="map-cell"><img src="${mapBanner(m.id)}" alt="" loading="lazy" onerror="this.remove()"><a>${esc(m.name)}</a>${m.level ? ` <span class="sub">level ${m.level}+</span>` : ''}</td>
          <td class="num">${m.minutes ? `${m.minutes} min` : ''}</td><td class="num">${quests}</td>${profile ? `<td class="num">${plan ? plan.objectives.length : ''}</td>` : ''}
          <td>${m.bosses.map(b => esc(bossName(b))).join(', ')}</td></tr>`;
      }).join('')}</tbody></table>`;
  }

  // the main page's quest lists: how many each shows before "and N more", and which are opened out
  const OVERVIEW_CAP = 8;
  const overviewOpen = new Set();
  function overviewPage() {
    const maps = wiki('maps', 'api/maps');
    let html = `<div class="page-head"><div class="t"><h2>Tushonkapedia</h2>
        <p>Quests, items, traders, maps, bots and the hideout as this server has them, with every change its mods make.
          The public wiki follows the live game, so where the two differ, trust this one.</p></div></div>`;

    if (profile) {
      const plans = mapPlan();
      const best = plans[0];
      const recs = recommended(best?.map).slice(0, 6);
      const soon = unlocksSoon().slice(0, 8);
      // most important first: what the quest is worth (Kappa, Lightkeeper, what it opens, its rewards),
      // then a lift for the ones you can work on during the raid this page suggests
      const onBest = q => best && q.objectives.some(o => objectiveMaps(q, o).includes(best.map));
      const rank = q => worth(q) + (onBest(q) ? 0.3 : 0);
      const pick = st => data.quests.filter(q => statusOf(q.id) === st).sort((a, b) => rank(b) - rank(a) || a.name.localeCompare(b.name));
      const list = (items, empty) => items.length ? `<div class="list">${items.join('')}</div>` : `<p class="hint">${empty}</p>`;
      // a long list shows its first few, and a button for the rest
      const capped = (st, empty) => {
        const all = pick(st), open = overviewOpen.has(st), shown = open ? all : all.slice(0, OVERVIEW_CAP);
        return list(shown.map(q => row(q, esc(traderName(q.trader)))), empty)
          + (all.length > OVERVIEW_CAP ? `<button class="more" data-ovmore="${st}">${open ? 'Show fewer' : `and ${all.length - OVERVIEW_CAP} more`}</button>` : '');
      };
      html += `<div class="two">
        <div class="col">
          ${card('Next raid', best
            ? `<p>Go to <a data-page="plan" data-id="${esc(best.map)}">${esc(best.map)}</a>: ${best.objectives.length} objective${best.objectives.length === 1 ? '' : 's'} from
                ${new Set(best.objectives.map(o => o.quest.id)).size} quests in progress${best.pickup.length ? `, and ${best.pickup.length} more to pick up` : ''}.</p>
              ${plans.length > 1 ? `<p class="sub">Also: ${plans.slice(1, 5).map(x => `<a data-page="plan" data-id="${esc(x.map)}">${esc(x.map)}</a> (${x.objectives.length})`).join(', ')}</p>` : ''}`
            : '<p class="hint">No quests in progress need a raid. Pick some up first.</p>')}
          ${card('Ready to hand in', capped('ready', 'Nothing to hand in.'), pick('ready').length)}
          ${card('In progress', capped('active', 'No quests started.'), pick('active').length)}
          ${card('Available now', capped('available', 'Nothing new to pick up right now.'), pick('available').length)}
        </div>
        <div class="col">
          ${card('Worth picking up', recs.length ? `<div class="recs">${recs.map(recCard).join('')}</div>` : '<p class="hint">Nothing new to pick up right now.</p>')}
          ${card('Unlocks soon', list(soon.map(x => row(x.quest, esc(traderName(x.quest.trader)), '', `<div class="missing">Needs: ${x.missing.map(m => m.text).join(' · ')}</div>`)), 'Nothing close to unlocking.'))}
        </div>
      </div>`;
    } else if (profiles.length) {
      html += '<p class="hint">Pick your profile at the top to see your progress, what to do next and where to go.</p>';
    }

    html += `<div class="two">
      <div class="col">${card('Quests by trader', tradersTable())}</div>
      <div class="col">
        ${card('Quest lists', questListsTable())}
        ${card('Maps', !maps ? '<div class="loading">Loading the maps…</div>' : maps.error ? '<p class="hint">Couldn\'t load the maps.</p>' : mapsTable(maps))}
      </div>
    </div>`;
    return html;
  }

  function planPage() {
    if (!profile) {
      return `<div class="page-head"><div class="t"><h2>Plan a raid</h2></div></div>
        <div class="intro">Pick your <b>profile</b> at the top. The planner then shows which map gets the most of your quests done,
          what to do there and which keys to bring.</div>`;
    }
    const plans = mapPlan();
    if (!plans.length) {
      return `<div class="page-head"><div class="t"><h2>Plan a raid</h2></div></div><div class="hint">None of your quests in progress need a raid, and there's nothing to pick up that does.</div>
        <div class="article"><div class="col">${shopCard()}</div><div class="col"></div></div>`;
    }
    const chosen = plans.find(p => p.map === (view.id || planMap)) || plans[0];
    planMap = chosen.map;
    const quests = [...new Set(chosen.objectives.map(o => o.quest))];
    const keys = quests.flatMap(q => q.needs.filter(n => n.kind === 'key' && (!n.objective || chosen.objectives.some(o => o.objective.id === n.objective))))
      .filter((k, i, all) => all.findIndex(x => x.text === k.text) === i);
    // items to place on this map, added up per item
    const bring = [];
    for (const q of quests) {
      for (const o of q.objectives.filter(o => ['LeaveItemAtLocation', 'PlaceBeacon'].includes(o.kind) && o.items.length && objectiveMaps(q, o).includes(chosen.map))) {
        const same = bring.find(b => b.items[0] === o.items[0]);
        if (same) same.count += o.count ?? 1;
        else bring.push({ items: o.items, count: o.count ?? 1 });
      }
    }
    const gear = quests.flatMap(q => q.needs.filter(n => ['wear', 'weapon', 'mods', 'caliber'].includes(n.kind)).map(n => ({ quest: q, need: n })));

    const objectiveRows = chosen.objectives.map(({ quest, objective }) => `<div class="todo-item${markClass(quest)}">${objective.items[0] ? icon(objective.items[0]) : ''}
      <div class="body">${linked(objective.text, objective.items)}<div class="q-of"><a class="ql" data-q="${quest.id}">${esc(quest.name)}</a> · ${esc(traderName(quest.trader))} · <span class="prio p${Math.min(3, Math.floor(worth(quest) / 0.55))}">${worthWord(quest)}</span></div>
      ${quest.needs.filter(n => n.kind === 'key' && n.objective === objective.id).map(keyRow).join('')}</div></div>`);

    needWhere(keys.map(k => k.ids[0]));
    const finds = chosen.finds.slice(0, 14).map(({ need, spot }) => `<div class="todo-item">${icon(need.id)}
      <div class="body">${need.key ? 'Key: ' : ''}${itemLink(need.id)} <span class="sub">× ${need.count}${need.key ? '' : ' found in raid'}</span>
        <div class="q-of">${[bestOdds(spot), need.shop ? 'on your shopping list' : '', need.quests.size ? 'for ' + [...need.quests].map(link).join(', ') : ''].filter(Boolean).join(' · ')}</div></div></div>`);
    const top = plans[0].score || 1;
    // the route: each objective with a place on this map, the best spots for what's on the shopping list, and the
    // map itself for its extracts and PMC starts
    const mapIds = wiki('maps', 'api/maps');
    const mid = (mapIds || []).find(m => baseMap(m.name) === chosen.map)?.id;
    const stops = mid ? [{ k: `|${mid}`, map: true, t: '' }] : [];
    if (mid) {
      for (const { quest, objective: o } of chosen.objectives) {
        const what = o.zones?.length ? `zone:${o.zones.slice(0, 8).join(',')}` : o.questItem && o.items.length ? o.items[0] : '';
        const text = o.text.length > 70 ? o.text.slice(0, 68) + '…' : o.text;
        // two objectives at the same place are one stop, with both written on it
        const same = what && stops.find(x => x.k === `${what}|${mid}`);
        if (same) same.t += `; ${text} (${quest.name})`;
        else if (what) stops.push({ k: `${what}|${mid}`, t: `${text} (${quest.name})` });
      }
      for (const id of Object.keys(shopList())) {
        if (whereCache.get(id)?.loot.some(l => baseMap(l.map) === chosen.map && l.total > 0)) stops.push({ k: `${id}|${mid}`, shop: true, t: itemName(id) });
      }
    }
    const routeCard = stops.length > 1 ? card(`Route on ${esc(chosen.map)}`, `<p class="sub">Click the blue dot where you spawned and the route starts from there. It ends at the nearest PMC extract.</p>
      <div class="lootmap route-map" data-route="1" data-lm="${esc('route|' + mid)}" data-parts="${esc(JSON.stringify(stops))}"></div>`) : '';

    return `<div class="page-head"><div class="t"><h2>Plan a raid</h2>
        <p>Maps ranked by what your quests get done there. Quests count by how much they're worth doing: Kappa and Lightkeeper,
          the later quests they open and rewards worth having count more; a dead-end quest with poor rewards counts less.
          Items your quests want found in raid, and keys you need, add a little where they spawn best; what's on your
          shopping list counts in full.</p></div></div>
      <div class="map-cards">${plans.map((p, i) => `<button class="map-card${p === chosen ? ' on' : ''}" data-plan="${esc(p.map)}">
        <span class="n">${esc(p.map)}</span>${i === 0 ? '<span class="best">Best</span>' : ''}
        <div class="s">${p.objectives.length} objective${p.objectives.length === 1 ? '' : 's'}${p.pickup.length ? ` · ${p.pickup.length} to pick up` : ''}${p.finds.length ? ` · ${p.finds.length} item${p.finds.length === 1 ? '' : 's'} to find` : ''}</div>
        <div class="meter"><span style="width:${Math.round(p.score * 100 / top)}%"></span></div></button>`).join('')}</div>
      <div class="article"><div class="col">
        ${routeCard}
        ${card(`To do on ${esc(chosen.map)}`, objectiveRows.length ? `<div class="todo">${objectiveRows.join('')}</div>` : '<p class="none">None of your quests in progress have objectives here.</p>', objectiveRows.length)}
        ${chosen.pickup.length ? card('Pick these up before you go', `<div class="list">${chosen.pickup.map(q => row(q, esc(traderName(q.trader)))).join('')}</div>`, chosen.pickup.length) : ''}
        ${finds.length ? card(`Look for on ${esc(chosen.map)}`, `<div class="todo">${finds.join('')}</div><div class="src">Spawn chances are worked out from this server's loot tables, mods included. They're averages, not promises.</div>`, chosen.finds.length) : ''}
      </div><div class="col">
        ${shopCard()}
        ${card('Keys', keys.length ? keys.map(k => `<div class="key-row">${icon(k.ids[0])}<div><div>${itemLink(k.ids[0], k.text)}</div><div class="how">${howToGet(k.ids[0])}</div></div></div>`).join('') + '<div class="src">Which keys: tarkov.dev (the live game) or keys.json. Where to get them: this server.</div>' : '<p class="none">No keys listed for these objectives.</p>')}
        ${bring.length ? card('Bring', bring.map(o => `<div class="item-tile" data-item="${esc(o.items[0])}">${icon(o.items[0])}<span class="t">${o.count} × ${esc(itemName(o.items[0]))}</span></div>`).join('')) : ''}
        ${gear.length ? card('Gear', gear.map(g => `<div class="need"><span class="k">${NEED_TEXT[g.need.kind]}</span><span>${esc(g.need.text)} <span class="sub">${esc(g.quest.name)}</span></span></div>`).join('')) : ''}
      </div></div>`;
  }

  // Items to hold on to: what the quests you haven't finished still ask for, added up.
  function itemsPage() {
    const open = q => !profile || !['done', 'failed', 'lockedOut'].includes(statusOf(q.id));
    const totals = new Map();
    for (const quest of data.quests) {
      if (!open(quest) || (keepFilter && !(quest.flags & keepFilter))) continue;
      for (const o of quest.objectives) {
        if (!['HandoverItem', 'LeaveItemAtLocation', 'PlaceBeacon'].includes(o.kind) || !o.items.length || !firstOfGroup(quest, o)) continue;
        // a quest that takes several different items lists the first; they're alternatives
        const id = o.items[0];
        if (questItems.has(id)) continue;
        const t = totals.get(id) || { id, count: 0, fir: 0, quests: new Set() };
        t.count += o.count ?? 1;
        if (o.fir) t.fir += o.count ?? 1;
        t.quests.add(quest.id);
        totals.set(id, t);
      }
    }
    const list = [...totals.values()].sort((a, b) => itemName(a.id).localeCompare(itemName(b.id)));
    needWhere(list.map(t => t.id));
    const chip = (value, label) => `<button class="chip${keepFilter === value ? ' on' : ''}" data-keep="${value}">${label}</button>`;
    // what the profile has that counts: found-in-raid ones for what must be found in raid, any for the rest
    const owned = profile?.owned;
    const counts = t => { const [all, fir] = owned?.[t.id] || [0, 0]; return { fir: Math.min(fir, t.fir), any: Math.min(all - Math.min(fir, t.fir), t.count - t.fir), all, firAll: fir }; };
    const enough = t => { const c = counts(t); return c.fir >= t.fir && c.any >= t.count - t.fir; };
    const have = t => {
      const c = counts(t);
      const got = c.fir + c.any;
      return `<span class="have ${enough(t) ? 'all' : got ? 'some' : 'none'}">${got} / ${t.count}</span>${t.fir && c.firAll < t.fir && c.all > c.firAll ? `<div class="sub">${c.all - c.firAll} not found in raid</div>` : ''}`;
    };
    return `<div class="page-head"><div class="t"><h2>Items to keep</h2>
        <p>${list.length} items the ${profile ? 'quests you haven\'t finished' : 'quests'} still ask for. Found in raid means it has to be found in raid, not bought.</p></div></div>
      <div class="toolbar">${chip(0, 'All quests')}${chip(KAPPA, '<span class="sw kappa"></span>Kappa only')}${chip(LIGHTKEEPER, '<span class="sw lk"></span>Lightkeeper only')}${owned ? `<span class="sep"></span><button class="chip${prefs.hideHave ? ' on' : ''}" data-hidehave="1">Hide what I already have (${list.filter(t => enough(t)).length})</button>` : ''}</div>
      ${owned ? `<p class="sub keep-sum">You have enough of <b>${list.filter(t => enough(t)).length}</b> of these ${list.length} items. Counted from ${esc(profile.name)}'s stash and kit, found-in-raid ones apart; it updates as the game saves.</p>` : ''}
      <div class="card"><table class="items-table keep-table"><thead><tr><th>Item</th><th class="num">Needed</th>${owned ? '<th class="num">You have</th>' : ''}<th>Where to get it</th><th>For</th></tr></thead><tbody>${(owned && prefs.hideHave ? list.filter(t => !enough(t)) : list).map(t => `<tr${owned && enough(t) ? ' class="have-all"' : ''}>
        <td><span class="item">${icon(t.id)}<span>${itemLink(t.id)}</span></span></td>
        <td class="num">${t.count}${t.fir ? `<span class="fir">${t.fir === t.count ? 'FIR' : t.fir + ' FIR'}</span>` : ''}</td>
        ${owned ? `<td class="num">${have(t)}</td>` : ''}
        <td class="how">${howToGet(t.id)}</td>
        <td class="who">${[...t.quests].map(link).join(', ')}</td></tr>`).join('')}</tbody></table></div>`;
  }

  // A trader's page: their quest lines, their stock and what each loyalty level needs.
  function traderPage(id) {
    const tab = view.tab || (data.traders[id] ? 'quests' : 'stock');
    const tabs = `<div class="tabs">${[['quests', 'Quests'], ['stock', 'Stock'], ['loyalty', 'Loyalty levels']].map(([t, label]) =>
      `<button class="tab${tab === t ? ' on' : ''}" data-tab="${t}">${label}</button>`).join('')}</div>`;
    if (tab !== 'quests') return `${crumbs(['traders', 'Traders'], [null, traderName(id)])}${traderHead(id)}${tabs}${traderStock(id, tab)}`;
    return `${crumbs(['traders', 'Traders'], [null, traderName(id)])}${traderQuests(id, tabs)}`;
  }

  function traderHead(id) {
    const trader = wikiCache.get('trader:' + id);
    return `<div class="page-head">${avatar(id, 'big')}<div class="t"><h2>${esc(traderName(id))}</h2>
      <p>${trader?.fullName ? esc(trader.fullName) : ''}${trader?.location ? ` · ${esc(trader.location)}` : ''}</p>
      ${traderLockNote(id) ? `<p class="locked-trader">${traderLockNote(id)}</p>` : ''}</div></div>`;
  }

  function traderStock(id, tab) {
    const trader = wiki('trader:' + id, 'api/trader?id=' + encodeURIComponent(id));
    if (!trader) return '<div class="loading">Loading…</div>';
    if (trader.error) return '<div class="hint">This trader has no stock list.</div>';
    Object.assign(data.items, trader.items);
    const mine = profile?.traders?.[id]?.loyalty;
    if (tab === 'loyalty') {
      return `${trader.description ? card('About', `<div class="brief">${esc(trader.description)}</div>`) : ''}
        <div class="card"><table class="items-table"><tr><td><b>Level</b></td><td><b>Your level</b></td><td><b>Spent with them</b></td><td><b>Standing</b></td></tr>
        ${trader.levels.map(l => `<tr${mine >= l.level ? ' class="reached"' : ''}><td>LL${l.level}${mine === l.level ? ' <span class="status available">You</span>' : ''}</td><td>${l.minLevel ?? '–'}</td>
          <td>${l.minSalesSum != null ? money(l.minSalesSum, currencyId(trader.currency)) : '–'}</td><td>${l.minStanding ?? '–'}</td></tr>`).join('')}</table></div>`;
    }
    const levels = [...new Set(trader.offers.map(o => o.level))].sort((a, b) => a - b);
    return levels.map(level => `<div class="section"><h3>Loyalty level ${level}${mine != null ? (mine >= level ? ' <span class="status available">Unlocked</span>' : ' <span class="status locked">Not yet</span>') : ''}</h3>
      <div class="item-cards">${trader.offers.filter(o => o.level === level).sort((a, b) => itemName(a.item).localeCompare(itemName(b.item))).map(o => `<button class="item-card${o.quest ? ' locked' : ''}" data-item="${o.item}">
        <span class="pic">${pic(o.item)}</span>
        <span class="n">${esc(itemName(o.item))}</span>
        <span class="s">${o.currency ? money(o.price, o.currency) : `barter: ${o.barter.map(b => `${b.count} × ${itemLink(b.id)}`).join(', ')}`}</span>
        ${o.quest ? `<span class="s lock">Needs ${byId.has(o.quest) ? link(o.quest) : 'a quest'}</span>` : ''}</button>`).join('')}</div></div>`).join('')
      || `<div class="hint">${trader.randomStock ? `${esc(trader.name)}'s stock isn't a fixed list: it's made of what players sell him and a random selection that changes every few minutes. Check it in the game.` : 'No fixed stock on this server.'}</div>`;
  }

  function traderQuests(id, tabs) {
    const all = data.quests.filter(q => q.trader === id);
    const list = filtered(all, true, true);
    const p = progress(all);
    rowPic = true;
    const t = list.length ? tree(list.map(q => q.id)) : { roots: [], html: `<div class="hint">${profile && prefs.hideUnavailable ? 'Nothing you can take from them now. Turn off <b>Hide locked</b> to see the rest.' : 'No quests match.'}</div>` };
    rowPic = false;
    const lock = traderLockNote(id);
    return `<div class="page-head">${avatar(id, 'big')}<div class="t"><h2>${esc(traderName(id))}</h2>
        <p>${all.length} quests${profile ? ` · ${p.done} done` : ''}</p>
        ${lock ? `<p class="locked-trader">${lock}</p>` : ''}
        ${profile ? `<div class="meter"><span style="width:${p.pct}%"></span></div>` : ''}</div></div>
      ${tabs}
      ${toolbar(all, t.roots, true, true)}
      ${t.html}`;
  }

  function goalPage(flag) {
    const kappa = flag === KAPPA;
    const all = data.quests.filter(q => q.flags & flag);
    const p = progress(all);
    const list = filtered(all, false);
    const end = byId.get(kappa ? COLLECTOR : KNOCK_KNOCK);
    const sections = [];
    const roots = [];
    for (const trader of traders) {
      const mine = list.filter(q => q.trader === trader);
      if (!mine.length) continue;
      const t = tree(mine.map(q => q.id));
      roots.push(...t.roots);
      const tp = progress(all.filter(q => q.trader === trader));
      sections.push(`<div class="section"><h3>${avatar(trader)}${esc(traderName(trader))}<span class="n">${profile ? `${tp.done}/${tp.total}` : mine.length}</span></h3>${goalView === 'tree' ? questDiagram(mine.map(q => q.id), trader) : t.html}</div>`);
    }
    return `<div class="page-head"><div class="t"><h2>${kappa ? 'Kappa' : 'Lightkeeper'}</h2>
        <p>${all.length} quests lead to ${end ? `<a class="ql" data-q="${end.id}">${esc(end.name)}</a>` : (kappa ? 'Collector' : 'Knock-Knock')}${profile ? ` · ${p.done} done` : ''}</p>
        ${profile ? `<div class="meter ${kappa ? 'kappa' : 'lk'}"><span style="width:${p.pct}%"></span></div>` : ''}</div></div>
      <div class="intro ${kappa ? 'gold' : 'blue'}">Every quest you need to finish before ${kappa ? 'Collector, the Kappa container quest' : 'Knock-Knock, which unlocks Lightkeeper'}, as this server has it now.
        Where a quest accepts an earlier one failed, the rival quest that fails it is on the list too.</div>
      <div class="toolbar">${[['list', 'List'], ['tree', 'Tree']].map(([v, t]) => `<button class="chip${goalView === v ? ' on' : ''}" data-goalview="${v}">${t}</button>`).join('')}${goalView === 'tree' ? `<span class="sep"></span>${profile ? `<button class="chip${prefs.treeFoldDone ? ' on' : ''}" data-treefold="1">Fold finished quests</button>` : ''}<span class="sub">Click a quest to light up what it needs and what needs it.</span>` : ''}</div>
      ${toolbar(all, roots, false)}
      ${sections.join('')}
      ${kappa ? collectorCard(end) : ''}`;
  }

  // One trader's quests as a tree: a column for each step down the prerequisites, a line from each quest to the ones
  // it opens. A dashed line means the next quest opens even when this one failed.
  let goalView = 'list';
  function questDiagram(ids, trader) {
    const set = new Set(ids);
    const before = id => (requiredBy.get(id) || []).filter(p => set.has(p));
    const colOf = new Map();
    const col = id => {
      if (!colOf.has(id)) { colOf.set(id, 0); colOf.set(id, before(id).reduce((n, p) => Math.max(n, col(p) + 1), 0)); }
      return colOf.get(id);
    };
    ids.forEach(col);
    const finished = id => !!profile && statusOf(id) === 'done';
    // finished quests fold into one box when asked
    const fold = !!prefs.treeFoldDone && !!profile;
    const shown = ids.filter(id => !(fold && finished(id)));
    const cols = new Map();
    for (const id of shown) { const c = colOf.get(id); if (!cols.has(c)) cols.set(c, []); cols.get(c).push(id); }
    const W = 168, H = 46, GX = 44, GY = 10;
    const pos = new Map();
    let tall = 0;
    const colList = [...cols.keys()].sort((a, b) => a - b);
    const xOf = new Map(colList.map((c, i) => [c, i * (W + GX)]));
    for (const c of colList) {
      cols.get(c).sort((a, b) => (byId.get(a).level ?? 0) - (byId.get(b).level ?? 0) || byId.get(a).name.localeCompare(byId.get(b).name))
        .forEach((id, i) => { pos.set(id, { x: xOf.get(c), y: i * (H + GY) }); tall = Math.max(tall, (i + 1) * (H + GY)); });
    }
    const doneCount = ids.length - shown.length;
    let foldY = 0;
    if (doneCount) { foldY = tall; tall += H + GY; }
    const wide = Math.max(1, colList.length) * (W + GX);
    const edges = [];
    for (const e of data.edges) {
      if (e.kind !== 'requires' || !set.has(e.from) || !set.has(e.to) || !pos.has(e.from) || !pos.has(e.to)) continue;
      const a = pos.get(e.from), b = pos.get(e.to), x1 = a.x + W, y1 = a.y + H / 2, x2 = b.x, y2 = b.y + H / 2, mid = (x1 + x2) / 2;
      edges.push(`<path d="M${x1} ${y1} C${mid} ${y1} ${mid} ${y2} ${x2} ${y2}" class="${e.status.includes('Fail') ? 'dash' : ''}" data-from="${e.from}" data-to="${e.to}"/>`);
    }
    const klass = id => !profile ? '' : finished(id) ? 'done' : ['active', 'ready'].includes(statusOf(id)) ? 'active' : statusOf(id) === 'available' ? 'avail'
      : before(id).every(p => ['done', 'active', 'ready'].includes(statusOf(p))) && !shutOut(id) ? 'next' : 'shut';
    const boxes = shown.map(id => {
      const p = pos.get(id), q = byId.get(id);
      const where = q.maps[0] && q.maps[0] !== 'Any' ? esc(q.maps[0]) : '';
      return `<div class="dq ${klass(id)}" data-dq="${id}" style="left:${p.x}px;top:${p.y}px;width:${W}px;height:${H}px"><a data-q="${id}" title="${esc(q.name)}">${esc(q.name)}</a><span class="sub">${[q.level ? `Level ${q.level}` : '', where].filter(Boolean).join(' · ')}</span></div>`;
    }).join('') + (doneCount ? `<div class="dq done" style="left:0;top:${foldY}px;width:${W}px;height:${H}px"><a>${doneCount} finished</a></div>` : '');
    return `<div class="diagram-wrap"><div class="diagram" style="width:${wide}px;height:${tall}px"><svg width="${wide}" height="${tall}">${edges.join('')}</svg>${boxes}</div></div>`;
  }

  // The quests before and after one quest, in order: what you finished is folded into one line, the quest is marked
  // and what comes after follows step by step.
  async function showChain(id) {
    const quest = byId.get(id);
    if (!quest) return;
    const back = new Set();
    const todo = [id];
    while (todo.length) { const at = todo.pop(); for (const p of requiredBy.get(at) || []) if (!back.has(p)) { back.add(p); todo.push(p); } }
    const ahead = [...unlocked(id)].filter(x => x !== id);
    const order = list => list.map(x => byId.get(x)).filter(Boolean).sort((a, b) => a.depth - b.depth || (a.level ?? 0) - (b.level ?? 0) || a.name.localeCompare(b.name));
    const done = order([...back]).filter(q => profile && statusOf(q.id) === 'done');
    const line = (q, cls = '') => `<div class="chain-q ${cls}"><a class="ql" data-q="${q.id}">${esc(q.name)}</a> <span class="sub">${esc(traderName(q.trader))}${profile && statusOf(q.id) ? ' · ' + (STATUS_TEXT[statusOf(q.id)] || '') : ''}</span></div>`;
    const body = `<div class="chain">
      ${done.length ? `<div class="chain-q fold sub">${done.length} finished before this</div>` : ''}
      ${order([...back]).filter(q => !done.includes(q)).map(q => line(q)).join('')}
      ${line(quest, 'here')}
      ${order(ahead).map(q => line(q)).join('') || '<div class="chain-q sub">Nothing comes after it.</div>'}</div>`;
    const wasOpen = await confirmBox({ title: `${quest.name}: the chain`, ok: 'Close', alone: true, body });
    void wasOpen;
  }

  // The items Collector asks for: whether you have them, and where they turn up.
  function collectorCard(end) {
    const wanted = end ? end.objectives.filter(o => o.items.length && ['HandoverItem', 'FindItem'].includes(o.kind)) : [];
    if (!wanted.length) return '';
    needWhere(wanted.map(o => o.items[0]));
    const handed = profile && statusOf(end.id) === 'done';
    const owned = profile?.owned;
    const rows = wanted.map(o => {
      const id = o.items[0], need = o.count ?? 1;
      const [all, fir] = owned?.[id] || [0, 0];
      const state = handed ? 'handed' : !owned ? '' : fir >= need ? 'fir' : all >= need && !o.fir ? 'fir' : all >= need ? 'notfir' : 'missing';
      return { o, id, need, all, fir, state };
    });
    const rank = { missing: 0, notfir: 1, fir: 2, handed: 3, '': 0 };
    rows.sort((a, b) => rank[a.state] - rank[b.state] || itemName(a.id).localeCompare(itemName(b.id)));
    const word = { handed: 'Handed over', fir: 'Have it', notfir: 'Have it, but not found in raid', missing: 'Missing', '': '' };
    const have = rows.filter(r => r.state === 'fir' || r.state === 'handed').length;
    return `<div class="card collector">${'<h3>Collector items</h3>'}
      ${owned ? `<p class="sub">${have} of ${rows.length} ready. Counted from your stash and kit; it needs to be found in raid.</p>` : ''}
      <table class="items-table"><thead><tr><th>Item</th>${owned ? '<th>You</th>' : ''}<th>Where to get it</th></tr></thead><tbody>${rows.map(r => `<tr class="${r.state === 'handed' ? 'have-all' : ''}">
        <td><div class="item">${icon(r.id, 'sm')}${itemLink(r.id)}${r.need > 1 ? ` × ${number(r.need)}` : ''}${r.o.fir ? '<span class="fir">FIR</span>' : ''}</div></td>
        ${owned ? `<td class="${r.state}">${word[r.state]}</td>` : ''}
        <td class="how">${howToGet(r.id)}</td></tr>`).join('')}</tbody></table></div>`;
  }

  // Quests that fail each other, grouped: finish one and the others in the group fail for good.
  function lockGroups() {
    const links = new Map();
    const add = (a, b) => { if (!links.has(a)) links.set(a, new Set()); links.get(a).add(b); };
    for (const edge of data.edges) {
      if (edge.kind === 'fails') { add(edge.from, edge.to); add(edge.to, edge.from); }
    }
    const seen = new Set();
    const groups = [];
    for (const id of links.keys()) {
      if (seen.has(id)) continue;
      const group = [];
      const todo = [id];
      while (todo.length) {
        const next = todo.pop();
        if (seen.has(next)) continue;
        seen.add(next);
        group.push(next);
        todo.push(...links.get(next));
      }
      groups.push(group.sort((a, b) => byId.get(a).name.localeCompare(byId.get(b).name)));
    }
    return groups.sort((a, b) => byId.get(a[0]).name.localeCompare(byId.get(b[0]).name));
  }

  function lockoutPage() {
    const groups = lockGroups();
    let html = `<div class="page-head"><div class="t"><h2>Lock-outs</h2><p>${groups.length} choices</p></div></div>
      <div class="intro red">In each group, finishing one quest makes the others fail, and most can't be started again. Check what each one leads to before you hand one in.</div>`;
    for (const group of groups) {
      // each one fails all the others: say so once, else list who fails whom
      const fails = group.flatMap(id => (outgoing.get(id) || []).filter(e => e.kind === 'fails' && group.includes(e.to)));
      const why = fails.length === group.length * (group.length - 1) && fails.every(e => e.status.length === 1 && e.status[0] === 'Success')
        ? ['Finishing any one of these fails the others.']
        : fails.map(e => `${link(e.from)} ${e.status.length === 1 && e.status[0] === 'Success' ? 'finished' : 'when ' + words(e.status)} fails ${link(e.to)}`);
      html += `<div class="card lock-group"><div class="row">${group.map(id => `<div>${row(byId.get(id), esc(traderName(byId.get(id).trader)))}
        <div class="why-chips"><span class="why-chip">opens ${opens(id)} later quests</span></div></div>`).join('')}</div>
        <div class="why">${why.map(w => `<div>${w}</div>`).join('')}</div></div>`;
    }
    return html;
  }

  // ---------- a quest's own page ----------

  function link(id) {
    const quest = byId.get(id);
    return quest ? `<a class="ql" data-q="${id}">${esc(quest.name)}</a>` : `<span class="sub">${esc(id)} (not on this server)</span>`;
  }

  function qchip(id, cls = '') {
    const quest = byId.get(id);
    if (!quest) return '';
    return `<span class="qchip ${statusOf(id)} ${cls}" data-q="${id}">${esc(quest.name)}</span>`;
  }

  function words(status) {
    const map = { Success: 'finished', Started: 'started', Fail: 'failed', AvailableForFinish: 'ready to hand in' };
    return status.length ? status.map(s => map[s] || s).join(' or ') : 'any status';
  }

  function duration(seconds) {
    return seconds >= 3600 ? `${Math.round(seconds / 360) / 10} h` : `${Math.round(seconds / 60)} min`;
  }

  function number(n) {
    return Number(n).toLocaleString();
  }

  // "Fence standing <= -1" as "Fence standing -1 or lower"
  const COMPARE_WORDS = { '>=': v => `${v} or higher`, '<=': v => `${v} or lower`, '>': v => `above ${v}`, '<': v => `below ${v}`, '=': v => `exactly ${v}`, '==': v => `exactly ${v}` };
  function plainCondition(text) {
    const asked = /^FindItem:\s*(.+?)\s*>=\s*1$/.exec(text);
    if (asked) return `Have ${asked[1]}`;
    return text.replace(/\s*(>=|<=|==|=|>|<)\s*(-?[\d.]+)$/, (all, sign, value) => ` ${COMPARE_WORDS[sign](value)}`);
  }

  // A quest laid out like a wiki article: the briefing, what you need to start it, what to do, what to bring and
  // what you get on the left; an info box, the quest line and quests to do in the same raid on the right.
  // which map a quest's page shows, when its objectives are on several
  const questMapPick = new Map();

  function questPage(quest) {
    const id = quest.id;
    const st = statusOf(id);
    const met = startMet(quest);
    const reqLine = (ok, html) => `<div class="req${ok == null ? '' : ok ? ' met' : ' unmet'}"><span class="mark">${ok == null ? '•' : ok ? '✓' : '✕'}</span><div>${html}</div></div>`;

    // what you need to start it: the trader, level and trader standing first, then the quests before it
    const basics = [];
    let total = 0, ok = 0;
    if (startsLocked(quest.trader) || (profile && !traderOpen(quest.trader))) {
      const by = data.traders[quest.trader]?.unlockedBy || [];
      total++; if (traderOpen(quest.trader)) ok++;
      basics.push(reqLine(profile ? traderOpen(quest.trader) : null,
        `${esc(traderName(quest.trader))} unlocked${by.length ? ` <span class="sub">by finishing ${by.map(link).join(' or ')}</span>` : ''}`));
    }
    const links = [];
    quest.start.forEach((c, i) => {
      if (c.kind !== 'Quest') {
        total++; if (met[i]) ok++;
        let text = esc(plainCondition(c.text));
        if (c.kind === 'Level' && profile && !met[i]) text += ` <span class="sub">(you're ${profile.level})</span>`;
        basics.push(reqLine(met[i], text));
        return;
      }
      const rival = c.status.length && c.status.every(s => s === 'Fail');
      const wait = c.availableAfter ? `, then wait ${duration(c.availableAfter)}` : '';
      for (const target of c.quests) {
        const linked = c.status.includes('Fail') && byId.get(target);
        const rivals = linked ? linked.failsWhen.filter(f => f.kind === 'Quest').flatMap(f => f.quests) : [];
        const done = profile ? linkMet(statusOf(target), c.status) : null;
        total++; if (done) ok++;
        links.push({
          id: target,
          why: `must be ${words(c.status)}${wait}${rival ? ' (rival path)' : ''}${rivals.length ? ` · fails if you finish ${rivals.map(r => esc(byId.get(r)?.name || r)).join(' or ')}` : ''}`,
          ok: done
        });
      }
    });
    let prereqs;
    if (!links.length) {
      prereqs = '<p class="none">None</p>';
    } else if (links.length <= GROUP_PREREQS) {
      prereqs = `<div class="mini-list">${links.map(l => mini(l.id, l.why)).join('')}</div>`;
    } else {
      // long lists (Collector) are grouped by trader, each folded with its count
      const groups = new Map();
      for (const l of links) {
        const t = byId.get(l.id)?.trader || '';
        if (!groups.has(t)) groups.set(t, []);
        groups.get(t).push(l);
      }
      prereqs = [...groups.entries()]
        .sort((a, b) => traders.indexOf(a[0]) - traders.indexOf(b[0]))
        .map(([t, list]) => {
          const done = list.filter(l => l.ok).length;
          return `<details class="by-trader"><summary>${avatar(t)}${esc(traderName(t))}<span class="n">${profile ? `${done}/${list.length} done` : `${list.length} quests`}</span></summary>
            <div class="list">${list.map(l => mini(l.id, l.why)).join('')}</div></details>`;
        }).join('');
    }
    const linksDone = links.filter(l => l.ok).length;
    const meter = profile && total ? `<div class="reqmeter"><span>${ok} of ${total} met</span><div class="meter"><span style="width:${Math.round(ok * 100 / total)}%"></span></div></div>` : '';

    const keysFor = objective => quest.needs.filter(n => n.kind === 'key' && n.objective === objective);
    const hintsShown = prefs.showHints || revealed.has(quest.id);
    // a quest item lying at a set spot: where, on the quest's map (the server's own forced loot spots)
    const mapIds = wiki('maps', 'api/maps');
    // where an objective happens, drawn right under it once the hints are shown: the places it sends you (go to,
    // stand in, place an item at) and where a quest item lies, on each of the quest's maps
    const placesMaps = (() => {
      if (!Array.isArray(mapIds)) return '';
      const real = id => mapIds.find(m => m.id.toLowerCase() === String(id).toLowerCase())?.id;
      const byMap = new Map();
      quest.objectives.forEach((o, i) => {
        const what = o.zones?.length ? `zone:${o.zones.slice(0, 8).join(',')}` : o.questItem && o.items.length ? o.items[0] : '';
        if (!what) return;
        const named = (o.maps.length ? o.maps : quest.maps).filter(m => m !== 'Any').map(n => mapIds.find(m => m.name === n)?.id);
        // where its places really are; one map of a pair is enough (day and night, the two Ground Zeros)
        let ids = [...new Set((o.zoneMaps?.length ? o.zoneMaps.map(real) : named).filter(Boolean))];
        ids = ids.filter(m => !(m === 'factory4_night' && ids.includes('factory4_day')) && !(m === 'Sandbox_high' && ids.includes('Sandbox')));
        const label = o.text.length > 90 ? o.text.slice(0, 88) + '…' : o.text;
        for (const m of ids) {
          if (!byMap.has(m)) byMap.set(m, []);
          byMap.get(m).push({ k: `${what}|${m}`, n: i + 1, t: label });
        }
      });
      const list = [...byMap.keys()];
      if (!list.length) return '';
      const on = list.includes(questMapPick.get(id)) ? questMapPick.get(id) : list[0];
      const name = m => esc(mapIds.find(x => x.id === m)?.name || m);
      return `${list.length > 1 ? `<div class="toolbar map-pick">${list.map(m => `<button class="chip${m === on ? ' on' : ''}" data-qmap="${esc(id + '|' + m)}">${name(m)} <span class="sub">${byMap.get(m).length}</span></button>`).join('')}</div>` : `<div class="obj-maps-head">${name(on)}</div>`}
        <div class="lootmap obj-map" data-lm="${esc(`multi|${on}`)}" data-parts="${esc(JSON.stringify(byMap.get(on)))}"></div>`;
    })();
    const objRow = (o, n, sub) => `<div class="obj${sub ? ' alt' : ''}"><span class="n">${n}</span>
      <div class="body">${linked(o.text, o.items)}${o.count > 1 && !o.items.length ? `<span class="obj-n">× ${number(o.count)}</span>` : ''}${o.fir ? '<span class="fir">FIR</span>' : ''}${o.maps.length ? ` <span class="sub">${o.maps.map(esc).join(', ')}</span>` : ''}
      ${hintsShown ? `${o.steps?.length ? `<ul class="steps">${o.steps.map(s => `<li>${linked(s, o.items)}</li>`).join('')}</ul>` : ''}${keysFor(o.id).map(keyRow).join('')}` : ''}</div>
      ${o.items.length ? `<span class="icons">${o.items.slice(0, 3).map(t => icon(t, 'sm')).join('')}</span>` : ''}</div>`;
    // objectives where any one will do show as one line with its choices under it
    let shownAt = 0;
    const seenGroup = new Set();
    const objectives = quest.objectives.map(o => {
      if (!o.any) return objRow(o, ++shownAt, false);
      if (seenGroup.has(o.any)) return '';
      seenGroup.add(o.any);
      const group = quest.objectives.filter(x => x.any === o.any);
      return `<div class="obj any"><span class="n">${++shownAt}</span><div class="body"><div class="any-head">Do any one of these <span class="sub">(any one of ${group.length})</span></div>
        <div class="any-list">${group.map(x => objRow(x, '•', true)).join('')}</div></div></div>`;
    }).join('')
      + (hintsShown && placesMaps ? `<div class="obj-maps">${placesMaps}</div>` : '');
    // reads like a pick-one list: a mod quest asking for a few different things of the same kind
    const maybeAny = (() => {
      if (quest.mod == null || quest.objectives.some(o => o.any)) return false;
      const kinds = quest.objectives.filter(o => ['HandoverItem', 'FindItem'].includes(o.kind) && o.items.length);
      if (kinds.length < 3) return false;
      const key = o => `${o.kind}|${o.count ?? 1}|${!!o.fir}`;
      const ids = kinds.flatMap(o => o.items);
      return kinds.every(o => key(o) === key(kinds[0])) && new Set(ids).size === ids.length;
    })();

    const needs = quest.needs.map(n => `<div class="need ${n.kind}"><span class="k">${NEED_TEXT[n.kind] || esc(n.kind)}${n.any ? ` <span class="sub">any one of ${n.anyOf}</span>` : ''}</span>
      <span>${n.ids.length && n.ids.length <= 3 ? `<span class="icons">${n.ids.map(t => icon(t, 'sm')).join('')}</span>` : ''}${n.ids.length === 1 && n.kind !== 'caliber' ? itemLink(n.ids[0], n.text) : linked(n.text, n.ids)}${n.note ? ` <span class="sub">${esc(n.note)}</span>` : ''}${n.onMap === true ? ' <span class="sub">Lock is by the objective on this server.</span>' : n.onMap === false ? ' <span class="sub">No lock of its by the objective on this server: check before you buy it.</span>' : ''}</span></div>`).join('');
    const keySources = [...new Set(quest.needs.filter(n => n.kind === 'key' || n.kind === 'lock').map(n => n.source))];

    // rewards: a table per moment (start, finish, fail), one row each with a picture, what it is and how much; the
    // game's bookkeeping ones left out
    const groupsWhen = { Started: 'When you start', Success: 'When you finish', Fail: 'If it fails' };
    const sign = v => `${v >= 0 ? '+' : ''}${Math.abs(v) >= 1000 ? number(v) : v}`;
    const mark = text => REWARD_ICON[text] ? rewardIcon(text) : `<span class="rw-mark">${text}</span>`;
    const atText = r => /\(([^)]+)\)\s*$/.test(r.text) ? r.text.match(/\(([^)]+)\)\s*$/)[1] : '';
    const rewardRow = r => {
      const fir = r.kind === 'Item' && r.text.includes('found in raid');
      const [ico, what, amount, cls] =
        r.kind === 'Experience' ? [mark('XP'), 'Experience', sign(r.value), 'xp']
        : r.kind === 'TraderStanding' ? [avatar(r.trader), `${esc(traderName(r.trader))} standing`, sign(r.value), r.value >= 0 ? 'standing' : 'loss']
        : r.kind === 'Item' ? [icon(r.item), `${itemLink(r.item)}${fir ? '<span class="fir">FIR</span>' : ''}`, `× ${number(r.count || 1)}`, 'item']
        : r.kind === 'AssortmentUnlock' ? [icon(r.item), `Can buy ${itemLink(r.item)}<div class="sub">${esc(r.text.replace(/^.* at /, 'at '))}</div>`, '', 'unlock']
        : r.kind === 'ProductionScheme' ? [icon(r.item), `Can craft ${itemLink(r.item)}${atText(r) ? `<div class="sub">at the ${esc(atText(r))}</div>` : ''}`, '', 'unlock']
        : r.kind === 'Skill' ? [mark('SK'), esc(r.text.replace(/\s*[+-]?\d+(\.\d+)?$/, '')), sign(r.value), 'skill']
        : r.kind === 'TraderUnlock' ? [avatar(r.trader), `Unlocks the trader <a data-page="trader" data-id="${esc(r.trader)}">${esc(traderName(r.trader))}</a>`, '', 'unlock']
        : r.kind === 'Achievement' ? [mark('ACH'), `Achievement: ${esc(r.text.replace(/^Achievement:\s*/, ''))}`, '', 'unlock']
        : r.kind === 'Pockets' ? [mark('PKT'), esc(r.text), '', 'unlock']
        : r.kind === 'CustomizationDirect' ? [mark('COS'), `Cosmetic: ${esc(r.text.replace(/^CustomizationDirect\s*/, ''))}`, '', 'unlock']
        : ['', linked(r.text, r.item ? [r.item] : []), '', ''];
      return `<tr class="rwr ${cls}"${r.item && r.kind === 'Item' ? ` data-item="${esc(r.item)}"` : ''}><td class="rwi">${ico}</td><td>${what}</td><td class="num rw-v">${amount}</td></tr>`;
    };
    const hiddenKinds = ['WebPromoCode', 'NotificationPopup', 'TraderStandingRestore'];
    const order = ['Experience', 'TraderStanding', 'Skill', 'TraderUnlock', 'Item', 'AssortmentUnlock', 'ProductionScheme'];
    const rank = r => (order.indexOf(r.kind) + 1) || 99;
    const rewards = [...new Set(quest.rewards.map(r => r.when))].map(when => {
      const list = quest.rewards.filter(r => r.when === when && !hiddenKinds.includes(r.kind)).sort((a, b) => rank(a) - rank(b));
      return list.length ? `<h4 class="rw-when">${esc(groupsWhen[when] || when)}</h4><table class="wt rw-table"><tbody>${list.map(rewardRow).join('')}</tbody></table>` : '';
    }).join('');

    // the way here: every quest before this one, by step
    const before = [...chain(id)].filter(q => q !== id).map(q => byId.get(q));
    const steps = new Map();
    for (const q of before) {
      if (!steps.has(q.depth)) steps.set(q.depth, []);
      steps.get(q.depth).push(q);
    }
    const stepHtml = [...steps.keys()].sort((a, b) => a - b).map((d, i) =>
      `<div class="step" data-n="${i + 1}"><div class="chips">${steps.get(d).sort((a, b) => a.name.localeCompare(b.name)).map(q => qchip(q.id)).join('')}</div></div>`).join('')
      + `<div class="step here"><div class="chips"><span class="qchip">${esc(quest.name)}</span></div></div>`;
    const doneBefore = before.filter(q => statusOf(q.id) === 'done').length;
    const path = !before.length ? '<p class="none">None. This is a starting quest.</p>'
      : before.length > 15 ? `<details><summary>Show all ${before.length} quests, in order</summary><div class="path">${stepHtml}</div></details>`
        : `<div class="path">${stepHtml}</div>`;

    const out = outgoing.get(id) || [];
    const unlocks = out.filter(e => e.kind === 'requires').map(e => qchip(e.to));
    const ifFailed = out.filter(e => e.kind === 'rival').map(e => qchip(e.to));
    const fails = out.filter(e => e.kind === 'fails').map(e => qchip(e.to, 'fails'));
    const failsIf = quest.failsWhen.map(c => c.kind === 'Quest' ? `${c.quests.map(link).join(' / ')} is ${words(c.status)}` : esc(plainCondition(c.text)));
    const opensTraders = Object.entries(data.traders).filter(([, t]) => t.unlockedBy.includes(id)).map(([t]) => esc(traderName(t)));

    // quests to do in the same raid: others in progress or available with objectives on this quest's maps
    const myMaps = new Set(quest.objectives.flatMap(o => objectiveMaps(quest, o)));
    const together = profile && myMaps.size ? data.quests.filter(q => q.id !== id && ['active', 'available'].includes(statusOf(q.id))
      && q.objectives.some(o => objectiveMaps(q, o).some(m => myMaps.has(m)))).slice(0, 12) : [];

    const brief = quest.description ? `<div class="brief">${esc(quest.description)}</div>` : '';

    return `${crumbs(['quests', 'Quests'], ['quests', traderName(quest.trader), quest.trader], [null, quest.name])}
      <div class="qhead${markClass(quest)}">${quest.image ? `<img class="pic" src="${esc(quest.image)}" alt="" onerror="this.remove()">` : avatar(quest.trader, 'big')}<div>
        <h2>${esc(quest.name)}</h2>
        <div class="s">Given by <a class="ql" data-page="trader" data-id="${quest.trader}">${esc(traderName(quest.trader))}</a> · ${quest.level ? `Level ${quest.level}` : 'Any level'} · ${quest.maps.map(esc).join(', ')}</div>
        <div class="tags">${st ? `<span class="status ${st}">${STATUS_TEXT[st]}</span>` : ''}${tags(quest)}</div>
      </div></div>
      ${notOfferedNote(quest)}
      ${(() => { const ids = [...new Set(fails.length ? out.filter(e => e.kind === 'fails').map(e => e.to) : [])].filter(q => byId.has(q));
        return ids.length ? `<div class="note lockout"><b>This is a multiple choice quest.</b> Finishing it fails ${ids.map(q => `<a data-page="quest" data-id="${q}">${esc(byId.get(q).name)}</a>`).join(', ')}. Check what each one leads to before you hand one in.</div>` : ''; })()}
      ${st === 'available' ? `<div class="note go"><b>You can start this now.</b> ${esc(traderName(quest.trader))} has it waiting for you.</div>` : ''}
      ${quest.changed ? '<div class="note mod">A mod on this server changes this quest from the one SPT ships, so this page shows the changed version.</div>' : ''}
      ${quest.mod != null ? `<div class="note mod">Added by ${quest.mod ? `the mod <b>${esc(quest.mod)}</b>` : 'a mod'}. ${quest.wiki
        ? "It's a quest from the live game that SPT doesn't have, brought back here. The live wiki page can differ from this server's version: what this page says is what you get here."
        : "It isn't in the game, so the public wiki won't have it."}</div>` : ''}
      ${quest.liveName ? `<div class="note renamed">The live game (Tarkov 1.0 and later) calls this quest <b>${esc(quest.liveName)}</b>, so the wiki page has that name.
        BSG renamed and reordered quests after the version this server runs, so the wiki may describe it differently. This page shows how it works here.</div>` : ''}
      <div class="article quest-art">
        <div class="col">
          ${brief ? card('Briefing', brief) : ''}
          ${card('To start', `${meter}<div class="start-cols">${basics.length ? `<div><h4 class="req-h">You need</h4>${basics.join('')}</div>` : ''}
            <div><h4 class="req-h">Quests to finish first${links.length ? `<span>${profile ? `${linksDone}/${links.length} done` : links.length}</span>` : ''}</h4>${prereqs}</div></div>`)}
          ${card(`Objectives${(needs || quest.objectives.some(o => o.steps?.length || keysFor(o.id).length || o.questItem || o.zones?.length)) && !prefs.showHints ? `<button class="hint-toggle${hintsShown ? ' on' : ''}" data-spoiler="${esc(quest.id)}">${hintsShown ? 'Hide the details' : 'Show all quest details'}</button>` : ''}`, (maybeAny ? '<p class="sub">This mod quest lists several of the same kind of thing. Some mods let you finish just one of them, and the quest data on this server has no way to mark it. Check in game before you gather all of them.</p>' : '') + (objectives || '<p class="none">None</p>'), shownAt)}
          ${card('You will need', needs ? (hintsShown ? needs : '<p class="none">Hidden with the quest details. Press <b>Show all quest details</b> on the objectives to see it.</p>') + (keySources.length ? `<div class="src">The quest data doesn't list keys. ${keySources.map(s => s === 'map' ? 'Locks nearby: this server\'s map data, so a mod that moves a door is known, but a lock nearby may not stand in your way.' : s === 'tarkov.dev' ? 'Keys: tarkov.dev\'s list for the live game, checked against this server\'s locks.' : 'Keys: keys.json on this server.').join(' ')}</div>` : '') : '<p class="none">No items or keys listed.</p>')}
          ${card('Rewards', (rewards || '<p class="none">None</p>') + (opensTraders.length ? `<div class="src">Unlocks the trader ${opensTraders.join(', ')}.</div>` : ''))}
        </div>
        <div class="col">
          ${card('Why do it', `<div class="prio-big p${Math.min(3, Math.floor(worth(quest) / 0.55))}">${worthWord(quest)}</div>
            ${worthReasons(quest).length ? `<ul class="plain">${worthReasons(quest).map(r => `<li>${esc(r)}</li>`).join('')}</ul>` : '<p class="none">No big reward, and it opens little. Do it when it\'s on your way.</p>'}
            ${quest.rewardValue ? `<div class="src">Item rewards worth about ${money(quest.rewardValue, ROUBLES)}.</div>` : ''}`)}
          <div class="card infobox"><div class="box-title">${avatar(quest.trader, 'mid')}${esc(traderName(quest.trader))}</div><dl>
            <dt>Level</dt><dd>${quest.level ?? 'Any'}</dd>
            <dt>Maps</dt><dd>${quest.maps.map(esc).join(', ')}</dd>
            <dt>Type</dt><dd>${esc(quest.type || '')}${quest.restartable ? ', can be restarted' : ''}</dd>
            <dt>Kappa</dt><dd>${quest.flags & KAPPA ? '<span class="tag kappa">Needed</span>' : 'No'}</dd>
            <dt>Lightkeeper</dt><dd>${quest.flags & LIGHTKEEPER ? '<span class="tag lk">Needed</span>' : 'No'}</dd>
            <dt>Opens</dt><dd>${opens(id)} later quest${opens(id) === 1 ? '' : 's'}</dd>
            ${before.length || opens(id) ? `<dt>Chain</dt><dd><button class="btn" data-chain="${id}">Show full chain</button></dd>` : ''}
            ${quest.mod != null ? `<dt>Added by</dt><dd>${esc(modName(quest))}</dd>` : ''}
            ${quest.wiki ? `<dt>Wiki</dt><dd><a href="${esc(quest.wiki)}" target="_blank" rel="noopener">${quest.liveName ? `“${esc(quest.liveName)}” on the live wiki ↗` : 'Live game page ↗'}</a><div class="sub">${quest.liveName ? 'Renamed in the live game.' : 'May differ from this server.'}</div></dd>` : ''}
            <dt>Quest id</dt><dd class="copy">${id}</dd>
          </dl></div>
          ${card('Leads to', unlocks.length ? `<div class="chips">${unlocks.join('')}</div>` : '<p class="none">Nothing</p>')}
          ${ifFailed.length ? card('Opens if this fails', `<div class="chips">${ifFailed.join('')}</div>`) : ''}
          ${fails.length ? card('Finishing this fails', `<div class="chips">${fails.join('')}</div>`) : ''}
          ${failsIf.length ? card('This quest fails if', `<ul class="plain">${failsIf.map(f => `<li>${f}</li>`).join('')}</ul>`) : ''}
          ${together.length ? card('Do in the same raid', `<div class="chips">${together.map(q => qchip(q.id)).join('')}</div>`, together.length) : ''}
          ${card('The way here', path, before.length ? (profile ? `${doneBefore}/${before.length} done` : `${before.length} quest${before.length === 1 ? '' : 's'}`) : '')}
        </div>
      </div>`;
  }

  // ---------- great rewards ----------

  // The character screen: level and experience, raid record, traders, skills and achievements.
  function playerPage() {
    const head = crumbs([null, 'My character']);
    if (!profile) return `${head}<div class="intro">Pick your profile at the top to see your character.</div>`;
    const p = profile.player;
    if (!p) return `${head}<div class="intro">This profile has no character yet.</div>`;
    const n = x => Number(x || 0).toLocaleString();
    const side = String(profile.side || '').toUpperCase();
    const span = p.levelTo ? p.levelTo - p.levelFrom : 0;
    const pct = span ? Math.max(0, Math.min(100, Math.round((p.experience - p.levelFrom) * 100 / span))) : 100;
    const finished = p.survived + p.runThrough;
    const survival = p.raids ? Math.round(p.survived * 100 / p.raids) : 0;
    const kd = p.deaths ? (p.kills / p.deaths).toFixed(1) : n(p.kills);
    const hours = Math.round((p.timePlayed || 0) / 360) / 10;
    const since = p.registered ? new Date(p.registered * 1000).toLocaleDateString() : '';
    const stat = (value, label, sub = '') => `<div class="stat"><div class="v">${value}</div><div class="l">${label}</div>${sub ? `<div class="sub">${sub}</div>` : ''}</div>`;
    const done = data.quests.filter(q => statusOf(q.id) === 'done').length;

    const traderRows = traders.filter(t => profile.traders?.[t]).map(t => {
      const info = profile.traders[t];
      return `<button class="trow" data-page="trader" data-id="${t}">${avatar(t)}<span class="nm">${esc(traderName(t))}</span>
        <span class="ll">LL${info.loyalty || 1}<span class="sub"> / ${data.traders[t]?.levels || 4}</span></span>
        <span class="sub">${info.unlocked === false ? 'locked' : `${Number(info.standing || 0).toFixed(2)} standing`}</span></button>`;
    }).join('');
    const skills = (p.skills || []).map(s => `<div class="skill"><span class="nm">${esc(s.name)}</span><span class="lv">${s.level}</span>
      <div class="meter"><span style="width:${Math.round(s.level * 100 / 51)}%"></span></div></div>`).join('');
    const achievements = (p.achievements || []).map(a => `<div class="ach" title="${esc(a.name)}${a.at ? ' · ' + new Date(a.at * 1000).toLocaleDateString() : ''}">
      ${a.image ? `<img src="${esc(a.image)}" alt="" loading="lazy" onerror="this.remove()">` : '<span class="noimg">?</span>'}<span>${esc(a.name)}</span></div>`).join('');

    return `${head}
      <div class="ihead player">${profile.portrait
        ? `<img class="portrait" src="api/portrait?id=${encodeURIComponent(profile.id)}&v=${esc(profile.portrait)}" alt="">`
        : `<div class="pic side ${side.toLowerCase()}" title="The game plugin draws your character once you open the Character screen in the game">${esc(side || '?')}</div>`}<div>
        <h2>${esc(profile.name)}</h2>
        <div class="s">Level ${p.level}${side ? ' · ' + side : ''}${p.prestige ? ` · Prestige ${p.prestige}` : ''}${since ? ` · since ${since}` : ''}</div>
        <div class="xp"><div class="meter"><span style="width:${pct}%"></span></div>
          <span class="sub">${n(p.experience)} XP${p.levelTo ? ` · ${n(p.levelTo - p.experience)} to level ${p.level + 1}` : ' · top level'}</span></div>
      </div></div>
      <div class="article"><div class="col">
        ${card('Raids', `<div class="stats">
          ${stat(n(p.raids), 'raids as a PMC')}
          ${stat(survival + '%', 'survived', `${n(p.survived)} survived · ${n(p.runThrough)} run-throughs`)}
          ${stat(n(p.killedInRaid + p.missing), 'deaths in raid', p.missing ? `${n(p.missing)} missing in action` : '')}
          ${stat(n(p.kills), 'kills', `${n(p.killedPmc)} PMCs · ${n(p.killedSavage)} Scavs${p.killedBoss ? ` · ${n(p.killedBoss)} bosses` : ''}`)}
          ${stat(kd, 'kills per death')}
          ${stat(n(p.longestStreak), 'longest survival streak', p.currentStreak ? `${n(p.currentStreak)} right now` : '')}
          ${stat(hours + ' h', 'time in raid')}
          ${stat(`${done} / ${data.quests.length}`, 'quests completed')}
        </div>${finished ? '' : ''}`)}
        ${card('Skills', skills ? `<div class="skills">${skills}</div>` : '<p class="none">No skill levels yet.</p>', (p.skills || []).length)}
        ${card('Achievements', achievements ? `<div class="achs">${achievements}</div>` : '<p class="none">None yet.</p>', (p.achievements || []).length)}
      </div><div class="col">
        ${card('Traders', traderRows ? `<div class="trows">${traderRows}</div>` : '<p class="none">No traders met yet.</p>')}
        ${card('Quest goals', `<table class="wt"><tbody>${[['Kappa', KAPPA], ['Lightkeeper', LIGHTKEEPER]].map(([t, flag]) => { const q = progress(data.quests.filter(x => x.flags & flag)); return `<tr><td>${t}</td><td class="num">${q.done} / ${q.total}</td></tr>`; }).join('')}</tbody></table>`)}
      </div></div>`;
  }

  function rewardsPage() {
    const open = q => !profile || !['done', 'failed', 'lockedOut'].includes(statusOf(q.id));
    // done ones are hidden unless asked for
    const list = data.quests.filter(q => q.highlights.length && (prefs.rewardsAll || open(q)))
      .sort((a, b) => b.rewardScore - a.rewardScore || a.name.localeCompare(b.name));
    const chip = profile ? `<button class="chip${prefs.rewardsAll ? '' : ' on'}" data-rewardsall="1">Hide completed</button>` : '';
    return `<div class="page-head"><div class="t"><h2>Great rewards</h2>
        <p>Quests whose rewards are worth going out of your way for: a big item case, a new trader, stash rows,
          the chance to buy something valuable, or a large pile of loot. Worked out from this server's rewards and prices.</p></div></div>
      ${profile ? `<div class="toolbar">${chip}</div>` : ''}
      <div class="list">${list.map(q => row(q, esc(traderName(q.trader)), '', rewardChips(q))).join('')}</div>`;
  }

  // ---------- an item's page ----------

  // Like a wiki page for one item, from this server: where it spawns and how likely, who sells it (and whether
  // you can buy it yet), what crafts it, which quests give it or want it, and what the hideout needs it for.
  function itemPage(id) {
    const item = itemCache.get(id);
    if (!item) {
      if (!itemCache.has(id)) {
        itemCache.set(id, null);
        getJson('api/item?id=' + encodeURIComponent(id))
          .then(found => { itemCache.set(id, found); Object.assign(data.items, found.items); })
          .catch(e => itemCache.set(id, { error: true, missing: String(e.message).startsWith('404') }))
          .finally(() => { if (view.page === 'item' && view.id === id) render(); });
      }
      return '<div class="loading">Looking the item up…</div>';
    }
    if (item.error && !item.missing) {
      // the server was restarting or unreachable: ask again next time the page is opened
      itemCache.delete(id);
      return `${crumbs(['items', 'Items'], [null, id])}<div class="intro red">Couldn't reach the server to look this item up. Is it running? Reload the page to try again.</div>`;
    }
    if (item.error) return `${crumbs(['items', 'Items'], [null, id])}<div class="intro red">This server has no item ${esc(id)}.</div>`;
    if (item.kind) return kindPage(item);

    const best = item.loot[0];
    const total = best?.total || 0;
    const cash = item.buy.filter(o => o.currency);
    const openOffers = item.buy.filter(offerOpen);

    // the best ways to get it, for this profile when one is picked
    const ways = [];
    // a quest item: the quests it's for, linked; it only turns up while you're on one
    const forQuests = [...new Set(item.usedIn.quests.map(u => u.quest))];
    if (item.questItem) ways.push({ icon: '', text: forQuests.length ? `A quest item for ${forQuests.map(link).join(', ')}: it's there while you're on the quest.` : 'A quest item: it turns up while you do the quest that asks for it.' });
    const buyNow = openOffers.filter(o => o.currency).sort((a, b) => a.price - b.price)[0];
    if (buyNow) ways.push({ icon: '', text: `Buy from ${offerText(buyNow)}${buyNow.limit ? ` <span class="sub">(${buyNow.limit} per restock)</span>` : ''}` });
    const barterNow = openOffers.find(o => !o.currency);
    if (barterNow && !buyNow) ways.push({ icon: '', text: `Trade at ${offerText(barterNow)}` });
    if (best) {
      ways.push({ icon: '', text: `Look in: ${lootLine(item.loot)}` });
    }
    if (item.carriedBy?.length) {
      // each bot a link to its page (when the bot list knows the name); the rest a click away
      const bots = wiki('bots', 'api/bots');
      const botLink = name => {
        const bot = Array.isArray(bots) ? bots.find(b => b.name.toLowerCase() === name.toLowerCase()) : null;
        return bot ? `<a class="il" data-page="bot" data-id="${esc(bot.id)}">${esc(name)}</a>` : esc(name);
      };
      const who = item.carriedBy.slice(0, 8).map(botLink).join(', ') + (item.carriedBy.length > 8
        ? ` <a class="il" data-reveal>and ${item.carriedBy.length - 8} more</a><span hidden>, ${item.carriedBy.slice(8).map(botLink).join(', ')}</span>` : '');
      ways.push({ icon: '', text: `Spawns on: ${who}` });
    }
    if (item.airdrops?.length && !item.questItem) {
      ways.push({ icon: '', text: `Can come in airdrops <span class="sub">(${item.airdrops.map(esc).join(', ')})</span>` });
    }
    if (!buyNow && !barterNow && item.buy.length) {
      const next = item.buy[0];
      // what's still in the way for this character: the loyalty level, the quest, or both
      const have = +(profile?.traders?.[next.trader]?.loyalty ?? 0);
      const blocks = profile ? [have < next.level && `needs ${esc(traderName(next.trader))} LL${next.level}, you're LL${have || 1}`,
        next.quest && statusOf(next.quest) !== 'done' && `unlocks with ${link(next.quest)}`].filter(Boolean) : [];
      ways.push({ icon: '', text: `Sold by ${offerText(next)}${blocks.length ? ` <span class="locked-note">Locked: ${blocks.join(' and ')}</span>` : profile ? '' : ' <span class="sub">(locked until you reach it)</span>'}` });
    }
    // a quest item isn't really traded: a flea price for it means nothing
    const quote = item.flea === false || item.questItem ? null : fleaQuote(id);
    if (quote?.price > 0) {
      ways.push({ icon: '', text: `On the flea market for about <b>${money(quote.price, ROUBLES)}</b>${quote.stock ? ' <span class="sub">(stock build)</span>' : ''}` });
    }
    if (item.craft.length) ways.push({ icon: '', text: `Craft at ${esc(item.craft[0].areaName || 'the hideout')} level ${item.craft[0].level}` });
    const reward = item.fromQuests.find(f => f.kind === 'Item');
    if (reward) ways.push({ icon: '', text: `Reward from ${link(reward.quest)}` });

    const firstSpot = item.loot.find(spot => spot.location);
    if (firstSpot) openFirstMap(id + '|' + firstSpot.location);
    const lootRowList = item.loot.map(spot => `<div class="loot-row">
        <div class="map">${esc(spot.map)}</div>
        <div class="lbar"><span style="width:${Math.max(3, Math.round(spot.total * 100 / total))}%"></span></div>
        <div class="odds">${chance(spot.total)}</div>
        <div class="where">${byName(spot.containers).map(c => `${boxLink(c.ids, c.name)} <span class="sub">${containerOdds(c)}</span>`).join(' · ')}${spot.points && spot.loose >= 0.001 ? `${spot.containers.length ? ' · ' : ''}loose, ${spot.points} spot${spot.points === 1 ? '' : 's'} <span class="sub">${chance(spot.loose)}</span>` : ''}
</div>
        ${spot.location ? `<div class="lm-row"><button class="lm-btn" data-lootmap="${esc(id + '|' + spot.location)}">${lootMapOpen.has(id + '|' + spot.location) ? 'Hide map' : 'Show on map'}</button></div>` : ''}
      </div>${lootMapOpen.has(id + '|' + spot.location) ? `<div class="lootmap" data-lm="${esc(id + '|' + spot.location)}"></div>` : ''}`);
    // the four likeliest maps, the rest a click away
    const lootRows = lootRowList.slice(0, 4).join('') + (lootRowList.length > 4 ? `<details class="more-maps"><summary>Show all ${lootRowList.length} maps</summary>${lootRowList.slice(4).join('')}</details>` : '');

    const offers = item.buy.map(o => {
      const ok = offerOpen(o);
      const price = o.currency ? `<b>${money(o.price, o.currency)}</b>` : o.barter.map(b => `<span class="barter">${icon(b.id, 'sm')}${b.count} × ${itemLink(b.id)}</span>`).join(' + ');
      return `<div class="offer${profile ? (ok ? ' ok' : ' no') : ''}">${avatar(o.trader, 'mid')}
        <div class="body"><div><b>${esc(traderName(o.trader))}</b> <span class="sub">LL${o.level}</span>${profile ? (ok ? ' <span class="status available">You can buy</span>' : ' <span class="status locked">Not yet</span>') : ''}</div>
          <div>${price}</div>
          ${o.quest ? `<div class="sub">${o.questState === 'started' ? 'Once you start' : o.questState === 'fail' ? 'If you fail' : 'After you finish'} ${link(o.quest)}</div>` : ''}
          ${o.limit ? `<div class="sub">${o.limit} per restock</div>` : ''}</div></div>`;
    }).join('');

    // each craft boxed like a trader's offer: where and how long, what goes in (and how many you have), and whether
    // the picked character could start it now, with the way to the hideout's own Craft button when they can
    const crafts = item.craft.map(c => {
      const owned = i => profile?.owned?.[i.id]?.[0] || 0;
      const short = profile ? c.inputs.filter(i => owned(i) < i.count) : [];
      const level = profile ? +(profile.hideout?.[c.area] ?? 0) : 0;
      const why = !profile ? [] : [
        level < c.level && `Needs ${esc(c.areaName || 'the area')} level ${c.level}, yours is ${level}`,
        c.quest && statusOf(c.quest) !== 'done' && `Needs ${link(c.quest)} done first`,
        short.length && `Short of ${short.length === c.inputs.length ? 'every part' : `${short.length} of the ${c.inputs.length} parts`}`
      ].filter(Boolean);
      const ok = profile && !why.length;
      const time = c.hours >= 1 ? `${c.hours} h` : `${Math.round(c.hours * 60)} min`;
      return `<div class="offer craft-offer${profile ? (ok ? ' ok' : ' no') : ''}">${icon(id)}
        <div class="body"><div><b><a data-page="hideout" data-id="${esc(String(c.area))}">${esc(c.areaName || 'Hideout')}</a></b> <span class="sub">level ${c.level} · ${time}${c.count > 1 ? ` · makes ${c.count}` : ''}</span>${profile ? (ok ? ' <span class="status available">You can craft</span>' : ' <span class="status locked">Not yet</span>') : ''}</div>
          <div class="inputs">${c.inputs.map(i => `<span class="barter">${icon(i.id, 'sm')}${i.count} × ${itemLink(i.id)}${i.tool ? ' <span class="sub">(tool)</span>' : ''}${profile ? `<span class="have ${owned(i) >= i.count ? 'all' : owned(i) ? 'some' : 'none'}" title="In your stash">${owned(i)}</span>` : ''}</span>`).join('')}</div>
          ${c.quest && !(profile && statusOf(c.quest) !== 'done') ? `<div class="sub">Unlocked by ${link(c.quest)}</div>` : ''}
          ${why.map(w => `<div class="sub">${w}</div>`).join('')}</div>
        ${ok ? `<a class="btn craft-go" data-page="hideout" data-id="${esc(String(c.area))}">Craft in ${esc(c.areaName || 'the hideout')}</a>` : ''}</div>`;
    }).join('');

    const kindWord = { HandoverItem: 'Hand over', FindItem: 'Find', LeaveItemAtLocation: 'Place', PlaceBeacon: 'Place', SellItemToTrader: 'Sell', WeaponAssembly: 'Build', key: 'Key needed', wear: 'Wear', weapon: 'Use', mods: 'Weapon mod' };
    const hideout = item.usedIn.hideout.map(h => `<div class="need"><span class="k">${esc(h.areaName || 'Area ' + h.area)}</span><span>level ${h.level}: ${h.count}${h.fir ? ' found in raid' : ''}</span></div>`).join('');
    const madeInto = item.usedIn.crafts.map(c => `<div class="barter-row">${icon(c.output, 'sm')}${itemLink(c.output)} <span class="sub">${c.need} at ${esc(c.areaName || 'hideout')} ${c.level}${c.tool ? ', as a tool' : ''}</span></div>`).join('');
    const tradedFor = item.usedIn.barters.map(b => `<div class="barter-row">${icon(b.output, 'sm')}${itemLink(b.output)} <span class="sub">${b.need} at ${esc(traderName(b.trader))} LL${b.level}</span></div>`).join('');

    // what the quests ask for in all: items handed over, found or placed, and how many of those must be found in raid
    const counted = item.usedIn.quests.filter(u => ['HandoverItem', 'FindItem', 'LeaveItemAtLocation', 'PlaceBeacon'].includes(u.kind));
    const questNeed = { total: counted.reduce((n, u) => n + (u.count || 1), 0), fir: counted.filter(u => u.fir).reduce((n, u) => n + (u.count || 1), 0), quests: new Set(counted.map(u => u.quest)).size };
    const badges = [
      // nothing on the maps: say where it does come from, bots or airdrops, rather than "not found"
      item.lootReady && (total > 0 ? `<span class="tag rarity r${['Common', 'Uncommon', 'Rare', 'Very rare'].indexOf(rarityWord(total))}">${rarityWord(total)}</span>`
        : `<span class="tag rarity r-1">${item.carriedBy?.length && item.airdrops?.length ? 'Only on bots and in airdrops' : item.carriedBy?.length ? 'Only on bots'
          : item.airdrops?.length ? 'Only in airdrops' : 'No map spawns'}</span>`),
      item.modded && `<span class="tag mod">Added by ${esc(itemMod(id, item) || 'a mod')}</span>`,
      item.unused && '<span class="tag lockout" title="No loot, trader, craft, bot or quest on this server gives it. The flea can still list it if a player or mod does.">Nothing here gives it</span>',
      item.questItem && '<span class="tag key">Quest item</span>',
      item.isCase && '<span class="tag reward">Case</span>',
      item.flea === false && '<span class="tag lockout">Not on the flea</span>',
      questNeed.quests && `<span class="tag keep" title="Quests on this server ask for it, so don't sell it">Required a total of ${number(questNeed.total)} in ${questNeed.quests} quest${questNeed.quests === 1 ? '' : 's'}${questNeed.fir ? `, ${questNeed.fir === questNeed.total ? 'all' : number(questNeed.fir)} of which need${questNeed.fir === 1 ? 's' : ''} to be found in raid` : ''}</span>`,
      item.usedIn.hideout.length && `<span class="tag keep" title="Hideout upgrades on this server ask for it">Needed for ${item.usedIn.hideout.length} hideout upgrade${item.usedIn.hideout.length === 1 ? '' : 's'}</span>`,
      byId.get(COLLECTOR)?.objectives.some(o => o.items.includes(id)) && '<span class="tag collector" title="Collector, the Kappa quest, asks for it">Required for Collector</span>'
    ].filter(Boolean).join('');

    // what it opens, for a key
    if (item.opens?.length) openFirstMap(`door:${item.id}|${item.opens[0].id}`);
    const opensCard = item.opens?.length ? card('What it opens', `${item.opens.map(m => `<div class="loot-row">
        <div class="map"><a data-page="map" data-id="${esc(m.id)}">${esc(m.name)}</a></div>
        <div class="odds">${m.locks === 1 ? '1 lock' : `${m.locks} locks`}</div>
        <div class="lm-row"><button class="lm-btn" data-lootmap="${esc(`door:${item.id}|${m.id}`)}">${lootMapOpen.has(`door:${item.id}|${m.id}`) ? 'Hide map' : 'Show on map'}</button></div></div>
        ${lootMapOpen.has(`door:${item.id}|${m.id}`) ? `<div class="lootmap" data-lm="${esc(`door:${item.id}|${m.id}`)}"></div>` : ''}`).join('')}
        <div class="src">Where the lock stands, from the game's map files.</div>`, item.opens.length) : '';
    const spawnCard = card('Where it spawns', item.lootReady ? (lootRows || `<p class="none">No loose or container spawns on this server.${item.carriedBy?.length || item.airdrops?.length ? ' Bots or airdrops can still have it: see above.' : ''}</p>`) + (lootRows ? '<div class="src">From this server\'s loot tables, mods and loot multipliers included. “1 in 220” is the chance one container of that kind holds it. “Map-wide” counts every container and spot on the map, more than one raid can search. Averages, not promises.</div>' : '') : '<p class="none">The server is still working out where items spawn. Try again in a minute.</p>', item.loot.length ? `${item.loot.length} map${item.loot.length === 1 ? '' : 's'}` : '');
    // a part or round: the guns and parts it goes on, the first few shown and the rest a click away
    const fitList = (ids, label) => {
      if (!ids?.length) return '';
      const chip = i => `<span class="fit" data-item="${esc(i)}">${icon(i, 'sm')}<span>${esc(itemName(i))}</span></span>`;
      return `<div class="sub" style="margin:6px 0">${label} <span class="n">${ids.length}</span></div><div class="fits">${ids.slice(0, 18).map(chip).join('')}</div>
        ${ids.length > 18 ? `<details class="fits-more"><summary>${ids.length - 18} more</summary><div class="fits">${ids.slice(18).map(chip).join('')}</div></details>` : ''}`;
    };
    const fitsCard = item.fitsOn ? card(item.stats?.some(g => g.title === 'Ammo') ? 'Fires from' : 'Fits on',
      fitList(item.fitsOn.guns, 'Guns') + fitList(item.fitsOn.parts, item.stats?.some(g => g.title === 'Ammo') ? 'Magazines and parts' : 'Parts')
      + '<div class="src">From the slots of every gun and part on this server, mods included.</div>', (item.fitsOn.guns?.length || 0) + (item.fitsOn.parts?.length || 0)) : '';
    // every quest that wants it or gives it, in one table; with a character picked, grouped by where they stand
    const QUEST_GROUPS = [['Doing now', ['active', 'ready']], ['Can start', ['available']], ['Locked', ['locked', 'lockedOut', 'hidden']], ['Done', ['done', 'failed']]];
    const questRows = [
      ...item.usedIn.quests.map(u => ({ id: u.quest, what: `${kindWord[u.kind] || esc(u.kind)}${u.count ? ` ${u.count}` : ''}${u.fir ? ', found in raid' : ''}` })),
      ...item.fromQuests.map(f => ({ id: f.quest, what: f.kind === 'Item' ? `Gives ${f.count > 1 ? f.count : 'one'} as a reward` : f.kind === 'AssortmentUnlock' ? 'Unlocks buying it' : 'Unlocks crafting it' }))
    ].filter(r => byId.has(r.id));
    const questRow = r => { const q = byId.get(r.id), st = statusOf(r.id);
      return `<tr class="q-${st || 'none'}"><td>${link(r.id)}</td><td class="sub">${esc(traderName(q.trader))}</td><td>${r.what}</td>${profile ? `<td class="num"><span class="status ${st}">${STATUS_TEXT[st] || ''}</span></td>` : ''}</tr>`; };
    const questBody = profile
      ? QUEST_GROUPS.map(([g, sts]) => { const list = questRows.filter(r => sts.includes(statusOf(r.id))); return list.length ? `<tr class="med-group"><td colspan="4">${g} <span class="n">${list.length}</span></td></tr>${list.map(questRow).join('')}` : ''; }).join('')
      : questRows.map(questRow).join('');
    const questCard = questRows.length ? card('Quests', `<table class="wt quest-uses"><thead><tr><th>Quest</th><th>Trader</th><th>For this item</th>${profile ? '<th class="num">Status</th>' : ''}</tr></thead><tbody>${questBody}</tbody></table>`, questRows.length) : '';
    const useCards = [
      fitsCard,
      opensCard
    ].filter(Boolean);
    const getCards = [
      offers ? card('Traders', `<div class="offers">${offers}</div>`, item.buy.length) : '',
      questCard,
      madeInto || tradedFor ? card('Used to make', `${madeInto}${tradedFor ? `<div class="sub" style="margin:8px 0 4px">Traded for</div>${tradedFor}` : ''}`) : '',
      crafts ? card('Crafted in the hideout', `<div class="offers">${crafts}</div>`, item.craft.length) : '',
      hideout ? card('Hideout upgrades', hideout, item.usedIn.hideout.length) : '',
      spawnCard].filter(Boolean);
    // what it's for, in a few lines: the quests and upgrades that want it, what it goes into, what it opens or fits
    const more = (list, n, fmt) => list.slice(0, n).map(fmt).join(', ') + (list.length > n ? ` and ${list.length - n} more` : '');
    const outputs = [...new Set(item.usedIn.crafts.map(c => c.output))].filter(o => o !== id);
    const uses = [
      item.usedIn.quests.length && { icon: '', text: `Quests: ${more(item.usedIn.quests, 3, u => `${link(u.quest)} <span class="sub">(${kindWord[u.kind] || esc(u.kind)}${u.count ? ` ${u.count}` : ''}${u.fir ? ', found in raid' : ''})</span>`)}` },
      item.usedIn.hideout.length && { icon: '', text: `Hideout: ${more(item.usedIn.hideout, 3, h => `<a data-page="hideout" data-id="${h.area}">${esc(h.areaName || 'Area ' + h.area)}</a> level ${h.level} <span class="sub">(${h.count}${h.fir ? ', found in raid' : ''})</span>`)}` },
      outputs.length && { icon: '', text: `Goes into ${more(outputs, 3, o => itemLink(o))}` },
      item.usedIn.crafts.some(c => c.output === id) && { icon: '', text: 'Can be used up and refilled with a hideout craft' },
      item.usedIn.barters.length && { icon: '', text: `Traded for ${more(item.usedIn.barters, 3, b => `${itemLink(b.output)} <span class="sub">(${esc(traderName(b.trader))} LL${b.level})</span>`)}` },
      item.opens?.length && { icon: '', text: `Opens ${item.opens.reduce((n, m) => n + m.locks, 0)} lock${item.opens.reduce((n, m) => n + m.locks, 0) === 1 ? '' : 's'} on ${more(item.opens, 3, m => `<a data-page="map" data-id="${esc(m.id)}">${esc(m.name)}</a>`)}` },
      item.fitsOn && { icon: '', text: `Fits ${item.fitsOn.guns?.length ? `${item.fitsOn.guns.length} gun${item.fitsOn.guns.length === 1 ? '' : 's'}` : ''}${item.fitsOn.guns?.length && item.fitsOn.parts?.length ? ' and ' : ''}${item.fitsOn.parts?.length ? `${item.fitsOn.parts.length} part${item.fitsOn.parts.length === 1 ? '' : 's'}` : ''}` }
    ].filter(Boolean);
    const line = w => `<div class="way"><span class="i">${w.icon}</span><span>${w.text}</span></div>`;
    const glance = `<div class="glance">
      <div class="glance-col"><h3>Getting it</h3>${ways.length ? ways.map(line).join('') : item.startKit ? '<p>You start with one on some game editions. Nothing else on this server gives it.</p>' : '<p class="none">No trader sells it, nothing crafts it, and no loot spot, bot or airdrop on this server has it.</p>'}</div>
      <div class="glance-col"><h3>What it's for</h3>${uses.length ? uses.map(line).join('') : '<p class="none">No quest, hideout upgrade, craft or trade on this server uses it. Sell it, or keep it for a barter.</p>'}</div></div>`;

    // what it does: each group of figures as a strip of small tiles
    // a stim's side effects get their own line under what it does for you
    const statRow = rows => `<div class="st-row">${rows.map(r =>
      r.value === 'yes' ? `<div class="st flag ${r.tone || ''}"><span class="st-v">${esc(r.label)}</span></div>`
        : `<div class="st ${r.tone || ''}"><span class="st-l">${esc(r.label)}</span><span class="st-v">${esc(r.value)}</span></div>`).join('')}</div>`;
    const arm = item.armor && (item.armor.row || item.armor.self) ? item.armor : null;
    const stats = (item.stats || []).filter(g => g.rows?.length && !(arm && (g.title === 'While worn' || g.title === 'Protection')) && !(item.weapon && g.title === 'Weapon') && !(item.ammo && g.title === 'Ammo') && !(item.med && (g.title === 'When used' || g.title === 'Effects')) && !(item.container && g.title === 'Storage')).map(g => {
      const bad = g.rows.filter(r => r.tone === 'bad');
      const body = g.title === 'Effects' && bad.length && bad.length < g.rows.length
        ? `${statRow(g.rows.filter(r => r.tone !== 'bad'))}<div class="st-title st-side">Side effects</div>${statRow(bad)}` : statRow(g.rows);
      return `<div class="st-group${g.title === 'Effects' ? ' wide' : ''}"><div class="st-title">${esc(g.title)}</div>${body}</div>`;
    }).join('');

    return `${crumbs(['items', 'Items'], item.categoryId ? ['items', item.category, item.categoryId] : [null, item.category || 'Item'], [null, item.name])}
      ${item.changed?.length ? `<div class="note mod">A mod on this server changes this item from the one SPT ships: ${item.changed.slice(0, 6).map(c => esc(human(c).toLowerCase())).join(', ')}${item.changed.length > 6 ? ` and ${item.changed.length - 6} more` : ''}.</div>` : ''}
      <div class="article item-top"><div class="col">
      <div class="ihead item-head"><div class="pic">${pic(id, item.shortName)}</div><div class="ih-main">
        <h2>${esc(item.name)}</h2>
        <div class="s">${[item.shortName, item.category].filter(Boolean).map(esc).join(' · ')}</div>
        <div class="tags">${badges}</div>
        ${wishControl(id)}
        ${item.description ? `<p class="ih-desc">${esc(item.description)}</p>` : ''}
        ${arm ? armorLead(arm) : ''}
        ${item.weapon ? weaponLead(item.weapon) : ''}
        ${item.ammo ? ammoLead(item.ammo) : ''}
        ${item.med ? medLead(item.med) : ''}
        ${item.container ? containerLead(item.container, item) : ''}
        ${item.unpacks ? `<p class="ih-unpacks">Opens into <b>${number(item.unpacks.count)} ×</b> ${icon(item.unpacks.id, 'sm')}${itemLink(item.unpacks.id)}</p>` : ''}
      </div></div>
      ${stats ? `<div class="card stats-card">${stats}</div>` : ''}
      ${insideCard(item)}
      ${arm ? armorCards(arm) : ''}
      ${item.weapon ? weaponCards(item.weapon) : ''}
      ${item.ammo ? ammoCards(item.ammo) : ''}
      ${item.med ? medCards(item.med) : ''}
      ${glance}
        ${getCards.join('')}${useCards.join('')}
        ${item.med ? medKin(item.med) : ''}
        ${item.container ? containerKin(item.container) : ''}
      </div><div class="col">
        <div class="card infobox"><dl>
          <dt>Size</dt><dd>${item.width ?? 1} × ${item.height ?? 1}</dd>
          ${item.weight != null ? `<dt>Weight</dt><dd>${item.weight} kg</dd>` : ''}
          ${item.stack > 1 ? `<dt>Stack</dt><dd>${number(item.stack)}</dd>` : ''}
          ${item.uses ? `<dt>Uses</dt><dd>${item.uses}</dd>` : ''}
          ${item.price ? `<dt>Value</dt><dd>${money(item.price, ROUBLES)}<div class="sub">server's base price</div></dd>` : ''}
          ${item.questItem ? '' : `<dt>Flea</dt><dd>${item.flea === false ? 'Can\'t be sold' : 'Can be sold'}</dd>`}
          <dt>Item id</dt><dd class="copy">${esc(id)}</dd>
        </dl></div>
        ${arm ? armorBox(arm) : ''}
        ${item.weapon ? weaponBox(item.weapon, (item.stats || []).find(g => g.title === 'Weapon')?.rows) : ''}
        ${item.ammo ? ammoBox(item.ammo) : ''}
        ${item.med ? medBox(item.med) : ''}
        ${item.container ? containerBox(item.container, item) : ''}
        ${keepCard(id, item) || haveCard(id)}
        ${item.questItem ? "" : fleaCard(id, item)}
        ${sellCard(item)}
        ${mapsCard(item)}
      </div></div>`;
  }

  // The side column's smaller boxes, laid out like the facts at the top.
  const sideBox = (title, rows, foot = '') => rows.length ? `<div class="card infobox"><h3>${title}</h3><dl>${rows.map(([k, v]) => `<dt>${k}</dt><dd>${v}</dd>`).join('')}</dl>${foot}</div>` : '';

  // how many the picked character has
  function haveCard(id) {
    const have = profile?.owned?.[id];
    if (!profile) return '';
    return sideBox('In your stash', [['You have', have ? number(have[0]) : 'None'], ...(have?.[1] ? [['Found in raid', number(have[1])]] : [])]);
  }

  // who buys it and for how much, at their first loyalty level, and what that is per slot of space
  function sellCard(item) {
    const list = item.sellTo || [];
    if (!list.length || item.questItem) return '';
    const slots = (item.width || 1) * (item.height || 1);
    const rows = list.slice(0, 6).map(t => [esc(t.name), money(t.price, t.currency)]);
    if (slots > 1) rows.push(['Per slot', `${money(Math.floor(list[0].roubles / slots), ROUBLES)}<div class="sub">at ${esc(list[0].name)}, ${slots} slots</div>`]);
    return sideBox('Traders pay', rows, '<p class="sub">At loyalty level 1, for the item on its own. Higher levels pay a little more.</p>');
  }

  // the maps it turns up on most
  function mapsCard(item) {
    const best = [...(item.loot || [])].filter(l => l.total > 0).sort((a, b) => b.total - a.total).slice(0, 4);
    return sideBox('Best maps for it', best.map(l => [esc(l.map || l.location), `~${l.total >= 10 ? Math.round(l.total) : l.total.toFixed(1)} a raid`]));
  }

  // How many of an item the quests and hideout still want, what of that has to be found in raid, and what you've got.
  function keepCard(id, item) {
    const counted = ['HandoverItem', 'FindItem', 'LeaveItemAtLocation', 'PlaceBeacon'];
    const open = item.usedIn.quests.filter(u => counted.includes(u.kind) && !isDone(u.quest));
    const quests = open.reduce((n, u) => n + (u.count || 1), 0), questFir = open.filter(u => u.fir).reduce((n, u) => n + (u.count || 1), 0);
    const hideout = item.usedIn.hideout.reduce((n, h) => n + (h.count || 1), 0), hideoutFir = item.usedIn.hideout.filter(h => h.fir).reduce((n, h) => n + (h.count || 1), 0);
    if (!quests && !hideout) return '';
    const fir = questFir + hideoutFir, need = quests + hideout;
    const have = profile?.owned?.[id];
    // laid out like the facts above it
    const row = (k, v) => `<dt>${k}</dt><dd>${v}</dd>`;
    return `<div class="card infobox keep-box"><h3>Worth keeping</h3><dl>
      ${quests ? row(`For quests${open.length > 1 ? ` (${open.length})` : ''}`, number(quests)) : ''}
      ${hideout ? row(`For hideout (${item.usedIn.hideout.length})`, number(hideout)) : ''}
      ${row('Found in raid', fir === need ? 'All of them' : fir ? number(fir) : 'None')}
      ${have ? row('You have', `${number(have[0])}${have[1] ? ` <span class="sub">${number(have[1])} found in raid</span>` : ''}`) : ''}
      ${have ? row('You still need', `<b>${number(stillNeed(need, fir, have))}</b>`) : ''}</dl></div>`;
  }

  // found-in-raid needs can only be met by found-in-raid items; anything left over counts towards the rest
  function stillNeed(need, fir, have) {
    const firUsed = Math.min(fir, have[1] || 0);
    return Math.max(0, fir - firUsed) + Math.max(0, need - fir - (have[0] - firUsed));
  }

  // A kind of item, which the game's rules can name instead of one item ("Jewelry"): what's in it on this server.
  function kindPage(kind) {
    const name = human(kind.name);
    return `${crumbs(['items', 'Items'], [null, name])}
      <div class="page-head"><div class="t"><h2>${esc(name)}</h2><p>This is a kind of item rather than one item. ${number(kind.items.length)} item${kind.items.length === 1 ? '' : 's'} on this server count as ${esc(name.toLowerCase())}.</p></div></div>
      <table class="wt items-list"><tbody>${kind.items.map(i => `<tr data-item="${i.id}"><td class="pic-cell"><span class="pic">${pic(i.id)}</span></td><td><a>${esc(i.name)}</a>${modMark(i)}</td><td class="num">${i.price ? money(i.price, ROUBLES) : ''}</td></tr>`).join('')}</tbody></table>`;
  }

  // A container's page: its inside drawn out, what goes in it, and how it compares with the ones around its size.
  // For testing: one group for each set of sections an item page can get, each with an example from every
  // handbook category that has it. Worked out by the server from its own items, so mods add their own groups.
  const KIND_SECTIONS = { armor: 'Armour', grids: 'Inside (room for items)', gridLayout: 'Inside, pockets placed as in game', med: 'What it does (meds, food, drink)',
    weapon: 'Weapon', ammo: 'Ammo', unpacks: 'Opens into (packs)', fitsOn: 'Fits on (parts)', opens: 'Opens doors (keys)', uses: 'Limited uses',
    questItem: 'Quest item', isCase: 'Case' };
  function kindsPage() {
    const head = `${crumbs([null, 'Test items'])}<h2>Test items</h2><p class="intro">One line for each part an item page can have, with up to three items that have it. Open them after changing the item page.</p>`;
    const kinds = wiki('kinds', 'api/kinds');
    if (!kinds) return `${head}<div class="loading">Sorting every item on the server…</div>`;
    if (kinds.error) return `${head}<div class="intro red">Couldn't load the list from the server.</div>`;
    return head + `<table class="wt kinds"><thead><tr><th>Part of the page</th><th class="num">Items</th><th>Try</th></tr></thead><tbody>${kinds.map(k => `<tr>
      <td>${k.section ? esc(KIND_SECTIONS[k.section] || k.section) : 'Plain page, none of the others'}</td><td class="num">${number(k.count)}</td>
      <td>${k.examples.map(e => `<span class="kind-ex">${icon(e.id, 'sm')}<span>${itemLink(e.id, e.name)}<span class="s">${esc([e.category, e.modded ? 'from a mod' : ''].filter(Boolean).join(', '))}</span></span></span>`).join('')}</td></tr>`).join('')}</tbody></table>`;
  }

  function insideCard(item) {
    if (!item.grids?.length) return '';
    const c = item.container;
    // each part (pocket) drawn as one framed block of its own size; the sizes are listed under it, biggest first
    const sizes = new Map();
    for (const g of item.grids) { const k = `${g.w || 0}×${g.h || 0}`; sizes.set(k, (sizes.get(k) || 0) + 1); }
    const sizeList = [...sizes].sort((a, b) => { const n = s => s.split('×').reduce((x, y) => x * y, 1); return n(b[0]) - n(a[0]); })
      .map(([k, n]) => `${n > 1 ? `${number(n)} of ` : 'one '}${k}`).join(', ');
    // where the game puts each part, when the game plugin has measured the layout: placed as in game, in % so it shrinks to fit
    // a layout with fewer parts than the item: the game draws only the first ones, so only those are drawn here
    const placed = item.gridLayout?.length && item.gridLayout.length <= item.grids.length ? item.gridLayout : null;
    const unseen = placed ? item.grids.length - placed.length : 0;
    const boxW = placed ? Math.max(...placed.map(b => b[0] + b[2])) : 0, boxH = placed ? Math.max(...placed.map(b => b[1] + b[3])) : 0;
    // a big grid is drawn with smaller cells, so it fits beside what it takes
    const wide = Math.max(...item.grids.map(g => g.w || 1));
    const px = Math.max(16, Math.min(34, Math.floor(340 / wide)));
    const part = (g, style) => `<div class="cells" title="${g.w || 0}×${g.h || 0}" style="${style}">${'<i></i>'.repeat((g.w || 0) * (g.h || 0))}</div>`;
    const drawn = placed
      // the game's cells are 63 px
      ? `<div class="inside placed" style="width:${Math.round(boxW * 34 / 63)}px;aspect-ratio:${boxW}/${boxH}">${item.grids.slice(0, placed.length).map((g, i) => { const [x, y, w, h] = placed[i];
          return part(g, `left:${x / boxW * 100}%;top:${y / boxH * 100}%;width:${w / boxW * 100}%;height:${h / boxH * 100}%;grid-template-columns:repeat(${g.w || 1},1fr);grid-template-rows:repeat(${g.h || 1},1fr)`); }).join('')}</div>`
      : `<div class="inside" style="--cell:${px}px">${item.grids.map(g => part(g, `grid-template-columns:repeat(${g.w || 1},${px}px)`)).join('')}</div>`;
    const cells = c?.cells ?? item.grids.reduce((n, g) => n + (g.w || 0) * (g.h || 0), 0);
    const outside = (item.width || 1) * (item.height || 1);
    const inside = card('Inside', `${drawn}${unseen ? `<p class="sub">The game only shows ${number(placed.length)} of its ${number(item.grids.length)} parts: the layout the game draws it with has room for ${number(placed.length)}, so the other ${unseen > 1 ? `${number(unseen)} don't` : "one doesn't"} appear in game.</p>` : ''}${placed || item.grids.length < 2 ? '' : '<p class="sub">Parts in a row: the game plugin measures how the game lays them out the next time you sit in the main menu.</p>'}
        <p>${number(cells)} cells inside${item.grids.length > 1 ? ` in ${number(item.grids.length)} parts (${sizeList})` : ` (${sizeList})`}, and it takes up ${number(outside)} in your stash${cells > outside ? `, so you gain ${number(cells - outside)}` : cells < outside ? `, so you lose ${number(outside - cells)}` : ''}.</p>`);
    // a container that takes anything bar a few (a rig, a bag, a belt) gets no list: only one that picks what it takes does
    if (!c || c.anything) return inside;
    return `<div class="two"><div class="col">${inside}</div><div class="col">${containerTakes(c)}</div></div>`;
  }

  // what a container takes and what it won't: kinds by name, single items as links
  function containerTakes(c) {
    // kinds first, then single items, each under its own heading when there are both
    const list = l => { const both = l.kinds.length && l.items.length;
      return `${l.kinds.length ? `${both ? '<h4 class="cont-h">Kinds of item</h4>' : ''}<ul class="cont-list">${l.kinds.map(k => `<li>${esc(k.name)}</li>`).join('')}</ul>` : ''}${l.items.length ? `${both ? '<h4 class="cont-h">Single items</h4>' : ''}<ul class="cont-list">${l.items.map(x => `<li>${itemLink(x)}</li>`).join('')}</ul>` : ''}`; };
    const nTake = c.takes ? c.takes.kinds.length + c.takes.items.length : 0, nBar = c.bars.kinds.length + c.bars.items.length;
    const parts = [];
    parts.push(c.anything ? `<p>Anything${nBar ? ', bar these:' : '.'}</p>` : `<p>Only these${nBar ? ', and not the ones listed after' : ''}:</p>${list(c.takes)}`);
    if (nBar) parts.push(`${c.anything ? '' : '<p>Not these:</p>'}${list(c.bars)}`);
    return card('What it takes', `${parts.join('')}<div class="src">From this server's own filter for it, mods included.</div>`, nTake + nBar || null);
  }

  function containerLead(c, item) {
    const outside = (item.width || 1) * (item.height || 1);
    const nTake = c.takes ? c.takes.kinds.length + c.takes.items.length : 0, nBar = c.bars.kinds.length + c.bars.items.length;
    const bits = [`A ${esc((c.kind || 'container').toLowerCase())} with ${number(c.cells)} cells inside, taking up ${number(outside)} in your stash${c.cells > outside ? `: ${number(Math.round(c.cells / outside * 10) / 10)} cells for each one it takes` : ''}.`];
    bits.push(c.anything ? (nBar ? 'It takes most things.' : 'It takes anything.') : `It takes only ${number(nTake)} kind${nTake === 1 ? '' : 's'} and item${nTake === 1 ? '' : 's'}.`);
    const n = c.storedIn.reduce((k, g) => k + g.items.length, 0);
    bits.push(n ? `It fits in ${number(n)} other container${n === 1 ? '' : 's'}.` : 'No other container on this server takes it.');
    return `<p class="ih-lead">${bits.join(' ')}</p>`;
  }

  function containerBox(c, item) {
    const outside = (item.width || 1) * (item.height || 1);
    return sideBox('Container', [
      c.kind ? ['Type', esc(c.kind)] : null,
      ['Inside', `${number(c.cells)} cells${item.grids.length > 1 ? `<div class="sub">in ${number(item.grids.length)} parts</div>` : ''}`],
      ['Takes up', `${number(outside)} cells`],
      c.cells !== outside ? [c.cells > outside ? 'Gain' : 'Loss', `<span class="${c.cells > outside ? 'med-good' : 'med-bad'}">${number(Math.abs(c.cells - outside))} cells</span>`] : null,
      item.weight ? ['Cells per kg', number(Math.round(c.cells / item.weight * 10) / 10)] : null
    ].filter(Boolean));
  }

  // the containers it can itself go in, by kind, and every other container of its kind
  function containerKin(c) {
    const out = [];
    if (c.storedIn.length) {
      const n = c.storedIn.reduce((k, g) => k + g.items.length, 0);
      out.push(card('Fits in', `<div class="cont-groups">${c.storedIn.map(g => `<div><h4 class="cont-h">${esc(g.name)} <span class="n">${g.items.length}</span></h4>
        <ul class="cont-list">${g.items.map(x => `<li>${itemLink(x)}</li>`).join('')}</ul></div>`).join('')}</div>
        <div class="src">Every container a player can own on this server with room for it and a filter that lets it in, mods included.</div>`, n));
    }
    const sibs = c.siblings || [];
    if (sibs.length > 1) {
      out.push(card(`Other ${(c.kind || 'containers').toLowerCase()}${/s$/.test(c.kind || 's') ? '' : 's'}`, `<table class="wt"><thead><tr><th>Item</th><th class="num">Inside</th><th class="num">Parts</th><th class="num">Takes up</th><th class="num">Gain</th><th class="num">Weight</th></tr></thead><tbody>
        ${sibs.map(s => { const out = (s.width || 1) * (s.height || 1), gain = s.cells - out;
          return `<tr${s.self ? ' class="here"' : ''}><td class="with-icon">${icon(s.id, 'sm')}<a data-item="${esc(s.id)}">${esc(s.name)}</a>${s.mod != null ? ` <span class="sub">(added by ${esc(itemMod(s.id, s) || 'a mod')})</span>` : ''}</td>
          <td class="num">${number(s.cells)}</td><td class="num">${s.parts > 1 ? number(s.parts) : ''}</td><td class="num">${s.width || 1}×${s.height || 1}</td>
          <td class="num${gain > 0 ? ' med-good' : gain < 0 ? ' med-bad' : ''}">${gain ? rsigned(gain) : ''}</td><td class="num">${s.weight ? `${number(s.weight)} kg` : ''}</td></tr>`; }).join('')}</tbody></table>
        <div class="src">Every item of this kind on this server, mods included, most room first.</div>`, sibs.length));
    }
    return out.join('');
  }

  // ---------- wiki pages ----------

  // data for the wiki's other pages, fetched once and kept while the page is open
  const wikiCache = new Map();

  function wiki(key, url) {
    if (wikiCache.has(key)) return wikiCache.get(key);
    wikiCache.set(key, null);
    getJson(url).then(found => wikiCache.set(key, found)).catch(() => wikiCache.set(key, { error: true }))
      .finally(() => render());
    return null;
  }

  function crumbs(...parts) {
    return `<div class="crumbs" role="navigation" aria-label="Where you are"><button class="back-btn" data-back title="Back (Alt+←)">‹ Back</button><a data-page="overview" data-id="">Main page</a>${parts.map(([page, text, id]) =>
      ` <span>›</span> ${page ? `<a data-page="${page}" data-id="${esc(id || '')}">${esc(text)}</a>` : `<b>${esc(text)}</b>`}`).join('')}</div>`;
  }

  // Quests: the quest lists and every trader's quests.
  function questsPage() {
    const maps = wiki('maps', 'api/maps');
    const mapId = name => (Array.isArray(maps) ? maps : []).find(m => baseMap(m.name) === name)?.id;
    const byMap = new Map();
    for (const q of data.quests) {
      // "Any" and no map at all are one row
      const where = [...new Set(q.maps.map(m => /^any$/i.test(baseMap(m)) ? '' : baseMap(m)))];
      if (!where.length) where.push('');
      for (const m of where) { if (!byMap.has(m)) byMap.set(m, []); byMap.get(m).push(q); }
    }
    const counts = list => `<td class="num">${list.length}</td><td class="num">${list.filter(q => q.flags & KAPPA).length || ''}</td>
      <td class="num">${list.filter(q => q.flags & LIGHTKEEPER).length || ''}</td>${profile ? `<td class="num">${progress(list).done}</td>` : ''}`;
    const heads = first => `<thead><tr><th>${first}</th><th class="num">Quests</th><th class="num">Kappa</th><th class="num">Lightkeeper</th>${profile ? '<th class="num">Done</th>' : ''}</tr></thead>`;
    const mapRows = [...byMap.entries()].sort((x, y) => (x[0] === '') - (y[0] === '') || y[1].length - x[1].length).map(([m, list]) => {
      const id = m && mapId(m);
      return `<tr${id ? ` data-page="map" data-id="${esc(id)}"` : ''}><td>${!m ? 'Any map' : id ? `<a>${esc(m)}</a>` : esc(m)}</td>${counts(list)}</tr>`;
    }).join('');
    // by the level they open at, in steps of ten
    const bands = new Map();
    for (const q of data.quests) {
      const band = Math.floor((q.level || 1) / 10);
      if (!bands.has(band)) bands.set(band, []);
      bands.get(band).push(q);
    }
    const levelRows = [...bands.entries()].sort((x, y) => x[0] - y[0])
      .map(([b, list]) => `<tr><td>${b === 0 ? '1 to 9' : `${b * 10} to ${b * 10 + 9}`}</td>${counts(list)}</tr>`).join('');
    return `${crumbs([null, 'Quests'])}
      <div class="page-head"><div class="t"><h2>Quests</h2><p>${data.quests.length} quests from ${traders.length} traders${profile ? `, ${progress(data.quests).done} of them done by ${esc(profile.name)}` : ''}. A trader's page has their quest lines in order.</p></div></div>
      <div class="two">
        <div class="col">${card('By trader', tradersTable())}${card('By level', `<table class="wt">${heads('Opens at level')}<tbody>${levelRows}</tbody></table>`)}</div>
        <div class="col">${card('Quest lists', questListsTable())}${card('By map', `<table class="wt">${heads('Map')}<tbody>${mapRows}</tbody></table>`)}</div>
      </div>`;
  }


  // Items: browse by the game's own handbook categories, mods included.
  let catalogFilter = '';
  let catalogSort = 'name';
  let catalogModded = false;
  let catalogUnused = false;
  let catalogShown = 240;

  function catalogPage(category) {
    const whole = wiki('catalog', 'api/catalog');
    if (!whole) return `${crumbs([null, 'Items'])}<div class="loading">Loading every item…</div>`;
    if (whole.error) return `${crumbs([null, 'Items'])}<div class="intro red">Couldn't load the item list.</div>`;
    // armour's built-in inserts and items nothing gives stay out of the lists unless asked for
    const skipped = whole.items.filter(i => i.fixedPart || i.unused).length;
    const catalog = catalogUnused ? whole : { ...whole, items: whole.items.filter(i => !i.fixedPart && !i.unused) };
    const cats = new Map(catalog.categories.map(c => [c.id, c]));
    const kids = new Map();
    for (const c of catalog.categories) {
      if (!kids.has(c.parent || '')) kids.set(c.parent || '', []);
      kids.get(c.parent || '').push(c);
    }
    const under = new Map();
    const below = id => {
      if (under.has(id)) return under.get(id);
      const set = new Set([id]);
      for (const k of kids.get(id) || []) below(k.id).forEach(x => set.add(x));
      under.set(id, set);
      return set;
    };
    const countIn = id => { const set = below(id); return catalog.items.filter(i => set.has(i.category)).length; };
    const top = (kids.get('') || []).filter(c => countIn(c.id));
    const chosen = cats.get(category);
    // not a handbook category: one of the server's item-tree groups (armour plates), found from search
    const groups = !chosen && category ? wiki('catgroups', 'api/catalog/groups') : null;
    const group = Array.isArray(groups) ? groups.find(g => g.id === category) : null;
    const groupIds = group ? wiki(`catgroup|${group.id}`, `api/catalog/group?id=${encodeURIComponent(group.id)}`) : null;
    const inGroup = Array.isArray(groupIds) ? new Set(groupIds) : null;
    if (!chosen && category && (!groups || (group && !inGroup))) return `${crumbs([null, 'Items'])}<div class="loading">Loading the list…</div>`;
    const listed = chosen || group;
    const path = [];
    for (let at = chosen; at; at = cats.get(at.parent)) path.unshift(at);
    const text = catalogFilter.toLowerCase();
    let items = catalog.items.filter(i => (!chosen || below(chosen.id).has(i.category)) && (!group || inGroup.has(i.id))
      && (!text || i.name.toLowerCase().includes(text) || (i.shortName || '').toLowerCase().includes(text))
      && (!catalogModded || i.modded));
    // the figures this category's items carry, as columns (the server picks them from the items themselves)
    const stats = listed ? wiki(`catstats|${listed.id}`, `api/catalog/stats?category=${encodeURIComponent(listed.id)}`) : null;
    const cols = stats && !stats.error ? stats.columns : [];
    const cell = (i, c) => stats?.items?.[i.id]?.[c];
    if (catalogSort === 'price') items = [...items].sort((a, b) => b.price - a.price);
    if (catalogSort === 'price:a') items = [...items].sort((a, b) => a.price - b.price);
    const colSort = /^col:(\d+):([ad])$/.exec(catalogSort);
    if (colSort && cols[+colSort[1]]) {
      const c = +colSort[1], dir = colSort[2] === 'd' ? -1 : 1;
      // items without the figure go last either way
      items = [...items].sort((a, b) => {
        const x = cell(a, c), y = cell(b, c);
        if (!x || !y) return !x - !y;
        return (x[1] != null && y[1] != null ? x[1] - y[1] : String(x[0]).localeCompare(String(y[0]), undefined, { numeric: true })) * dir;
      });
    }
    // a column none of the items shown fills (only the hidden built-in inserts had it) is left out
    const shownCols = cols.map((c, k) => [c, k]).filter(([, k]) => items.some(i => cell(i, k)));
    const colHead = (c, k) => `<th class="sortable num${colSort && +colSort[1] === k ? ' sorted' : ''}" data-csort="${k}">${esc(c.label)}${colSort && +colSort[1] === k ? (colSort[2] === 'a' ? ' ▲' : ' ▼') : ''}</th>`;
    const subcats = chosen ? (kids.get(chosen.id) || []).filter(c => countIn(c.id)) : [];

    return `${crumbs(...(chosen ? [['items', 'Items'], ...path.slice(0, -1).map(c => ['items', c.name, c.id]), [null, chosen.name]] : group ? [['items', 'Items'], [null, group.name]] : [[null, 'Items']]))}
      <div class="page-head"><div class="t"><h2>${listed ? esc(listed.name) : 'Items'}</h2>
        <p>${number(items.length)} item${items.length === 1 ? '' : 's'}${listed ? '' : `, ${number(catalog.items.filter(i => i.modded).length)} of them added by mods`}.</p></div></div>
      ${listed || text || catalogModded ? '' : catalogFront(catalog, top, kids, below, countIn)}
      ${subcats.length ? `<ul class="idx">${subcats.map(c => `<li><a data-page="items" data-id="${c.id}">${esc(c.name)}</a> <span class="sub">${number(countIn(c.id))}</span></li>`).join('')}</ul>` : ''}
      <div class="toolbar">
        <input class="filter" id="catalogFilter" placeholder="Filter by name" value="${esc(catalogFilter)}">
        <button class="chip${catalogSort === 'name' ? ' on' : ''}" data-sort="name">By name</button>
        <button class="chip${catalogSort === 'price' ? ' on' : ''}" data-sort="price">Most valuable</button>
        <button class="chip${catalogModded ? ' on' : ''}" data-modded="1">Added by mods</button>
        ${skipped ? `<button class="chip${catalogUnused ? ' on' : ''}" data-unused="1" title="Armour's built-in inserts, and items no loot, trader, craft, bot or quest on this server gives">Show unused (${number(skipped)})</button>` : ''}
      </div>
      ${listed || text || catalogModded ? '<p class="sub">Click a heading to sort by it, again to reverse.</p>' : ''}
      ${listed || text || catalogModded ? `<div class="table-scroll"><table class="wt items-list"><thead><tr><th colspan="2">Item</th>${shownCols.map(([c, k]) => colHead(c, k)).join('')}<th class="sortable num${catalogSort.startsWith('price') ? ' sorted' : ''}" data-vsort="1">Value${catalogSort === 'price' ? ' ▼' : catalogSort === 'price:a' ? ' ▲' : ''}</th></tr></thead><tbody>${items.slice(0, catalogShown).map(i => `<tr data-item="${i.id}">
          <td class="pic-cell"><span class="pic">${pic(i.id, i.shortName)}</span></td>
          <td><a>${esc(i.name)}</a>${modMark(i)}</td>${shownCols.map(([, k]) => { const v = cell(i, k); return `<td class="num${v?.[2] ? ' ' + v[2] : ''}">${v ? esc(v[0]) : ''}</td>`; }).join('')}<td class="num">${i.price ? money(i.price, ROUBLES) : ''}</td></tr>`).join('')}</tbody></table></div>
        ${items.length > catalogShown ? `<button class="btn more-items" data-moreitems="1">Show ${Math.min(240, items.length - catalogShown)} more</button>` : ''}` : ''}`;
  }

  // The items front page: every category with what's in it, and the most valuable items on this server.
  function catalogFront(catalog, top, kids, below, countIn) {
    const rows = top.map(c => {
      const set = below(c.id);
      const mods = catalog.items.filter(i => i.modded && set.has(i.category)).length;
      const subs = (kids.get(c.id) || []).filter(k => countIn(k.id)).sort((x, y) => x.name.localeCompare(y.name));
      return `<tr data-page="items" data-id="${c.id}"><td class="cat-cell">${catIcon(c)}<a>${esc(c.name)}</a></td><td class="num">${number(countIn(c.id))}</td><td class="num">${mods ? number(mods) : ''}</td>
        <td class="sub">${subs.map(k => `<a data-page="items" data-id="${k.id}">${esc(k.name)}</a>`).join(', ')}</td></tr>`;
    }).join('');
    const dear = [...catalog.items].filter(i => i.price > 0).sort((x, y) => y.price - x.price).slice(0, 15);
    return `<div class="two wide-left">
      <div class="col">${card('Categories', `<table class="wt"><thead><tr><th>Category</th><th class="num">Items</th><th class="num">From mods</th><th>Includes</th></tr></thead><tbody>${rows}</tbody></table>`)}</div>
      <div class="col">${card('Most valuable', `<table class="wt items-list"><tbody>${dear.map(i => `<tr data-item="${i.id}">
          <td class="pic-cell"><span class="pic">${pic(i.id, i.shortName)}</span></td><td><a>${esc(i.name)}</a>${modMark(i)}</td>
          <td class="num">${money(i.price, ROUBLES)}</td></tr>`).join('')}</tbody></table>
        <p class="sub">Value: what the game's handbook says an item is worth, in roubles.</p>`)}</div>
    </div>`;
  }


  // Traders: everyone side by side, what they sell and offer, and what each loyalty level needs.
  function tradersPage() {
    const info = wiki('rules|traders', 'api/rules?page=traders');
    const head = `${crumbs([null, 'Traders'])}
      <div class="page-head"><div class="t"><h2>Traders</h2><p>Each trader's page has their stock, what their loyalty levels need and their quests.</p></div></div>`;
    if (!Array.isArray(info)) return `${head}${info?.error ? '<p class="hint">Couldn\'t load the traders\' details.</p>' + tradersTable(true) : '<div class="loading">Loading the traders…</div>'}`;
    const order = id => { const i = traders.indexOf(id); return i < 0 ? 99 : i; };
    // quest givers the server lists without a loyalty table (Lightkeeper) still get a row
    // a trader the server lists with nothing to do (no quests, stock or services) is left out
    const useful = t => t.offers || t.randomStock || t.insures || t.repairs || t.heals || data.quests.some(q => q.trader === t.id);
    const list = [...info.filter(useful), ...traders.filter(id => !info.some(t => t.id === id)).map(id => ({ id, name: traderName(id), levels: [], offers: 0 }))]
      .sort((x, y) => order(x.id) - order(y.id) || x.name.localeCompare(y.name));
    const face = t => data.traders[t.id] ? avatar(t.id, 'mid') : t.avatar ? `<img class="avatar mid" src="${esc(t.avatar)}" alt="" loading="lazy" onerror="this.remove()">` : '';
    // a restock time only means something for a trader with stock to restock
    const restock = r => !r ? '' : r.min === r.max ? duration(r.min) : `${duration(r.min)} to ${duration(r.max)}`;
    const services = t => [t.insures && rulesLink('insurance', 'Insurance'), t.repairs && rulesLink('repairs', 'Repairs'), t.heals && rulesLink('health', 'Healing')].filter(Boolean).join(', ');
    const most = Math.max(2, ...list.map(t => t.levels.length));
    const step = (t, l) => {
      const parts = [l.minLevel ? `Level ${l.minLevel}` : '', l.standing ? `${Math.round(l.standing * 100) / 100} standing` : '', l.sales ? `${money(l.sales, currencyId(t.currency))} spent` : ''].filter(Boolean);
      return parts.length ? `${parts[0]}${parts.length > 1 ? `<div class="sub">${parts.slice(1).join(', ')}</div>` : ''}` : '<span class="sub">Nothing</span>';
    };
    return `${head}
      <table class="wt"><thead><tr><th colspan="2">Trader</th><th class="num">Quests</th>${profile ? '<th class="num">Done</th>' : ''}<th class="num">Sells</th><th>Restocks every</th><th>Also does</th><th>Unlock</th></tr></thead><tbody>
        ${list.map(t => {
          const quests = data.quests.filter(q => q.trader === t.id);
          return `<tr data-page="trader" data-id="${t.id}"><td class="pic-cell">${face(t)}</td><td><a>${esc(t.name)}</a>${t.location ? `<div class="sub">${esc(t.location)}</div>` : ''}</td>
            <td class="num">${quests.length || ''}</td>${profile ? `<td class="num">${quests.length ? progress(quests).done : ''}</td>` : ''}
            <td class="num">${t.offers ? `${number(t.offers)}<div class="sub">${t.barters ? `${number(t.barters)} barters` : ''}${t.barters && t.questLocked ? ', ' : ''}${t.questLocked ? `${number(t.questLocked)} after quests` : ''}</div>` : ''}</td>
            <td>${t.offers || t.randomStock ? restock(t.restock) : ''}</td><td>${services(t)}</td><td class="sub">${data.traders[t.id] ? traderLockNote(t.id) : ''}</td></tr>`;
        }).join('')}</tbody></table>
      ${card('Loyalty levels', `<p class="sub">What each loyalty level needs: your character level, standing with the trader and money spent with them.${profile ? ' Your level with each is in bold.' : ''}</p>
        <table class="wt"><thead><tr><th>Trader</th>${Array.from({ length: most - 1 }, (_, i) => `<th>LL${i + 2}</th>`).join('')}</tr></thead><tbody>
        ${list.filter(t => t.levels.length > 1).map(t => {
          const mine = profile?.traders?.[t.id]?.loyalty || 0;
          return `<tr><td><a data-page="trader" data-id="${t.id}">${esc(t.name)}</a></td>${Array.from({ length: most - 1 }, (_, i) => {
            const l = t.levels[i + 1];
            return `<td${l && mine === l.level ? ' class="here"' : ''}>${l ? step(t, l) : ''}</td>`;
          }).join('')}</tr>`;
        }).join('')}</tbody></table>`)}`;
  }

  const rulesLink = (id, text) => `<a data-page="rules" data-id="${id}">${text}</a>`;

  // ---------- Mechanics: the server's own rules, one page each ----------

  // Every figure comes from /wiki/api/rules, read from the server's settings as they are now, mods included.
  const RULES = [
    ['insurance', 'Insurance', 'Who insures, how long items take to come back, how often they do and what it costs.'],
    ['flea', 'Flea market', 'When it opens, listing fees, offer slots, limits and what can\'t be sold.'],
    ['repairs', 'Repairs', 'Which traders repair, what it costs and how much durability it loses, and repair kits.'],
    ['ammo', 'Ammo chart', 'Every round by calibre: damage, penetration, armour damage, fragmentation and speed.'],
    ['armor', 'Armour chart', 'Every armoured item: class, durability, material, the zones it covers and its penalties.'],
    ['skills', 'Skills', 'What each skill does and how it levels.'],
    ['levels', 'Character levels', 'Experience for each level, what each level unlocks, and where experience comes from.'],
    ['health', 'Health', 'Body part health, injuries and their cost to heal, and healing between raids.'],
    ['karma', 'Scav karma', "What each standing with the scavs' trader changes, and the standing you lose for each kill."],
    ['achievements', 'Achievements', 'Every achievement, its rarity and what it gives.'],
    ['prestige', 'Prestige', 'What each prestige level asks for and what it gives.'],
    ['events', 'Events', 'The seasonal events this server knows, their dates and whether one is on now.'],
    ['circle', 'Cultist Circle', 'Sacrifice times by value, and the fixed trades.'],
    ['containers', 'Containers', 'Secure containers and cases: size outside, space inside, what they hold.']
  ];
  const RULE_GROUPS = [['Trading', ['flea', 'insurance', 'repairs', 'karma']], ['Gear', ['ammo', 'armor', 'containers']],
    ['Character', ['levels', 'skills', 'health', 'prestige', 'achievements']], ['World', ['events', 'circle']]];
  let ruleFilter = '';
  let ruleSort = '';

  // "durabilityPointCostArmor" to "Durability point cost armor"
  function human(key) {
    const words = String(key).replace(/_/g, ' ').replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2').trim().toLowerCase();
    return words.charAt(0).toUpperCase() + words.slice(1);
  }

  const val = v => typeof v === 'boolean' ? (v ? 'Yes' : 'No') : typeof v === 'number' ? (Number.isInteger(v) ? number(v) : String(Math.round(v * 10000) / 10000)) : esc(String(v));

  // a settings object as rows of name and value; nested objects become "Outer · inner"; lists of plain values join
  function settingsRows(obj, path = '') {
    if (obj == null || typeof obj !== 'object') return [];
    const rows = [];
    for (const [k, v] of Object.entries(obj)) {
      const name = path ? `${path} · ${human(k)}` : human(k);
      if (v == null || k === 'ExtensionData') continue;
      if (Array.isArray(v)) {
        if (v.length && v.every(x => typeof x !== 'object')) rows.push([name, v.map(val).join(', ')]);
        else v.forEach((x, i) => rows.push(...settingsRows(x, `${name} ${i + 1}`)));
      } else if (typeof v === 'object') rows.push(...settingsRows(v, name));
      else rows.push([name, val(v)]);
    }
    return rows;
  }

  function settingsTable(obj) {
    const rows = settingsRows(obj);
    return rows.length ? `<table class="wt kv"><tbody>${rows.map(([k, v]) => `<tr><th>${esc(k)}</th><td>${v}</td></tr>`).join('')}</tbody></table>` : '<p class="hint">No settings.</p>';
  }

  // an item-tree node's own name ("RadioTransmitter") read as words; real item names pass through
  const plainName = n => /^[A-Za-z]+$/.test(n) && /[a-z][A-Z]/.test(n) ? human(n) : n;
  const pct = v => v == null ? '' : `${Math.round(v * 100) / 100}%`;
  const hours = h => h == null ? '' : h >= 48 && h % 24 === 0 ? `${h / 24} days` : `${h} h`;
  const itemA = (id, name) => `<a data-item="${esc(id)}">${esc(name)}</a>`;

  // the pictures SPT ships for rewards that aren't items
  const REWARD_ICON = { SK: 'icon_info.png', ACH: 'EfT_UI_Icons_BigTrophy.png', PKT: 'icon_gear_rigs.png', COS: 'icon_gear_facecovers.png', TAG: 'EfT_UI_Icons_DogTag.png' };
  const rewardIcon = kind => `<span class="rw-pic"><img src="/files/handbook/${REWARD_ICON[kind]}" alt="" onerror="this.remove()"></span>`;

  // What each mechanics page is about, in a sentence or two.
  const RULE_HERO = {
    insurance: "Insure your gear before a raid and, if you die and nobody takes it, the trader posts it back to you. It isn't a promise: they have to get it back first, and they might keep the good attachments.",
    flea: "The flea market is where you buy and sell with everyone else. This page is how this server has it set up, which can be a long way from the live game.",
    repairs: "Everything wears down, and every repair takes a little off an item's maximum for good. Traders do it for money, kits let you do it yourself. How much a trader takes off matters more than the price on anything you plan to keep.",
    ammo: "What a round does when it hits something. Penetration decides whether it gets through armour, damage is what happens to you once it has. Pick a calibre if you only care about one gun.",
    armor: "Class is the number that matters most, but a class 6 plate on a carrier that only covers your chest won't do anything for your stomach. On a plate carrier the class is whatever plate you put in it: open a row to see every plate that fits and the best one this server has.",
    skills: "Skills go up by doing the thing. Running trains Endurance, searching bodies trains Search, and so on. Pick one from the list to see what it does and how this server has it set.",
    levels: "XP comes from finishing raids, kills, quests and a bit of healing. Most of what a level gets you is access: the flea, traders' next loyalty level and new quests.",
    health: "You've got seven body parts, each with its own health. Lose your head or thorax and you're dead. Lose anything else and it stops working, and further hits spread to the rest of you.",
    karma: "The scavs' trader keeps score. Kill other scavs while playing as one and it drops, kill PMCs and it goes up. That score changes his prices, your wait between scav raids and whether scavs will shoot you.",
    achievements: "Most achievements are just for the bragging, but plenty hand out a cosmetic or an item too. Pick your profile and the ones you've earned get marked.",
    prestige: "Prestige is starting over on purpose. You give up your character's progress and pay a lot for the privilege, and you get cosmetics and a head start for it.",
    events: "Seasonal events change who spawns and where, and can switch on things like cultist summons or the infection. This is what this server has planned and when.",
    circle: "The Cultist Circle is a hideout area. Put items on it, wait, and it gives you something back worth around what you gave up. A few exact combinations always give the same thing.",
    containers: "Secure containers keep what's inside when you die. Cases pack more into your stash than they take up, so look at what they hold against what they cost you in space."
  };

  // The pictures beside a mechanics page's heading: the things the page itself is about, from what the server sent
  // for it (the traders who insure, the most valuable capped items, the rounds that go through most armour...).
  // Only where a picture says something about the page: a page about numbers or rules alone
  // (skills, character levels, health, flea market limits, events) has none.
  function rulePictures(id, r) {
    if (!r || r.error) return [];
    const items = list => list.filter(Boolean).slice(0, 6).map(x => ({ item: x }));
    const traders = list => list.filter(Boolean).slice(0, 6).map(x => ({ trader: x }));
    const images = list => list.filter(Boolean).slice(0, 6).map(x => ({ image: x }));
    const top = (list, key) => [...(list || [])].sort((a, b) => (b[key] ?? 0) - (a[key] ?? 0));
    switch (id) {
      case 'insurance': return traders((r.traders || []).map(t => t.id));
      case 'repairs': return traders((r.traders || []).map(t => t.id)).concat(items((r.kits || []).map(k => k.id))).slice(0, 6);
      case 'ammo': return items(top(r.rounds, 'pen').map(x => x.id));
      case 'armor': return items(top((r.armor || []).filter(x => !x.modded), 'price').map(x => x.id));
      case 'containers': return items(top((r.containers || []).filter(c => c.secure), 'cells').map(c => c.id));
      case 'karma': return traders([data.fenceId]);
      case 'achievements': return images((r.list || []).filter(x => x.image && !x.hidden).map(x => x.image));
      case 'prestige': return images((r.levels || []).map(l => l.image));
      default: return [];
    }
  }

  // one picture in its tile; a picture that won't load takes its tile with it
  const rulePicture = x => {
    const src = x.trader ? data.traders[x.trader]?.avatar : x.item ? `icon/${x.item}?v=${ICON_V}` : x.image;
    const title = x.trader ? traderName(x.trader) : x.item ? itemName(x.item) : '';
    return src ? `<span class="rh-tile${x.trader ? ' face' : ''}"${x.item ? ` data-item="${esc(x.item)}"` : x.trader ? ` data-page="trader" data-id="${esc(x.trader)}"` : ''} title="${esc(title)}"><img src="${esc(src)}" alt="" loading="lazy" onerror="this.parentElement.remove()"></span>` : '';
  };

  // a row of headline numbers: what it is, the number, and a note under it
  const facts = list => `<dl class="headline">${list.filter(Boolean).map(([k, v, note]) => `<div><dt>${k}</dt><dd>${v}</dd>${note ? `<span class="sub">${note}</span>` : ''}</div>`).join('')}</dl>`;
  // pick from a list on the left, read about it on the right
  const pickPanel = (groups, body) => `<div class="pick-panel"><div class="pick-list">${groups.map(([title, rows]) => `${title ? `<h4>${title}</h4>` : ''}${rows.map(([id, label, note]) => `<button class="pick-row${rulePick === id ? ' on' : ''}" data-rpick="${esc(id)}"><span>${label}</span>${note != null ? `<span class="sub">${note}</span>` : ''}</button>`).join('')}`).join('')}</div><div class="pick-body">${body}</div></div>`;
  // the game's own icon for a handbook category
  const catIcon = c => c?.icon ? `<img class="cat-ico" src="${esc(c.icon)}" alt="" loading="lazy" onerror="this.remove()">` : '';
  const sortReset = () => ruleSort ? '<button class="chip" data-rsortclear="1">Back to the normal layout</button>' : '';
  let rulePick = '';

  function rulesIndex() {
    return `${crumbs([null, 'Mechanics'])}
      <div class="page-head"><div class="t"><h2>Mechanics</h2><p>How this server plays: read from its settings as they are now, so a mod that changes insurance, the flea or anything else shows here.</p></div></div>
      <div class="two">${RULE_GROUPS.map(([title, ids]) => card(title, `<table class="wt"><tbody>${ids.map(id => RULES.find(x => x[0] === id)).filter(Boolean)
        .map(([id, name, text]) => `<tr data-page="rules" data-id="${id}"><td><a>${name}</a><div class="sub">${text}</div></td></tr>`).join('')}</tbody></table>`)).join('')}</div>`;
  }

  const rulePicks = {};
  function rulesPage(id) {
    const entry = RULES.find(r => r[0] === id);
    if (!entry) return rulesIndex();
    const r = wiki(`rules|${id}`, `api/rules?page=${id}`);
    // three of the candidates, picked at random once per page, so the head stays one row tall
    // and a filter click redrawing the page doesn't reshuffle them
    const candidates = rulePictures(id, r);
    if (candidates.length && !rulePicks[id])
      rulePicks[id] = candidates.map(x => [Math.random(), x]).sort((a, b) => a[0] - b[0]).slice(0, 3).map(x => x[1]);
    const pictures = rulePicks[id] || [];
    const head = () => `${crumbs(['rules', 'Mechanics'], [null, entry[1]])}
      <div class="rule-head"><div class="rule-head-text"><h2>${entry[1]}</h2><p>${RULE_HERO[id] || entry[2]}</p></div>
        ${pictures.length ? `<div class="rule-pics">${pictures.map(rulePicture).join('')}</div>` : ''}</div>`;
    if (!r) return `${head()}<div class="loading">Loading…</div>`;
    if (r.error) return `${head()}<div class="intro red">Couldn't load this page from the server.</div>`;
    const pages = { insurance: insurancePage, flea: fleaPage, repairs: repairsPage, ammo: ammoPage, armor: armorPage, skills: skillsPage, levels: levelsPage,
      health: healthPage, karma: karmaPage, achievements: achievementsPage, prestige: prestigePage, events: eventsPage, circle: circlePage, containers: containersPage };
    return head() + pages[id](r);
  }

  function insurancePage(r) {
    const notes = [];
    if (r.returnOverrideSeconds > 0) notes.push(`This server returns insured items after <b>${duration(r.returnOverrideSeconds)}</b>, whoever insured them.`);
    if (r.storageOverrideSeconds > 0) notes.push(`Returned items wait in your messages for <b>${duration(r.storageOverrideSeconds)}</b>.`);
    if (r.attachmentsTaken) notes.push(`Before a gun comes back, attachments worth over <b>${money(r.minAttachmentPrice, ROUBLES)}</b> may be taken off it. ${r.noAttachmentsTakenPercent}% of the time nothing is taken.`);
    if (r.markOfUnknown != null && r.markOfUnknown !== 1) notes.push(`Carrying the Mark of the Unknown makes returns take ${Math.round(r.markOfUnknown * 100)}% as long.`);
    const editions = new Map();
    for (const [edition, mult] of Object.entries(r.editions || {})) {
      if (mult == null || mult === 1) continue;
      editions.set(/edge_of_darkness|^eod/i.test(edition) ? 'Edge of Darkness' : human(edition), mult);
    }
    for (const [name, mult] of editions) notes.push(`${esc(name)} accounts get items back in ${Math.round(mult * 100)}% of the time.`);
    if (r.onlyOnDeath) notes.push('Insurance only pays out when you die.');
    notes.push(`The server checks for returns every ${duration(r.checkSeconds)}.`);
    return `${card('Traders', r.traders.length ? `<table class="wt"><thead><tr><th colspan="2">Trader</th><th>Back in</th><th>Kept in messages</th><th class="num">Comes back</th><th>Cost by loyalty level</th><th>Won't insure</th></tr></thead><tbody>
        ${r.traders.map(t => `<tr><td class="pic-cell">${avatar(t.id)}</td><td><a data-page="trader" data-id="${t.id}">${esc(t.name)}</a></td>
          <td>${r.returnOverrideSeconds > 0 ? duration(r.returnOverrideSeconds) : `${t.minHours}–${t.maxHours} h`}</td><td>${hours(t.storageHours)}</td>
          <td class="num">${t.returnChance != null ? pct(t.returnChance) : ''}</td>
          <td>${t.levels.map(l => pct(l.coef)).join(' / ')}</td><td>${t.excluded.map(x => esc(plainName(x))).join(', ')}</td></tr>`).join('')}</tbody></table>
        <p class="sub">Cost is the share of the item's base value you pay, at loyalty level 1, 2 and so on.${r.traders.some(t => t.minPayment) ? ' Some traders charge a minimum.' : ''}</p>`
        : '<p class="hint">No trader on this server offers insurance.</p>')}
      ${card('How it works here', `<ul>${notes.map(n => `<li>${n}</li>`).join('')}</ul>`)}`;
  }

  function fleaPage(r) {
    const slots = r.offerSlots.map(s => `<tr><td>${s.from <= -1000 ? `below ${s.to}` : s.to >= 1000 ? `${s.from} and up` : `${s.from} to ${s.to}`}</td><td class="num">${s.count}</td></tr>`).join('');
    const levelTable = list => `<table class="wt"><thead><tr><th class="num">Level</th><th>Items</th></tr></thead><tbody>${[...groupBy(list, x => x.level)].sort((a, b) => a[0] - b[0])
      .map(([level, items]) => `<tr><td class="num">${level}</td><td>${items.map(i => itemA(i.id, i.name)).join(', ')}</td></tr>`).join('')}</tbody></table>`;
    const fee = r.sell?.fees === false ? 'None' : `${r.tax.community}% + ${r.tax.requirement}%`;

    let html = facts([
      ['Opens at', r.enabled === false ? 'Off' : `Level ${r.minLevel}`],
      ['A listing lasts', hours(r.offerHours)],
      ['Listing fee', fee, r.sell?.fees === false ? '' : 'on the item, then on what you ask'],
      r.sell?.chance && ['Fair price sells', `${r.sell.chance.base}% of the time`],
      ['Unsold items wait', hours(r.unsoldStorageHours), 'in your messages'],
      ['Found in raid only', r.foundInRaidOnly ? 'Yes' : 'No', r.foundInRaidOnly ? 'nothing else can be listed' : 'anything sellable can be listed']
    ]);
    html += `<div class="two"><div class="col">${card('How many offers you can have', `<p>Your flea rating decides this. It goes up ${r.rating.up} for every ${money(r.rating.upPer, ROUBLES)} you sell.</p>
        <table class="wt"><thead><tr><th>Rating</th><th class="num">Offers at once</th></tr></thead><tbody>${slots}</tbody></table>`)}</div>
      <div class="col">${banFinder(r.banned.map(i => i.id))}</div></div>`;
    if (r.limits.length) html += card('Most you can put in one offer', `<p>The game caps a few valuable items so one seller can't dump a whole stack at once. This only matters if you're listing them.</p>
      <div class="lim-grid">${r.limits.map(l => `<div class="lim" data-item="${esc(l.id)}">${icon(l.id, 'sm')}<a>${esc(l.name)}</a><b>${l.max}</b></div>`).join('')}</div>`, r.limits.length);
    if (r.tiered) html += card('What unlocks when', `${r.tiered.categories.length ? `<p>Whole categories: ${r.tiered.categories.map(c => `${esc(c.name)} at ${c.level}`).join(', ')}.</p>` : ''}${levelTable(r.tiered.items.concat(r.tiered.ammo || []))}`);
    return html;
  }

  // what can't go on the flea: type a name to check an item, with how the ban splits by kind of item
  function banFinder(ids) {
    const catalog = wiki('catalog', 'api/catalog');
    const banned = new Set(ids);
    const text = ruleFilter.trim().toLowerCase();
    const items = catalog?.items?.filter(i => banned.has(i.id) && !i.fixedPart && !i.unused) || [];
    const hits = text.length >= 2 ? items.filter(i => i.name.toLowerCase().includes(text)).slice(0, 12) : [];
    const cats = new Map((catalog?.categories || []).map(c => [c.id, c]));
    const top = id => { let at = cats.get(id); while (at?.parent && cats.get(at.parent)) at = cats.get(at.parent); return at; };
    const kinds = [...groupBy(items, i => top(i.category)?.id)].map(([id, list]) => [cats.get(id), list.length]).filter(([c]) => c).sort((a, b) => b[1] - a[1]).slice(0, 7);
    return card("Banned from the flea", `<p>${number(items.length)} items can't be listed at all. Type a name to check one.</p>
      <input class="filter" id="ruleFind" placeholder="Item name" value="${esc(ruleFilter)}">
      ${text.length >= 2 ? (hits.length ? `<div class="ban-hits">${hits.map(i => `<div data-item="${i.id}">${icon(i.id, 'sm')}<a>${esc(i.name)}</a></div>`).join('')}</div>` : '<p class="none">Nothing by that name is banned.</p>')
        : `<table class="wt"><tbody>${kinds.map(([c, n]) => `<tr data-page="items" data-id="${c.id}"><td class="pic-cell">${catIcon(c)}</td><td><a>${esc(c.name)}</a></td><td class="num">${number(n)}</td></tr>`).join('')}</tbody></table>`}`);
  }

  function groupBy(list, key) {
    const map = new Map();
    for (const x of list) { const k = key(x); if (!map.has(k)) map.set(k, []); map.get(k).push(x); }
    return map;
  }

  function repairsPage(r) {
    // the trader who takes the least off an item's maximum durability is the one to use on gear you're keeping
    const kindest = [...r.traders].sort((a, b) => a.quality - b.quality)[0];
    const level = l => `×${Math.round((l.coef > 0 ? l.coef / 100 + 1 : 1) * 100) / 100}`;
    return `${facts([
        kindest && ['Kindest to your gear', esc(kindest.name), 'loses the least durability'],
        ['Kit cost, weapons', `${r.gunPointCost} points`, 'for each point repaired'],
        ['Kit cost, armour', `${r.armorPointCost} points`, 'more for higher classes'],
        r.buffLevel != null && ['Kit buffs from', `Skill level ${r.buffLevel}`, 'in the maintenance skills']
      ])}
      <div class="two wide-left"><div class="col">${card('Traders', `<table class="wt"><thead><tr><th colspan="2">Trader</th><th>Paid in</th><th class="num">Durability loss</th><th>Price by loyalty level</th></tr></thead><tbody>
        ${r.traders.map(t => `<tr><td class="pic-cell">${avatar(t.id)}</td><td><a data-page="trader" data-id="${t.id}">${esc(t.name)}</a>${t.excluded.length ? `<div class="sub">Won't touch ${t.excluded.map(x => esc(plainName(x))).join(', ')}</div>` : ''}</td><td>${esc(t.currency)}</td>
          <td class="num">×${t.quality}</td><td>${t.levels.map(level).join(' / ')}</td></tr>`).join('')}</tbody></table>
        <p class="sub">Durability loss is how much of the maximum goes for good on each repair, so lower is better${r.randomLoss ? ', and there is some luck in it' : ''}. The price is the item's repair cost per point times that number${r.priceMultiplier !== 1 ? `, then times ${r.priceMultiplier} on this server` : ''}.</p>`)}
        ${r.treatment ? card('Skill rolls', `<p>Each kit repair trains Weapon Maintenance. There's a ${Math.round(r.treatment.critSuccessChance * 100)}% chance of ${r.treatment.critSuccessAmount} extra points and a ${Math.round(r.treatment.critFailureChance * 100)}% chance of losing ${r.treatment.critFailureAmount}.</p>
          ${r.buffPrices ? `<p>A buffed item costs more to repair at a trader: ${Object.entries(r.buffPrices).map(([k, v]) => `${esc(human(k).toLowerCase())} ×${v}`).join(', ')}.</p>` : ''}`) : ''}</div>
      <div class="col">${card('Repair kits', `<table class="wt items-list"><tbody>${r.kits.map(k => `<tr data-item="${k.id}"><td class="pic-cell"><span class="pic">${pic(k.id)}</span></td><td><a>${esc(k.name)}</a><div class="sub">${k.points != null ? `${number(k.points)} repair points` : ''}</div></td><td class="num">${money(k.price, ROUBLES)}</td></tr>`).join('')}</tbody></table>`, r.kits.length)}</div></div>`;
  }

  // a sortable table: click a heading to sort by it, again to reverse
  function sortRows(rows, cols) {
    if (!ruleSort) return rows;
    const [key, dir] = ruleSort.split(':');
    const col = cols.find(c => c[0] === key);
    if (!col) return rows;
    const get = col[2] || (x => x[key]);
    return [...rows].sort((a, b) => { const x = get(a), y = get(b); return (typeof x === 'string' ? x.localeCompare(y) : (x ?? -1e9) - (y ?? -1e9)) * (dir === 'd' ? -1 : 1); });
  }
  const sortHead = cols => cols.map(([key, label, , cls]) => `<th class="sortable ${cls || ''}${ruleSort.startsWith(key + ':') ? ' sorted' : ''}" data-rsort="${key}">${label}${ruleSort === key + ':a' ? ' ▲' : ruleSort === key + ':d' ? ' ▼' : ''}</th>`).join('');

  function ammoPage(r) {
    const calibers = [...new Set(r.rounds.map(x => x.caliber))].sort();
    const shown = r.rounds.filter(x => !ruleFilter || x.caliber === ruleFilter);
    const cols = [['name', 'Round', x => x.name], ['damage', 'Damage', x => x.damage * (x.pellets || 1), 'num'], ['pen', 'Penetration', null, 'num'], ['armor', 'Armour damage', null, 'num'],
      ...[1, 2, 3, 4, 5, 6].map(c => [`c${c}`, `Class ${c}`, x => penChance(x.pen ?? 0, c), 'pen']),
      ['frag', 'Frag', null, 'num'], ['speed', 'Speed', null, 'num'], ['accuracy', 'Accuracy', null, 'num'], ['recoil', 'Recoil', null, 'num'], ['price', 'Value', null, 'num']];
    const rowsFor = list => sortRows(list, cols).map(x => `<tr data-item="${x.id}"><td><a>${esc(x.name)}</a>${x.tracer ? tagBox('Tracer') : ''}${modMark(x)}</td>
      <td class="num">${x.pellets ? `${x.damage} × ${x.pellets}` : x.damage}</td><td class="num">${x.pen ?? ''}</td><td class="num">${x.armor != null ? `${x.armor}%` : ''}</td>
      ${[1, 2, 3, 4, 5, 6].map(c => penCell(x.pen ?? 0, c)).join('')}
      <td class="num">${x.frag != null ? `${Math.round(x.frag * 100)}%` : ''}</td><td class="num">${x.speed != null ? `${x.speed} m/s` : ''}</td>
      <td class="num">${x.accuracy ? (x.accuracy > 0 ? '+' : '') + x.accuracy + '%' : ''}</td><td class="num">${x.recoil ? (x.recoil > 0 ? '+' : '') + x.recoil : ''}</td><td class="num">${x.price ? money(x.price, ROUBLES) : ''}</td></tr>`).join('');
    const filter = `<div class="toolbar"><label class="qbar-map">Calibre <select data-rfilter><option value="">All calibres</option>${calibers.map(c => `<option${c === ruleFilter ? ' selected' : ''}>${esc(c)}</option>`).join('')}</select></label>
      <span class="sub">${shown.length} rounds. Click a heading to sort.</span>${sortReset()}</div>
      <p class="sub">Class 1 to 6: how likely one round is to go through armour of that class at full durability, from the penetration
        formula in the game's code. High is 80% or more, Medium 40% or more, Low 10% or more, Hardly ever under 10%. Worn armour lets more through.</p>`;
    const table = list => `<div class="wide-table"><table class="wt sortable-table ammo-table"><thead><tr>${sortHead(cols)}</tr></thead><tbody>${rowsFor(list)}</tbody></table></div>`;
    return filter + (ruleFilter || ruleSort ? table(shown) : [...groupBy(shown, x => x.caliber)].sort((a, b) => a[0].localeCompare(b[0]))
      .map(([cal, list]) => card(esc(cal), table(list.sort((a, b) => (b.pen ?? 0) - (a.pen ?? 0))), list.length)).join(''));
  }

  // The chance, in percent, that a round goes through armour of a class at full durability: the formula in the game's
  // code, with a class's resistance ten times its number
  function penChance(pen, armorClass) {
    const armor = armorClass * 10 * (121 - 5000 / (45 + 100 * 2)) / 100;
    if (armor >= pen + 15) return 0;
    if (armor >= pen) return 0.4 * (armor - pen - 15) ** 2;
    return 100 + pen / (0.9 * armor - pen);
  }

  function penCell(pen, armorClass) {
    const c = penChance(pen, armorClass);
    const [word, cls] = c >= 80 ? ['High', 'hi'] : c >= 40 ? ['Medium', 'mid'] : c >= 10 ? ['Low', 'lo'] : c > 0 ? ['Hardly ever', 'rare'] : ['No', 'no'];
    return `<td class="pen ${cls}" title="${Math.round(c)}%">${word}</td>`;
  }

  const matName = m => /^[A-Z0-9]+$/.test(m) ? m : human(m);
  // a body zone by the game's own name for it; a plate's zone ("Plate_Granit_SAPI_chest") has none, so it goes by
  // the side or the last word of its node name ("Chest plate")
  const zoneName = z => data?.words?.[z] || (/^Plate_/i.test(z)
    ? (/side_?left/i.test(z) ? 'Left side plate' : /side_?right/i.test(z) ? 'Right side plate' : `${human(z.split('_').pop())} plate`) : human(z));

  const ARMOR_KINDS = { armor: 'Body armour', rig: 'Armoured rigs', helmet: 'Helmets', face: 'Face protection', part: 'Helmet parts',
    eyewear: 'Eyewear', headset: 'Headsets', plate: 'Plates', other: 'Other armour' };
  // the armour chart's figures: as sold, or with the best plates this server gives; and the rows opened to show their plates
  let armorWith = 'sold';
  const armorOpen = new Set();

  const signedPct = v => v ? `${v > 0 ? '+' : ''}${Math.round(v * 100) / 100}%` : '';
  const kg = v => v != null ? `${Math.round(v * 1000) / 1000} kg` : '';
  const zoneList = zones => [...new Set(zones.map(zoneName))].join(', ');
  const slotName = n => human(n.replace(/_plate$/i, '').replace(/^mod_equipment(_\d+)?$/i, 'Attachment'));

  // An opened row of the armour chart: what it weighs and costs you three ways, the soft armour built in, and for
  // each plate slot every plate that fits, the one it comes with and the best this server gives you
  function armorDetail(x, plates, span) {
    return `<tr class="armor-open"><td colspan="${span}">${armorBody(x, plates)}</td></tr>`;
  }

  function armorBody(x, plates, withFacts = true) {
    const facts = !withFacts ? [] : [
      x.heavy && x.heavy !== 'None' ? `${x.heavy === 'Heavy' ? 'Heavy' : 'Light'} armour: trains the ${x.heavy === 'Heavy' ? 'Heavy' : 'Light'} Vests skill` : '',
      x.material ? `Material: ${esc(matName(x.material))}` : '',
      x.blunt != null ? `Blunt damage through: ${Math.round(x.blunt * 1000) / 10}%` : '',
      x.ricochet?.max ? `Ricochet: ${Math.round(x.ricochet.min * 100)} to ${Math.round(x.ricochet.max * 100)}% of hits past ${x.ricochet.angle}°` : '',
      x.deaf && x.deaf !== 'None' ? `Muffles hearing: ${esc(x.deaf)}` : '',
      x.cells ? `Space: ${x.cells} cells in ${x.grids} pocket${x.grids === 1 ? '' : 's'}` : '',
      x.blocks.length ? `Can't be worn with: ${esc(x.blocks.join(', '))}` : ''
    ].filter(Boolean);
    const way = (label, s, armour) => `<tr><th>${label}</th><td class="num">${armour ? s.armorClass || '' : ''}</td><td class="num">${armour ? s.durability || '' : ''}</td>
      <td class="num">${kg(s.weight)}</td><td class="num">${signedPct(s.speed)}</td><td class="num">${signedPct(s.turn)}</td><td class="num">${signedPct(s.ergo)}</td><td class="zones">${armour ? zoneList(s.zones) : ''}</td></tr>`;
    const ways = `<table class="wt"><thead><tr><th></th><th class="num">Class</th><th class="num">Durability</th><th class="num">Weight</th><th class="num">Speed</th><th class="num">Turning</th><th class="num">Ergonomics</th><th>Covers</th></tr></thead><tbody>
      ${x.slots.length ? way('Shell alone', x.shell, false) : ''}
      ${x.builtIn.armorClass ? way(x.slots.length ? 'No plates' : 'As sold', { ...x.shell, ...x.builtIn }, true) : ''}
      ${x.slots.length ? way('As sold', x.sold, true) + way('Best plates here', x.best, true) : ''}</tbody></table>`;
    // slots that take the same plates are shown once: front and back usually do
    const groups = [];
    for (const s of x.slots) {
      const key = s.fits.slice().sort().join(',');
      const g = groups.find(g => g.key === key);
      if (g) g.slots.push(s); else groups.push({ key, slots: [s] });
    }
    const plateTable = g => {
      const comes = new Set(g.slots.map(s => s.comes).filter(Boolean));
      const best = new Set(g.slots.map(s => s.best).filter(Boolean));
      const list = g.slots[0].fits.map(id => ({ id, ...plates[id] })).sort((a, b) => (b.armorClass || 0) - (a.armorClass || 0) || (a.weight || 0) - (b.weight || 0) || a.name.localeCompare(b.name));
      return `<h4>${esc(g.slots.map(s => slotName(s.name)).join(', '))} <span class="sub">${list.length} that fit</span></h4>
        <table class="wt plate-table"><thead><tr><th>Plate</th><th class="num">Class</th><th class="num">Durability</th><th>Material</th><th class="num">Weight</th><th class="num">Speed</th><th class="num">Turning</th><th class="num">Ergonomics</th><th>Covers</th><th></th></tr></thead><tbody>
        ${list.map(p => `<tr class="${p.given ? '' : 'cant'}"><td><a data-item="${p.id}">${esc(p.name)}</a>${modMark(p)}</td><td class="num">${p.armorClass ?? ''}</td><td class="num">${p.durability ?? ''}</td>
          <td>${esc(p.material ? matName(p.material) : '')}</td><td class="num">${kg(p.weight)}</td><td class="num">${signedPct(p.speed)}</td><td class="num">${signedPct(p.turn)}</td><td class="num">${signedPct(p.ergo)}</td>
          <td class="zones">${zoneList(p.zones)}</td><td class="sub">${[comes.has(p.id) ? 'Comes with it' : '', best.has(p.id) ? 'Best here' : '', p.given ? '' : 'Not on this server'].filter(Boolean).join('. ')}</td></tr>`).join('')}</tbody></table>`;
    };
    return `${facts.length ? `<p class="armor-facts">${facts.join('<br>')}</p>` : ''}${ways}
      ${x.builtIn.armorClass && x.slots.length ? `<p class="sub">Built-in soft armour: class ${x.builtIn.armorClass}, ${x.builtIn.durability} durability, covers ${zoneList(x.builtIn.zones)}.</p>` : ''}
      ${groups.map(plateTable).join('')}
      ${x.slots.length ? '<p class="sub">Plates carry their own weight and penalties, so they are added to the shell above. Best here is the highest class this server sells, drops or rewards, lighter first.</p>' : ''}`;
  }

  // The item page for anything that protects you, laid out like the wiki: a lead sentence, a protection box at the
  // side, a card with how it wears three ways and every plate that fits, and for a plate, what it fits in.
  const KIND_WORD = { armor: 'body armour', rig: 'armoured rig', helmet: 'helmet', face: 'face protection', part: 'helmet part',
    eyewear: 'eyewear', headset: 'headset', plate: 'armour plate', insert: 'soft armour insert', other: 'armour' };
  function armorLead(a) {
    const x = a.row, p = a.self;
    if (!x && !p) return '';
    const s = x ? x.sold : p;
    const kind = KIND_WORD[x?.kind || 'plate'] || 'armour';
    const bits = [];
    if (s.armorClass) bits.push(`${/^[aeiou]/.test(kind) ? 'An' : 'A'} ${kind} with class ${s.armorClass} protection${x && x.best.armorClass > s.armorClass ? ` as sold, up to class ${x.best.armorClass} with the best plates this server gives` : ''}.`);
    else bits.push(`${/^[aeiou]/.test(kind) ? 'An' : 'A'} ${kind}${x?.slots.length ? ' with no protection of its own until plates go in' : ' with no armour class'}.`);
    if (s.zones?.length) bits.push(`It covers ${zoneList(s.zones).toLowerCase()}.`);
    if (x?.heavy && x.heavy !== 'None') bits.push(`It counts as ${x.heavy === 'Heavy' ? 'heavy' : 'light'} armour, so wearing it trains the ${x.heavy} Vests skill.`);
    if (x?.slots.length) {
      const comes = x.slots.filter(sl => sl.comes);
      bits.push(`It takes plates in ${x.slots.length} slot${x.slots.length === 1 ? '' : 's'} (${x.slots.map(sl => slotName(sl.name).toLowerCase()).join(', ')})${comes.length ? `, and comes with ${[...new Set(comes.map(sl => a.plates[sl.comes]?.name).filter(Boolean))].map(esc).join(' and ')}` : ', and comes with none'}.`);
    }
    if (a.compatible?.length) {
      const carriers = new Set(a.compatible.map(c => c.id));
      bits.push(`It fits ${carriers.size} piece${carriers.size === 1 ? '' : 's'} of armour on this server.`);
    }
    if (x?.blocks.length) bits.push(`You can't wear it with ${esc(x.blocks.map(b => b.toLowerCase()).join(' or '))}.`);
    return `<p class="ih-lead">${bits.join(' ')}</p>`;
  }

  function armorBox(a) {
    const x = a.row, p = a.self;
    if (!x && !p) return '';
    const s = x ? x.sold : p;
    const mat = x?.material || p?.material;
    const m = mat && a.materials?.[mat];
    const rows = [
      ['Armour class', s.armorClass ? `${s.armorClass}${x && x.best.armorClass > s.armorClass ? `<div class="sub">up to ${x.best.armorClass} with the best plates here</div>` : ''}` : 'None'],
      s.durability ? ['Durability', `${s.durability}${x && x.best.durability !== s.durability && x.slots.length ? `<div class="sub">${x.best.durability} with the best plates here</div>` : ''}`] : null,
      mat ? ['Material', `${esc(matName(mat))}${m ? `<div class="sub">${Math.round(m.destructibility * 100) / 100} destructibility</div>` : ''}`] : null,
      s.zones?.length ? ['Covers', esc(zoneList(s.zones))] : null,
      x?.heavy && x.heavy !== 'None' ? ['Armour type', `${x.heavy === 'Heavy' ? 'Heavy' : 'Light'}`] : null,
      x?.blunt != null ? ['Blunt damage', `${Math.round(x.blunt * 1000) / 10}% gets through`] : null,
      x?.ricochet?.max ? ['Ricochet', `${Math.round(x.ricochet.min * 100)} to ${Math.round(x.ricochet.max * 100)}% past ${x.ricochet.angle}°`] : null,
      s.speed ? ['Move speed', signedPct(s.speed)] : null,
      s.turn ? ['Turning', signedPct(s.turn)] : null,
      s.ergo ? ['Ergonomics', signedPct(s.ergo)] : null,
      x?.deaf && x.deaf !== 'None' ? ['Hearing', `Muffled (${esc(x.deaf)})`] : null,
      x?.cells ? ['Space', `${x.cells} cells in ${x.grids} pocket${x.grids === 1 ? '' : 's'}`] : null,
      x?.slots.length ? ['Plate slots', esc(x.slots.map(sl => slotName(sl.name)).join(', '))] : null,
      x?.blocks.length ? ["Can't wear with", esc(x.blocks.join(', '))] : null
    ].filter(Boolean);
    return sideBox('Protection', rows, x?.slots.length ? '<p class="sub">Figures as sold, with the plates it comes with.</p>' : '');
  }

  function armorCards(a) {
    const out = [];
    if (a.row?.slots.length) out.push(card('Armour and plates', armorBody(a.row, a.plates || {}, false), '', 'armor-card'));
    if (a.compatible?.length) {
      const by = new Map();
      for (const c of a.compatible) {
        const e = by.get(c.id) || { ...c, slots: [], comes: false };
        e.slots.push(slotName(c.slot));
        e.comes = e.comes || c.comes;
        by.set(c.id, e);
      }
      const list = [...by.values()].sort((p, q) => (q.comes - p.comes) || p.name.localeCompare(q.name));
      out.push(card('Fits in', `<table class="wt"><thead><tr><th>Armour</th><th>Type</th><th>Slot</th><th class="sub"></th></tr></thead><tbody>
        ${list.map(c => `<tr><td class="with-icon">${icon(c.id, 'sm')}<a data-item="${c.id}">${esc(c.name)}</a></td><td>${esc(ARMOR_KINDS[c.kind] || c.kind)}</td>
          <td>${esc([...new Set(c.slots)].join(', '))}</td><td class="sub">${c.comes ? 'Comes with it' : ''}</td></tr>`).join('')}</tbody></table>`, list.length));
    }
    return out.join('');
  }

  // The item page for a gun, laid out like the wiki: a lead sentence, a weapon box at the side, then the rounds it
  // fires, every mod slot with the parts that go in it, and the builds the server keeps for it.
  const fitChip = i => `<span class="fit" data-item="${esc(i)}">${icon(i, 'sm')}<span>${esc(itemName(i))}</span></span>`;
  const chipList = (ids, n = 12) => `<div class="fits">${ids.slice(0, n).map(fitChip).join('')}</div>${ids.length > n
    ? `<details class="fits-more"><summary>${ids.length - n} more</summary><div class="fits">${ids.slice(n).map(fitChip).join('')}</div></details>` : ''}`;
  function weaponLead(w) {
    const bits = [];
    const kind = (w.kind || 'weapon').toLowerCase();
    bits.push(`${/^[aeiou]/.test(kind) ? 'An' : 'A'} ${esc(kind)}${w.caliber ? ` chambered in ${esc(w.caliber)}` : ''}${w.rate ? `, firing ${number(w.rate)} rounds a minute` : ''}.`);
    if (w.use) bits.push(w.use === 'Primary' ? 'It goes in a primary weapon slot.' : w.use.startsWith('Secondary') ? 'It goes in the holster.' : '');
    if (w.defMag || w.defAmmo) bits.push(`It comes with ${[w.defMag && itemLink(w.defMag), w.defAmmo && `${itemLink(w.defAmmo)} rounds`].filter(Boolean).join(' loaded with ')}.`);
    if (w.ammo?.length) bits.push(`${w.ammo.length} kind${w.ammo.length === 1 ? '' : 's'} of round fit it on this server.`);
    if (w.slots?.length) bits.push(`It has ${w.slots.length} mod slot${w.slots.length === 1 ? '' : 's'}${w.slots.some(s => s.required) ? `, ${w.slots.filter(s => s.required).length} of them needed for it to fire` : ''}.`);
    if (w.builds?.length > 1) bits.push(`The server keeps ${w.builds.length} ready-made builds of it.`);
    return `<p class="ih-lead">${bits.filter(Boolean).join(' ')}</p>`;
  }

  function weaponBox(w, statRows) {
    const extra = label => (statRows || []).find(r => r.label === label)?.value;
    const rows = [
      w.kind ? ['Type', esc(w.kind)] : null,
      w.use ? ['Slot', esc(w.use)] : null,
      w.caliber ? ['Calibre', esc(w.caliber)] : null,
      w.rate ? ['Fire rate', `${number(w.rate)} rpm`] : null,
      extra('Fire modes') ? ['Fire modes', esc(extra('Fire modes'))] : null,
      extra('Muzzle velocity') ? ['Muzzle velocity', esc(extra('Muzzle velocity'))] : null,
      w.ergo != null ? ['Ergonomics', number(w.ergo)] : null,
      w.up ? ['Vertical recoil', number(w.up)] : null,
      w.back ? ['Horizontal recoil', number(w.back)] : null,
      w.eff ? ['Effective range', `${number(w.eff)} m`] : null,
      w.sight ? ['Sighting range', `${number(w.sight)} m`] : null,
      w.durability ? ['Durability', number(w.durability)] : null,
      // the server's mastery group: its name when it reads as one ("AK74"), not when it's a code ("1301")
      w.mastery ? ['Mastery', `Level 2 at ${number(w.mastery.level2)} points, level 3 at ${number(w.mastery.level3)}${w.mastery.others
        ? `<div class="sub">Shared with ${number(w.mastery.others)} other gun${w.mastery.others === 1 ? '' : 's'}${/[a-z]/i.test(w.mastery.group || '') ? ` in the ${esc(w.mastery.group)} group` : ''}</div>` : ''}`] : null,
      w.defMag ? ['Default magazine', `${icon(w.defMag, 'sm')}${itemLink(w.defMag)}`] : null,
      w.defAmmo ? ['Default ammo', `${icon(w.defAmmo, 'sm')}${itemLink(w.defAmmo)}`] : null
    ].filter(Boolean);
    return sideBox('Weapon', rows, '<p class="sub">Figures for the bare gun, before any parts go on.</p>');
  }

  function weaponCards(w) {
    const out = [];
    if (w.ammo?.length) out.push(card('Ammo', `<table class="wt"><thead><tr><th>Round</th><th>Damage</th><th>Penetration</th><th>Armour damage</th><th>Speed</th><th class="sub"></th></tr></thead><tbody>
      ${w.ammo.map(a => `<tr><td class="with-icon">${icon(a.id, 'sm')}<a data-item="${esc(a.id)}">${esc(a.name)}</a></td>
        <td>${a.damage != null ? `${number(a.damage)}${a.pellets ? ` × ${a.pellets}` : ''}` : ''}</td><td>${a.pen != null ? number(a.pen) : ''}</td>
        <td>${a.armor != null ? `${number(a.armor)}%` : ''}</td><td>${a.speed ? `${number(a.speed)} m/s` : ''}</td>
        <td class="sub">${[a.given && 'Comes loaded', a.tracer && 'Tracer'].filter(Boolean).join(', ')}</td></tr>`).join('')}</tbody></table>
      <div class="src">Every round this gun's chamber takes on this server, best penetration first.</div>`, w.ammo.length));
    if (w.slots?.length) out.push(card('Mods', `<table class="wt mod-slots"><tbody>
      ${w.slots.map(s => `<tr><td class="slot-name">${esc(s.name)}${s.required ? '<div class="sub">needed</div>' : ''}</td><td>${chipList(s.fits)}</td></tr>`).join('')}</tbody></table>
      <div class="src">Each slot on the bare gun and every part this server lets go in it, mods included. Parts add slots of their own.</div>`, w.slots.length));
    if (w.builds?.length) out.push(card('Builds', w.builds.map(b => `<div class="build"><div class="build-name">${esc(b.name || 'Build')}${b.standard ? ' <span class="sub">standard</span>' : ''}</div>
      ${chipList([...new Set(b.parts.map(p => p.id).filter(Boolean))], 10)}</div>`).join(''), w.builds.length));
    return out.join('');
  }

  // a round's page: what it does to the target, what it does to the gun, and the rest of its calibre
  const rpct = v => `${Math.round(v * 1000) / 10}%`;
  const rsigned = v => `${v > 0 ? '+' : ''}${Math.round(v * 100) / 100}`;
  function ammoLead(a) {
    const bits = [];
    bits.push(`A ${a.caliber ? `${esc(a.caliber)} ` : ''}round${a.pellets ? ` firing ${a.pellets} pellets` : ''}${a.damage != null ? ` that does ${number(a.damage)} damage${a.pellets ? ' a pellet' : ''}` : ''}${a.pen != null ? ` with ${number(a.pen)} penetration` : ''}.`);
    if (a.speed) bits.push(`It leaves the muzzle at ${number(a.speed)} m/s${a.subsonic ? ', below the speed of sound, so a suppressed shot stays quiet' : ''}.`);
    if (a.tracer) bits.push('It is a tracer: each shot draws a visible line.');
    if (a.frag) bits.push(`It breaks apart in the body ${rpct(a.frag)} of the time for extra damage.`);
    const n = a.siblings?.length || 0;
    if (n > 1) {
      const rank = a.siblings.findIndex(s => s.self) + 1;
      bits.push(`${n} rounds of this calibre exist on this server${rank ? `; this one is ${rank === 1 ? 'the best' : `number ${rank}`} through armour` : ''}.`);
    }
    return `<p class="ih-lead">${bits.join(' ')}</p>`;
  }

  function ammoBox(a) {
    const off1 = v => v != null && v !== 1 ? `${rsigned((v - 1) * 100)}%` : null;
    const rows = [
      a.caliber ? ['Calibre', esc(a.caliber)] : null,
      a.damage != null ? ['Damage', `${number(a.damage)}${a.pellets ? ` × ${a.pellets}` : ''}`] : null,
      a.pen != null ? ['Penetration', number(a.pen)] : null,
      a.armor != null ? ['Armour damage', `${number(a.armor)}%`] : null,
      a.speed ? ['Velocity', `${number(a.speed)} m/s`] : null,
      a.frag != null ? ['Fragmentation', rpct(a.frag)] : null,
      a.ricochet ? ['Ricochet chance', rpct(a.ricochet)] : null,
      a.lightBleed ? ['Light bleed', signedPct(a.lightBleed * 100)] : null,
      a.heavyBleed ? ['Heavy bleed', signedPct(a.heavyBleed * 100)] : null,
      a.stamina ? ['Stamina burn', `${Math.round(a.stamina * 1000) / 1000} per damage`] : null,
      a.accuracy ? ['Accuracy', rsigned(a.accuracy)] : null,
      a.recoil ? ['Recoil', rsigned(a.recoil)] : null,
      off1(a.burn) ? ['Durability burn', off1(a.burn)] : null,
      off1(a.heat) ? ['Heat', off1(a.heat)] : null,
      a.misfire ? ['Misfire chance', `${Math.round(a.misfire * 1000) / 1000}<div class="sub">lower is better</div>`] : null,
      a.feed ? ['Feed failure', `${Math.round(a.feed * 1000) / 1000}<div class="sub">lower is better</div>`] : null,
      ['Tracer', a.tracer ? 'Yes' : 'No'],
      a.subsonic ? ['Subsonic', 'Yes'] : null,
      a.stack ? ['Stack', number(a.stack)] : null
    ].filter(Boolean);
    return sideBox('Ammo', rows, '<p class="sub">Durability burn and heat are against a plain round of no effect.</p>');
  }

  function ammoCards(a) {
    if (!(a.siblings?.length > 1)) return '';
    return card('Other rounds in this calibre', `<table class="wt"><thead><tr><th>Round</th><th>Damage</th><th>Penetration</th><th>Armour damage</th><th>Speed</th><th>Fragmentation</th><th class="sub"></th></tr></thead><tbody>
      ${a.siblings.map(s => `<tr${s.self ? ' class="here"' : ''}><td class="with-icon">${icon(s.id, 'sm')}<a data-item="${esc(s.id)}">${esc(s.name)}</a></td>
        <td>${s.damage != null ? `${number(s.damage)}${s.pellets ? ` × ${s.pellets}` : ''}` : ''}</td><td>${s.pen != null ? number(s.pen) : ''}</td>
        <td>${s.armor != null ? `${number(s.armor)}%` : ''}</td><td>${s.speed ? `${number(s.speed)} m/s` : ''}</td>
        <td>${s.frag != null ? rpct(s.frag) : ''}</td><td class="sub">${s.tracer ? 'Tracer' : ''}</td></tr>`).join('')}</tbody></table>
      <div class="src">Every round of this calibre on this server, mods included, best penetration first.</div>`, a.siblings.length);
  }

  const medAmount = b => !b.value ? '' : b.absolute || Math.abs(b.value) >= 1 ? rsigned(b.value) : `${rsigned(b.value * 100)}%`;
  const medTreat = t => t.stops ? `holds off ${t.label.toLowerCase()}${t.duration ? ` for ${number(t.duration)} s` : ''}` : `${t.label.toLowerCase()}${t.cost ? ` (${number(t.cost)} HP)` : ''}`;
  function medLead(m) {
    const bits = [];
    const kind = (m.kind || 'medical item').toLowerCase();
    // food and drink: the category already names it, so the lead says how long it takes and what it does
    if (m.food) bits.push(`${m.amount > 1 ? `It holds ${number(m.amount)} units` : 'It is used up in one go'}${m.useTime ? `, taking ${number(m.useTime)} s to use` : ''}.`);
    else bits.push(`A ${esc(kind)}${m.hp ? m.rate ? ` with ${number(m.hp)} HP of use` : ` with ${number(m.hp)} use${m.hp === 1 ? '' : 's'}` : ''}${m.rate ? ` that heals up to ${number(m.rate)} HP a use` : ''}${m.useTime ? `, taking ${number(m.useTime)} s to apply` : ''}.`);
    const fix = m.treats.filter(t => !t.stops), stop = m.treats.filter(t => t.stops);
    if (fix.length) bits.push(`It cures ${fix.map(t => esc(medTreat(t))).join(', ')}.`);
    if (stop.length) bits.push(`It ${stop.map(t => esc(medTreat(t))).join(' and ')}.`);
    if (m.buffs.length) {
      const bad = m.buffs.filter(b => b.bad).length;
      const end = Math.max(...m.buffs.map(b => b.delay + b.duration));
      bits.push(`Once ${m.food ? 'used' : 'injected'} it gives ${m.buffs.length - bad} effect${m.buffs.length - bad === 1 ? '' : 's'}${bad ? ` and ${bad} side effect${bad === 1 ? '' : 's'}` : ''}, all over within ${number(end)} s.`);
    }
    if (m.health.length) bits.push(`Using ${m.amount > 1 ? 'all of it' : 'it'} changes ${m.health.map(h => `${esc(h.label.toLowerCase())} by ${rsigned(h.value)}`).join(' and ')}.`);
    return `<p class="ih-lead">${bits.join(' ')}</p>`;
  }

  function medBox(m) {
    const rows = [
      m.kind ? ['Type', esc(m.kind)] : null,
      m.amount > 1 ? ['Amount', `${number(m.amount)} units`] : null,
      m.hp ? [m.rate ? 'HP resource' : 'Uses', number(m.hp)] : null,
      m.rate ? ['Heals per use', `up to ${number(m.rate)} HP`] : null,
      m.useTime ? ['Use time', `${number(m.useTime)} s`] : null,
      ...m.treats.map(t => [t.stops ? 'Holds off' : 'Cures', `${esc(t.label)}${t.cost ? `<div class="sub">${number(t.cost)} HP</div>` : t.duration ? `<div class="sub">for ${number(t.duration)} s</div>` : ''}`]),
      ...m.health.map(h => [esc(h.label), `<span class="${h.value < 0 ? 'med-bad' : 'med-good'}">${rsigned(h.value)}</span>`]),
      m.buffs.length ? ['Effects', `${m.buffs.filter(b => !b.bad).length} good, ${m.buffs.filter(b => b.bad).length} bad`] : null
    ].filter(Boolean);
    return sideBox(m.food ? 'When used' : 'Medical', rows);
  }

  // What a med does, as one table: what it fixes, then its effects, then its side effects. A column that reads the
  // same on every row is dropped and said once under the table instead.
  function medCards(m) {
    if (!m.treats.length && !m.buffs.length && !m.health.length) return '';
    const from = m.food ? 'using it' : 'the injection';
    const span = v => v < 60 ? `${number(v)} s` : `${Math.floor(v / 60)} min${v % 60 ? ` ${number(v % 60)} s` : ''}`;
    const rows = [
      ...m.treats.map(t => ({ group: 'Fixes', label: t.label, change: t.stops ? 'Holds it off' : `Cures it${t.cost ? ` <span class="sub">for ${number(t.cost)} HP of the item</span>` : ''}`,
        start: 0, length: t.stops ? t.duration : null, chance: 1 })),
      // what using it changes at once, such as energy and hydration
      ...m.health.map(h => ({ group: 'Effects', bad: h.value < 0, good: h.value > 0, label: h.label,
        change: `${rsigned(h.value)}${m.amount > 1 ? ' <span class="sub">all of it</span>' : ''}`, start: 0, length: null, chance: 1 })),
      ...[...m.buffs].sort((a, b) => a.delay - b.delay || a.duration - b.duration).map(b => ({ group: b.bad ? 'Side effects' : 'Effects', bad: b.bad, good: !b.bad,
        label: b.label, change: medAmount(b), start: b.delay, length: b.duration, chance: b.chance }))
    ];
    const timed = rows.filter(r => r.group !== 'Fixes' && r.length);
    const one = (list, f) => list.length && list.every(r => f(r) === f(list[0]));
    const showStart = rows.some(r => r.start) && !one(rows, r => r.start);
    const showLength = !one(rows, r => r.length);
    const showChance = rows.some(r => r.chance < 1) && !one(timed, r => r.chance);
    const notes = [];
    if (!showStart && rows[0].start) notes.push(`Everything starts ${span(rows[0].start)} after ${from}.`);
    if (!showLength && rows[0].length) notes.push(`Everything lasts ${span(rows[0].length)}.`);
    if (!showChance && timed.length && timed[0].chance < 1) notes.push(`Each effect has a ${rpct(timed[0].chance)} chance.`);
    if (m.treats.some(t => t.stops)) notes.push('What it holds off comes back when the time runs out.');
    if (timed.length && (showStart || showLength)) notes.push(`Times count from ${from}.`);
    const cols = 2 + showStart + showLength + showChance;
    const groups = [...groupBy(rows, r => r.group)];
    const body = part => part.map(([g, list]) => `${groups.length > 1 ? `<tr class="med-group"><td colspan="${cols}">${g}</td></tr>` : ''}${list.map(r => `<tr>
      <td class="${r.bad ? 'med-bad' : r.good ? 'med-good' : ''}">${esc(r.label)}</td><td class="num">${r.change}</td>
      ${showStart ? `<td class="num">${r.start ? span(r.start) : 'At once'}</td>` : ''}${showLength ? `<td class="num">${r.length ? span(r.length) : ''}</td>` : ''}
      ${showChance ? `<td class="num">${r.chance < 1 ? rpct(r.chance) : ''}</td>` : ''}</tr>`).join('')}`).join('');
    const table = part => `<table class="wt med-table"><thead><tr><th>${m.buffs.length || m.health.length ? 'Effect' : 'Condition'}</th><th class="num">Change</th>
      ${showStart ? '<th class="num">Starts</th>' : ''}${showLength ? '<th class="num">Lasts</th>' : ''}${showChance ? '<th class="num">Chance</th>' : ''}</tr></thead><tbody>${body(part)}</tbody></table>`;
    // a two-column table would leave a gap across the middle, so it goes in two halves: by group where there are
    // groups, else by rows
    let tables = table(groups);
    // a few rows of name and change (a food's or drink's figures) as a strip of tiles: a two-column table that short
    // leaves a gap across the middle
    if (cols === 2 && rows.length < 4) {
      tables = `<div class="st-row">${rows.map(r => `<div class="st ${r.bad ? 'bad' : r.good ? 'good' : ''}"><span class="st-l">${esc(r.label)}</span><span class="st-v">${r.change}</span></div>`).join('')}</div>`;
    } else if (cols === 2) {
      let halves;
      if (groups.length > 1) {
        let n = 0, cut = groups.findIndex(([, l]) => (n += l.length + 1) >= (rows.length + groups.length) / 2) + 1;
        cut = Math.min(Math.max(cut, 1), groups.length - 1);
        halves = [groups.slice(0, cut), groups.slice(cut)];
      } else {
        const [g, list] = groups[0], h = Math.ceil(list.length / 2);
        halves = [[[g, list.slice(0, h)]], [[g, list.slice(h)]]];
      }
      tables = `<div class="med-split">${halves.map(table).join('')}</div>`;
    }
    return card(m.buffs.length || m.health.length ? 'What it does' : 'What it fixes', `${tables}
      ${notes.length ? `<div class="src">${notes.join(' ')}</div>` : ''}`, rows.length);
  }

  // every other item of the same kind, for the bottom of a med's page
  function medKin(m) {
    const out = [];
    const sibs = m.siblings || [];
    if (sibs.length > 1) {
      // only the columns some item of this kind fills
      const has = { hp: sibs.some(s => s.hp), rate: sibs.some(s => s.rate), amount: sibs.some(s => s.amount > 1), useTime: sibs.some(s => s.useTime), treats: sibs.some(s => s.treats.length) };
      // one column for each thing using them changes (energy, hydration, ...), in the order the server lists them
      const effects = [...new Map(sibs.flatMap(s => s.health || []).map(h => [h.effect, h.label]))];
      const cell = (s, e) => { const v = (s.health || []).find(h => h.effect === e)?.value; return `<td class="num${v < 0 ? ' med-bad' : v > 0 ? ' med-good' : ''}">${v ? rsigned(v) : ''}</td>`; };
      out.push(card(`Other ${(m.kind || 'items').toLowerCase()}${/s$/.test(m.kind || 's') ? '' : 's'}`, `<table class="wt"><thead><tr><th>Item</th>${has.hp ? '<th class="num">HP or uses</th>' : ''}${has.rate ? '<th class="num">Heals per use</th>' : ''}${has.amount ? '<th class="num">Units</th>' : ''}${effects.map(([, l]) => `<th class="num">${esc(l)}</th>`).join('')}${has.useTime ? '<th class="num">Use time</th>' : ''}${has.treats ? '<th>Fixes</th>' : ''}</tr></thead><tbody>
        ${sibs.map(s => `<tr${s.self ? ' class="here"' : ''}><td class="with-icon">${icon(s.id, 'sm')}<a data-item="${esc(s.id)}">${esc(s.name)}</a>${s.mod != null ? ` <span class="sub">(added by ${esc(itemMod(s.id, s) || 'a mod')})</span>` : ''}</td>
          ${has.hp ? `<td class="num">${s.hp ? number(s.hp) : ''}</td>` : ''}${has.rate ? `<td class="num">${s.rate ? number(s.rate) : ''}</td>` : ''}${has.amount ? `<td class="num">${s.amount > 1 ? number(s.amount) : ''}</td>` : ''}
          ${effects.map(([e]) => cell(s, e)).join('')}${has.useTime ? `<td class="num">${s.useTime ? `${number(s.useTime)} s` : ''}</td>` : ''}
          ${has.treats ? `<td class="sub">${s.treats.map(t => esc(t.label)).join(', ')}</td>` : ''}</tr>`).join('')}</tbody></table>
        <div class="src">Every item of this kind on this server, mods included, ${has.hp ? 'most HP first' : 'biggest effect first'}.</div>`, sibs.length));
    }
    return out.join('');
  }

  // heavy or light, as the server marks it: it decides which Vests skill wearing it trains
  const armorType = x => x.heavy && x.heavy !== 'None' ? (x.heavy === 'Heavy' ? 'Heavy' : 'Light') : '';
  function armorPage(r) {
    const kinds = Object.keys(ARMOR_KINDS).filter(k => r.armor.some(x => x.kind === k));
    const shown = r.armor.filter(x => !/_(dev|test)$/i.test(x.name) && (!ruleFilter || x.kind === ruleFilter));
    const at = x => armorWith === 'best' ? x.best : x.sold;
    const cols = [['name', 'Item', x => x.name], ['armorClass', 'Class', x => at(x).armorClass, 'num'], ...(r.armor.some(x => x.heavy && x.heavy !== 'None') ? [['heavy', 'Type', x => armorType(x)]] : []), ['durability', 'Durability', x => at(x).durability, 'num'],
      ['zones', 'Covers', x => at(x).zones.length], ['slots', 'Plate slots', x => x.slots.length, 'num'], ['weight', 'Weight', x => at(x).weight, 'num'],
      ['speed', 'Speed', x => at(x).speed, 'num'], ['turn', 'Turning', x => at(x).turn, 'num'], ['ergo', 'Ergonomics', x => at(x).ergo, 'num'], ['cells', 'Space', x => x.cells, 'num']];
    const rowsFor = list => sortRows(list, cols).map(x => {
      const s = at(x);
      const row = `<tr class="armor-row${armorOpen.has(x.id) ? ' open' : ''}" data-armor="${x.id}"><td><a data-item="${x.id}">${esc(x.name)}</a>${x.modded ? `<div class="sub">${esc(itemMod(x.id, x) || 'a mod')}</div>` : ''}</td>
        <td class="num">${s.armorClass || ''}</td>${cols.some(c => c[0] === 'heavy') ? `<td>${armorType(x)}</td>` : ''}<td class="num">${s.durability || ''}</td><td class="zones">${zoneList(s.zones)}</td>
        <td class="num">${x.slots.length ? `<a>${x.slots.length}</a>` : ''}</td><td class="num">${kg(s.weight)}</td>
        <td class="num">${signedPct(s.speed)}</td><td class="num">${signedPct(s.turn)}</td><td class="num">${signedPct(s.ergo)}</td><td class="num">${x.cells ?? ''}</td></tr>`;
      return row + (armorOpen.has(x.id) ? armorDetail(x, r.plates, cols.length) : '');
    }).join('');
    const table = list => `<div class="wide-table"><table class="wt sortable-table armor-table"><thead><tr>${sortHead(cols)}</tr></thead><tbody>${rowsFor(list)}</tbody></table></div>`;
    const filter = `<div class="toolbar"><label class="qbar-map">Kind <select data-rfilter><option value="">Everything</option>${kinds.map(k => `<option value="${k}"${k === ruleFilter ? ' selected' : ''}>${ARMOR_KINDS[k]}</option>`).join('')}</select></label>
      <label class="qbar-map">Figures <select data-armorwith><option value="sold"${armorWith === 'sold' ? ' selected' : ''}>As sold</option><option value="best"${armorWith === 'best' ? ' selected' : ''}>Best plates here</option></select></label>
      <span class="sub">${shown.length} items, modded ones included. Click a row for every plate that fits it, a name for its page, a heading to sort.</span>${sortReset()}</div>`;
    const materials = r.materials ? card('Materials', `<p>Softer materials wear out faster when hit, and some lose more of their maximum each time they're repaired.</p><table class="wt"><thead><tr><th>Material</th><th class="num">How fast it wears</th><th class="num">Max lost per repair</th></tr></thead><tbody>
      ${Object.entries(r.materials).map(([m, x]) => `<tr><td>${esc(matName(m))}</td><td class="num">${x.destructibility ?? ''}</td><td class="num">${x.repairDegradation != null ? `${Math.round(x.repairDegradation * 100)}%` : ''}</td></tr>`).join('')}</tbody></table>`) : '';
    const order = Object.keys(ARMOR_KINDS);
    return filter + (ruleFilter || ruleSort ? table(shown) : [...groupBy(shown, x => x.kind)].sort((a, b) => order.indexOf(a[0]) - order.indexOf(b[0]))
      .map(([kind, list]) => card(ARMOR_KINDS[kind] || esc(kind), table(list.sort((a, b) => at(b).armorClass - at(a).armorClass || a.name.localeCompare(b.name))), list.length)).join('')) + materials;
  }

  const SKILL_GROUPS = [['Physical', ['Endurance', 'Strength', 'Vitality', 'Health', 'StressResistance', 'Metabolism', 'Immunity']],
    ['Mental', ['Perception', 'Intellect', 'Attention', 'Charisma', 'Memory']],
    ['Practical', ['CovertMovement', 'Search', 'MagDrills', 'AimDrills', 'RecoilControl', 'Surgery', 'Crafting', 'HideoutManagement', 'TroubleShooting', 'WeaponTreatment', 'LightVests', 'HeavyVests']]];

  // Pick a skill on the left, read it on the right: nothing to expand, and the page stays one screen long.
  function skillsPage(r) {
    const known = new Set(SKILL_GROUPS.flatMap(g => g[1]));
    const mine = name => profile?.player?.skills?.find(s => s.name === name)?.level;
    const row = s => [s.id, esc(s.name), mine(s.name) != null ? `Level ${mine(s.name)}` : null];
    // a skill the server trains by firing (its settings have a shot action) is a weapon skill; anything else no group
    // names (a mod's own skill) goes under Other
    const firing = sk => Object.keys(sk.settings || {}).some(k => /ShotAction$/.test(k));
    const groups = [...SKILL_GROUPS.map(([title, ids]) => [title, ids.map(id => r.skills.find(s => s.id === id)).filter(Boolean).map(row)]),
      ['Weapons', r.skills.filter(s => !known.has(s.id) && firing(s)).map(row)],
      ['Other', r.skills.filter(s => !known.has(s.id) && !firing(s)).map(row)]].filter(g => g[1].length);
    const s = r.skills.find(x => x.id === rulePick) || r.skills.find(x => x.id === groups[0][1][0][0]);
    if (!rulePick) rulePick = s.id;
    return `${facts([
        ['Points for a level', r.pointsPerLevel ?? ''],
        ['Fatigue after', `${r.fatiguePoints ?? ''} point${r.fatiguePoints === 1 ? '' : 's'}`, 'of one skill in a row, then it gains less'],
        ['Fatigue wears off', r.fatigueReset != null ? duration(r.fatigueReset) : ''],
        ['Skills fade if unused', r.atrophy ? 'Yes' : 'No']
      ])}
      ${pickPanel(groups, `<h3>${esc(s.name)}</h3>${mine(s.name) != null ? `<p class="sub">You're at level ${mine(s.name)}.</p>` : ''}${s.text ? `<p>${esc(s.text)}</p>` : ''}
        ${skillEffects(s, mine(s.name))}
        ${s.how ? `<h4 class="req-h">How it levels</h4><p>${esc(s.how)}</p>` : ''}
        <details class="skill-raw"><summary>Points you get for each action</summary>${settingsTable(s.settings)}<p class="sub">Each of these is how many skill points that action earns, before fatigue.</p></details>`)}`;
  }

  // the game writes its bonuses as "Reduces pain shock chance by [{0:0.#%}]": the number goes in a column instead
  const effectWords = t => t.replace(/\s*\[?\{0[^}]*\}%?\]?\s*/g, ' ').replace(/\s+by\s*$/, '').replace(/\s+$/, '');
  const effectValue = (t, v) => /\{0[^}]*%\}/.test(t) ? `${Math.round(v * 1000) / 10}%` : /\{0[^}]*\}%/.test(t) ? `${Math.round(v * 10) / 10}%` : `${Math.round(v * 100) / 100}`;

  function skillEffects(s, level) {
    const steps = [10, 25, 50];
    const rows = (s.effects || []).filter(e => e.perLevel > 0);
    const perks = (s.effects || []).filter(e => !(e.perLevel > 0));
    const at = (e, n) => effectValue(e.text, e.perLevel * n);
    return `${rows.length ? `<table class="wt skill-steps"><thead><tr><th>What you get</th>${level != null ? '<th class="num">You now</th>' : ''}${steps.map(n => `<th class="num">Level ${n}</th>`).join('')}<th class="num">Elite (51)</th></tr></thead><tbody>
      ${rows.map(e => `<tr><td>${esc(effectWords(e.text))}</td>${level != null ? `<td class="num"><b>${at(e, Math.min(level, 50))}</b></td>` : ''}${steps.map(n => `<td class="num">${at(e, n)}</td>`).join('')}<td class="num elite">${e.elite != null ? effectValue(e.text, e.elite) : at(e, 50)}</td></tr>`).join('')}</tbody></table>` : ''}
      ${perks.length ? `<h4 class="req-h">At Elite</h4><ul class="plain">${perks.map(e => `<li>${esc(e.text.replace(/\s*\[?\{0[^}]*\}%?\]?/g, e.elite != null ? ` ${effectValue(e.text, e.elite)}` : '').replace(/\n/g, ', '))}</li>`).join('')}</ul>` : ''}`;
  }

  function levelsPage(r) {
    let total = 0;
    const all = r.table.map((step, i) => {
      total += step;
      const level = i + 1;
      const unlocks = [];
      if (level === r.fleaLevel) unlocks.push('<a data-page="rules" data-id="flea">Flea market</a>');
      for (const t of r.traders) for (const l of t.levels) if (l.minLevel === level && level > 1 && l.level > 1) unlocks.push(`${data.traders[t.id] ? avatar(t.id) : ''}${esc(t.name)} LL${l.level}`);
      const quests = data.quests.filter(q => q.level === level);
      if (quests.length) unlocks.push(`${quests.length} quest${quests.length === 1 ? '' : 's'}`);
      return { level, total, next: r.table[i + 1], unlocks };
    });
    const everyLevel = rulePick === 'all';
    const shown = all.filter(x => everyLevel || x.level === profile?.level || x.unlocks.some(u => !u.endsWith('quest') && !u.endsWith('quests')));
    const raid = r.raid || {}, kill = r.kill || {};
    const combo = (kill.combo || []).map(c => c.percent).filter(p => p > 0);
    return `<div class="two wide-left"><div class="col">${card(everyLevel ? 'Every level' : 'Levels where something opens', `<div class="toolbar"><button class="chip${everyLevel ? ' on' : ''}" data-rpick="${everyLevel ? 'few' : 'all'}">Show every level</button></div>
        <table class="wt lvl-table"><thead><tr><th class="num">Level</th><th class="num">Total XP</th><th>Opens</th></tr></thead><tbody>
        ${shown.map(x => `<tr${profile?.level === x.level ? ' class="here"' : ''}><td class="num">${x.level}</td><td class="num">${number(x.total)}</td><td class="lvl-opens">${x.unlocks.join('<span class="sep"></span>')}</td></tr>`).join('')}</tbody></table>`)}</div>
      <div class="col">${card('Finishing a raid', `<ul class="plain">
          <li>Survive and you get <b>${number(raid.survived_exp_reward ?? 0)} XP</b>, and everything you earned in the raid is multiplied by ${raid.survivedMult}.</li>
          <li>Leave too fast (under ${duration(raid.survived_seconds_requirement ?? 0)}, or under ${number(raid.survived_exp_requirement ?? 0)} XP) and it's a run-through: ${number(raid.runner_exp_reward ?? 0)} XP and only ×${raid.runnerMult}.</li>
          <li>Die and you keep what you earned (×${raid.killedMult}). Go missing and it's ×${raid.miaMult} plus ${number(raid.mia_exp_reward ?? 0)} XP.</li>
          ${raid.leftMult === 0 ? '<li>Quit the raid and you get nothing.</li>' : ''}</ul>`)}
        ${card('Kills', `<ul class="plain">
          <li>A headshot is worth ×${kill.botHeadShotMult} on a bot and ×${kill.pmcHeadShotMult} on a PMC.</li>
          ${kill.longShotDistance ? `<li>A kill from past ${kill.longShotDistance} m counts as a long shot.</li>` : ''}
          ${combo.length ? `<li>Kills in a row pay more, up to +${Math.max(...combo)}%.</li>` : ''}</ul>`)}
        ${card('Small stuff', `<ul class="plain"><li>Opening a locked door: ${r.doors?.open} XP. Breaching one: ${r.doors?.breach} XP.</li>
          <li>Healing, food and water pay ${r.heal?.expForHeal} XP per point restored.</li></ul>`)}</div></div>`;
  }

  // what each injury is, as you'd explain it to someone in the raid with you
  const EFFECTS = {
    LightBleeding: ['Light bleed', 'Slowly drains the part it\'s on until you bandage it.'],
    HeavyBleeding: ['Heavy bleed', 'Drains health fast and leaves a blood trail. Needs a tourniquet or a hemostat, not a bandage.'],
    Fracture: ['Fracture', 'A broken limb. You limp on a leg and shake on an arm until a splint goes on.'],
    BreakPart: ['Blacked-out part', 'A part at zero health. Hits to it spread to the rest of you, and only surgery brings it back.'],
    Pain: ['Pain', 'Comes from injuries. Your hands shake and the screen blurs until painkillers kick in.'],
    PainKiller: ['Painkiller', 'What keeps pain off you while it lasts.'],
    Contusion: ['Contusion', 'A hit to the head or a blast. Muffled hearing and a shaky view for a while.'],
    Stun: ['Stun', 'You lose control for a moment after a big hit.'],
    Flash: ['Flash', 'A flashbang in the face. You go blind for a few seconds.'],
    Tremor: ['Tremor', 'Hands shake, so aiming gets harder.'],
    Dehydration: ['Dehydration', 'Run out of water and you start losing health.'],
    Exhaustion: ['Exhaustion', 'Run out of energy and you start losing health.'],
    Intoxication: ['Poison', 'Slowly hurts you until an antidote or a good medkit clears it.'],
    RadExposure: ['Radiation', 'Lingering damage from radioactive areas.'],
    MildMusclePain: ['Muscle pain', 'What you get from overdoing it. Stamina comes back slower.'],
    SevereMusclePain: ['Bad muscle pain', 'The same but worse.'],
    ChronicStaminaFatigue: ['Stamina fatigue', 'Lower maximum stamina after running yourself into the ground.'],
    LowEdgeHealth: ['Low health', 'What happens once a part is nearly gone.'],
    Existence: ['Hunger and thirst', 'How fast energy and water drain just from being alive.'],
    Regeneration: ['Regeneration', 'Slow healing between raids.'],
    Berserk: ['Berserk', 'A stim effect.'],
    ZombieInfection: ['Infection', 'From the Halloween event.']
  };

  // an injury's name where a med's own properties call it something else
  const TREAT_KEY = { BreakPart: 'DestroyedPart' };

  // the body as a plain figure with each part's health on it
  function bodyMap(parts) {
    const hp = Object.fromEntries(parts.map(p => [p.part, p.hp]));
    const part = (k, shape, x, y) => `${shape}<text x="${x}" y="${y}">${hp[k] ?? ''}</text>`;
    return `<svg class="body-map" viewBox="0 0 220 330" role="img" aria-label="Health of each body part">
      ${part('Head', '<circle cx="110" cy="34" r="26"/>', 110, 40)}
      ${part('Chest', '<rect x="72" y="66" width="76" height="72" rx="8"/>', 110, 108)}
      ${part('Stomach', '<rect x="76" y="142" width="68" height="50" rx="8"/>', 110, 173)}
      ${part('LeftArm', '<rect x="32" y="68" width="34" height="122" rx="14"/>', 49, 134)}
      ${part('RightArm', '<rect x="154" y="68" width="34" height="122" rx="14"/>', 171, 134)}
      ${part('LeftLeg', '<rect x="72" y="196" width="36" height="128" rx="14"/>', 90, 266)}
      ${part('RightLeg', '<rect x="112" y="196" width="36" height="128" rx="14"/>', 130, 266)}
    </svg>`;
  }

  function healthPage(r) {
    const total = r.parts.reduce((n, p) => n + (p.hp || 0), 0);
    const hp = r.healPrice || {};
    // every effect the server has settings for, bar the ones that only time meds and stims (start delays, buffs)
    const shown = Object.entries(r.effects || {}).filter(([k, v]) => v && typeof v === 'object' && !('Dummy' in v) && !('Buffs' in v) && !Object.keys(v).some(x => /StartDelay$/.test(x)));
    const effectName = k => esc(r.effectNames?.[k] || human(k));
    if (!shown.some(([k]) => k === rulePick) && shown.length) rulePick = shown.find(([k]) => k === 'Fracture')?.[0] || shown[0][0];
    const picked = (r.effects || {})[rulePick];
    const treats = r.treats?.[TREAT_KEY[rulePick] || rulePick] || [];
    return `<div class="two wide-left"><div class="col">${card('Your body', `<div class="body-wrap">${bodyMap(r.parts)}<div>
          <p>${total} health in all. Fall damage and fire hit each part differently, and when a blacked-out part takes a hit, the damage carries on into the rest of you.</p>
          <table class="wt"><thead><tr><th>Part</th><th class="num">Falls and fire</th><th class="num">Carried on</th></tr></thead><tbody>
          ${r.parts.map(p => `<tr><td>${esc(r.partNames?.[p.part] || human(p.part))}</td><td class="num">×${p.environment}</td><td class="num">×${p.overDamage}</td></tr>`).join('')}</tbody></table></div></div>`)}</div>
      <div class="col">${facts([
          r.body?.Energy && ['Energy', r.body.Energy.Maximum],
          r.body?.Hydration && ['Water', r.body.Hydration.Maximum],
          r.falling && ['Safe fall', `${r.falling.SafeHeight} m`, `then ${r.falling.DamagePerMeter} damage a metre`]
        ])}
        ${card(`Healing at ${esc(traderName(data.therapistId))}`, `<p>Between raids the charge is ${hp.HealthPointPrice != null ? money(hp.HealthPointPrice, ROUBLES) : '?'} per point of health${hp.HydrationPointPrice ? `, ${money(hp.HydrationPointPrice, ROUBLES)} per point of water` : ''}${hp.EnergyPointPrice ? `, ${money(hp.EnergyPointPrice, ROUBLES)} per point of energy` : ''}, plus a fee for each injury.</p>
          ${hp.TrialLevels || hp.TrialRaids ? `<p>New characters heal free up to level ${hp.TrialLevels}, for their first ${hp.TrialRaids} raids.</p>` : ''}`)}</div></div>
      ${card('Injuries', pickPanel([['', shown.map(([k, v]) => [k, effectName(k), v.RemovePrice ? money(v.RemovePrice, ROUBLES) : null])]],
        `<h3>${effectName(rulePick)}</h3>${EFFECTS[rulePick]?.[1] ? `<p>${EFFECTS[rulePick][1]}</p>` : ''}
        ${facts([picked?.RemovePrice && [`${esc(traderName(data.therapistId))} charges`, money(picked.RemovePrice, ROUBLES)], picked?.HealExperience && ['Healing it gives', `${picked.HealExperience} XP`]])}
        ${treats.length ? `<h4 class="req-h">What fixes it on this server</h4><div class="treat-grid">${treats.map(i => `<div data-item="${esc(i.id)}">${icon(i.id, 'sm')}<a>${esc(i.name)}</a>${modMark(i)}</div>`).join('')}</div>` : ''}`))}`;
  }

  // Fence's levels, in words: what each of the server's settings changes
  const KARMA_COLS = [['SavageCooldownModifier', 'Scav cooldown', v => `×${v}`, 'Multiplies the wait between scav raids.'],
    ['PriceModifier', 'Trader prices', v => `×${v}`, 'What he charges. Under 1 is cheaper.'],
    ['ScavCaseTimeModifier', 'Scav case', v => `×${v}`, 'How long the Scav Case in your hideout takes.'],
    ['PaidExitCostModifier', 'Paid exits', v => `×${v}`, 'What extracts that charge you cost.'],
    ['AvailableExits', 'Exits', v => v, 'How many extracts you get as a scav.'],
    ['HostileScavs', 'Scavs hostile', val, 'Whether other scavs shoot you when you play as one.'],
    ['HostileBosses', 'Bosses hostile', val, 'Whether bosses and their guards shoot you as a scav.'],
    ['ScavEquipmentSpawnChanceModifier', 'Scav gear', v => `${v > 0 ? '+' : ''}${v}%`, 'Better or worse kit on your scav.'],
    ['TransitGridSize', 'Transit stash', v => `${v.x} × ${v.y}`, 'How big an item you can send on during a transit between maps, in cells. It\'s a size, not a percentage.'],
    ['DeliveryGridSize', 'BTR delivery', v => `${v.x} × ${v.y}`, 'How much the BTR driver will carry back to your stash, in cells.']];

  function karmaPage(r) {
    const scavHelp = l => { const v = ['BotFollowChance', 'BotHelpChance', 'BotGetInCoverChance', 'BotSpreadoutChance', 'BotStopChance', 'BotApplySilenceChance'].map(k => l.settings?.[k]).filter(x => x != null); return v.length ? `${Math.round(v.reduce((a, b) => a + b, 0) / v.length)}%` : ''; };
    return `${facts([
        r.standingMin != null && ['Standing runs from', `${r.standingMin} to ${r.standingMax}`],
        ['Scav cooldown', duration(r.scavCooldown), 'at standing 0'],
        r.paidExitStanding != null && ['Paying for an exit', `+${r.paidExitStanding}`, 'standing, as a scav'],
        r.pmcKillMultiplier != null && ['Killing PMCs as a scav', `×${r.pmcKillMultiplier}`, 'on the standing it changes']
      ])}
      ${card('What each standing gets you', `<div class="wide-table"><table class="wt"><thead><tr><th class="num">Standing</th>${KARMA_COLS.map(c => `<th title="${esc(c[3])}">${c[1]}</th>`).join('')}<th title="How often friendly scavs follow you, help you or do what you do">Scavs back you up</th></tr></thead><tbody>
        ${r.levels.map(l => `<tr><td class="num">${l.standing}</td>${KARMA_COLS.map(([k, , f]) => `<td>${l.settings?.[k] != null ? f(l.settings[k]) : ''}</td>`).join('')}<td>${scavHelp(l)}</td></tr>`).join('')}</tbody></table></div>
        <p class="sub">Each row applies from that standing up to the next one.</p>
        <dl class="glossary">${KARMA_COLS.map(c => `<div><dt>${c[1]}</dt><dd>${c[3]}</dd></div>`).join('')}<div><dt>Scavs back you up</dt><dd>The average chance a friendly scav follows you, helps you in a fight or takes cover with you.</dd></div></dl>`)}
      ${card('Standing lost or gained per kill', r.kills.length ? `<p>This only counts while you're playing as a scav. A minus hurts your standing with ${esc(traderName(data.fenceId))}.</p><div class="kill-grid">
          ${r.kills.map(k => `<div data-page="bot" data-id="${esc(k.role)}"><a>${esc(k.name)}</a><b class="${k.standing < 0 ? 'bad' : 'good'}">${k.standing > 0 ? '+' : ''}${k.standing}</b></div>`).join('')}</div>` : '<p class="hint">No bot changes Fence standing when killed.</p>', r.kills.length)}`;
  }

  // Each group of guns under its kind (assault rifles, pistols ...), with its pictures rather than a wall of names.
  function rewardText(x) {
    if (x.itemName) return `${x.count > 1 ? `${number(x.count)} × ` : ''}${icon(x.item, 'sm')}${itemA(x.item, x.itemName)}`;
    if (x.type === 'TraderStanding') return `${x.value > 0 ? '+' : ''}${x.value} standing with ${esc(x.trader || '')}`;
    if (x.type === 'Experience') return `${number(x.value)} experience`;
    if (x.type === 'Skill') return `${esc(x.target || 'skill')} +${x.value}`;
    if (x.type === 'TraderUnlock') return `Unlocks ${esc(x.trader || '')}`;
    if (x.type === 'CustomizationDirect' || x.type === 'CustomizationOffer') return `Cosmetic: ${esc(x.target || '')}`;
    return esc(x.target || human(x.type || ''));
  }

  let achRarity = '';
  // Achievements by rarity, each with its picture: the ones you've earned are marked and come first.
  function achievementsPage(r) {
    const order = ['Common', 'Rare', 'Legendary'];
    const text = ruleFilter.trim().toLowerCase();
    const earned = new Map((profile?.player?.achievements || []).map(a => [a.id, a]));
    // the game lists some twice, once for each side's version of the same achievement
    const list = [...new Map(r.list.map(a => [`${a.name}|${a.rarity}|${a.text || ''}`, a])).values()];
    const hasIt = a => r.list.some(x => x.name === a.name && earned.has(x.id));
    const hit = a => (!achRarity || (achRarity === 'earned' ? hasIt(a) : achRarity === 'gifts' ? a.rewards.length : a.rarity === achRarity)) && (!text || a.name.toLowerCase().includes(text) || (a.text || '').toLowerCase().includes(text));
    const rarities = [...new Set(list.map(a => a.rarity).filter(Boolean))].sort((a, b) => order.indexOf(a) - order.indexOf(b));
    const one = a => `<div class="ach-row${hasIt(a) ? ' got' : ''}"><img src="${esc(a.image || '')}" alt="" loading="lazy" onerror="this.style.visibility='hidden'"><div>
      <b>${esc(a.name)}</b>${hasIt(a) ? ' <span class="ach-got">Earned</span>' : ''}${a.side && a.side !== 'All' ? ` <span class="sub">${a.side === 'Pmc' ? 'PMC' : esc(a.side)}</span>` : ''}
      <div class="sub">${a.hidden && !hasIt(a) ? 'Hidden until you earn it.' : esc(a.text || '')}</div>
      ${a.rewards.length ? `<div class="ach-gives">Gives ${a.rewards.map(rewardText).join(', ')}</div>` : ''}</div></div>`;
    const groups = rarities.map(rarity => {
      const here = list.filter(a => a.rarity === rarity && hit(a)).sort((a, b) => hasIt(b) - hasIt(a) || a.name.localeCompare(b.name));
      return here.length ? card(esc(rarity), `<div class="ach-grid">${here.map(one).join('')}</div>`, here.length) : '';
    }).join('');
    const chip = (v, t) => `<button class="chip${achRarity === v ? ' on' : ''}" data-achrarity="${esc(v)}">${t}</button>`;
    return `<div class="toolbar"><input class="filter" id="ruleFind" placeholder="Filter by name" value="${esc(ruleFilter)}">
        ${chip('', `All ${list.length}`)}${rarities.map(x => chip(x, `${esc(x)} ${list.filter(a => a.rarity === x).length}`)).join('')}
        ${chip('gifts', `With a reward ${list.filter(a => a.rewards.length).length}`)}${profile ? chip('earned', `Earned ${list.filter(hasIt).length}`) : ''}</div>
      ${groups || '<p class="none">Nothing here.</p>'}`;
  }

  function prestigePage(r) {
    if (!r.levels.length) return '<p class="hint">This server has no prestige levels.</p>';
    const hideout = wiki('hideout', 'api/hideout');
    const areaName = type => (hideout && !hideout.error ? hideout.areas.find(a => String(a.type) === String(type))?.name : null) || `Hideout area ${type}`;
    const need = c => c.kind === 'Level' ? [`Level ${c.value}`, profile ? (profile.level || 0) >= c.value : null]
      : c.kind === 'Quest' ? [`Finish ${link(c.quest)}`, profile ? isDone(c.quest) : null]
      : c.kind === 'Skill' ? [`${esc(c.skill)} at level ${c.value}`, null]
      : c.kind === 'HideoutArea' ? [`<a data-page="hideout" data-id="${c.area}">${esc(areaName(c.area))}</a> at level ${c.value}`, null]
      : c.kind === 'HasItem' && c.items.length ? [c.items.length === 1 && c.items[0].id === ROUBLES ? `Pay ${money(c.value, ROUBLES)}` : `Hand in ${number(c.value)} × ${c.items.map(i => itemA(i.id, i.name)).join(' or ')}`, null]
      : [c.text ? esc(c.text) : esc(human(c.kind || '')), null];
    const mark = ok => ok == null ? '<span class="mark">•</span>' : ok ? '<span class="mark">✓</span>' : '<span class="mark">✕</span>';
    const gift = x => x.item ? `<div class="pg-gift" data-item="${esc(x.item.id)}">${icon(x.item.id)}<a>${esc(x.item.name)}</a></div>`
      : x.type === 'Skill' ? `<div class="pg-gift">${rewardIcon('SK')}<span>${esc(x.target || 'Skill')} +${x.value} points</span></div>`
      : `<div class="pg-gift">${rewardIcon(/dogtag/i.test(x.target || '') ? 'TAG' : 'COS')}<span>${esc(x.target || human(x.type || ''))}</span></div>`;
    const carry = t => {
      const stash = t?.stashConfig, skills = t?.skillConfig?.transferMultiplier, guns = t?.masteringConfig?.transferMultiplier;
      return [stash && `a stash of ${stash.xCellCount} × ${stash.yCellCount} cells for what you pick`, skills != null && `${Math.round(skills * 100)}% of your skill points`, guns != null && `${Math.round(guns * 100)}% of your weapon mastery`].filter(Boolean);
    };
    return r.levels.map(l => {
      const kept = carry(l.transfer);
      return `<section class="pg"><div class="pg-badge">${l.image ? `<img src="${esc(l.image)}" alt="" onerror="this.remove()">` : ''}<h3>Prestige ${l.level}</h3></div>
        <div><h4 class="req-h">What it takes</h4>${l.conditions.map(c => { const [t, ok] = need(c); return `<div class="req${ok == null ? '' : ok ? ' met' : ' unmet'}">${mark(ok)}<div>${t}</div></div>`; }).join('')}</div>
        <div><h4 class="req-h">What you get</h4><div class="pg-gifts">${l.rewards.map(gift).join('')}</div>
          ${kept.length ? `<p class="sub">You carry over ${kept.join(', ')}. Everything else starts again.</p>` : ''}</div></section>`;
    }).join('');
  }

  // a location id ("bigmap") as the map's name, once the map list is in
  function mapName(id) {
    const maps = wiki('maps', 'api/maps');
    return (Array.isArray(maps) ? maps.find(m => m.id.toLowerCase() === String(id).toLowerCase())?.name : null) || id;
  }

  // what an event actually does, for the ones SPT ships
  const EVENT_TEXT = {
    Halloween: 'Bosses get swapped about, cultists come out in force and the zombie infection can spread across the maps.',
    Christmas: 'Santa turns up, bots wear festive gear and gift boxes get into the loot.',
    NewYears: 'The same as Christmas, for the first day of the year.',
    AprilFools: 'One day of the game not taking itself seriously.'
  };


  function eventsPage(r) {
    const monthName = new Intl.DateTimeFormat(undefined, { month: 'short', timeZone: 'UTC' });
    const months = Array.from({ length: 12 }, (_, i) => monthName.format(Date.UTC(2025, i, 1)));
    const date = d => `${d.day} ${months[d.month - 1] || d.month}`;
    const yearDay = d => Math.floor((Date.UTC(2025, d.month - 1, d.day) - Date.UTC(2025, 0, 1)) / 864e5);
    const now = new Date();
    const today = yearDay({ month: now.getMonth() + 1, day: now.getDate() });
    const bar = (from, to, e) => `<span class="cal-bar${e.active ? ' on' : ''}" style="left:${from / 3.65}%;width:${Math.max((to - from + 1) / 3.65, 1)}%" title="${label(e)}: ${date(e.start)} to ${date(e.end)}"></span>`;
    const bars = e => { const a = yearDay(e.start), b = yearDay(e.end); return a <= b ? bar(a, b, e) : bar(a, 364, e) + bar(0, b, e); };
    const until = e => { let d = yearDay(e.start) - today; if (d < 0) d += 365; return e.active ? 'On now' : d === 0 ? 'Starts today' : `In ${d} day${d === 1 ? '' : 's'}`; };
    const list = [...r.events].sort((a, b) => ((yearDay(a.start) - today + 365) % 365) - ((yearDay(b.start) - today + 365) % 365));
    const label = e => esc(human(e.name));
    const zombies = r.events.find(e => e.settings?.zombieSettings?.mapInfectionAmount)?.settings.zombieSettings;
    return `${facts([
        ['Dates on this server', r.detection ? 'Followed' : 'Ignored', r.detection ? 'events start by themselves' : 'only events forced on will run'],
        ['On now', r.events.filter(e => e.active).map(label).join(', ') || 'Nothing'],
        ['Next up', list[0] ? `${label(list[0])}` : '', list[0] ? until(list[0]) : '']
      ])}
      ${card('The year', `<div class="cal"><div class="cal-months"><span></span><div>${months.map(m => `<span>${m}</span>`).join('')}</div></div>
        ${list.map(e => `<div class="cal-row"><span class="cal-name">${label(e)}</span><div class="cal-track">${bars(e)}<i style="left:${today / 3.65}%"></i></div></div>`).join('')}</div>
        <p class="sub">The line is today.</p>`)}
      <div class="two">${list.map(e => card(label(e), `<p>${EVENT_TEXT[e.type] || ''}</p>${facts([['Dates', `${date(e.start)} to ${date(e.end)}`], ['Allowed', e.enabled ? 'Yes' : 'No'], ['When', until(e)]])}`)).join('')}</div>
      ${r.bosses && Object.values(r.bosses).some(m => m?.length) ? card('Where the event bosses go', `<table class="wt"><tbody>${Object.entries(r.bosses).filter(([, maps]) => maps?.length)
        .map(([ev, maps]) => `<tr><th>${esc(human(ev.replace(/^halloween/, 'halloween ')))}</th><td>${maps.map(m => esc(mapName(m))).join(', ')}</td></tr>`).join('')}</tbody></table>`) : ''}
      ${zombies ? card('How much of each map gets infected', `<div class="kill-grid">${Object.entries(zombies.mapInfectionAmount).sort((a, b) => b[1] - a[1]).map(([m, v]) => `<div><span>${esc(mapName(m))}</span><b>${v}%</b></div>`).join('')}</div>`) : ''}`;
  }

  function circlePage(r) {
    const value = n => money(n, ROUBLES);
    const rows = r.times.map(t => `<div class="circle-step"><b>${duration(r.timeOverride > 0 ? r.timeOverride : t.seconds)}</b><span class="sub">${value(t.min)} to ${t.max >= 9999999 ? 'anything' : value(t.max)}</span></div>`).join('');
    const stack = list => list.map(i => `<span class="trade-item" data-item="${esc(i.id)}">${icon(i.id, 'sm')}<a>${esc(i.name)}</a></span>`).join('');
    return `${facts([
        r.rewardValue && ['You get back', `${Math.round(r.rewardValue.min * 100)}% to ${Math.round(r.rewardValue.max * 100)}%`, 'of what went in, in value'],
        ['At most', `${r.maxRewards} items`, 'from one sacrifice'],
        r.highValue && ['Better loot above', value(r.highValue)],
        r.craftThreshold && ['Quest and hideout items above', value(r.craftThreshold)]
      ])}
      ${r.times.length ? card('How long it takes', `<p>The more you put in, the longer you wait. Add up the value of everything on the circle and find it here.</p><div class="circle-steps">${rows}</div>`) : '<p class="hint">No circle settings on this server.</p>'}
      ${r.direct.length ? card('Set trades', `<p>Put exactly these in and you always get the same thing out, instead of a random reward.</p>
        <table class="wt trades"><thead><tr><th>Sacrifice</th><th>You get</th><th>Takes</th></tr></thead><tbody>
        ${r.direct.map(d => `<tr><td>${stack(d.give)}</td><td>${stack(d.get)}</td><td>${duration(d.seconds)}${d.repeatable ? '' : '<div class="sub">once only</div>'}</td></tr>`).join('')}</tbody></table>`, r.direct.length) : ''}`;
  }

  function containersPage(r) {
    const worth = c => c.width && c.height ? c.cells / (c.width * c.height) : 0;
    const table = list => `<table class="wt items-list"><thead><tr><th colspan="2">Container</th><th class="num">Takes up</th><th class="num">Holds</th><th class="num">Gain</th><th>What goes in</th><th class="num">Value</th></tr></thead><tbody>
      ${list.sort((a, b) => b.cells - a.cells || a.name.localeCompare(b.name)).map(c => `<tr data-item="${c.id}"><td class="pic-cell"><span class="pic">${pic(c.id)}</span></td><td><a>${esc(c.name)}</a>${modMark(c)}</td>
        <td class="num">${c.width}×${c.height}</td><td class="num">${c.cells}</td><td class="num">${worth(c) ? `×${Math.round(worth(c) * 10) / 10}` : ''}</td><td class="sub">${containerHolds(c)}</td>
        <td class="num">${c.price ? money(c.price, ROUBLES) : ''}</td></tr>`).join('')}</tbody></table>`;
    return `${card('Secure containers', `<p>Whatever's in here survives your death. You only ever wear one, so the only number that matters is how much it holds.</p>${table(r.containers.filter(c => c.secure))}`, r.containers.filter(c => c.secure).length)}
      ${card('Cases and other containers', `<p>Gain is the point of a case: how many cells you get back for each cell it takes up in your stash.</p>${table(r.containers.filter(c => !c.secure))}`, r.containers.filter(c => !c.secure).length)}`;
  }

  // what a container takes, in a line
  function containerHolds(c) {
    if (c.anything || (!c.allowed.length && !c.singles)) return `Anything${c.excluded ? `, bar ${number(c.excluded)} items and kinds` : ''}`;
    return [c.allowed.length ? esc(c.allowed.join(', ')) : '', c.singles ? `${c.allowed.length ? 'plus ' : ''}${number(c.singles)} single item${c.singles === 1 ? '' : 's'}` : ''].filter(Boolean).join(', ');
  }

  // a bot's name as this server calls it (its quests, maps and locale), once the list is in
  const bossName = id => botOf(id)?.name || Object.entries(wiki('botnames', 'api/botnames') || {}).find(([r]) => r.toLowerCase() === String(id).toLowerCase())?.[1] || id;
  // the bot list's entry for a role (any case), once the list is in
  const botOf = role => {
    const list = wikiCache.get('bots');
    return Array.isArray(list) ? list.find(b => b.roles.some(r => r.toLowerCase() === String(role).toLowerCase())) : null;
  };
  // what an extract needs, when its own tip doesn't say
  // what an extract asks of you: its icon, what to call it and its colour (none: just walk out)
  const EXIT_NEEDS = {
    None: ['', '', 'open'],
    TransferItem: ['', 'Pay to leave', 'pay'],
    WorldEvent: ['', 'Needs the power on', 'event'],
    ScavCooperation: ['', 'Co-op with a scav', 'coop'],
    Train: ['', 'Leave by train', 'train'],
    Reference: ['', 'Needs a set item', 'item'],
    Empty: ['', 'Without a backpack', 'item'],
    HasItem: ['', 'Needs an item', 'item']
  };

  // one side's extracts, those always open first
  function extractList(list) {
    const odds = c => c == null || c >= 100 ? ['always', 'sure'] : [`${Math.round(c)}%`, c >= 50 ? 'often' : 'rarely'];
    return list.sort((a, b) => (b.chance ?? 100) - (a.chance ?? 100) || a.name.localeCompare(b.name)).map(e => {
      const [, need, tone] = EXIT_NEEDS[e.requirement] || ['', esc((e.requirement || '').replace(/([a-z])([A-Z])/g, '$1 $2')), 'item'];
      const [chanceText, chanceTone] = odds(e.chance);
      return `<div class="ex-row"><div class="ex-main"><b>${esc(e.name)}</b>
        <div class="ex-tags">${need ? `<span class="ex-need ${tone}">${need}</span>` : ''}${e.tip ? `<span class="sub">${esc(e.tip)}</span>` : ''}${e.pmc && e.scav ? '<span class="ex-both">PMC and Scav</span>' : ''}</div></div>
        <span class="ex-chance ${chanceTone}">${chanceText}</span></div>`;
    }).join('');
  }

  function mapsPage() {
    const maps = wiki('maps', 'api/maps');
    if (!maps) return `${crumbs([null, 'Maps'])}<div class="loading">Loading the maps…</div>`;
    return `${crumbs([null, 'Maps'])}
      <div class="page-head"><div class="t"><h2>Maps</h2><p>A map's page has its extracts, boss spawns, keys, best loot and the quests you do there.</p></div></div>
      ${maps.error ? '<p class="hint">Couldn\'t load the maps.</p>' : mapsTable(maps)}`;
  }

  let mapOnScreen = null;
  // Factory by day and by night are one map with two sets of raid settings: one page, with a switch
  const oneFactory = maps => maps.filter(m => m.id !== 'factory4_night');

  function mapPage(id) {
    // the bot list names the bosses (mods' too) and links them
    wiki('bots', 'api/bots');
    const version = mapVersion(id);
    const asked = wiki(`map:${id}|${version}`, `api/map?id=${encodeURIComponent(id)}${version ? `&version=${encodeURIComponent(version)}` : ''}`);
    // while another version of the map loads, the one on screen stays, so the page doesn't jump
    const map = asked || (mapOnScreen?.id === id ? mapOnScreen.map : null);
    if (asked && !asked.error) mapOnScreen = { id, map: asked };
    if (!map) return `${crumbs(['maps', 'Maps'], [null, id])}<div class="loading">Loading the map…</div>`;
    if (map.error) return `${crumbs(['maps', 'Maps'], [null, id])}<div class="intro red">This server has no map ${esc(id)}.</div>`;
    Object.assign(data.items, map.items);
    const name = baseMap(map.name);
    const quests = data.quests.filter(q => q.maps.some(x => baseMap(x) === name) && !(prefs.hideLockedLists && shutOut(q.id)));
    const usedKeys = data.quests.flatMap(q => q.needs.filter(n => n.kind === 'key' && n.ids.length && (n.text.includes(`(${name})`) || q.maps.length === 1 && baseMap(q.maps[0]) === name)).map(n => ({ quest: q, need: n })));
    const keyIds = [...new Set(usedKeys.map(k => k.need.ids[0]))];
    const plan = profile ? mapPlan().find(p => p.map === name) : null;
    const pct = n => `${Math.round(n)}%`;
    const sections = [];
    const add = (anchor, title, body, n, ico) => { sections.push({ anchor, title, n, ico }); return `<div id="s-${anchor}">${card(title, body, n)}</div>`; };
    const pmcExits = map.extracts.filter(e => e.pmc), scavExits = map.extracts.filter(e => e.scav);
    const mapList = wiki('maps', 'api/maps');
    // versions of one map, as the server keys them: ids that share a stem and differ only after the last "_" (a day
    // and a night one), each named by that last part
    const stem = m => /_[^_]+$/.test(m) ? m.replace(/_[^_]+$/, '') : null;
    const twins = stem(id) && Array.isArray(mapList) ? mapList.filter(m => stem(m.id) === stem(id)).map(m => m.id) : [];
    const twin = twins.length > 1;

    const html = [
      add('map', 'Map', `<div class="lootmap big" data-lm="${esc('|' + id)}"></div>
        ${map.versions ? `<div class="src">${map.stock ? "This is the map as SPT ships it: its own bosses, extracts and containers." : "This is the 1.0 version of the map."} A map selection mod lets you play either, so pick the one you're on. Loose loot is worked out for the version the server loads by default.</div>` : ''}`, null, ''),
      plan ? add('plan', 'Your quests here', `<p class="sub">${plan.objectives.length} objectives from quests you're on${plan.finds.length ? `, ${plan.finds.length} items to look for` : ''}.</p><button class="btn" data-page="plan" data-id="${esc(name)}">Plan a raid here</button>`, null, '') : '',
      map.extracts.length ? add('extracts', 'Extracts', `<div class="ex-sides">
        ${pmcExits.length ? `<div class="ex-side"><h4><span class="side-tag pmc">PMC</span>${pmcExits.length} extracts</h4>${extractList(pmcExits)}</div>` : ''}
        ${scavExits.length ? `<div class="ex-side"><h4><span class="side-tag scav">Scav</span>${scavExits.length} extracts</h4>${extractList(scavExits)}</div>` : ''}</div>
        <div class="src">How often each one is open in a raid, on this server.</div>`, map.extracts.length, '') : '',
      add('quests', 'Quests here', `${profile ? `<div class="toolbar">${`<button class="chip${prefs.hideLockedLists ? ' on' : ''}" data-hidelocked="1">Hide locked</button>`}</div>` : ''}${quests.length ? `<div class="list">${quests.sort((a, b) => shutOut(a.id) - shutOut(b.id) || (a.level ?? 0) - (b.level ?? 0)).map(q => row(q, esc(traderName(q.trader)))).join('')}</div>` : '<p class="none">No quests send you here.</p>'}`, quests.length, ''),
      add('keys', 'Keys', `${keyIds.length ? `<div class="sub" style="margin-bottom:6px">Quests here need</div>${keyIds.map(k => `<div class="key-row">${icon(k)}<div><div>${itemLink(k)}</div><div class="how">for ${[...new Set(usedKeys.filter(u => u.need.ids[0] === k).map(u => u.quest.id))].map(link).join(', ')}</div></div></div>`).join('')}` : ''}
        ${map.keys.length ? `<div class="sub" style="margin:10px 0 6px">Spawn here</div><div class="item-cards small">${map.keys.map(k => `<button class="item-card" data-item="${k.id}"><span class="pic">${pic(k.id)}</span><span class="n">${esc(k.name)}</span><span class="s">${chance(k.perRaid)}</span></button>`).join('')}</div>` : ''}`, keyIds.length + map.keys.length || null, '')
    ].join('');
    // the short lists go down the side, under the contents and the map's description
    const side = [
      map.bosses.length ? add('bosses', 'Bosses', `<div class="boss-list">${map.bosses.map(b => `<div class="boss-row">${botOf(b.name) ? `<a class="bl" data-page="bot" data-id="${esc(botOf(b.name).id)}"><b>${esc(bossName(b.name))}</b></a>` : `<b>${esc(bossName(b.name))}</b>`}<span class="chance">${pct(b.chance)}</span>
        <span class="sub">${b.escorts && b.escorts !== '0' ? `with ${esc(b.escorts)} guards · ` : ''}${esc((b.zones || '').split(',').filter(Boolean).slice(0, 4).map(z => z.replace(/^Zone/, '')).join(', '))}</span></div>`).join('')}</div>
        <div class="src">${map.stock ? "SPT's own spawn chances for this version of the map, before mods change them." : 'Spawn chances on this server, after any mod that changes them.'}</div>`, map.bosses.length, '') : '',
      map.bestLoot.length ? add('loot', 'Best loot', `<div class="item-cards small">${map.bestLoot.map(i => `<button class="item-card" data-item="${i.id}"><span class="pic">${pic(i.id, i.shortName)}</span>
        <span class="n">${esc(i.name)}${modMark(i)}</span><span class="s">${money(i.price, ROUBLES)} · ${chance(i.perRaid)}</span><span class="s">${esc(i.where.join(', '))}</span></button>`).join('')}</div>
        <div class="src">What's worth the most here on average: value times how often it spawns, from this server's loot tables.</div>`, null, '') : ''
    ].join('');

    return `${crumbs(['maps', 'Maps'], [null, map.name])}
      <div class="ihead map-head"><div class="map-title"><h2>${esc(twin ? baseMap(map.name) : map.name)}</h2>${twin ? `<span class="lm-version map-versions">${twins.map(m => [m, human(m.slice(stem(m).length + 1))]).map(([m, t]) => `<button class="chip${m === id ? ' on' : ''}" data-page="map" data-id="${m}">${t}</button>`).join('')}</span>` : ''}${map.versions ? `<span class="lm-version map-versions" title="Which version of the map: the one SPT ships or the 1.0 one a mod brings back">${map.versions.map(v => `<button class="chip${v.on ? ' on' : ''}" data-lmversion="${esc(v.id)}" data-map="${esc(id)}">${esc(v.name)}</button>`).join('')}</span>` : ''}</div>
        <div class="map-stats">${[map.minutes && `${map.minutes} min`, map.minPlayers && `${map.minPlayers}–${map.maxPlayers} players`, map.level && `Level ${map.level}+`,
          pmcExits.length && `${pmcExits.length} PMC · ${scavExits.length} Scav exits`, map.bosses.length && `${map.bosses.length} boss${map.bosses.length === 1 ? '' : 'es'}`,
          map.insurance === false && 'No insurance'].filter(Boolean).map(x => `<span class="map-stat">${x}</span>`).join('')}</div></div></div>
      <div class="article"><div class="col">${html}</div><div class="col">
        <nav class="card toc"><h3>On this page</h3>${sections.map(x => `<a data-scroll="s-${x.anchor}"><span class="ti">${x.ico || '•'}</span><span class="tt">${x.title}</span>${x.n ? `<span class="tn">${x.n}</span>` : ''}</a>`).join('')}</nav>
        ${map.description ? card('About', `<img class="map-banner" src="${mapBanner(id)}" alt="" loading="lazy" onerror="this.remove()"><div class="brief">${esc(map.description)}</div>`) : ''}
        ${side}
      </div></div>`;
  }

  // A kind of container: the maps it stands on, most of them first, each with a map of where they stand (from the
  // game's map files: the server's tables leave the positions at 0, 0, 0), and what's worth finding in one.
  function boxPage(ids) {
    const box = wiki('box:' + ids, 'api/box?id=' + encodeURIComponent(ids));
    if (!box) return `${crumbs(['maps', 'Maps'], [null, 'Container'])}<div class="loading">Loading the container…</div>`;
    if (box.error) return `${crumbs(['maps', 'Maps'], [null, 'Container'])}<div class="intro red">No map on this server has that container, or the loot index isn't ready yet.</div>`;
    Object.assign(data.items, box.items);
    const most = box.maps[0]?.onMap || 1;
    const lm = m => `box:${ids}|${m.id}`;
    if (box.maps.length) openFirstMap(lm(box.maps[0]));
    const name = box.name.replace(/^./, ch => ch.toUpperCase());
    const cards = list => `<div class="item-cards small">${list.map(i => `<button class="item-card" data-item="${i.id}"><span class="pic">${pic(i.id)}</span>
      <span class="n">${esc(i.name)}</span><span class="s">${oneIn(i.each)} has one · ${money(i.price, ROUBLES)}</span></button>`).join('')}</div>`;
    return `${crumbs(['maps', 'Maps'], [null, name])}
      <div class="ihead"><span class="pic box-pic"><img src="/files/handbook/icon_gear_cases.png" alt=""></span><div><h2>${esc(name)}</h2>
        <div class="s">On ${box.maps.length} map${box.maps.length === 1 ? '' : 's'} · most on ${esc(box.maps[0].name)}</div></div></div>
      <div class="article"><div class="col">
        ${card('Where to find it', `${box.maps.map(m => `<div class="loot-row">
          <div class="map"><a data-page="map" data-id="${esc(m.id)}">${esc(m.name)}</a></div>
          <div class="lbar"><span style="width:${Math.max(3, Math.round(m.onMap * 100 / most))}%"></span></div>
          <div class="odds">${perRaid(m.onMap)}</div>
          <div class="lm-row"><button class="lm-btn" data-lootmap="${esc(lm(m))}">${lootMapOpen.has(lm(m)) ? 'Hide map' : 'Show on map'}</button></div></div>
          ${lootMapOpen.has(lm(m)) ? `<div class="lootmap" data-lm="${esc(lm(m))}"></div>` : ''}`).join('')}
          <div class="src">How many spawn in a raid on average, from this server's loot tables. Where they stand: the game's map files.</div>`, box.maps.length)}
        ${box.priciest?.length ? card('Worth the most', `${cards(box.priciest)}<div class="src">The most valuable things one can hold, by price (leaving out anything rarer than 1 in 5,000).</div>`) : ''}
        ${box.best.length ? card('Best on average', `${cards(box.best)}<div class="src">Price times the chance one holds it: what pays off most over many searches.</div>`) : ''}
      </div><div class="col">
        ${box.common.length ? card('Most often inside', cards(box.common)) : ''}
      </div></div>`;
  }

  function perRaid(n) {
    return `${n >= 2 ? `~${Math.round(n)}` : n} a raid`;
  }

  // ---------- bots ----------
  // the kinds of bot the server's list has, in its order, each by the server's word for it
  const kindName = b => b.kindName || 'Other';
  const botGroups = list => [...new Map(list.map(b => [b.kind, kindName(b)]))];
  let botKind = '';
  // the most picks a gear slot shows before the rest fold away
  const GEAR_SHOWN = 8;
  // a bot's name as a title: "crazed Scavs" reads as part of a sentence elsewhere
  const botTitle = name => name.charAt(0).toUpperCase() + name.slice(1);

  // Every kind of bot on this server, mods' included, grouped as players think of them.
  function botsPage() {
    const list = wiki('bots', 'api/bots');
    if (!list) return `${crumbs([null, 'Bots'])}<div class="loading">Loading the bots…</div>`;
    if (list.error) return `${crumbs([null, 'Bots'])}<div class="intro red">Couldn't load the bots.</div>`;
    const line = b => `<tr data-page="bot" data-id="${esc(b.id)}"><td class="pic-cell"><img class="bot-thumb" src="botpic/${esc(b.id)}?v=3" alt="" loading="lazy" onerror="this.remove()"></td>
      <td><a>${esc(botTitle(b.name))}</a>${b.mod ? ` <span class="sub">${esc(b.modName || 'mod')}</span>` : ''}</td><td class="num">${b.health ? number(b.health) : ''}</td>
      <td>${b.maps.length ? esc(b.maps.join(', ')) : '<span class="sub">events only</span>'}</td></tr>`;
    const groups = botGroups(list).map(([kind, title]) => {
      const here = list.filter(b => b.kind === kind);
      if (!here.length || (botKind && botKind !== kind)) return '';
      return card(esc(title), `<table class="wt bots-table"><thead><tr><th></th><th>Bot</th><th class="num">HP</th><th>Spawns on</th></tr></thead><tbody>${here.map(line).join('')}</tbody></table>`, here.length);
    }).join('');
    const mods = list.filter(b => b.mod).length;
    return `${crumbs([null, 'Bots'])}
      <div class="page-head"><div class="t"><h2>Bots</h2><p>Every kind of bot on this server, from its bot files and the maps' spawn lists${mods ? `, ${mods} of them added by mods` : ''}.</p></div></div>
      <div class="toolbar"><button class="chip${botKind ? '' : ' on'}" data-botkind="">All</button>${botGroups(list).map(([k, t]) => `<button class="chip${botKind === k ? ' on' : ''}" data-botkind="${k}">${esc(t)}</button>`).join('')}</div>
      ${groups}`;
  }

  // One kind of bot: where it spawns, how much health, what killing one gives, what it carries, and the quests that
  // send you after it.
  function botPage(id) {
    const answer = wiki('bot:' + id, 'api/bot?id=' + encodeURIComponent(id));
    if (!answer) return `${crumbs(['bots', 'Bots'], [null, '…'])}<div class="loading">Loading…</div>`;
    const bot = answer.bot || answer;
    if (answer.error) return `${crumbs(['bots', 'Bots'])}<div class="intro red">No such bot on this server.</div>`;
    Object.assign(data.items, bot.items);
    const mapList = wiki('maps', 'api/maps');
    const mapId = name => Array.isArray(mapList) ? mapList.find(x => x.name === name)?.id : null;
    const mapLink = m => mapId(m) ? `<a data-page="map" data-id="${esc(mapId(m))}">${esc(m)}</a>` : esc(m);
    const kind = kindName(bot);
    const xp = (bot.xp || []).filter(x => x.max > 0);
    const range = (a, b) => a === b ? number(a) : `${number(a)}–${number(b)}`;

    // the facts box: what it is, where, how often, how tough, what it's worth
    const facts = [
      ['Type', esc(kind)],
      bot.mod && ['Added by', esc(bot.modName || 'a mod')],
      ['Found on', bot.maps.length ? bot.maps.map(m => `<div class="bf-map">${m.map === 'Every map' ? 'Every map' : mapLink(m.map)}<span>${m.chance >= 100 ? 'every raid' : `${m.chance}%`}</span></div>`).join('') : '<span class="sub">Events, or with another bot</span>'],
      ['Health', `<b>${number(bot.healthTotal)}</b> HP`],
      xp.length && ['Kill XP', xp.map(x => `${range(x.min, x.max)}${xp.length > 1 ? ` <span class="sub">${esc(x.difficulty)}</span>` : ''}`).join('<br>')],
      ['Role', `<span class="mono">${bot.roles.map(esc).join('<br>')}</span>`]
    ].filter(Boolean);
    const body = bot.health ? `<div class="bf-body">${bot.health.map(p => `<div><span>${esc(p.part)}</span><b>${p.hp}</b></div>`).join('')}</div>` : '';
    // the head shot only stands in for a missing banner: with one, it's the same picture again
    const infobox = `<aside class="bot-info"><img class="bot-portrait" src="botpic/${esc(id)}?v=3" alt="" onerror="this.remove()">
      <dl>${facts.map(([k, v]) => `<dt>${k}</dt><dd>${v}</dd>`).join('')}</dl>${body}</aside>`;

    // a slot of gear: its picks, likeliest first, with their share; a few shown and the rest a click away
    const pick = g => `<li data-item="${esc(g.id)}">${icon(g.id, 'sm')}<span class="pn">${esc(itemName(g.id))}</span><span class="pp">${g.share}%</span></li>`;
    const slot = (title, items, chance) => `<div class="ld-slot"><h4>${esc(title)}${chance != null && chance < 100 ? `<span>${Math.round(chance)}% carry one</span>` : ''}</h4>
      <ul>${items.slice(0, GEAR_SHOWN).map(pick).join('')}</ul>
      ${items.length > GEAR_SHOWN ? `<details><summary>${items.length - GEAR_SHOWN} more</summary><ul>${items.slice(GEAR_SHOWN).map(pick).join('')}</ul></details>` : ''}</div>`;
    const short = bot.name.replace(/'s (guards|helpers|snipers)$/, '').toLowerCase();
    // a quest whose kill objective names it, as a whole word ("Killa", not "Killa's helmet" in a hand-over)
    const named = new RegExp(`\\b${short.replace(/[^a-z0-9 ]/gi, '.')}\\b`, 'i');
    const quests = data.quests.filter(q => q.objectives.some(o => /\b(kill|eliminate|neutrali[sz]e)\b/i.test(o.text) && named.test(o.text) && !/hand over|find|obtain|stash/i.test(o.text)));
    const section = (title, html, note = '') => html ? `<section class="bot-sec"><h3>${title}</h3>${note ? `<p class="bot-note">${note}</p>` : ''}${html}</section>` : '';

    return `${crumbs(['bots', 'Bots'], [null, botTitle(bot.name)])}
      <header class="bot-hero">
        <img class="bot-hero-bg" src="botpic/${esc(id)}?kind=poster&v=3" alt="" onerror="this.remove()" onload="${HERO_FOCUS}">
        <div class="bot-hero-text"><div class="bot-kicker">${esc(kind)}${bot.mod ? ` · added by ${esc(bot.modName || 'a mod')}` : ''}</div>
          <h2>${esc(botTitle(bot.name))}</h2>
          ${answer.about ? `<p>${esc(answer.about)}</p>` : ''}</div>
      </header>
      <div class="bot-layout"><div class="bot-main">
        ${section('Loadout', (bot.gear || []).length ? `<div class="ld-grid">${bot.gear.map(g => slot(g.title, g.items, g.chance)).join('')}</div>` : '',
          "From its bot file on this server. With ABPS or another progression mod installed, what bots carry in raid can differ from this.")}
        ${section('Loot on it', (bot.loot || []).length ? `<div class="ld-grid">${bot.loot.map(l => slot(l.title, l.items)).join('')}</div>` : '')}
        ${section(`Quests that send you after it <span class="n">${quests.length}</span>`, quests.length ? `<div class="mini-list">${quests.slice(0, 30).map(q => mini(q.id)).join('')}</div>` : '')}
      </div>${infobox}</div>`;
  }

  // Where a bot's banner looks: tarkov.dev's posters are either a scene (wide, the head high up), a close-up (square,
  // the face in the middle) or a tall one, shown whole. Once the banner shows, the infobox's head shot,
  // a crop of the same picture, goes.
  const HERO_FOCUS = "const r=this.naturalWidth/this.naturalHeight;this.style.objectPosition='50% '+(r>=1.5?'8%':r>=0.9?'45%':'22%');this.closest('.bot-hero')?.classList.add('has-pic');if(r<0.9)this.closest('.bot-hero')?.classList.add('tall');document.querySelector('.bot-portrait')?.remove()";

  // ---------- my stash ----------
  // What's in the character's stash, listed by item with how many there are, what it's worth, and any flea offers they have up. Read only.
  let stashOpen = '';

  let stashNote = null;
  let stashSort = 'flea:d';
  let stashFind = '';

  let stashCat = '';

  // asks how many to sell, showing what that comes to; 0 when cancelled
  let sellQty = 0;
  let sellRate = null;
  window.stashQty = input => {
    sellQty = Math.max(1, Math.min(+input.max, Math.floor(+input.value) || 1));
    input.closest('.cf-body').querySelector('.qty-total').innerHTML = money(Math.floor(sellRate.each * sellQty), sellRate.currency);
  };
  async function sellAsk(title, max, each, currency, note = '') {
    sellQty = max;
    sellRate = { each, currency };
    const body = `${max > 1 ? `<p><label>How many <input class="filter qty" style="width:90px;min-width:0;flex:none" type="number" min="1" max="${max}" value="${max}" oninput="stashQty(this)"></label> of ${number(max)}</p>` : ''}
      <p>You get <b class="qty-total">${money(Math.floor(each * max), currency)}</b>${max > 1 ? `, ${money(Math.floor(each), currency)} each` : ''}.</p>${note}
      <p class="sub">It goes for good. The trader's money lands in your stash.</p>`;
    return await confirmBox({ title, body, ok: 'Sell', danger: true }) ? sellQty : 0;
  }

  // sells `count` across these stacks, one call per stack, stopping at the first refusal
  async function sellStacks(stacks, trader, count) {
    let left = count, sold = 0, result = { ok: true };
    for (const x of stacks) {
      if (left <= 0) break;
      const take = Math.min(left, x.count);
      result = await act('api/stash/sell', { profile: profile.id, item: x.id, trader, count: take });
      if (!result.ok) break;
      left -= take;
      sold += take;
      // shown at once; the server's own figures follow
      const st = wikiCache.get(`stash|${profile.id}`);
      if (st) {
        x.flea = x.count ? x.flea * (x.count - take) / x.count : 0;
        x.count -= take;
        if (x.count <= 0) st.items = st.items.filter(i => i !== x);
      }
    }
    stashNote = result.ok ? { ok: true, text: `Sold ${number(sold)}${result.viaGame ? ' through the game' : ''}.` }
      : { ok: false, text: `${sold ? `Sold ${number(sold)}, then stopped: ` : ''}${hxWhy(result.why) || "It didn't work."}` };
    if (!stashItems().some(i => i.id === stashOpen)) stashOpen = '';
    stashRefresh();
  }

  // after a sale or repair: the page stays as it is, and the stash is read again behind it
  function stashRefresh() {
    for (const key of [...wikiCache.keys()]) if (key.startsWith('stash-item|')) wikiCache.delete(key);
    const key = `stash|${profile.id}`, id = profile.id;
    render();
    getJson(`api/stash?profile=${encodeURIComponent(id)}`).then(found => { if (!found?.error) { wikiCache.set(key, found); if (profile?.id === id) render(); } }).catch(() => {});
  }

  const stashItems = () => wikiCache.get(`stash|${profile.id}`)?.items || [];

  // the row's Sell button: any number of that item, to whichever trader pays most. Ones not found in raid go first,
  // as quests want the found-in-raid ones.
  async function stashQuick(tpl) {
    const stacks = stashItems().filter(x => x.tpl === tpl && !x.holds).sort((a, b) => a.fir - b.fir || a.count - b.count);
    let o = null;
    if (stacks.length) {
      try {
        o = (await getJson(`api/stash/item?profile=${encodeURIComponent(profile.id)}&item=${encodeURIComponent(stacks[0].id)}`)).options;
      } catch (error) {
        o = null;
      }
    }
    const best = o?.sell?.length ? [...o.sell].sort((a, b) => b.price - a.price)[0] : null;
    const why = !stacks.length ? 'Empty it to sell it.' : !o ? "Couldn't ask the traders." : o.busy ? hxWhy(o.busy) : !best ? 'No trader buys this.' : '';
    if (why) { stashNote = { ok: false, text: why }; render(); return; }
    const have = stacks.reduce((n, x) => n + x.count, 0);
    const fir = stacks.filter(x => x.fir).reduce((n, x) => n + x.count, 0);
    const count = await sellAsk(`Sell ${itemName(tpl)} to ${best.name}?`, have, best.price / o.count, best.currency,
      fir && fir < have ? `<p class="sub">The ${number(have - fir)} not found in raid go first.</p>` : '');
    if (count) await sellStacks(stacks, best.trader, count);
  }

  // what can be done with the picked item: who buys it for how much, who repairs it for how much
  function stashActions(x) {
    if (!editsOn()) return '';
    const r = wiki(`stash-item|${x.id}`, `api/stash/item?profile=${encodeURIComponent(profile.id)}&item=${encodeURIComponent(x.id)}`);
    if (!r) return '<div class="loading">Asking the traders…</div>';
    const o = r.options;
    if (r.error || !o) return '';
    const off = r.off ? 'Turned off for this address (hideoutActions in config.json).' : o.busy ? hxWhy(o.busy) : '';
    const row = (t, text, attr, label) => `<tr>${data.traders[t.trader] ? `<td class="pic-cell">${avatar(t.trader)}</td>` : '<td></td>'}<td>${esc(t.name)}</td><td class="num">${text}</td><td><button class="btn" ${attr}${off ? ' disabled' : ''}>${label}</button></td></tr>`;
    const sell = [...o.sell].sort((a, b) => b.price - a.price);
    return `${off ? `<p class="sub">${esc(off)}</p>` : o.viaGame ? `<p class="sub">${VIA_GAME}</p>` : ''}
      ${o.holds ? '<p class="sub">Empty it to sell it.</p>' : ''}
      ${sell.length ? `<h4 class="req-h">Sell${o.count > 1 ? `<span>for all ${number(o.count)}</span>` : ''}</h4><table class="wt"><tbody>${sell.map(t => row(t, `${money(t.price, t.currency)}${o.count > 1 ? `<div class="sub">${money(Math.floor(t.price / o.count), t.currency)} each</div>` : ''}`, `data-stashsell="${esc(x.id + '|' + t.trader)}"`, 'Sell')).join('')}</tbody></table>` : ''}
      ${o.repair.length ? `<h4 class="req-h">Repair<span>${o.durability.now} / ${o.durability.max}</span></h4><table class="wt"><tbody>${o.repair.map(t => row(t, money(t.cost, ROUBLES), `data-stashrepair="${esc(x.id + '|' + t.trader)}"`, 'Repair')).join('')}</tbody></table><p class="sub">A repair takes a bit off the maximum for good.</p>` : ''}`;
  }

  // the panel's Sell: this one stack, to the trader picked
  async function stashSellOne(value) {
    const [item, trader] = value.split('|');
    const o = wikiCache.get(`stash-item|${item}`)?.options;
    const t = o?.sell.find(x => x.trader === trader);
    const x = stashItems().find(i => i.id === item);
    if (!t || !x) return;
    const count = await sellAsk(`Sell ${itemName(x.tpl)} to ${t.name}?`, o.count, t.price / o.count, t.currency);
    if (count) await sellStacks([x], trader, count);
  }

  async function stashDo(kind, value, label) {
    const [item, trader] = value.split('|');
    if (!await confirmBox({ title: `${label}?`, ok: kind === 'sell' ? 'Sell' : 'Repair', danger: kind === 'sell', body: kind === 'sell' ? "<p>It goes for good. The trader's money lands in your stash.</p>" : '<p>The cost comes out of your stash.</p>' })) return;
    let result;
    result = await act(`api/stash/${kind}`, { profile: profile.id, item, trader });
    stashNote = result.ok ? { ok: true, text: `${kind === 'sell' ? 'Sold' : 'Repaired'}${result.viaGame ? ' through the game' : ''}.` } : { ok: false, text: hxWhy(result.why) || "It didn't work." };
    if (result.ok && kind === 'sell') stashOpen = '';
    stashRefresh();
  }

  function stashPage() {
    const head = `${crumbs([null, 'My stash'])}<div class="page-head"><div class="t"><h2>My stash</h2><p>Everything your character has in the stash, as it was last saved. Click an item to sell or repair it.</p></div></div>`;
    if (!profile) return `${head}<div class="intro">Pick your <b>profile</b> at the top to see your stash.</div>`;
    const st = wiki(`stash|${profile.id}`, `api/stash?profile=${encodeURIComponent(profile.id)}`);
    if (!st) return `${head}<div class="loading">Opening the stash…</div>`;
    if (st.error) return `${head}<div class="intro red">Couldn't read this character's stash.</div>`;
    Object.assign(data.items, st.names);
    const byId = new Map(st.items.map(x => [x.id, x]));
    const where = x => x.parent === st.root ? 'Stash' : byId.has(x.parent) ? itemName(byId.get(x.parent).tpl) : 'Stash';
    const places = list => [...new Set(list.map(where))].join(', ');
    // one row per kind of item, sorted by the picked heading
    // each item's kind, from the item list (loaded alongside)
    const whole = wiki('catalog', 'api/catalog');
    const catOf = new Map((whole?.items || []).map(i => [i.id, i.category]));
    const cats = new Map((whole?.categories || []).map(c => [c.id, c]));
    const topOf = id => { let c = cats.get(id); while (c?.parent && cats.has(c.parent)) c = cats.get(c.parent); return c?.name || ''; };
    const find = stashFind.trim().toLowerCase();
    const rows = [...groupBy(st.items, x => x.tpl)].map(([tpl, list]) => ({ tpl, list, name: itemName(tpl), where: places(list), cat: cats.get(catOf.get(tpl))?.name || '', top: topOf(catOf.get(tpl)),
      count: list.reduce((n, x) => n + x.count, 0), fir: list.reduce((n, x) => n + (x.fir ? x.count : 0), 0), flea: list.reduce((n, x) => n + x.flea, 0) }));
    for (const r of rows) r.each = r.flea / Math.max(1, r.count);
    const tops = [...new Set(rows.map(r => r.top).filter(Boolean))].sort();
    const [key, dir] = stashSort.split(':');
    const shown = rows.filter(r => (!stashCat || r.top === stashCat) && (!find || `${r.name}|${r.cat}|${r.top}|${r.where}`.toLowerCase().includes(find)));
    shown.sort((a, b) => (typeof a[key] === 'string' ? a[key].localeCompare(b[key]) : a[key] - b[key]) * (dir === 'd' ? -1 : 1) || a.name.localeCompare(b.name));
    const head2 = (k, label, cls = '') => `<th class="sortable ${cls}${key === k ? ' sorted' : ''}" data-stashsort="${k}">${label}${key === k ? (dir === 'd' ? ' ▼' : ' ▲') : ''}</th>`;
    const open = byId.get(stashOpen);
    const same = open ? st.items.filter(x => x.tpl === open.tpl) : [];
    return `${head}
      ${facts([['Worth on the flea', money(st.total, ROUBLES)], ['Worth to traders', money(st.traders, ROUBLES), 'at the best price any of them pays'], ['Roubles', money(st.roubles, ROUBLES)], ['Items', number(st.items.length)]])}
      <div class="article"><div class="col">
        ${card(`Stash <span class="n">${number(shown.length)}</span>`, `<div class="toolbar"><input class="filter" id="stashFind" placeholder="Find an item, category or container" value="${esc(stashFind)}"></div>
          <div class="toolbar">${['', ...tops].map(t => `<button class="chip${stashCat === t ? ' on' : ''}" data-stashcat="${esc(t)}">${t ? esc(t) : 'Everything'}</button>`).join('')}</div>${shown.length ? '' : '<p class="none">Nothing matches.</p>'}<table class="wt items-list stash-list"><thead><tr><th></th>${head2('name', 'Item')}${head2('cat', 'Category')}${head2('count', 'How many', 'num')}${head2('fir', 'Found in raid', 'num')}${head2('where', 'Where')}${head2('each', 'Each', 'num')}${head2('flea', 'Flea value', 'num')}<th></th></tr></thead><tbody>${shown.map(r => `<tr class="${open?.tpl === r.tpl ? 'on' : ''}" data-stashopen="${esc(open?.tpl === r.tpl ? open.id : r.list[0].id)}"><td class="pic-cell"><span class="pic">${pic(r.tpl)}</span></td><td><a>${esc(itemName(r.tpl))}</a><div class="sub phone-only">${r.fir ? `${number(r.fir)} found in raid · ` : ''}${money(r.flea, ROUBLES)}</div></td><td class="sub">${esc(r.cat)}</td><td class="num">${number(r.count)}</td><td class="num">${r.fir ? number(r.fir) : '<span class="sub">0</span>'}</td><td class="sub">${esc(r.where)}</td><td class="num">${money(Math.round(r.each), ROUBLES)}</td><td class="num">${money(r.flea, ROUBLES)}</td><td><button class="btn" data-stashquick="${esc(r.tpl)}">Sell</button></td></tr>`).join('')}</tbody></table>`)}
      </div><div class="col stash-side">
        ${stashNote ? `<div class="note ${stashNote.ok ? 'done' : 'hidden'}">${esc(stashNote.text)}</div>` : ''}
        ${open ? card(esc(itemName(open.tpl)), `${same.length > 1 ? `<p class="sub">You have ${number(same.length)} of these. Pick one:</p><table class="wt stash-list"><tbody>${same.map(x => `<tr class="${x.id === open.id ? 'on' : ''}" data-stashopen="${esc(x.id)}"><td>${esc(where(x))}</td><td class="num">${x.count > 1 ? `× ${number(x.count)}` : ''}</td><td class="sub">${x.fir ? 'Found in raid' : ''}</td><td class="num">${money(x.flea, ROUBLES)}</td></tr>`).join('')}</tbody></table>` : `<p class="sub">In ${esc(where(open))}.</p>`}
          <p class="sub">Worth ${money(open.flea, ROUBLES)} on the flea. <a data-item="${esc(open.tpl)}">Item page</a></p>${stashActions(open)}`) : '<p class="sub">Click an item to sell or repair it.</p>'}
        ${card('My flea listings', st.offers.length ? `<table class="wt items-list"><tbody>${st.offers.map(o => `<tr data-item="${esc(o.tpl)}"><td class="pic-cell"><span class="pic">${pic(o.tpl)}</span></td><td><a>${esc(itemName(o.tpl))}</a>${o.count > 1 ? ` <span class="sub">× ${number(o.count)}</span>` : ''}${o.ends ? `<div class="sub">Ends ${new Date(o.ends * 1000).toLocaleString()}</div>` : ''}</td><td class="num">${o.price ? money(o.price, ROUBLES) : ''}</td></tr>`).join('')}</tbody></table>` : '<p class="none">Nothing listed right now.</p>', st.offers.length)}
      </div></div>`;
  }

  // ---------- wishlist ----------
  // The character's wishlist in the game, changed from the page: the same list, through the same server handlers
  // the game uses, and like the hideout only while the game is closed for that character.
  const WISH_GROUPS = ['Tasks', 'Hideout', 'Barter', 'Equipment', 'Other'];
  let wishFind = '';
  let wishGroup = 4;
  let wishNote = null;
  let wishNames = false;

  const wishState = () => profile ? wiki(`wish|${profile.id}`, `api/wishlist?profile=${encodeURIComponent(profile.id)}`) : null;
  // why it can't be changed now, or '' when it can
  const wishLocked = w => !w?.list ? 'unavailable' : w.off ? 'Changing it from here is only allowed on this PC or the home network (hideoutActions in config.json).' : w.list.busy ? hxWhy(w.list.busy) : '';

  async function wishDo(action, item, group) {
    let result;
    result = await act(`api/wishlist/${action}`, { profile: profile.id, item, group });
    wishNote = result.ok ? { ok: true, text: (result.viaGame ? 'Done through the game. ' : '') + (action === 'add' ? `Added ${itemName(item)} to ${WISH_GROUPS[group]}.` : action === 'remove' ? `Took ${itemName(item)} off the wishlist.` : `Moved ${itemName(item)} to ${WISH_GROUPS[group]}.`) }
      : { ok: false, text: hxWhy(result.why) || "It didn't work." };
    wikiCache.delete(`wish|${profile.id}`);
    render();
  }

  // on an item's page: whether it's on the wishlist, and a button to add or take it off
  function wishControl(id) {
    const w = wishState();
    if (!w?.list) return '';
    const on = w.list.items.find(x => x.id === id);
    const locked = wishLocked(w);
    if (!editsOn()) return on ? `<div class="wish-ctl">On your wishlist, under ${WISH_GROUPS[on.group] || 'Other'}. <a data-page="wishlist" data-id="">Wishlist</a></div>` : '';
    return `<div class="wish-ctl">${on ? `On your wishlist, under ${WISH_GROUPS[on.group] || 'Other'}. <button class="btn" data-wishdel="${esc(id)}"${locked ? ' disabled' : ''}>Take it off</button>`
      : `<button class="btn" data-wishadd="${esc(id)}"${locked ? ' disabled' : ''}>Add to my wishlist</button>`}
      <a data-page="wishlist" data-id="">Wishlist</a>${locked ? `<span class="sub"> ${esc(locked)}</span>` : ''}</div>`;
  }

  function wishlistPage() {
    const head = `${crumbs([null, 'Wishlist'])}<div class="page-head"><div class="t"><h2>Wishlist</h2>
      <p>Your character's wishlist, the same one the game shows. With the game open and in the menus, changes go straight into it. With it closed, they're there when it next logs in.</p></div></div>`;
    if (!profile) return `${head}<div class="intro">Pick your <b>profile</b> at the top to see and change its wishlist.</div>`;
    const w = wishState();
    if (!w) return `${head}<div class="loading">Loading the wishlist…</div>`;
    if (w.error || !w.list) return `${head}<div class="intro red">Couldn't load this character's wishlist.</div>`;
    const locked = wishLocked(w);
    const catalog = wiki('catalog', 'api/catalog');
    if (catalog?.items && !wishNames) { for (const i of catalog.items) data.items[i.id] ??= i.name; wishNames = true; }
    const edits = editsOn();
    const groupPick = (attrs, value) => !edits ? esc(WISH_GROUPS[value] || 'Other') : `<select ${attrs}${locked ? ' disabled' : ''}>${WISH_GROUPS.map((g, i) => `<option value="${i}"${i === value ? ' selected' : ''}>${g}</option>`).join('')}</select>`;
    const list = [...w.list.items].sort((a, b) => a.group - b.group || itemName(a.id).localeCompare(itemName(b.id)));
    const owned = profile.owned;
    needWhere(list.map(x => x.id));
    const rows = list.map(x => `<tr><td><span class="item">${icon(x.id)}<span>${itemLink(x.id)}</span></span></td>
      <td>${groupPick(`data-wishgroup="${esc(x.id)}"`, x.group)}</td>${owned ? `<td class="num">${number(owned[x.id]?.[0] || 0)}</td>` : ''}
      <td class="how">${howToGet(x.id)}</td>${edits ? `<td><button class="btn" data-wishdel="${esc(x.id)}"${locked ? ' disabled' : ''}>Remove</button></td>` : ''}</tr>`).join('');
    const text = wishFind.trim().toLowerCase();
    const hits = text.length >= 2 && catalog?.items ? catalog.items.filter(i => !w.list.items.some(x => x.id === i.id)
      && (i.name.toLowerCase().includes(text) || (i.shortName || '').toLowerCase().includes(text))).slice(0, 15) : [];
    return `${head}
      ${locked ? `<div class="note">${esc(locked)}</div>` : w.list.viaGame ? `<div class="note done">${VIA_GAME}</div>` : ''}
      ${wishNote ? `<div class="note ${wishNote.ok ? 'done' : 'hidden'}">${esc(wishNote.text)}</div>` : ''}
      ${card('On the list', list.length ? `<table class="items-table"><thead><tr><th>Item</th><th>Group</th>${owned ? '<th class="num">You have</th>' : ''}<th>Where to get it</th>${edits ? '<th></th>' : ''}</tr></thead><tbody>${rows}</tbody></table>`
        : '<p class="none">Nothing on the wishlist yet.</p>', list.length)}
      ${!edits ? '<p class="sub">Changing the wishlist from here is turned off for this character. Turn it on under Options.</p>' : card('Add an item', `<div class="toolbar"><input class="filter" id="wishFind" placeholder="Item name" value="${esc(wishFind)}">
          <label class="qbar-map">Group ${groupPick('id="wishGroup"', wishGroup)}</label></div>
        ${!catalog ? '<div class="loading">Loading the item list…</div>' : text.length < 2 ? '<p class="sub">Type two letters or more of the item\'s name.</p>'
          : hits.length ? `<table class="wt items-list"><tbody>${hits.map(i => `<tr><td class="pic-cell"><span class="pic">${pic(i.id, i.shortName)}</span></td>
            <td><a data-item="${i.id}">${esc(i.name)}</a>${modMark(i)}</td>
            <td><button class="btn" data-wishadd="${i.id}"${locked ? ' disabled' : ''}>Add</button></td></tr>`).join('')}</tbody></table>` : '<p class="none">No item by that name, or it\'s on the list already.</p>'}`)}`;
  }

  // how the last craft or upgrade started from the page went, shown on the hideout pages until the next one
  let hideoutNote = null;
  // the crafts list's filters: one area (its type), only what can start now, and the item's name
  let craftArea = '';
  let craftNow = false;
  let craftFilter = '';

  // Starts a craft or an upgrade, after asking; then the hideout and the
  // character are fetched again.
  async function hideoutDo(button) {
    const action = button.dataset.hx;
    const recipe = button.dataset.recipe;
    const area = button.dataset.area;
    const hideout = wikiCache.get('hideout');
    const craft = recipe && hideout?.areas?.flatMap(a => a.crafts).find(c => c.id === recipe);
    const where = hideout?.areas?.find(a => String(a.type) === String(area ?? view.id));
    const level = where?.levels.find(l => l.level === (+(profile.hideout?.[where.type] ?? 0) + 1));
    const line = (id, count, note = '') => `<div class="cf-item">${icon(id, 'sm')}<span>${number(count)} × ${esc(itemName(id))}</span>${note ? `<span class="sub">${note}</span>` : ''}</div>`;
    const ok = await confirmBox(action === 'craft'
      ? { title: `Craft ${itemName(craft?.item)}${craft?.count > 1 ? ` × ${craft.count}` : ''}?`, pic: craft?.item, ok: 'Start crafting',
          body: `<p class="sub">Takes ${craft ? (craft.hours >= 1 ? `${craft.hours} h` : `${Math.round(craft.hours * 60)} min`) : ''} before your skills. Uses from your stash:</p>
            ${craft?.inputs.map(i => line(i.id, i.count, i.tool ? 'tool, given back' : '')).join('') || ''}` }
      : { title: `Build ${where?.name || 'the area'} level ${level?.level ?? ''}?`, ok: 'Start building',
          body: `<p class="sub">${level?.hours ? `Takes ${level.hours} h. ` : ''}Takes these from your stash:</p>${(level?.items || []).map(i => line(i.id, i.count, i.fir ? 'found in raid' : '')).join('')}` });
    if (!ok) return;
    button.disabled = true;
    let result;
    result = await act(`api/hideout/${action}`, { profile: profile.id, recipe, area });
    hideoutNote = result.ok
      ? { ok: true, text: result.viaGame ? `Done through the game: ${action === 'craft' ? `crafting ${itemName(craft?.item)}` : 'building started'}. It's in the game now.` : action === 'craft' ? `Crafting ${itemName(craft?.item)}. The timer is on the page; it runs while the game is closed too (an area that needs power only works while the generator runs). Collect it in the game.` : 'Building started. The game finishes it when the time is up.' }
      : { ok: false, text: hxWhy(result.why) || "It didn't work." };
    for (const key of [...wikiCache.keys()]) if (key.startsWith('hideout-me|') || key.startsWith('hideout-crafts|')) wikiCache.delete(key);
    try { profile = await getJson('api/profile?id=' + encodeURIComponent(prefs.profile)); computeState(); offered(); } catch { /* the next poll catches up */ }
    render();
  }

  // why a craft or an upgrade can't start, in words: "missing:<ids>" names the items
  const hxWhy = w => w?.startsWith('missing:') ? `Missing ${w.slice(8).split(',').map(itemName).join(', ')}` : w || '';
  // seconds until a timer runs out, 0 once it has
  const hxLeft = (start, seconds) => Math.max(0, start + seconds - Date.now() / 1000);

  // what an area is crafting, as a pill
  // what an area is crafting, with a timer that counts down live
  function craftingPill(c) {
    // the server's own count of progress: a craft only runs at full speed with the power on
    const end = c.done ? 0 : Math.round(c.timing?.until ?? c.start + c.seconds);
    const slow = !c.done && c.timing?.speed < 1;
    return `<span class="hx-pill craft">${icon(c.item, 'sm')}${itemLink(c.item)} <span class="hx-timer" data-until="${end}">${countdown(end)}</span>${c.timing && !c.done ? ` <span class="sub">${c.timing.percent}%</span>` : ''}${slow ? ` <span class="hx-slow" title="Crafts in this area need power. With the generator off they run at ${Math.round(c.timing.speed * 100)}% speed, so turning it on cuts this right down.">Generator off</span>` : ''}</span>`;
  }

  // "1:04:09 left", or ready once the time is up
  function countdown(end) {
    const left = Math.max(0, Math.round(end - Date.now() / 1000));
    if (!left) return 'ready: take it out in game to free the area';
    const h = Math.floor(left / 3600), m = Math.floor(left / 60) % 60, sec = left % 60;
    return `${h ? `${h}:${String(m).padStart(2, '0')}` : m}:${String(sec).padStart(2, '0')} left`;
  }
  setInterval(() => {
    for (const el of document.querySelectorAll('[data-until]')) el.textContent = countdown(+el.dataset.until);
  }, 1000);

  // A question asked on the page instead of the browser's pop-up: a title, what it does, and two buttons.
  // Resolves true on the main button, false on Cancel, Esc or a click outside.
  // ---------- character passwords ----------
  // A character can have a password (set on My character). Changes from the page then need the key that unlocking
  // gives, kept in this browser for 30 days.
  // hidden-letter text boxes rather than password boxes, so the browser doesn't offer to save a login; Firefox can't
  // hide letters that way, so it keeps password boxes
  const PW = CSS.supports('-webkit-text-security', 'disc') ? 'type="text" autocomplete="off" data-lpignore="true" data-1p-ignore spellcheck="false"' : 'type="password" autocomplete="off"';
  const lockKey = id => { try { return localStorage.getItem('tk-lock-' + id) || ''; } catch (error) { return ''; } };
  const lockKeep = (id, key) => { try { if (key) localStorage.setItem('tk-lock-' + id, key); else localStorage.removeItem('tk-lock-' + id); } catch (error) { /* private window */ } };

  async function lockPost(kind, body) {
    try {
      const response = await fetch(`api/lock/${kind}`, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Tushonkapedia': '1' }, body: JSON.stringify(body) });
      return response.ok ? await response.json() : { ok: false, why: response.status === 403 ? 'Not allowed from this address (hideoutActions in config.json).' : `The server said ${response.status}.` };
    } catch (error) {
      return { ok: false, why: `Couldn't reach the server: ${error.message}` };
    }
  }

  // ---------- character changes ----------
  // Selling, repairing, crafting and the wishlist write into the character's profile on the server. They stay
  // hidden for a character until the player says, once, that they're fine with that; Options turns them off again.
  const editsKey = id => 'tk-edits-' + id;
  const editsState = id => { try { return localStorage.getItem(editsKey(id)); } catch { return null; } };
  const editsSet = (id, on) => { try { localStorage.setItem(editsKey(id), on ? '1' : '0'); } catch { /* private window: asked again next time */ } };
  const editsOn = () => !!profile && editsState(profile.id) === '1';
  let editsAsking = false;
  async function editsAsk() {
    if (!profile || editsAsking) return editsOn();
    editsAsking = true;
    const id = profile.id, name = profile.player?.name || profiles.find(p => p.id === id)?.name || 'this character';
    const yes = await confirmBox({ title: `Let the wiki change ${name}?`, ok: 'Turn them on', cancel: 'Keep them off',
      body: `<p>The wiki can sell and repair items in your stash, start crafts and hideout upgrades, and change your wishlist.</p>
        <p>With the game closed, these write straight into ${esc(name)}'s profile on the server. With it open, they go through the game. Either way your character really changes, the same as doing it in game, and it can't be undone from here.</p>
        <p>If you'd rather the wiki only reads your character, keep them off. Everything else still works, and you can change your mind any time under Options.</p>`,
      tick: `I understand these change ${name}'s profile` });
    editsAsking = false;
    if (profile?.id !== id) return false;
    editsSet(id, yes);
    render();
    return yes;
  }

  // every change to a character goes through here, with its key; a locked one asks for the password, then tries again
  async function act(url, body, again = true) {
    let result;
    if (!editsOn() && !await editsAsk()) return { ok: false, why: 'Changes to this character are turned off. Turn them on under Options.' };
    try {
      const response = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Tushonkapedia': '1', 'X-Tushonkapedia-Key': lockKey(body.profile) }, body: JSON.stringify(body) });
      result = response.ok ? await response.json() : { ok: false, why: response.status === 403 ? 'Not allowed from this address (hideoutActions in config.json).' : `The server said ${response.status}.` };
    } catch (error) {
      result = { ok: false, why: `Couldn't reach the server: ${error.message}` };
    }
    if (result.locked && again && await unlockAsk(body.profile)) return act(url, body, false);
    return result;
  }

  // a locked character asks for its password the first time this browser picks it, and again after it changes
  async function loggedIn(id) {
    let st;
    try {
      st = await (await fetch(`api/lock?profile=${encodeURIComponent(id)}`, { cache: 'no-cache', headers: { 'X-Tushonkapedia-Key': lockKey(id) } })).json();
    } catch (error) {
      return true;
    }
    profileLocked = !!st.locked;
    if (!st.locked || st.open) return true;
    lockKeep(id, '');
    return unlockAsk(id, true);
  }

  let lockTyped = '';
  window.lockType = input => { lockTyped = input.value; };
  async function unlockAsk(id, login = false) {
    let why = '';
    const name = profiles.find(p => p.id === id)?.name || 'This character';
    for (;;) {
      lockTyped = '';
      if (!await confirmBox({ title: `Log in to ${name}`, ok: 'Log in',
        body: `<p>${login ? 'This character has a password.' : 'This character is locked.'} Enter it to ${login ? 'open it here' : 'make changes from here'}.</p><p><input class="filter pw" ${PW} oninput="lockType(this)" style="width:100%"></p>${why ? `<p class="red">${esc(why)}</p>` : ''}` })) return false;
      const result = await lockPost('unlock', { profile: id, password: lockTyped });
      if (result.ok) { lockKeep(id, result.key); return true; }
      why = result.why || 'That didn\'t work.';
      if (why.startsWith('Too many')) { await confirmBox({ title: 'Locked for now', body: `<p>${esc(why)}</p>`, ok: 'OK' }); return false; }
    }
  }

  // whether the picked character has a password, for the Options menu
  let profileLocked = false;

  // Options > Password: set one, or change or remove it, in a popup
  const pwVals = {};
  window.pwField = input => { pwVals[input.dataset.f] = input.value; };
  async function lockPopup() {
    const id = profile.id, name = profile.player?.name || profiles.find(p => p.id === id)?.name || 'this character';
    let why = '';
    for (;;) {
      for (const k of Object.keys(pwVals)) delete pwVals[k];
      const field = (f, label) => `<p><label class="sub">${label}</label><br><input class="filter pw" ${PW} data-f="${f}" oninput="pwField(this)" style="width:100%"></p>`;
      const body = profileLocked
        ? `${field('old', 'Current password')}${field('new', 'New password')}${field('again', 'New password again')}
          <p class="sub">Leave the new password empty to remove it. Forgot it? Stop the server, delete this character from locks.json in the mod's folder, and start it again.</p>`
        : `<p>Without a password, anyone who can open this wiki can see ${esc(name)} and sell, repair, craft and change the wishlist. With one, other browsers have to log in first.</p>
          ${field('new', 'Password')}${field('again', 'Password again')}<p class="sub">Only a salted hash is kept on the server.</p>`;
      if (!await confirmBox({ title: `Password for ${name}`, ok: profileLocked ? 'Save' : 'Set password', body: body + (why ? `<p class="red">${esc(why)}</p>` : '') })) return;
      const fresh = pwVals.new || '', old = pwVals.old || '';
      if (fresh !== (pwVals.again || '')) { why = "The new passwords don't match."; continue; }
      let result;
      if (fresh) {
        result = await lockPost('set', { profile: id, current: old, password: fresh });
        if (result.ok) lockKeep(id, result.key);
      } else if (profileLocked) {
        if (!await confirmBox({ title: 'Remove the password?', ok: 'Remove', danger: true, body: `<p>Anyone who can open the wiki will be able to see and change ${esc(name)} again.</p>` })) return;
        result = await lockPost('clear', { profile: id, current: old });
        if (result.ok) lockKeep(id, '');
      } else { why = 'Type a password.'; continue; }
      if (!result.ok) { why = result.why || "That didn't work."; continue; }
      profileLocked = !!fresh;
      render();
      await confirmBox({ title: fresh ? 'Password saved' : 'Password removed', ok: 'OK', alone: true,
        body: fresh ? '<p>This browser stays logged in. Other browsers will ask for it.</p>' : '<p>Anyone who can open the wiki can see and change this character again.</p>' });
      return;
    }
  }

  // Options > Log out: this browser forgets the login and drops the character
  async function lockLogout() {
    const id = profile.id;
    lockKeep(id, '');
    prefs.profile = '';
    $('profile').value = '';
    savePrefs();
    for (const key of [...wikiCache.keys()]) if (key.includes(id)) wikiCache.delete(key);
    await loadProfile();
    go({ page: 'overview', id: '' });
  }

  function confirmBox({ title, body = '', ok = 'OK', pic, danger, alone, cancel = 'Cancel', tick }) {
    return new Promise(resolve => {
      const shade = document.createElement('div');
      shade.className = 'cf-shade';
      shade.innerHTML = `<div class="cf-box" role="dialog" aria-modal="true" aria-label="${esc(title)}">
          <div class="cf-head">${pic ? icon(pic, 'big') : ''}<h3>${esc(title)}</h3></div>
          <div class="cf-body">${body}${tick ? `<label class="cf-tick"><input type="checkbox" class="cf-tickbox"> ${esc(tick)}</label>` : ''}</div>
          <div class="cf-buttons">${alone ? '' : `<button class="btn" data-cf="0">${esc(cancel)}</button>`}<button class="btn cf-ok${danger ? ' danger' : ''}" data-cf="1"${tick ? ' disabled' : ''}>${esc(ok)}</button></div></div>`;
      // with a tick box, OK only works once it's ticked
      const ticked = () => !tick || shade.querySelector('.cf-tickbox')?.checked;
      if (tick) shade.addEventListener('change', () => { shade.querySelector('.cf-ok').disabled = !ticked(); });
      const done = answer => { document.removeEventListener('keydown', key, true); shade.remove(); resolve(answer); };
      const key = e => { if (e.key === 'Escape') { e.stopPropagation(); done(false); } else if (e.key === 'Enter') { e.preventDefault(); if (ticked()) done(true); } };
      shade.addEventListener('click', e => {
        e.stopPropagation();
        const quest = e.target.closest('a[data-q]');
        if (quest) { const id = quest.dataset.q; done(false); go({ page: 'quest', id }); return; }
        const b = e.target.closest('[data-cf]');
        if (b) { if (b.dataset.cf !== '1' || ticked()) done(b.dataset.cf === '1'); } else if (e.target === shade) done(false);
      });
      document.addEventListener('keydown', key, true);
      document.body.appendChild(shade);
      (shade.querySelector('.cf-body input') || shade.querySelector('.cf-ok')).focus();
    });
  }

  const VIA_GAME = "The game is open, so what you do here goes through it and shows up there straight away.";

  // the note under a hideout page's title: the game running, actions turned off, or how the last one went
  function hideoutNotes(me) {
    const st = me?.state;
    return `${st?.busy ? `<div class="note hidden">${esc(st.busy)}</div>` : st && me.off ? '<div class="note">Starting crafts and upgrades from here is turned off for this address (hideoutActions in config.json).</div>' : ''}
      ${st?.viaGame ? `<div class="note done">${VIA_GAME}</div>` : ''}
      ${hideoutNote ? `<div class="note ${hideoutNote.ok ? 'done' : 'hidden'}">${esc(hideoutNote.text)}</div>` : ''}`;
  }

  // Crafts, each with what it takes, how much of it your stash holds and a Craft button when a character is picked
  // (greyed out with the reason when it can't start); the ones that can start now come first.
  function craftRows(list, st, can) {
    // 0 can start now, then by how many different items are short, then anything else in the way (area level,
    // a quest, a busy area) last
    const rank = c => {
      if (!st?.crafts?.[c.id]) return 1000;
      const w = st.crafts[c.id].why;
      return !w ? 0 : w.startsWith('missing:') ? w.slice(8).split(',').length : 100;
    };
    const shown = st ? [...list].sort((a, b) => rank(a) - rank(b)) : list;
    return shown.map(c => {
      // the server leaves "why" out when nothing stops it
      const entry = st?.crafts?.[c.id];
      const w = entry?.why;
      const ready = !!entry && !w;
      const button = entry
        ? `<button class="btn hx-craft" data-hx="craft" data-recipe="${esc(c.id)}"${can && !w ? '' : ' disabled'}${w ? ` title="${esc(hxWhy(w))}"` : ''}>Craft</button>` : '';
      return `<div class="craft${ready ? ' ready' : ''}">${ready ? '<span class="craft-ready">Can craft now</span>' : ''}<div class="what">${icon(c.item, 'sm')}${itemLink(c.item)}${c.count > 1 ? ` × ${c.count}` : ''} <span class="sub">${c.areaName ? `${esc(c.areaName)} ` : ''}level ${c.level} · ${c.hours >= 1 ? `${c.hours} h` : `${Math.round(c.hours * 60)} min`}${c.quest ? ` · after ${link(c.quest)}` : ''}</span>${button}</div>
        <div class="inputs">${c.inputs.map(i => `<span class="barter">${icon(i.id, 'sm')}${i.count} × ${itemLink(i.id)}${i.tool ? ' <span class="sub">(tool)</span>' : ''}${st?.have ? `<span class="have ${(st.have[i.id] || 0) >= i.count ? 'all' : st.have[i.id] ? 'some' : 'none'}" title="In your stash">${st.have[i.id] || 0}</span>` : ''}</span>`).join('')}</div>
        ${w && !st.busy ? `<div class="hx-why">${esc(hxWhy(w))}</div>` : ''}</div>`;
    }).join('');
  }

  // Every craft on the server, by area, filtered by area, by what can start now and by name.
  function craftsTab(hideout) {
    const me = profile ? wiki(`hideout-crafts|${profile.id}`, `api/hideout/crafts?profile=${encodeURIComponent(profile.id)}`) : null;
    const st = me?.state;
    const can = st && !me.off && !st.busy;
    const text = craftFilter.trim().toLowerCase();
    const withCrafts = hideout.areas.filter(a => a.crafts.length);
    const match = c => (!craftNow || st && st.crafts?.[c.id] && !st.crafts[c.id].why) && (!text || itemName(c.item).toLowerCase().includes(text)
      || c.inputs.some(i => itemName(i.id).toLowerCase().includes(text)));
    const ready = st ? withCrafts.flatMap(a => a.crafts).filter(c => st.crafts?.[c.id] && !st.crafts[c.id].why).length : 0;
    const chip = (attr, value, label, on) => `<button class="chip${on ? ' on' : ''}" ${attr}="${esc(value)}">${label}</button>`;
    const groups = withCrafts.filter(a => !craftArea || String(a.type) === craftArea).map(a => {
      const rows = a.crafts.filter(match);
      if (!rows.length) return '';
      const running = st?.crafting?.[a.type];
      return `<div class="card craft-area"><h3><a data-page="hideout" data-id="${a.type}">${esc(a.name)}</a><span class="n">${rows.length}</span>${running ? craftingPill(running) : ''}</h3>${craftRows(rows, st, can)}</div>`;
    }).join('');
    // what you can craft right now, first and on its own
    const readyNow = st ? withCrafts.filter(a => !craftArea || String(a.type) === craftArea)
      .flatMap(a => a.crafts.filter(c => st.crafts?.[c.id] && !st.crafts[c.id].why && match(c)).map(c => ({ ...c, areaName: a.name }))) : [];
    const running = st ? withCrafts.filter(a => st.crafting?.[a.type]).map(a => `<div class="craft running"><span class="sub">${esc(a.name)}</span> ${craftingPill(st.crafting[a.type])}</div>`).join('') : '';
    const runningCard = running ? `<div class="card craft-area running-now"><h3>Crafting now</h3>${running}</div>` : '';
    const readyCard = readyNow.length ? `<div class="card craft-area ready-now"><h3>Can craft now<span class="n">${readyNow.length}</span></h3>${craftRows(readyNow, st, can)}</div>` : '';
    return `<div class="toolbar">${chip('data-craftarea', '', 'All areas', !craftArea)}${withCrafts.map(a => chip('data-craftarea', String(a.type), esc(a.name), craftArea === String(a.type))).join('')}</div>
      <div class="toolbar"><input class="filter" id="craftFilter" placeholder="Filter by item made or used" value="${esc(craftFilter)}">
        ${st ? `<span class="sep"></span>${chip('data-craftnow', '1', `Can craft now (${ready})`, craftNow)}` : ''}</div>
      ${profile && !me ? '<div class="loading">Checking your stash…</div>' : hideoutNotes(me)}
      ${runningCard}${readyCard}${craftNow ? '' : groups || ''}${!readyCard && !(craftNow ? '' : groups) ? `<p class="none">No crafts match.${craftNow ? ' Turn off <b>Can craft now</b> to see the rest.' : ''}</p>` : ''}`;
  }

  // Every craft on the server in one list, its own page in the menu.
  function craftsPage() {
    const hideout = wiki('hideout', 'api/hideout');
    if (!hideout) return `${crumbs([null, 'Crafts'])}<div class="loading">Loading the crafts…</div>`;
    if (hideout.error) return `${crumbs([null, 'Crafts'])}<div class="intro red">Couldn't load the hideout.</div>`;
    Object.assign(data.items, hideout.items);
    const total = hideout.areas.reduce((n, a) => n + a.crafts.length, 0);
    return `${crumbs(['hideout', 'Hideout'], [null, 'Crafts'])}
      <div class="page-head"><div class="t"><h2>Crafts</h2><p>All ${total} crafts on this server, by hideout area.${profile ? ' Start one from here while the game is closed.' : ' Pick a character to see what you can craft and start it from here.'}</p></div></div>
      ${craftsTab(hideout)}`;
  }

  function hideoutPage(type) {
    const hideout = wiki('hideout', 'api/hideout');
    if (!hideout) return `${crumbs([null, 'Hideout'])}<div class="loading">Loading the hideout…</div>`;
    if (hideout.error) return `${crumbs([null, 'Hideout'])}<div class="intro red">Couldn't load the hideout.</div>`;
    Object.assign(data.items, hideout.items);
    const area = hideout.areas.find(a => String(a.type) === String(type));
    if (!area) return hideoutIndex(hideout);
    // this character's side of the area, when one is picked: its level, what it's building or crafting, and the
    // buttons to start a craft or the next level (only while the game is closed)
    const me = profile ? wiki(`hideout-me|${profile.id}|${area.type}`, `api/hideout/me?profile=${encodeURIComponent(profile.id)}&area=${area.type}`) : null;
    const st = me?.state;
    const can = st && !me.off && !st.busy;
    const next = st ? st.level + 1 : 0;
    const tab = view.tab === 'crafts' && area.crafts.length ? 'crafts' : 'levels';
    const status = st ? `<div class="hx-status">
        <span class="hx-lvl">${st.level ? `Your level <b>${st.level}</b>` : 'Not built yet'}</span>
        ${st.constructing ? `<span class="hx-pill build">Building level ${next}${st.completeTime ? ` · ${hxLeft(st.completeTime, 0) ? `ready in ${duration(hxLeft(st.completeTime, 0))}` : 'done: the game finishes it when you log in'}` : ''}</span>` : ''}
        ${st.crafting ? craftingPill(st.crafting) : ''}
      </div>` : '';
    const upgradeRow = l => {
      if (!st || l.level !== next || st.constructing) return '';
      const ok = can && !st.upgrade.why;
      return `<div class="hx-actions">
        <button class="btn hx-go" data-hx="upgrade" data-area="${area.type}"${ok ? '' : ' disabled'}>Start building level ${l.level}</button>
        ${st.upgrade.why && !st.busy ? `<span class="hx-why">${esc(hxWhy(st.upgrade.why))}</span>` : ''}</div>`;
    };
    const tileHave = (l, i) => {
      if (!st || l.level !== next) return '';
      const u = st.upgrade.items.find(x => x.id === i.id);
      if (!u) return '';
      const got = Math.min(u.count, u.have);
      return `<span class="have ${got >= u.count ? 'all' : got ? 'some' : 'none'}">${got} / ${u.count}</span>`;
    };
    const levels = area.levels.map(l => `<div class="card level${st && l.level <= st.level ? ' built' : ''}"><h3>Level ${l.level}${st && l.level <= st.level ? '<span class="hx-done">✓ built</span>' : ''}${l.hours ? `<span class="n">${l.hours} h to build</span>` : ''}</h3>
      ${l.description ? `<p class="sub">${esc(l.description)}</p>` : ''}
      ${l.bonuses?.length ? `<p class="gives"><b>Gives:</b> ${l.bonuses.map(bonusText).join(', ')}</p>` : ''}
      ${l.items.length ? `<div class="item-grid">${l.items.map(i => `<div class="item-tile" data-item="${i.id}">${icon(i.id)}<span><span class="t">${number(i.count)} × ${esc(i.name)}</span>${i.fir ? '<span class="c">found in raid</span>' : ''}${tileHave(l, i)}</span></div>`).join('')}</div>` : ''}
      ${[...l.areas.map(a => `<a data-page="hideout" data-id="${a.area}">${esc(a.name || 'Area ' + a.area)}</a> level ${a.level}`),
        ...l.traders.map(t => `${esc(traderName(t.trader))} LL${t.level}`),
        ...l.skills.map(sk => `${esc(sk.skill)} level ${sk.level}`)].map(x => `<span class="why-chip">${x}</span>`).join(' ')}${upgradeRow(l)}</div>`).join('');
    const tabs = area.crafts.length ? `<div class="tabs">${[['levels', 'Upgrades'], ['crafts', `Crafts <span class="n">${area.crafts.length}</span>`]].map(([t, label]) =>
      `<button class="tab${tab === t ? ' on' : ''}" data-tab="${t}">${label}</button>`).join('')}</div>` : '';
    return `${crumbs(['hideout', 'Hideout'], [null, area.name])}
      <div class="page-head"><div class="t"><h2>${esc(area.name)}</h2>${area.description ? `<p>${esc(area.description)}</p>` : ''}</div></div>
      ${status}${hideoutNotes(me)}${tabs}
      ${tab === 'crafts' ? `<div class="card craft-area">${craftRows(area.crafts, st, can)}</div>`
        : `<div class="levels">${levels || '<p class="none">No levels to build.</p>'}</div>`}`;
  }

  // what a hideout level gives, as the game words it: "Fuel consumption -50%"
  const PCT_BONUS = new Set(['FuelConsumption', 'ExperienceRate', 'ScavCooldownTimer', 'QuestMoneyReward', 'InsuranceReturnTime', 'RagfairCommission', 'SkillGroupLevelingBoost', 'DebuffEndDelay']);
  function bonusText(b) {
    const shown = !b.value || /^Unlock|^StashSize$/.test(b.type) ? '' : ` ${b.value > 0 ? '+' : ''}${Math.round(b.value * 100) / 100}${PCT_BONUS.has(b.type) ? '%' : ''}`;
    return `${esc(b.text)}${b.skill ? ` (${esc(b.skill)})` : ''}${shown}`;
  }

  const buildTime = h => h ? `${number(Math.round(h))} h` : '';

  // The hideout's front page: every area with its next step and what it gives, then every item the levels take,
  // added up. With a character picked, only the levels they haven't built count.
  function hideoutIndex(hideout) {
    const built = a => profile ? +(profile.hideout?.[a.type] ?? 0) : 0;
    const left = a => a.levels.filter(l => l.level > built(a));
    const needs = l => [...l.areas.map(x => `<a data-page="hideout" data-id="${x.area}">${esc(x.name || 'Area ' + x.area)}</a> ${x.level}`),
      ...l.traders.map(t => `${esc(traderName(t.trader))} LL${t.level}`), ...l.skills.map(s => `${esc(s.skill)} ${s.level}`)].join(', ') || 'Nothing else';
    const gives = a => [...new Set(a.levels.flatMap(l => (l.bonuses || []).map(b => b.text)))];
    const totals = new Map();
    for (const a of hideout.areas) {
      for (const l of left(a)) {
        for (const i of l.items) {
          const t = totals.get(i.id) || { id: i.id, count: 0, fir: 0, uses: [] };
          t.count += i.count;
          if (i.fir) t.fir += i.count;
          t.uses.push(`<a data-page="hideout" data-id="${a.type}">${esc(a.name)}</a> ${l.level}`);
          totals.set(i.id, t);
        }
      }
    }
    const list = [...totals.values()].sort((x, y) => y.uses.length - x.uses.length || itemName(x.id).localeCompare(itemName(y.id)));
    const levels = hideout.areas.reduce((n, a) => n + a.levels.length, 0);
    const toBuild = hideout.areas.reduce((n, a) => n + left(a).length, 0);
    const hoursLeft = hideout.areas.reduce((n, a) => n + left(a).reduce((m, l) => m + (l.hours || 0), 0), 0);
    const crafts = hideout.areas.reduce((n, a) => n + a.crafts.length, 0);
    // found-in-raid ones for what must be found in raid, any for the rest
    const owned = profile?.owned;
    const have = t => {
      const [all, fir] = owned?.[t.id] || [0, 0];
      const f = Math.min(fir, t.fir);
      const got = f + Math.min(all - f, t.count - t.fir);
      return `<span class="have ${got >= t.count ? 'all' : got ? 'some' : 'none'}">${number(got)} / ${number(t.count)}</span>`;
    };
    return `${crumbs([null, 'Hideout'])}
      <div class="page-head"><div class="t"><h2>Hideout</h2><p>${hideout.areas.length} areas with ${levels} levels and ${crafts} crafts on this server.
        ${profile ? `${esc(profile.name)} has ${toBuild} level${toBuild === 1 ? '' : 's'} left to build, ${buildTime(hoursLeft) || 'no time'} of building.` : `Building every level takes ${buildTime(hoursLeft)}.`}
        Every craft in one list is under <a data-page="crafts" data-id="">Crafts</a>.</p></div></div>
      <table class="wt hideout-table"><thead><tr><th>Area</th><th class="num">${profile ? 'Built' : 'Levels'}</th><th class="num">${profile ? 'Time left' : 'Build time'}</th>
        <th>${profile ? 'Next level needs' : 'Level 1 needs'}</th><th>Gives</th><th class="num">Crafts</th></tr></thead><tbody>
        ${hideout.areas.map(a => {
          const next = a.levels.find(l => l.level === built(a) + 1);
          return `<tr data-page="hideout" data-id="${a.type}"${profile && !next ? ' class="have-all"' : ''}><td><a>${esc(a.name)}</a></td>
            <td class="num">${profile ? `${built(a)} / ${a.levels.length}` : a.levels.length}</td>
            <td class="num">${buildTime(left(a).reduce((m, l) => m + (l.hours || 0), 0))}</td>
            <td class="sub">${next ? needs(next) : 'All built'}</td>
            <td class="sub">${esc(gives(a).join(', '))}</td><td class="num">${a.crafts.length || ''}</td></tr>`;
        }).join('')}</tbody></table>
      ${list.length ? card(profile ? 'Items for the levels left' : 'Items for every level', `<p class="sub">Added up over ${profile ? 'the levels not built yet' : 'every level'}. FIR: it has to be found in raid.</p>
        <table class="items-table"><thead><tr><th>Item</th><th class="num">Needed</th>${owned ? '<th class="num">You have</th>' : ''}<th>For</th></tr></thead><tbody>
        ${list.map(t => `<tr><td><span class="item">${icon(t.id)}<span>${itemLink(t.id)}</span></span></td>
          <td class="num">${number(t.count)}${t.fir ? `<span class="fir">${t.fir === t.count ? 'FIR' : number(t.fir) + ' FIR'}</span>` : ''}</td>
          ${owned ? `<td class="num">${have(t)}</td>` : ''}<td class="who">${t.uses.join(', ')}</td></tr>`).join('')}</tbody></table>`, list.length) : ''}`;
  }

  // ---------- loot map ----------
  // Where on one map an item spawns. There's no picture of the map: every loot spot on it is drawn faintly, which
  // outlines its buildings and roads, and this item's spots go on top, bigger and brighter the likelier they are.
  const lootMaps = new Map();
  const lootMapOpen = new Set();
  // maps someone closed: a page's first map opens by itself, but not after that
  const lootMapShut = new Set();
  const openFirstMap = key => { if (!lootMapShut.has(key)) lootMapOpen.add(key); };

  // the version of a map picked on its loot map (the SPT one or a 1.0 backport), if the viewer picked one
  const mapVersion = map => (prefs.mapVersion || {})[map.toLowerCase()] || '';

  function lootMapData(key) {
    const [item, map] = key.split('|');
    const version = mapVersion(map);
    const cached = key + '|' + version;
    if (!lootMaps.has(cached)) {
      // "box:<types>|<map>" is where those containers stand, from the container page; "door:<key>|<map>" what a key opens
      const what = item.startsWith('box:') ? `box=${encodeURIComponent(item.slice(4))}`
        : item.startsWith('door:') ? `door=${encodeURIComponent(item.slice(5))}`
        : item.startsWith('zone:') ? `zone=${encodeURIComponent(item.slice(5))}` : `id=${encodeURIComponent(item)}`;
      lootMaps.set(cached, getJson(`api/lootmap?${what}&map=${encodeURIComponent(map)}${version ? `&version=${encodeURIComponent(version)}` : ''}`)
        .catch(error => { lootMaps.delete(cached); throw error; }));
    }
    return lootMaps.get(cached);
  }

  function mountLootMaps() {
    for (const holder of document.querySelectorAll('.lootmap:not([data-drawn])')) {
      holder.dataset.drawn = '1';
      holder.innerHTML = '<p class="none">Loading the map…</p>';
      const parts = holder.dataset.parts ? JSON.parse(holder.dataset.parts) : null;
      (parts ? Promise.all(parts.map(p => lootMapData(p.k).catch(() => null))).then(answers => holder.dataset.route ? routeStops(answers, parts) : mergeParts(answers, parts)) : lootMapData(holder.dataset.lm))
        .then(answer => drawLootMap(holder, answer),
          error => { holder.innerHTML = `<p class="none">Couldn't load the map: ${esc(error.message)}</p>`; });
    }
  }

  // Several loot maps of one map as one: every place and spot, each tagged with its objective's number and text;
  // the objectives with nothing to show are listed
  function mergeParts(answers, parts) {
    const base = answers.find(Boolean);
    if (!base) throw new Error('none of the places could be loaded');
    const merged = { ...base, spots: [], boxes: [], containers: [], multi: true, missing: [] };
    answers.forEach((a, i) => {
      const tag = s => ({ ...s, num: parts[i].n, label: parts[i].t });
      const boxes = (a?.boxes || []).map(tag);
      const spots = (a?.spots || []).map(tag);
      if (!boxes.length && !spots.length) merged.missing.push(parts[i].n);
      merged.boxes.push(...boxes);
      merged.spots.push(...spots);
    });
    merged.missing = [...new Set(merged.missing)].filter(n => !merged.boxes.some(b => b.num === n) && !merged.spots.some(s => s.num === n));
    return merged;
  }

  // The planner's route map: each objective's places, and the two likeliest spots of each shopping list item, become
  // stops (close ones of the same thing as one); the plain map gives the extracts and PMC starts.
  function routeStops(answers, parts) {
    const base = answers.find((a, i) => a && parts[i].map) || answers.find(Boolean);
    if (!base) throw new Error('the map could not be loaded');
    const stops = [], missing = [];
    const add = (place, label) => {
      if (!stops.some(x => x.label === label && Math.hypot(x.x - place.x, x.z - place.z) < 40)) stops.push({ x: place.x, y: place.y, z: place.z, label, chance: 1 });
    };
    answers.forEach((a, i) => {
      const part = parts[i];
      if (part.map) return;
      const places = [...(a?.boxes || []), ...(a?.spots || [])];
      if (!places.length) { missing.push(part.t); return; }
      if (part.shop) places.sort((x, y) => (y.chance ?? 0) - (x.chance ?? 0)).slice(0, 2).forEach(x => add(x, `${part.t}${x.name ? `, ${x.name}` : ''}`));
      else places.forEach(x => add(x, part.t));
    });
    return { ...base, spots: stops, boxes: [], containers: [], multi: true, route: true, missing: [], routeMissing: missing };
  }

  // the PMC start picked on each route map, kept while the page is drawn again
  const routeStarts = new Map();

  function toggleLootMap(button) {
    const key = button.dataset.lootmap;
    const row = button.closest('.loot-row');
    const shown = row.nextElementSibling?.classList.contains('lootmap') ? row.nextElementSibling : null;
    if (shown) {
      shown.remove();
      lootMapOpen.delete(key);
      lootMapShut.add(key);
      button.textContent = 'Show on map';
      return;
    }
    lootMapOpen.add(key);
    row.insertAdjacentHTML('afterend', `<div class="lootmap" data-lm="${esc(key)}"></div>`);
    button.textContent = 'Hide map';
    mountLootMaps();
  }

  function drawLootMap(holder, answer) {
    const { map, outline, spots, containers, boxes = [], versions, art } = answer;
    const multi = !!answer.multi;
    const item = !holder.dataset.lm.startsWith('|') && !multi;
    const boxMode = /^(box|door|zone):/.test(holder.dataset.lm);
    const lockMode = !!(answer.locks || answer.zones || multi);
    const turns = prefs.mapTurn || (prefs.mapTurn = {});

    // tarkov.dev's top-down tiles where it has them (the picture most like the game), else its SVG drawing
    // (Factory's top-down picture is the dark night one, so it keeps its drawing)
    const drawingFirst = /^factory4/.test(holder.dataset.lm.split('|')[1] || '');
    let mode = art?.tiles && !(drawingFirst && art.svg) ? 'tiles' : art?.svg ? 'svg' : null;
    // a place on a floor only the drawing has (Interchange's upper floors): the drawing, so that floor can be shown
    const inside = (f, s) => (f.extents || []).some(e => s.y >= e.low && s.y <= e.high && (!e.areas.length
      || e.areas.some(([x1, z1, x2, z2]) => s.x >= Math.min(x1, x2) && s.x <= Math.max(x1, x2) && s.z >= Math.min(z1, z2) && s.z <= Math.max(z1, z2))));
    if (mode === 'tiles' && art.svg && (answer.locks || answer.zones || answer.multi)
      && (art.floors || []).some(f => f.svg && !f.tiles && [...boxes, ...spots].some(s => inside(f, s)))) mode = 'svg';
    // the ground level and the floors drawn over it (upper floors, basements, tunnels), as this mode has them
    const floors = [{ name: 'Ground level', at: 0, extents: [] }]
      .concat((art?.floors || []).map((f, i) => ({ ...f, at: i + 1 })).filter(f => mode === 'tiles' ? f.tiles : f.svg));
    // the floor a spot is on: the first whose heights (in the areas they cover, or anywhere) take it in
    const floorOf = s => {
      for (const f of floors) {
        for (const e of f.extents) {
          if (s.y < e.low || s.y > e.high) continue;
          if (!e.areas.length || e.areas.some(([x1, z1, x2, z2]) => s.x >= Math.min(x1, x2) && s.x <= Math.max(x1, x2) && s.z >= Math.min(z1, z2) && s.z <= Math.max(z1, z2))) return f.at;
        }
      }
      return 0;
    };
    for (const s of spots) s.floor = floorOf(s);
    for (const b of boxes) { b.floor = floorOf(b); b.box = true; }
    // a map page's own map: its extracts, boss spawns and PMC starts, each layer switched on or off in the bar
    const points = answer.points ? [
      ...answer.points.exits.map(e => ({ ...e, kind: 'exit', label: e.name, side: e.pmc && e.scav ? 'both' : e.scav ? 'scav' : 'pmc' })),
      ...answer.points.bosses.map(b => ({ ...b, kind: 'boss', label: b.name })),
      ...answer.points.pmc.map(q => ({ ...q, kind: 'pmc', label: 'PMC start' }))] : [];
    for (const q of points) q.floor = floorOf(q);

    // The route: from the PMC start you click (you can't pick one, so the route waits to hear where you are) or,
    // until then, from whichever stop makes the shortest walk; through every stop; out at the PMC extract nearest
    // the last. Straight lines, height counted double, as stairs and floors slow you down.
    let routeStart = null, routeEnd = null, routePath = [];
    const gap = (a, b) => Math.hypot(a.x - b.x, a.z - b.z) + Math.abs((a.y || 0) - (b.y || 0)) * 2;
    function routeOrder() {
      const exits = points.filter(q => q.kind === 'exit' && q.side !== 'scav');
      const exitFor = at => exits.reduce((b, e) => !b || gap(at, e) < gap(at, b) ? e : b, null);
      const length = list => {
        let n = routeStart && list.length ? gap(routeStart, list[0]) : 0;
        for (let i = 1; i < list.length; i++) n += gap(list[i - 1], list[i]);
        const e = list.length ? exitFor(list[list.length - 1]) : null;
        return n + (e ? gap(list[list.length - 1], e) : 0);
      };
      const walk = first => {
        const left = spots.filter(x => x !== first), out = [first];
        while (left.length) {
          const at = out[out.length - 1];
          let i = 0;
          left.forEach((x, j) => { if (gap(at, x) < gap(at, left[i])) i = j; });
          out.push(left.splice(i, 1)[0]);
        }
        return out;
      };
      let best = [];
      for (const first of spots) {
        const tried = walk(first);
        if (!best.length || length(tried) < length(best)) best = tried;
      }
      // then untangle: turn round any stretch that makes the walk shorter
      for (let round = 0, better = true; better && round < 40; round++) {
        better = false;
        for (let i = 0; i < best.length - 1; i++) {
          for (let j = i + 1; j < best.length; j++) {
            const tried = [...best.slice(0, i), ...best.slice(i, j + 1).reverse(), ...best.slice(j + 1)];
            if (length(tried) < length(best) - 0.5) { best = tried; better = true; }
          }
        }
      }
      best.forEach((x, i) => { x.num = i + 1; });
      routeEnd = best.length ? exitFor(best[best.length - 1]) : null;
      routePath = [...(routeStart ? [routeStart] : []), ...best, ...(routeEnd ? [routeEnd] : [])];
    }
    if (answer.route) {
      routeStart = points.find(q => q.kind === 'pmc' && q.name === routeStarts.get(holder.dataset.lm)) || null;
      routeOrder();
    }
    const layers = answer.route ? { exit: true, boss: false, pmc: true } : prefs.mapLayers || (prefs.mapLayers = { exit: true, boss: true, pmc: false });
    const shownPoints = () => points.filter(q => layers[q.kind]);
    const layerChip = (k, label, dot) => { const n = points.filter(q => q.kind === k).length;
      return n ? `<button class="chip lm-layer${layers[k] ? ' on' : ''}" data-lmlayer="${k}"><span class="lm-dot ${dot}"></span>${label} <span class="sub">${n}</span></button>` : ''; };
    // a few places to find (a quest's, a key's locks): open on the floor most of them are on
    let floor = 0;
    if (lockMode) {
      const count = new Map();
      for (const s of [...boxes, ...spots]) count.set(s.floor, (count.get(s.floor) || 0) + 1);
      floor = [...count].sort((a, b) => b[1] - a[1] || a[0] - b[0])[0]?.[0] ?? 0;
    }
    const onFloor = at => spots.filter(s => s.floor === at).length + boxes.filter(b => b.floor === at).length;
    const floorBar = () => floors.length > 1
      ? `<div class="lm-floors">${floors.map(f => `<button class="chip${f.at === floor ? ' on' : ''}" data-lmfloor="${f.at}">${esc(f.name)}${item ? ` <span class="sub">${onFloor(f.at)}</span>` : ''}</button>`).join('')}</div>`
      : '';

    holder.innerHTML = `<div class="lm-bar">
        ${item && !boxMode && !spots.length && !boxes.length && !answer.stock ? '<span>No loose spots here: on this map it only comes in the containers listed below.</span>' : ''}
        ${item && !boxMode && answer.stock ? "<span>SPT's version of the map: containers only, its loose spots aren't worked out.</span>" : ''}
        ${item && spots.length ? `<span><span class="lm-dot loose"></span>${spots.length} loose spot${spots.length === 1 ? '' : 's'}</span>
        ${spots.some(s => s.chance >= 1) ? '<span><span class="lm-dot sure"></span>Always there</span>' : ''}` : ''}
        ${answer.zones && !multi && !boxes.length ? "<span>This place isn't in the map files: the quest's mod makes it in raid, so it can't be shown.</span>" : ''}
        ${multi ? `<span><span class="lm-dot lock"></span>${answer.route ? 'Numbered in the order to do them' : 'Numbered as the objectives above'}</span>${answer.missing.length ? `<span>Objective ${answer.missing.join(', ')}: not in the map files (made in raid), so not shown.</span>` : ''}` : ''}
        ${points.length ? `<span class="lm-layers">${layerChip('exit', 'Extracts', 'exit')}${layerChip('boss', 'Boss spawns', 'boss')}${layerChip('pmc', 'PMC starts', 'pmc')}</span>` : ''}
        ${!multi && boxes.length ? lockMode ? `<span><span class="lm-dot lock"></span>${answer.zones ? (boxes.length === 1 ? 'The objective\'s place' : 'The objective\'s places') : boxes.length === 1 ? 'What it opens' : `${boxes.length} locks it opens`}</span>`
          : `<span><span class="lm-dot box"></span>${boxes.length} container${boxes.length === 1 ? '' : 's'}</span>` : ''}
        <span class="sub">${item && !boxMode ? 'Bigger is likelier. ' : ''}${art ? '' : item ? "Grey dots are the map's other loot spots. " : "No drawing of this map: its loot spots outline it. "}Scroll or pinch to zoom, drag to move.</span>
        <span class="lm-tools">${versions && holder.dataset.lm[0] !== '|' ? `<span class="lm-version" title="Which version of the map: the one SPT ships or the 1.0 one a mod brings back">${versions.map(v => `<button class="chip${v.on ? ' on' : ''}" data-lmversion="${esc(v.id)}" data-map="${esc(holder.dataset.lm.split('|')[1])}">${esc(v.name)}</button>`).join('')}</span>` : ''}${art ? '' : '<button class="btn sm" data-lmturn>Rotate ⟳</button>'}</span></div>
      <div class="lm-floorbar">${floorBar()}</div>
      <div class="lm-canvas"><canvas></canvas><button class="lm-fit" data-lmfit title="Back to the whole map">Zoom out</button><div class="lm-tip" hidden></div></div>
      ${answer.route ? '<div class="route-list"></div>' : ''}
      ${art ? `<div class="src">Map: <a href="https://tarkov.dev" target="_blank" rel="noopener">tarkov.dev</a> (CC BY-NC-SA 4.0). Maps are close, but may not match your game exactly.${answer.stock ? " SPT's version: the drawing is of the 1.0 map, so a few buildings may differ." : ''}${item && floors.length > 1 ? ' Spots on the other floors are the small grey dots: pick a floor above to see them.' : ''}</div>` : ''}
      ${boxes.length && !lockMode ? `<div class="src">Containers: where they stand in the game's map files.${boxMode ? '' : ' Bigger holds it more often.'}</div>` : ''}
      ${lockMode ? `<div class="src">${multi || answer.zones ? 'Places' : 'Locks'}: where they are in the game's map files.</div>` : ''}
      ${containers.length ? `<div class="src">Also in containers not drawn (no place known for them): ${containers.map(c => `${esc(c.name)}${c.count > 1 ? ` <span class="sub">× ${c.count} on the map</span>` : ''}`).join(' · ')}.</div>` : ''}`;
    const canvas = holder.querySelector('canvas');
    const tip = holder.querySelector('.lm-tip');

    // the route written out under the map
    function listRoute() {
      const box = holder.querySelector('.route-list');
      if (!box) return;
      const stopsInOrder = routePath.filter(x => x.num);
      box.innerHTML = `<div class="route-rows">
        <div class="route-row sub">${routeStart ? `Start at ${esc(routeStart.name || 'your spawn')}` : 'Start: click your spawn on the map'}</div>
        ${stopsInOrder.map(x => `<div class="route-row"><b>${x.num}</b><span>${esc(x.label)}</span></div>`).join('')}
        ${routeEnd ? `<div class="route-row sub">Extract at ${esc(routeEnd.label)}</div>` : ''}</div>
        ${answer.routeMissing?.length ? `<p class="sub">Not on the map (a mod makes the place in raid): ${answer.routeMissing.map(esc).join('; ')}.</p>` : ''}
        <p class="sub">Straight lines: the way round walls and buildings is longer. Extracts with a cost or a condition aren't left out, so check yours.</p>`;
    }
    listRoute();
    // a click (not a drag) on a PMC start: the route starts there; on the same one again, it forgets it
    function pickStart(e) {
      const box = canvas.getBoundingClientRect();
      const mx = e.clientX - box.left, my = e.clientY - box.top;
      let best = null, far = 30;
      for (const q of points.filter(q => q.kind === 'pmc')) {
        const [px, py] = screen(q.x, q.z);
        const g = Math.hypot(px - mx, py - my);
        if (g < far) { far = g; best = q; }
      }
      if (!best) return;
      routeStart = routeStart === best ? null : best;
      if (routeStart) routeStarts.set(holder.dataset.lm, routeStart.name); else routeStarts.delete(holder.dataset.lm);
      routeOrder();
      listRoute();
      focus();
      draw();
    }
    const ctx = canvas.getContext('2d');
    const css = getComputedStyle(document.documentElement);
    const colour = name => css.getPropertyValue(name).trim();
    const paint = { bg: colour('--bg'), dot: 'rgba(228,231,235,.16)', loose: colour('--green'), sure: colour('--kappa'), box: colour('--active') };

    // the world's x runs east and z north. With tarkov.dev's map, its own rotation and scale place a spot on it (as
    // its map page does); without one, the outline is turned a quarter at a time to match the map as you know it.
    let turn = turns[map] || 0;
    const rad = (art?.rotation || 0) * Math.PI / 180, cos = Math.cos(rad), sin = Math.sin(rad);
    const flat = (x, z) => {
      if (mode) {
        const [a, b, c, d] = art.transform;
        return [a * (x * cos - z * sin) + b, -c * (x * sin + z * cos) + d];
      }
      const u = x, v = -z;
      return turn === 1 ? [-v, u] : turn === 2 ? [-u, -v] : turn === 3 ? [v, -u] : [u, v];
    };
    // where a pair of corners lies, as [left, top, width, height] in the same units as the spots
    const rect = corners => {
      const [p, q] = [flat(corners[0][0], corners[0][1]), flat(corners[1][0], corners[1][1])];
      return [Math.min(p[0], q[0]), Math.min(p[1], q[1]), Math.abs(p[0] - q[0]), Math.abs(p[1] - q[1])];
    };
    // each floor's picture once it's had: { sheet, at: [left, top, width, height] }
    const sheets = new Map();
    let scale = 1, ox = 0, oy = 0, width = 0, height = 0;
    // the whole map fitted to the window: as far out as you can zoom
    let least = 1, area = [0, 0, 1, 1];
    const ratio = window.devicePixelRatio || 1;

    function fit() {
      width = holder.clientWidth;
      // as tall as the screen allows, so the map can be read
      height = holder.classList.contains('obj-map') ? Math.max(260, Math.min(380, Math.round(width * 0.6)))
        : Math.min(Math.max(320, window.innerHeight - 160), Math.max(300, Math.round(width * 0.75)));
      canvas.width = Math.round(width * ratio);
      canvas.height = Math.round(height * ratio);
      canvas.style.height = height + 'px';
      let minU = Infinity, maxU = -Infinity, minV = Infinity, maxV = -Infinity;
      const add = (u, v) => { minU = Math.min(minU, u); maxU = Math.max(maxU, u); minV = Math.min(minV, v); maxV = Math.max(maxV, v); };
      if (mode) {
        // the map itself: a spot or two off its edge (under the map, out of bounds) mustn't shrink it
        const [l, t, w, h] = rect(art.bounds);
        add(l, t);
        add(l + w, t + h);
      } else {
        // the middle 98% of the spots: a few strays far off the map would shrink it to a corner
        const us = [], vs = [];
        const keep = (x, z) => { const [u, v] = flat(x, z); us.push(u); vs.push(v); };
        for (let i = 0; i < outline.length; i += 2) keep(outline[i], outline[i + 1]);
        for (const s of spots) keep(s.x, s.z);
        us.sort((a, b) => a - b);
        vs.sort((a, b) => a - b);
        const cut = us.length > 50 ? Math.floor(us.length * 0.01) : 0;
        if (us.length) { add(us[cut], vs[cut]); add(us[us.length - 1 - cut], vs[vs.length - 1 - cut]); }
      }
      if (!isFinite(minU)) { minU = minV = -1; maxU = maxV = 1; }
      scale = least = Math.min((width - 24) / Math.max(1, maxU - minU), (height - 24) / Math.max(1, maxV - minV));
      area = [minU, minV, maxU, maxV];
      ox = width / 2 - (minU + maxU) / 2 * scale;
      oy = height / 2 - (minV + maxV) / 2 * scale;
    }

    const screen = (x, z) => { const [u, v] = flat(x, z); return [u * scale + ox, v * scale + oy]; };
    const here = s => floors.length < 2 || s.floor === floor;
    const size = s => s.kind ? (s.kind === 'pmc' ? 5 : 9) : !here(s) ? 4 : s.num ? 11 : s.box ? (s.chance == null ? 6 : 5 + 4 * Math.sqrt(s.chance)) : 7 + 5 * Math.sqrt(s.chance ?? 1);
    const kind = s => s.box ? (lockMode ? paint.sure : paint.box) : s.chance >= 1 ? paint.sure : paint.loose;
    // containers under the loose spots; likeliest drawn last, so on top
    const order = [...boxes].sort((a, b) => (a.chance ?? 0) - (b.chance ?? 0)).concat([...spots].sort((a, b) => a.chance - b.chance));
    // a loose spot is round, a container square
    const mark = (s, px, py, r) => {
      ctx.beginPath();
      if (s.box) ctx.roundRect(px - r, py - r, r * 2, r * 2, Math.max(1, r * 0.3));
      else ctx.arc(px, py, r, 0, Math.PI * 2);
    };

    let queued = false;
    const soon = () => { if (!queued) { queued = true; requestAnimationFrame(() => { queued = false; draw(); }); } };

    function draw() {
      ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
      ctx.fillStyle = paint.bg;
      ctx.fillRect(0, 0, width, height);
      const paste = entry => {
        const [l, t, w, h] = entry.at;
        ctx.drawImage(entry.sheet, l * scale + ox, t * scale + oy, w * scale, h * scale);
      };
      if (sheets.get(0)) paste(sheets.get(0));
      // another floor: the ground level dimmed under it
      if (floor && sheets.get(floor)) {
        ctx.fillStyle = 'rgba(11,12,16,.6)';
        ctx.fillRect(0, 0, width, height);
        paste(sheets.get(floor));
      }
      if (!mode) {
        ctx.fillStyle = paint.dot;
        const d = Math.max(1, Math.min(3, scale * 0.9));
        for (let i = 0; i < outline.length; i += 2) {
          const [px, py] = screen(outline[i], outline[i + 1]);
          if (px > -4 && py > -4 && px < width + 4 && py < height + 4) ctx.fillRect(px - d / 2, py - d / 2, d, d);
        }
      }
      // the route, dashed, under the stops
      if (routePath.length > 1) {
        ctx.save();
        ctx.setLineDash([8, 6]);
        ctx.lineWidth = 3;
        ctx.strokeStyle = 'rgba(255,255,255,.9)';
        ctx.shadowColor = 'rgba(0,0,0,.85)';
        ctx.shadowBlur = 4;
        ctx.beginPath();
        routePath.forEach((x, i) => { const [px, py] = screen(x.x, x.z); if (i) ctx.lineTo(px, py); else ctx.moveTo(px, py); });
        ctx.stroke();
        ctx.restore();
      }
      // spots on other floors: small grey dots underneath
      for (const s of order) {
        if (here(s)) continue;
        const [px, py] = screen(s.x, s.z);
        if (px < -20 || py < -20 || px > width + 20 || py > height + 20) continue;
        mark(s, px, py, 4);
        ctx.fillStyle = 'rgba(200,204,210,.55)';
        ctx.fill();
        ctx.lineWidth = 1;
        ctx.strokeStyle = 'rgba(0,0,0,.6)';
        ctx.stroke();
      }
      // a solid dot in a white ring with a soft shadow, so it stands out on any part of the map
      for (const s of order) {
        if (!here(s)) continue;
        const [px, py] = screen(s.x, s.z);
        if (px < -20 || py < -20 || px > width + 20 || py > height + 20) continue;
        const r = size(s);
        mark(s, px, py, r);
        ctx.shadowColor = 'rgba(0,0,0,.7)';
        ctx.shadowBlur = 6;
        ctx.shadowOffsetY = 1;
        ctx.fillStyle = '#fff';
        ctx.fill();
        ctx.shadowColor = 'transparent';
        mark(s, px, py, r - 2);
        ctx.fillStyle = kind(s);
        ctx.fill();
        // an objective's number, else the dark centre
        if (s.num) {
          ctx.fillStyle = '#111';
          ctx.font = `700 ${Math.round(r * 1.15)}px "Segoe UI", system-ui, sans-serif`;
          ctx.textAlign = 'center';
          ctx.textBaseline = 'middle';
          ctx.fillText(String(s.num), px, py + 1);
          continue;
        }
        mark(s, px, py, Math.max(1.5, r * 0.28));
        ctx.fillStyle = 'rgba(0,0,0,.45)';
        ctx.fill();
      }
      drawPoints();
    }

    const POINT = { pmc: '#5aa9ff', boss: '#ff4538', exit: { pmc: '#7dc24a', scav: '#e2ad4f', both: '#4fc3b5' } };
    function drawPoints() {
      const labels = scale >= least * 1.6;
      for (const q of shownPoints().sort((a, b) => (a.kind === 'pmc' ? 0 : a.kind === 'exit' ? 1 : 2) - (b.kind === 'pmc' ? 0 : b.kind === 'exit' ? 1 : 2))) {
        const [px, py] = screen(q.x, q.z);
        if (px < -40 || py < -40 || px > width + 40 || py > height + 40) continue;
        ctx.globalAlpha = here(q) ? 1 : 0.35;
        ctx.lineWidth = 2;
        ctx.strokeStyle = '#fff';
        ctx.beginPath();
        if (q.kind === 'pmc') {
          ctx.arc(px, py, 4, 0, Math.PI * 2);
          ctx.fillStyle = POINT.pmc;
          ctx.fill();
          ctx.lineWidth = 1;
          ctx.stroke();
          // the start picked for the route
          if (q === routeStart) {
            ctx.beginPath();
            ctx.arc(px, py, 10, 0, Math.PI * 2);
            ctx.lineWidth = 3;
            ctx.strokeStyle = POINT.pmc;
            ctx.stroke();
          }
        } else if (q.kind === 'boss') {
          ctx.moveTo(px, py - 9); ctx.lineTo(px + 9, py); ctx.lineTo(px, py + 9); ctx.lineTo(px - 9, py); ctx.closePath();
          ctx.fillStyle = POINT.boss;
          ctx.fill();
          ctx.stroke();
        } else {
          ctx.roundRect(px - 8, py - 8, 16, 16, 4);
          ctx.fillStyle = POINT.exit[q.side];
          ctx.fill();
          ctx.stroke();
        }
        if (labels && q.kind !== 'pmc') {
          ctx.font = '600 12px "Segoe UI", system-ui, sans-serif';
          ctx.textAlign = 'left';
          ctx.textBaseline = 'middle';
          ctx.lineWidth = 3;
          ctx.strokeStyle = 'rgba(0,0,0,.85)';
          ctx.strokeText(q.label, px + 12, py);
          ctx.fillStyle = '#fff';
          ctx.fillText(q.label, px + 12, py);
        }
        ctx.globalAlpha = 1;
      }
    }

    function nearest(mx, my) {
      let best = null, gap = 10;
      for (const s of [...order, ...shownPoints()]) {
        const [px, py] = screen(s.x, s.z);
        // a spot on this floor wins over one on another under it
        const g = Math.hypot(px - mx, py - my) - size(s) + (here(s) ? 0 : 4);
        if (g < gap) { gap = g; best = s; }
      }
      return best;
    }

    const odds = c => c >= 1 ? 'always' : c >= 0.1 ? Math.round(c * 100) + '%' : `1 in ${Math.round(1 / c)}`;
    function hover(e) {
      const box = canvas.getBoundingClientRect();
      const s = nearest(e.clientX - box.left, e.clientY - box.top);
      if (!s) { tip.hidden = true; return; }
      tip.innerHTML = s.kind ? `<b>${esc(s.label)}</b><br><span class="sub">${s.kind === 'exit' ? `Extract · ${s.side === 'both' ? 'PMC and Scav' : s.side === 'scav' ? 'Scav only' : 'PMC'}`
          : s.kind === 'boss' ? `Boss spawn${s.zone ? ` · ${esc(s.zone.replace(/^Zone/, ''))}` : ''}` : `PMC start · ${esc(s.name || '')}`}${floors.length > 1 ? ` · ${esc(floors.find(f => f.at === s.floor).name)}` : ''}</span>`
        : lockMode ? `<b>${s.num ? `${s.num}. ${esc(s.label)}` : answer.zones ? "The objective's place" : 'The lock this key opens'}</b><br><span class="sub">${floors.length > 1 ? `${esc(floors.find(f => f.at === s.floor).name)} · ` : ''}height ${Math.round(s.y)} m</span>` : s.box ? `<b>${esc(s.name)}</b>${s.chance == null ? '' : `<br>${s.chance >= 1 ? 'Always holds it' : s.chance >= 0.1 ? `Holds it in ${odds(s.chance)} of raids` : `Holds it about 1 raid in ${Math.round(1 / s.chance)}`}`}`
        + `<br><span class="sub">${floors.length > 1 ? `${esc(floors.find(f => f.at === s.floor).name)} · ` : ''}height ${Math.round(s.y)} m</span>` : `${s.chance >= 1 ? 'Always there' : s.chance >= 0.1 ? `There in ${odds(s.chance)} of raids` : `There about 1 raid in ${Math.round(1 / s.chance)}`}`
        + `<br><span class="sub">${floors.length > 1 ? `${esc(floors.find(f => f.at === s.floor).name)} · ` : ''}height ${Math.round(s.y)} m</span>`;
      tip.hidden = false;
      tip.style.left = Math.max(4, Math.min(e.clientX - box.left + 14, width - tip.offsetWidth - 4)) + 'px';
      tip.style.top = Math.max(4, e.clientY - box.top - tip.offsetHeight - 10) + 'px';
    }

    // keep the map in the window: centred along a side it doesn't fill, never dragged off along one it does
    function hold() {
      const [minU, minV, maxU, maxV] = area;
      const keep = (o, low, high, room) => {
        const span = (high - low) * scale;
        if (span <= room - 24) return room / 2 - (low + high) / 2 * scale;
        return Math.min(12 - low * scale, Math.max(room - 12 - high * scale, o));
      };
      ox = keep(ox, minU, maxU, width);
      oy = keep(oy, minV, maxV, height);
    }

    function zoom(factor, mx, my) {
      const next = Math.max(least, Math.min(least * 40, scale * factor));
      ox = mx - (mx - ox) * next / scale;
      oy = my - (my - oy) * next / scale;
      scale = next;
      hold();
      draw();
    }

    canvas.addEventListener('wheel', e => {
      e.preventDefault();
      const box = canvas.getBoundingClientRect();
      zoom(e.deltaY < 0 ? 1.2 : 1 / 1.2, e.clientX - box.left, e.clientY - box.top);
    }, { passive: false });

    // drag with one finger or the mouse, pinch with two fingers
    const pointers = new Map();
    let pinch = 0;
    let downAt = null;
    canvas.addEventListener('pointerdown', e => { try { canvas.setPointerCapture(e.pointerId); } catch { /* a pointer the browser no longer has */ } pointers.set(e.pointerId, [e.clientX, e.clientY]); pinch = 0; downAt = [e.clientX, e.clientY]; });
    canvas.addEventListener('pointermove', e => {
      const last = pointers.get(e.pointerId);
      if (!last) { hover(e); return; }
      if (pointers.size === 1) {
        ox += e.clientX - last[0];
        oy += e.clientY - last[1];
        hold();
        pointers.set(e.pointerId, [e.clientX, e.clientY]);
        tip.hidden = true;
        draw();
        return;
      }
      pointers.set(e.pointerId, [e.clientX, e.clientY]);
      const [a, b] = [...pointers.values()];
      const apart = Math.hypot(a[0] - b[0], a[1] - b[1]);
      const box = canvas.getBoundingClientRect();
      if (pinch) zoom(apart / pinch, (a[0] + b[0]) / 2 - box.left, (a[1] + b[1]) / 2 - box.top);
      pinch = apart;
    });
    const up = e => {
      // a tap without a drag shows that spot
      if (pointers.size === 1 && e.pointerType !== 'mouse') hover(e);
      if (answer.route && pointers.size === 1 && downAt && Math.hypot(e.clientX - downAt[0], e.clientY - downAt[1]) < 5) pickStart(e);
      pointers.delete(e.pointerId);
      pinch = 0;
    };
    canvas.addEventListener('pointerup', up);
    canvas.addEventListener('pointercancel', up);
    canvas.addEventListener('pointerleave', e => { if (e.pointerType === 'mouse') tip.hidden = true; });

    // One floor's tiles at the one zoom level that makes a picture about 2600 pixels across, put together as they
    // arrive. Tile (x, y) at zoom z covers size / 2^z units from x and y times that. Resolves false if none came.
    function loadTiles(at) {
      const { url, size: tile, min, max } = art.tiles;
      const [l, t, w, h] = rect(art.bounds);
      let z = min;
      while (z < max && Math.max(w, h) * 2 ** (z + 1) <= 2600) z++;
      const span = tile / 2 ** z;
      const x0 = Math.max(0, Math.floor(l / span)), y0 = Math.max(0, Math.floor(t / span));
      const x1 = Math.floor((l + w) / span), y1 = Math.floor((t + h) / span);
      const off = document.createElement('canvas');
      off.width = (x1 - x0 + 1) * tile;
      off.height = (y1 - y0 + 1) * tile;
      const pen = off.getContext('2d');
      const entry = { sheet: off, at: [x0 * span, y0 * span, (x1 - x0 + 1) * span, (y1 - y0 + 1) * span] };
      return new Promise(resolve => {
        let pending = (x1 - x0 + 1) * (y1 - y0 + 1), got = 0;
        const done = () => { if (--pending === 0) resolve(got > 0); };
        for (let x = x0; x <= x1; x++) {
          for (let y = y0; y <= y1; y++) {
            const img = new Image();
            img.onload = () => {
              pen.drawImage(img, (x - x0) * tile, (y - y0) * tile, tile, tile);
              got++;
              sheets.set(at, entry);
              soon();
              done();
            };
            img.onerror = done;
            img.src = `${url}&floor=${at}&z=${z}&x=${x}&y=${y}`;
          }
        }
      });
    }

    // tarkov.dev's SVG with only one floor's group showing (the ground level, or the floor over it), drawn once into
    // a picture big enough to zoom into, where its own bounds put it
    let svgText = null;
    function loadSvg(at) {
      const group = at ? floors.find(f => f.at === at)?.svg : art.layer;
      if (!group) return Promise.resolve(false);
      svgText ??= fetch(art.svg).then(r => r.ok ? r.text() : Promise.reject(new Error(r.status)));
      return svgText.then(text => new Promise((resolve, reject) => {
        const doc = new DOMParser().parseFromString(text, 'image/svg+xml');
        const root = doc.documentElement;
        if (root.nodeName !== 'svg') throw new Error('not an SVG');
        for (const g of [...root.children]) {
          if (g.nodeName === 'g' && g.id && g.id !== group && g.getAttribute('data-keep-with-group') !== group) g.remove();
        }
        const at4 = rect(art.svgBounds || art.bounds);
        const long = 3200;
        const pw = Math.round(at4[2] >= at4[3] ? long : long * at4[2] / at4[3]), ph = Math.round(at4[3] > at4[2] ? long : long * at4[3] / at4[2]);
        root.setAttribute('width', pw);
        root.setAttribute('height', ph);
        const url = URL.createObjectURL(new Blob([new XMLSerializer().serializeToString(doc)], { type: 'image/svg+xml' }));
        const img = new Image();
        img.onload = () => {
          const off = document.createElement('canvas');
          off.width = pw;
          off.height = ph;
          off.getContext('2d').drawImage(img, 0, 0, pw, ph);
          URL.revokeObjectURL(url);
          sheets.set(at, { sheet: off, at: at4 });
          soon();
          resolve(true);
        };
        img.onerror = () => { URL.revokeObjectURL(url); reject(new Error('the drawing would not load')); };
        img.src = url;
      })).catch(() => false);
    }

    const loading = new Map();
    const load = at => {
      if (!loading.has(at)) loading.set(at, mode === 'tiles' ? loadTiles(at) : mode === 'svg' ? loadSvg(at) : Promise.resolve(false));
      return loading.get(at);
    };

    // the ground level first; with no tiles to be had, the SVG; with neither, the outline of the loot
    if (mode) {
      load(0).then(ok => {
        if (ok) return;
        if (mode === 'tiles' && art.svg) {
          mode = 'svg';
          loading.clear();
          load(0).then(svgOk => { if (!svgOk) { mode = null; fit(); draw(); } else if (floor) load(floor); });
        } else {
          mode = null;
          fit();
          draw();
        }
      });
    }

    holder.querySelector('.lm-floorbar').addEventListener('click', e => {
      const button = e.target.closest('[data-lmfloor]');
      if (!button) return;
      floor = Number(button.dataset.lmfloor);
      holder.querySelector('.lm-floorbar').innerHTML = floorBar();
      if (floor) load(floor);
      draw();
    });
    holder.querySelector('[data-lmturn]')?.addEventListener('click', () => {
      turn = (turn + 1) % 4;
      turns[map] = turn;
      savePrefs();
      fit();
      draw();
    });
    holder.querySelector('[data-lmfit]').addEventListener('click', () => { fit(); focus(); draw(); });
    holder.querySelector('.lm-layers')?.addEventListener('click', e => {
      const chip = e.target.closest('[data-lmlayer]');
      if (!chip) return;
      layers[chip.dataset.lmlayer] = !layers[chip.dataset.lmlayer];
      chip.classList.toggle('on', layers[chip.dataset.lmlayer]);
      savePrefs();
      draw();
    });

    // Places to find: zoomed in on them, with room around, instead of the whole map. "Zoom out" shows it all.
    function focus() {
      // a route: all of it, whatever floor each stop is on
      const targets = answer.route ? (routePath.length ? routePath : spots) : [...boxes, ...spots].filter(here);
      if (!targets.length) return false;
      let minU = Infinity, maxU = -Infinity, minV = Infinity, maxV = -Infinity;
      for (const s of targets) {
        const [u, v] = flat(s.x, s.z);
        minU = Math.min(minU, u); maxU = Math.max(maxU, u); minV = Math.min(minV, v); maxV = Math.max(maxV, v);
      }
      // as close as fits them all with a border; one lone spot gets a fixed close-up
      scale = Math.max(least, Math.min(least * 12, (width - 160) / Math.max(maxU - minU, 1e-6), (height - 120) / Math.max(maxV - minV, 1e-6)));
      // centred on them, even near a map edge
      ox = width / 2 - (minU + maxU) / 2 * scale;
      oy = height / 2 - (minV + maxV) / 2 * scale;
      return true;
    }
    fit();
    // the button goes back to the items when there are some, else to the whole map
    if (focus()) {
      const back = holder.querySelector('[data-lmfit]');
      // named for what the map shows
      const what = answer.route ? 'the route' : (answer.multi || answer.zones) ? 'the objectives' : answer.locks ? 'the locks' : item ? 'the item' : 'the places';
      back.textContent = 'Center';
      back.title = 'Zoom to ' + what + ' on this map';
    }
    if (floor) load(floor);
    draw();
  }

  // What it costs on the flea, bought new: a gun as its stock build, a key with all its uses. The server's own flea
  // price. Null while asking; a price of 0 when it can't be had there.
  const fleaQuotes = new Map();

  function fleaQuote(id) {
    if (fleaQuotes.has(id)) return fleaQuotes.get(id);
    fleaQuotes.set(id, null);
    getJson('api/flea?id=' + encodeURIComponent(id)).then(quote => {
      fleaQuotes.set(id, { price: quote.price, stock: quote.stock });
      render();
    }).catch(() => { fleaQuotes.set(id, { price: 0 }); render(); });
    return null;
  }

  function fleaCard(id, item) {
    if (item.flea === false) return card('Flea market', "<p>Can't be sold on the flea.</p>");
    const quote = fleaQuote(id);
    const price = quote?.price;
    return card('Flea market', `<dl class="facts">
        <dt>Price</dt><dd>${price == null ? '…' : price ? `<b>${money(price, ROUBLES)}</b>${quote.stock ? ' <span class="sub">(stock build)</span>' : ''}` : '<span class="sub">No price here</span>'}</dd>
        <dt>Selling</dt><dd>Allowed</dd>
      </dl>`);
  }

  // ---------- I want to… ----------

  // A goal ("Therapist LL4", "buy the FLIR scope", "finish Collector", "Workbench level 3", "make money") worked
  // back into what to do: every quest on the way, in order and why, the player level and trader levels it takes
  // (with the quests that give the standing), and the items to gather with where to get them. With a profile picked,
  // what's done already is left out.
  let goalItemHits = [];
  let goalItemTimer = 0;

  function itemData(id) {
    if (!itemCache.has(id)) {
      itemCache.set(id, null);
      getJson('api/item?id=' + encodeURIComponent(id))
        .then(found => { itemCache.set(id, found); Object.assign(data.items, found.items); })
        .catch(() => itemCache.set(id, { error: true }))
        .finally(() => render());
    }
    return itemCache.get(id);
  }

  function newPlan() {
    return { quests: new Map(), level: 0, levelWhy: '', traders: new Map(), hideout: new Map(), items: new Map(), money: new Map(), notes: [], pending: false, seen: new Set() };
  }

  const isDone = id => !!profile && statusOf(id) === 'done';
  const standingFrom = (quest, trader) => quest.rewards.filter(r => r.when === 'Success' && r.kind === 'TraderStanding' && r.trader === trader).reduce((sum, r) => sum + (r.value || 0), 0);

  function needLevel(plan, level, why) {
    if (level && level > plan.level) { plan.level = level; plan.levelWhy = why; }
  }

  function addItem(plan, id, count, fir, why, key = false) {
    if (!id || questItems.has(id)) return;
    if (data.currencies?.[id]) { plan.money.set(id, (plan.money.get(id) || 0) + count); return; }
    const entry = plan.items.get(id) || { id, count: 0, fir: false, key, for: [] };
    entry.count += key ? 0 : count;
    if (key) entry.count = 1;
    entry.fir ||= fir;
    if (!entry.for.includes(why)) entry.for.push(why);
    plan.items.set(id, entry);
  }

  // a quest and, first, everything it needs: earlier quests, the trader, level, loyalty
  function planQuest(plan, id, why) {
    const quest = byId.get(id);
    if (!quest || isDone(id)) return;
    const known = plan.quests.get(id);
    if (known) { if (why && !known.why.includes(why)) known.why.push(why); return; }
    const st = statusOf(id);
    if (st === 'lockedOut' || st === 'failed') {
      plan.notes.push(`${link(id)} is ${STATUS_TEXT[st].toLowerCase()} on this profile, so this goal needs another way.`);
      return;
    }
    plan.quests.set(id, { quest, why: why ? [why] : [] });
    if (!traderOpen(quest.trader)) {
      const by = data.traders[quest.trader]?.unlockedBy || [];
      if (by[0]) planQuest(plan, by[0], `unlocks ${traderName(quest.trader)}`);
    }
    for (const c of quest.start) {
      if (c.kind === 'Quest') {
        // a "fail this one" link is the rival path: not something to plan for
        if (c.status.length && c.status.every(x => x === 'Fail')) continue;
        for (const t of c.quests) {
          if (byId.has(t) && !(profile && linkMet(statusOf(t), c.status))) planQuest(plan, t, `opens ${quest.name}`);
        }
      } else if (c.kind === 'Level') needLevel(plan, c.value, `to start ${quest.name}`);
      else if (c.kind === 'TraderLoyalty' && c.trader) planTrader(plan, c.trader, c.value, `to start ${quest.name}`);
      else if (c.kind !== 'Quest') plan.notes.push(`${link(id)} also needs: ${esc(c.text)}`);
    }
    for (const o of quest.objectives) {
      if (['HandoverItem', 'LeaveItemAtLocation', 'PlaceBeacon'].includes(o.kind) && o.items.length && firstOfGroup(quest, o)) {
        addItem(plan, o.items[0], o.count ?? 1, o.kind === 'HandoverItem' && o.fir, quest.name);
      }
    }
    for (const k of quest.needs.filter(n => n.kind === 'key' && n.ids.length)) addItem(plan, k.ids[0], 1, false, quest.name, true);
  }

  // a trader loyalty level: player level, standing (planning the quests that give it, cheapest first) and spend
  function planTrader(plan, trader, level, why) {
    const have = profile?.traders?.[trader]?.loyalty || 1;
    if (have >= level) return;
    const known = plan.traders.get(trader);
    if (known && known.level >= level) { if (!known.why.includes(why)) known.why.push(why); return; }
    const entry = { level, why: known ? [...known.why, why] : [why], standing: null, sales: 0 };
    plan.traders.set(trader, entry);
    if (!traderOpen(trader)) {
      const by = data.traders[trader]?.unlockedBy || [];
      if (by[0]) planQuest(plan, by[0], `unlocks ${traderName(trader)}`);
    }
    const info = wiki('trader:' + trader, 'api/trader?id=' + encodeURIComponent(trader));
    if (!info) { plan.pending = true; return; }
    const needs = info.error ? null : info.levels.find(l => l.level === level);
    if (!needs) return;
    needLevel(plan, needs.minLevel, `${traderName(trader)} LL${level}`);
    const standing = profile?.traders?.[trader]?.standing ?? 0;
    entry.standing = { have: standing, need: needs.minStanding ?? 0 };
    // the quests that close the gap are picked once everything else is planned: fillStanding
    entry.sales = Math.max(0, (needs.minSalesSum ?? 0) - (profile?.traders?.[trader]?.salesSum ?? 0));
    entry.currency = info.currency;
  }

  // a hideout area level, and every level and area before it
  function planHideout(plan, hideout, type, level, why) {
    const area = hideout.areas.find(a => String(a.type) === String(type));
    if (!area) return;
    const have = +(profile?.hideout?.[type] ?? 0);
    for (let at = have + 1; at <= level; at++) {
      const key = `${type}.${at}`;
      if (plan.seen.has(key)) continue;
      plan.seen.add(key);
      const stage = area.levels.find(l => l.level === at);
      if (!stage) continue;
      const label = `${area.name} ${at}`;
      for (const i of stage.items) addItem(plan, i.id, i.count, i.fir, label);
      for (const a of stage.areas) planHideout(plan, hideout, a.area, a.level, `for ${label}`);
      for (const t of stage.traders) planTrader(plan, t.trader, t.level, `for ${label}`);
      for (const sk of stage.skills) plan.notes.push(`${esc(label)} needs ${esc(sk.skill)} level ${sk.level}.`);
      const known = plan.hideout.get(type);
      plan.hideout.set(type, { name: area.name, level: Math.max(at, known?.level || 0), have, why: known?.why || why });
    }
  }

  // the cheapest way to be able to buy an item: fewest trader levels and quests still to go
  function planItem(plan, item, id) {
    const steps = o => {
      const have = profile?.traders?.[o.trader]?.loyalty || 1;
      let cost = (traderOpen(o.trader) ? 0 : 6) + Math.max(0, o.level - have) * 4 + (o.currency ? 0 : 0.5);
      if (o.quest && !(profile && offerOpen({ ...o, level: 0 }))) cost += [...chain(o.quest)].filter(c => !isDone(c)).length;
      return cost;
    };
    const offers = [...item.buy].sort((a, b) => steps(a) - steps(b));
    const best = offers[0];
    if (!best) return null;
    if (profile && offerOpen(best)) return { best, now: true, offers };
    planTrader(plan, best.trader, best.level, `${traderName(best.trader)} sells ${item.name} at LL${best.level}`);
    if (best.quest) planQuest(plan, best.quest, `${best.questState === 'started' ? 'starting it' : 'finishing it'} unlocks ${item.name} at ${traderName(best.trader)}`);
    return { best, now: false, offers };
  }

  // Items that only a quest opens up, for sale at a trader or as a craft, nearest first: how many quests are left
  // before you can get it, and the one to do now.
  let unlockKind = '';
  function unlocksList() {
    const left = new Map();
    const pathOf = id => {
      if (!left.has(id)) {
        const plan = newPlan();
        planQuest(plan, id, '');
        const quests = [...plan.quests.values()].map(x => x.quest);
        left.set(id, { count: quests.length, next: quests.find(q => ['available', 'active', 'ready'].includes(statusOf(q.id))), blocked: plan.notes.length > 0 });
      }
      return left.get(id);
    };
    const best = new Map();
    for (const quest of data.quests) {
      for (const r of quest.rewards) {
        if (r.when !== 'Success' || !r.item || !['AssortmentUnlock', 'ProductionScheme'].includes(r.kind)) continue;
        const how = r.kind === 'AssortmentUnlock' ? 'sale' : 'craft';
        const key = `${how}|${r.item}`;
        const path = pathOf(quest.id);
        const row = { id: r.item, how, quest, trader: r.trader, ...path, done: isDone(quest.id) };
        const have = best.get(key);
        if (!have || (row.done && !have.done) || (!have.done && !row.done && (row.blocked ? 999 : row.count) < (have.blocked ? 999 : have.count))) best.set(key, row);
      }
    }
    let rows = [...best.values()];
    const total = rows.length;
    if (unlockKind === 'sale' || unlockKind === 'craft') rows = rows.filter(r => r.how === unlockKind);
    else if (unlockKind === 'open') rows = rows.filter(r => r.done);
    else if (unlockKind === 'shut') rows = rows.filter(r => r.blocked);
    else rows = rows.filter(r => !r.done);
    rows.sort((a, b) => a.blocked - b.blocked || a.count - b.count || itemName(a.id).localeCompare(itemName(b.id)));
    const chip = (k, t) => `<button class="chip${unlockKind === k ? ' on' : ''}" data-unlockkind="${k}">${t}</button>`;
    return `<div class="card"><h3>What quests unlock <span class="n">${rows.length} of ${total}</span></h3>
      <div class="toolbar">${chip('', 'Not yet')}${chip('sale', 'Trader sale')}${chip('craft', 'Crafting')}${profile ? chip('open', 'Unlocked') : ''}${profile ? chip('shut', 'Out of reach') : ''}</div>
      <table class="items-table"><thead><tr><th>Item</th><th>Opens by</th><th class="num">Quests left</th><th>Do now</th></tr></thead><tbody>${rows.slice(0, 300).map(r => `<tr data-goal="item.${esc(r.id)}" class="clickrow">
        <td><div class="item">${icon(r.id, 'sm')}<a>${esc(itemName(r.id))}</a></div></td>
        <td>${r.how === 'sale' ? `Sold by ${esc(traderName(r.trader || r.quest.trader))}` : 'Hideout craft'}<div class="sub">${link(r.quest.id)}</div></td>
        <td class="num">${r.done ? 'Unlocked' : r.blocked ? 'Out of reach' : r.count}</td>
        <td>${r.done ? '' : r.next ? link(r.next.id) : '<span class="sub">Nothing open yet</span>'}</td></tr>`).join('')}</tbody></table>
      ${rows.length > 300 ? `<p class="sub">The first 300 of ${rows.length}.</p>` : ''}</div>`;
  }

  function goalsPage(goal) {
    const [kind, a, b] = String(goal || '').split('.');
    const picker = goalPicker();
    const head = `${crumbs([null, 'I want to…'])}
      <div class="page-head"><div class="t"><h2>I want to…</h2>
        <p>Pick what you're after. The wiki works out every quest on the way (and the ones before those), the level and
          trader standing it takes, and the items to gather, from this server's quests, traders and hideout.
          ${profile ? `What <b>${esc(profile.name)}</b> has done already is left out.` : 'Pick your <b>profile</b> at the top to leave out what you\'ve done.'}</p></div></div>`;
    if (!kind) return head + picker;
    if (kind === 'money') return head + picker + moneyGoal();
    if (kind === 'unlocks') return head + picker + unlocksList();

    const plan = newPlan();
    let title = '', intro = '';
    if (kind === 'trader') {
      const level = +b || 2;
      title = `${traderName(a)} loyalty level ${level}`;
      const have = profile?.traders?.[a]?.loyalty || 1;
      if (profile && have >= level) intro = `<div class="note done">✓ You're already LL${have} with ${esc(traderName(a))}.</div>`;
      planTrader(plan, a, level, 'your goal');
    } else if (kind === 'quest') {
      const quest = byId.get(a);
      if (!quest) return head + picker + '<div class="intro red">This server has no such quest.</div>';
      title = `Finish ${quest.name}`;
      if (isDone(a)) intro = '<div class="note done">✓ You\'ve finished it already.</div>';
      planQuest(plan, a, 'your goal');
    } else if (kind === 'hideout') {
      const hideout = wiki('hideout', 'api/hideout');
      if (!hideout) return head + picker + '<div class="loading">Loading the hideout…</div>';
      const area = hideout.areas?.find(x => String(x.type) === String(a));
      if (!area) return head + picker + '<div class="intro red">This server has no such hideout area.</div>';
      Object.assign(data.items, hideout.items);
      const level = Math.min(+b || 1, Math.max(...area.levels.map(l => l.level)));
      title = `Build ${area.name} level ${level}`;
      if (profile && +(profile.hideout?.[a] ?? 0) >= level) intro = '<div class="note done">✓ You\'ve built it already.</div>';
      planHideout(plan, hideout, a, level, 'your goal');
    } else if (kind === 'item') {
      const item = itemData(a);
      if (!item) return head + picker + '<div class="loading">Looking the item up…</div>';
      if (item.error) return head + picker + '<div class="intro red">This server has no such item.</div>';
      title = `Get ${item.name}`;
      const way = planItem(plan, item, a);
      if (way?.now) intro = `<div class="note done">✓ You can buy it now: ${offerText(way.best)}.</div>`;
      else if (way) intro = `<div class="note">Cheapest way: <b>${offerText(way.best)}</b>.${way.offers.length > 1 ? ` <span class="sub">${way.offers.length - 1} other offer${way.offers.length === 2 ? '' : 's'} on <a data-item="${esc(a)}">its page</a>.</span>` : ''}</div>`;
      else if (item.craft.length) {
        const craft = item.craft[0];
        const hideout = wiki('hideout', 'api/hideout');
        intro = `<div class="note">No trader sells it here. Craft it at <b>${esc(craft.areaName || 'the hideout')}</b> level ${craft.level}${craft.quest ? ` after ${link(craft.quest)}` : ''}.</div>`;
        if (hideout?.areas) planHideout(plan, hideout, craft.area, craft.level, `crafts ${item.name}`);
        else plan.pending = true;
        if (craft.quest) planQuest(plan, craft.quest, `unlocks crafting ${item.name}`);
      } else if (item.loot.length) {
        const spot = item.loot[0];
        intro = `<div class="note">No trader sells it and nothing crafts it here: find it. Look in: ${lootLine(item.loot)} <a data-item="${esc(a)}">Every spot</a>.</div>`;
      } else {
        intro = `<div class="note">This server has no trader, craft or loot spawn for it${item.fromQuests.length ? `; it comes from ${item.fromQuests.map(f => link(f.quest)).join(', ')}` : ''}.</div>`;
        for (const f of item.fromQuests.filter(f => f.kind === 'Item').slice(0, 1)) planQuest(plan, f.quest, `gives ${item.name}`);
      }
    }
    return head + picker + planHtml(plan, title, intro);
  }

  // Standing a goal still needs once its own quests are planned. Every planned quest's standing counts, for every
  // trader, gains and losses alike; then, one at a time, the quest that gives the most of what's still missing for
  // each new quest it adds (itself and any before it not yet planned) goes in, and everything is counted again.
  function standingGap(plan, trader) {
    const e = plan.traders.get(trader);
    if (!e?.standing) return 0;
    let gap = e.standing.need - e.standing.have;
    for (const { quest } of plan.quests.values()) gap -= standingFrom(quest, trader);
    return gap;
  }

  const levelOf = q => Math.max(0, ...q.start.filter(c => c.kind === 'Level').map(c => c.value || 0));

  function fillStanding(plan) {
    if (plan.standingDone) return;
    plan.standingDone = true;
    const before = plan.quests.size;
    const usable = q => !isDone(q.id) && !['lockedOut', 'failed'].includes(statusOf(q.id));
    const givers = data.quests.filter(q => usable(q) && q.rewards.some(r => r.when === 'Success' && r.kind === 'TraderStanding' && r.value > 0));
    for (let round = 0; round < 300; round++) {
      const gaps = new Map([...plan.traders.keys()].map(t => [t, standingGap(plan, t)]).filter(([, g]) => g > 0.001));
      if (!gaps.size) break;
      let best = null;
      for (const q of givers) {
        if (plan.quests.has(q.id)) continue;
        const adds = [...chain(q.id)].filter(c => byId.has(c) && !isDone(c) && !plan.quests.has(c));
        if (adds.some(c => !usable(byId.get(c)))) continue;
        // never raise the level the goal already needs (or the one you're at) just for standing
        if (adds.some(c => levelOf(byId.get(c)) > Math.max(plan.level, profile?.level || 0))) continue;
        let gain = 0;
        for (const [t, g] of gaps) gain += Math.min(g, Math.max(0, adds.reduce((n, c) => n + standingFrom(byId.get(c), t), 0)));
        if (gain <= 0.001) continue;
        const score = gain / adds.length;
        if (!best || score > best.score) best = { q, score };
      }
      if (!best) break;
      const why = [...gaps.keys()].filter(t => standingFrom(best.q, t) > 0)
        .map(t => `+${standingFrom(best.q, t).toFixed(2)} ${traderName(t)} standing (LL${plan.traders.get(t).level} needs ${plan.traders.get(t).standing.need})`).join(', ');
      planQuest(plan, best.q.id, why || 'opens a quest that gives standing');
    }
    plan.forStanding = plan.quests.size - before;
    for (const t of plan.traders.keys()) {
      const gap = standingGap(plan, t);
      if (gap > 0.001) plan.notes.push(`Even with every quest that gives it, ${esc(traderName(t))} standing stays ${gap.toFixed(2)} short of LL${plan.traders.get(t).level}. Repeatable quests or other trades will have to make it up.`);
    }
  }

  function planHtml(plan, title, intro) {
    if (!plan.pending) fillStanding(plan);
    const quests = [...plan.quests.values()].sort((x, y) => (x.quest.depth ?? 0) - (y.quest.depth ?? 0) || (x.quest.level ?? 0) - (y.quest.level ?? 0) || x.quest.name.localeCompare(y.quest.name));
    const items = [...plan.items.values()].sort((x, y) => y.key - x.key || y.fir - x.fir || itemName(x.id).localeCompare(itemName(y.id)));
    needWhere(items.map(i => i.id));
    const myLevel = profile?.level || 0;
    const levelText = plan.level ? (profile && myLevel >= plan.level ? `level ${plan.level} <span class="sub">(you're ${myLevel} ✓)</span>` : `level ${plan.level}${profile ? ` <span class="sub">(you're ${myLevel})</span>` : ''}`) : 'any level';
    const now = quests.filter(x => ['available', 'active', 'ready'].includes(statusOf(x.quest.id)));
    const traderRows = [...plan.traders.entries()].map(([t, e]) => `<div class="need"><span class="k">${avatar(t)} ${esc(traderName(t))} LL${e.level}</span><span>
        ${e.standing ? `standing ${e.standing.have.toFixed(2)} → <b>${e.standing.need}</b>` : ''}${e.sales ? ` · spend or sell ${money(e.sales, currencyId(e.currency))} more with them` : ''}
        <span class="sub">${e.why.map(esc).join(' · ')}</span></span></div>`).join('');
    const hideoutRows = [...plan.hideout.entries()].map(([type, h]) => `<div class="need"><span class="k"><a data-page="hideout" data-id="${type}">${esc(h.name)}</a></span><span>level ${h.have} → <b>${h.level}</b> <span class="sub">${esc(h.why)}</span></span></div>`).join('');
    const moneyRows = [...plan.money.entries()].map(([c, n]) => `<div class="need"><span class="k">Money</span><span><b>${money(n, c)}</b> to hand over</span></div>`).join('');
    const itemRows = items.map(i => `<tr><td>${icon(i.id, 'sm')}</td><td>${i.key ? 'Key: ' : ''}${itemLink(i.id)}${i.fir ? ' <span class="fir">FIR</span>' : ''}</td>
        <td class="num">${i.key ? 'key' : '× ' + number(i.count)}</td><td class="sub">${i.for.map(esc).join(', ')}</td><td>${howToGet(i.id)}</td></tr>`).join('');

    return `<div class="section goal-result"><h3>${esc(title)}</h3>${intro}
      ${plan.pending ? '<div class="loading">Reading trader and hideout data…</div>' : ''}
      <div class="tiles">
        <div class="tile"><div class="k">Quests to do</div><div class="v">${quests.length}</div><div class="s">${plan.forStanding ? `${number(quests.length - plan.forStanding)} on the way, ${number(plan.forStanding)} only for trader standing · ` : ''}${now.length ? `${now.length} you can do now` : quests.length ? 'none open yet' : 'none'}</div></div>
        <div class="tile"><div class="k">Player level</div><div class="v small">${levelText}</div><div class="s">${esc(plan.levelWhy)}</div></div>
        <div class="tile"><div class="k">Items to gather</div><div class="v">${items.length}</div><div class="s">${items.filter(i => i.fir).length} found in raid · ${items.filter(i => i.key).length} keys</div></div>
        <div class="tile"><div class="k">Trader levels</div><div class="v">${plan.traders.size}</div><div class="s">${[...plan.traders.entries()].map(([t, e]) => `${esc(traderName(t))} LL${e.level}`).join(', ') || 'none needed'}</div></div>
      </div>
      ${plan.notes.length ? `<div class="card"><h3>Good to know</h3><ul class="plain">${[...new Set(plan.notes)].map(n => `<li>${n}</li>`).join('')}</ul></div>` : ''}
      <div class="article"><div class="col">
        ${card('Quests, in order', quests.length ? `<p class="sub" style="margin-top:0">Top to bottom: each quest opens the ones below it. The chips say why it's on the list.</p>
          <div class="list">${quests.map(x => row(x.quest, esc(traderName(x.quest.trader)), '', reasonChips(x.why))).join('')}</div>` : '<p class="none">No quests needed.</p>', quests.length)}
        ${items.length || moneyRows ? card('Items to gather', `${moneyRows}${items.length ? `<table class="items-table goal-items">${itemRows}</table>` : ''}
          <div class="src">Hand-ins, items to place and keys, for every quest and upgrade above. FIR: found in raid, buying won't do.</div>`, items.length) : ''}
      </div><div class="col">
        ${traderRows ? card('Trader levels', traderRows) : ''}
        ${hideoutRows ? card('Hideout', hideoutRows) : ''}
        ${now.length ? card('Start with', `<div class="chips">${now.slice(0, 8).map(x => qchip(x.quest.id)).join('')}</div>${profile ? '<button class="btn" data-page="plan" data-id="" style="margin-top:10px">Plan a raid for them</button>' : ''}`) : ''}
      </div></div></div>`;
  }

  // Making money: which maps hold the most loot per raid, what's worth grabbing there, and quests that pay well.
  function moneyGoal() {
    const maps = wiki('maps', 'api/maps');
    if (!maps) return '<div class="loading">Reading the maps…</div>';
    const ranked = (maps.error ? [] : maps).filter(m => m.lootValue > 0).sort((x, y) => y.lootValue - x.lootValue);
    const best = ranked[0];
    const detail = best ? wiki('map:' + best.id, 'api/map?id=' + encodeURIComponent(best.id)) : null;
    if (detail?.items) Object.assign(data.items, detail.items);
    const top = ranked[0]?.lootValue || 1;
    const quests = data.quests.filter(q => (profile ? ['available', 'active', 'ready'].includes(statusOf(q.id)) : !q.hidden && !q.start.some(c => c.kind === 'Quest')) && q.rewardValue > 0)
      .sort((x, y) => y.rewardValue - x.rewardValue).slice(0, 8);
    return `<div class="section goal-result"><h3>Make money</h3>
      <div class="note">Go where the loot is worth the most, take what's valuable for its size, and do the quests that pay. Don't sell what your quests still want: <a data-page="keep" data-id="">Items to keep</a>.</div>
      <div class="article"><div class="col">
        ${card('Richest maps', ranked.length ? ranked.map(m => `<button class="loot-row link" data-page="map" data-id="${esc(m.id)}"><div class="map">${esc(m.name)}</div>
          <div class="lbar"><span style="width:${Math.max(3, Math.round(m.lootValue * 100 / top))}%"></span></div><div class="odds">${money(m.lootValue, ROUBLES)}</div></button>`).join('')
          + '<div class="src">Average value of all loot a raid holds, from this server\'s loot tables and base prices. You can only carry some of it: look for the valuable items below.</div>'
          : '<p class="none">The server is still working out where items spawn. Try again in a minute.</p>')}
        ${detail?.bestLoot?.length ? card(`Worth grabbing on ${esc(best.name)}`, `<div class="item-cards small">${detail.bestLoot.slice(0, 12).map(i => `<button class="item-card" data-item="${i.id}"><span class="pic">${pic(i.id)}</span>
          <span class="n">${esc(i.name)}</span><span class="s">${money(i.price, ROUBLES)} · ${chance(i.perRaid)}</span><span class="s">${esc(i.where.join(', '))}</span></button>`).join('')}</div>`) : ''}
      </div><div class="col">
        ${card(profile ? 'Quests you can do that pay' : 'Starting quests that pay', quests.length ? `<div class="list">${quests.map(q => row(q, esc(traderName(q.trader)), '', reasonChips([`items worth ${money(q.rewardValue, ROUBLES)}`]))).join('')}</div>` : '<p class="none">None right now.</p>')}
      </div></div></div>`;
  }

  function goalPicker() {
    const hideout = wiki('hideout', 'api/hideout');
    const areas = hideout?.areas || [];
    // keep the boxes on the goal being shown
    const [gk, ga, gb] = String(view.id || '').split('.');
    const sel = (kind, which, value) => gk === kind && String(which) === String(value) ? ' selected' : '';
    const quick = [
      ['money', 'Make money'],
      ['unlocks', 'See what quests unlock'],
      byId.has(COLLECTOR) && [`quest.${COLLECTOR}`, 'Get Kappa'],
      byId.has(KNOCK_KNOCK) && [`quest.${KNOCK_KNOCK}`, 'Reach Lightkeeper'],
      ...Object.entries(data.traders).filter(([t, info]) => info.unlockedBy?.length && t !== LIGHTKEEPER_TRADER && !traderOpen(t))
        .map(([t, info]) => [`quest.${info.unlockedBy[0]}`, `Unlock ${traderName(t)}`])
    ].filter(Boolean);
    return `<div class="goal-picker">
      <div class="gp"><h4>Reach a trader level</h4><div class="row-in">
        <select id="gTrader">${traders.filter(t => (data.traders[t]?.levels ?? 4) > 1).map(t => `<option value="${t}"${sel('trader', ga, t)}>${esc(traderName(t))}${profile ? ` (LL${profile.traders?.[t]?.loyalty || 1})` : ''}</option>`).join('')}</select>
        <select id="gLevel">${[2, 3, 4].map(l => `<option value="${l}"${gk === 'trader' ? sel('trader', gb, l) : l === 4 ? ' selected' : ''}>LL${l}</option>`).join('')}</select>
        <button class="btn" data-goalgo="trader">Show me</button></div></div>
      <div class="gp"><h4>Unlock an item to buy</h4><input class="filter" id="gItem" placeholder="Item name, e.g. FLIR" autocomplete="off"><div class="ghits" id="gItemHits"></div></div>
      <div class="gp"><h4>Finish a quest</h4><input class="filter" id="gQuest" placeholder="Quest name" autocomplete="off"><div class="ghits" id="gQuestHits"></div></div>
      <div class="gp"><h4>Build a hideout level</h4><div class="row-in">
        <select id="gArea">${areas.map(x => `<option value="${x.type}"${sel('hideout', ga, x.type)}>${esc(x.name)}${profile ? ` (${profile.hideout?.[x.type] ?? 0})` : ''}</option>`).join('')}</select>
        <select id="gAreaLevel">${[1, 2, 3, 4, 5, 6].map(l => `<option value="${l}"${sel('hideout', gb, l)}>level ${l}</option>`).join('')}</select>
        <button class="btn" data-goalgo="hideout">Show me</button></div></div>
      <div class="gp wide"><h4>Or</h4><div class="chips">${quick.map(([g, label]) => `<button class="chip${view.id === g ? ' on' : ''}" data-goal="${esc(g)}">${label}</button>`).join('')}</div></div>
    </div>`;
  }

  // typing in the item and quest boxes shows matches under them, without drawing the page again
  // letters and digits only, so "shooter born" and "shooterborn" both find Shooter Born in Heaven
  function plain(text) {
    return String(text || '').toLowerCase().replace(/[^\p{L}\p{N}]+/gu, '');
  }

  // a name's words, each also split where letters meet digits ("ak103" gives ak103, ak and 103)
  function wordsOf(text) {
    return String(text || '').split(/\s+/).map(plain).filter(Boolean).flatMap(w => [w, ...(w.match(/\p{L}+|\p{N}+/gu) || [])]);
  }

  // quests whose name (or live name) holds the text, or whose words start with each word typed; the closest first
  function findQuests(text) {
    const whole = plain(text);
    const words = text.split(/\s+/).map(plain).filter(Boolean);
    if (!whole) return [];
    return data.quests
      .map(q => {
        const names = [plain(q.name), plain(q.liveName)];
        const at = Math.min(...names.map(n => { const i = n.indexOf(whole); return i < 0 ? 999 : i; }));
        const parts = words.length > 1 ? wordsOf(q.name + ' ' + (q.liveName || '')) : [];
        const found = at < 999 || q.id === text || (words.length > 1 && words.every(w => parts.some(p => p.startsWith(w))));
        return found && { q, rank: names.includes(whole) ? -1 : at };
      })
      .filter(Boolean)
      .sort((a, b) => a.rank - b.rank || a.q.name.length - b.q.name.length || a.q.name.localeCompare(b.q.name))
      .map(x => x.q);
  }

  function goalHits(box) {
    const text = box.value.trim().toLowerCase();
    if (box.id === 'gQuest') {
      const found = !text ? [] : findQuests(text).slice(0, 8);
      $('gQuestHits').innerHTML = found.map(q => `<button class="ghit" data-goal="quest.${q.id}">${avatar(q.trader)}<span>${esc(q.name)} <span class="sub">${esc(traderName(q.trader))}</span></span></button>`).join('');
      return;
    }
    clearTimeout(goalItemTimer);
    if (text.length < 2) { $('gItemHits').innerHTML = ''; return; }
    goalItemTimer = setTimeout(async () => {
      try {
        goalItemHits = await getJson('api/items?q=' + encodeURIComponent(text));
        goalItemHits.forEach(i => { data.items[i.id] ||= i.name; });
        if ($('gItem')?.value.trim().toLowerCase() === text) {
          $('gItemHits').innerHTML = goalItemHits.slice(0, 8).map(i => `<button class="ghit" data-goal="item.${i.id}">${icon(i.id, 'sm')}<span>${esc(i.name)} <span class="sub">${esc(i.category || '')}</span></span></button>`).join('');
        }
      } catch { /* nothing to show */ }
    }, 200);
  }

  // ---------- navigation ----------

  // #trader=<id>, #quest=<id>, #kappa=1 ..., so a page can be bookmarked, shared and gone back to
  function hashFor(v) {
    const hash = new URLSearchParams();
    if (v.page !== 'overview') hash.set(v.page, v.id || '1');
    if (v.tab) hash.set('tab', v.tab);
    return hash.toString() ? '#' + hash : location.pathname + location.search;
  }

  function go(next) {
    // a quest's hints shown stay shown only while you're on it
    if (next.page !== view.page || next.id !== view.id) revealed.clear();
    if (next.page !== 'quest') { mapFilter = ''; flagFilter = 0; }
    if (next.page !== view.page || next.id !== view.id) { ruleFilter = ''; ruleSort = ''; rulePick = ''; }
    if (next.page === 'items' && next.id !== view.id) { catalogFilter = ''; catalogShown = 240; if (catalogSort.startsWith('col:')) catalogSort = 'name'; }
    // a kind of bot picked in the menu
    if (next.page === 'bots') botKind = next.id || '';
    if (next.page === 'crafts') craftArea = next.id || '';
    if (next.page !== view.page) wishNote = null;
    view = next;
    history.pushState(null, '', hashFor(view));
    render();
    window.scrollTo({ top: 0 });
  }

  function readHash() {
    const hash = new URLSearchParams(location.hash.slice(1));
    // #profile=<id> opens that profile's progress (a link for a player)
    if (hash.has('profile') && profiles.some(p => p.id === hash.get('profile'))) {
      prefs.profile = hash.get('profile');
      $('profile').value = prefs.profile;
      savePrefs();
    }
    view = { page: 'overview', id: '' };
    for (const page of ['quest', 'item', 'trader', 'map', 'box', 'kappa', 'lightkeeper', 'lockouts', 'plan', 'items', 'keep', 'rewards', 'quests', 'traders', 'maps', 'hideout', 'crafts', 'bots', 'bot', 'goal', 'player', 'rules', 'wishlist', 'stash', 'kinds']) {
      if (hash.has(page)) {
        const value = hash.get(page);
        view = { page, id: value === '1' ? '' : value, tab: hash.get('tab') || '' };
        break;
      }
    }
    if (view.page === 'bots') botKind = view.id;
    if (view.page === 'crafts') craftArea = view.id;
    // older links: #q=<id>
    if (hash.has('q')) view = { page: 'quest', id: hash.get('q') };
  }

  window.addEventListener('popstate', () => {
    readHash();
    render();
  });

  // ---------- events ----------

  $('nav').addEventListener('mouseleave', () => $('nav').classList.remove('shut'));
  $('nav').addEventListener('mouseover', e => {
    const menu = e.target.closest('[data-menu]');
    const want = menu && MENU_DATA[menu.dataset.menu];
    if (want && !wikiCache.has(want[0])) wiki(...want);
  });


  document.addEventListener('click', e => {
    const target = e.target;
    // a blurred hint: shown for this quest, before a link under the blur can be followed
    if (target.closest('[data-back]')) { if (history.length > 1) history.back(); else go({ page: 'overview', id: '' }); return; }
    const reveal = target.closest('[data-reveal]');
    if (reveal) { reveal.nextElementSibling?.removeAttribute('hidden'); reveal.remove(); return; }
    const hx = target.closest('[data-hx]');
    if (hx) { e.preventDefault(); e.stopPropagation(); if (!hx.disabled) hideoutDo(hx); return; }
    const hint = target.closest('[data-spoiler]');
    if (hint) {
      e.preventDefault();
      e.stopPropagation();
      if (revealed.has(hint.dataset.spoiler)) revealed.delete(hint.dataset.spoiler); else revealed.add(hint.dataset.spoiler);
      render();
      return;
    }
    const lootMap = target.closest('[data-lootmap]');
    if (lootMap) { toggleLootMap(lootMap); return; }
    // another version of a map (SPT's or the 1.0 one): the whole page drawn again with it
    const version = target.closest('[data-lmversion]');
    if (version) {
      if (!version.classList.contains('on')) {
        (prefs.mapVersion || (prefs.mapVersion = {}))[version.dataset.map.toLowerCase()] = version.dataset.lmversion;
        savePrefs();
        render();
      }
      return;
    }
    const fold = target.closest('[data-fold]');
    if (fold) {
      const id = fold.dataset.fold;
      if (fold.dataset.shut === '1') { folded.delete(id); opened.add(id); } else { folded.add(id); opened.delete(id); }
      savePrefs();
      // keep the row where it was on screen
      const y = fold.getBoundingClientRect().top;
      render();
      const again = document.querySelector(`[data-fold="${id}"]`);
      if (again) window.scrollBy(0, again.getBoundingClientRect().top - y);
      return;
    }
    const foldAll = target.closest('[data-foldall]');
    if (foldAll) {
      if (foldAll.dataset.foldall === '0') {
        // open everything, locked lines too
        folded.clear();
        prefs.showLocked = true;
      } else {
        foldAll.dataset.foldall.split(',').filter(Boolean).forEach(r => { folded.add(r); opened.delete(r); });
        opened.clear();
        prefs.showLocked = false;
      }
      savePrefs();
      render();
      return;
    }
    const tab = target.closest('[data-tab]');
    if (tab) { view = { ...view, tab: tab.dataset.tab }; history.replaceState(null, '', hashFor(view)); render(); return; }
    const scroll = target.closest('[data-scroll]');
    if (scroll) { $(scroll.dataset.scroll)?.scrollIntoView({ behavior: 'smooth', block: 'start' }); return; }
    if (target.closest('[data-focus]')) { search.focus(); return; }
    // a stat column on an item list: biggest first, then smallest first, then back to by name
    // the Value heading: most valuable first, then least, then back to by name
    if (target.closest('[data-vsort]')) { catalogSort = catalogSort === 'price' ? 'price:a' : catalogSort === 'price:a' ? 'name' : 'price'; render(); return; }
    const colSortBy = target.closest('[data-csort]');
    if (colSortBy) { const k = 'col:' + colSortBy.dataset.csort; catalogSort = catalogSort === k + ':d' ? k + ':a' : catalogSort === k + ':a' ? 'name' : k + ':d'; render(); return; }
    const sort = target.closest('[data-sort]');
    if (sort) { catalogSort = sort.dataset.sort; render(); return; }
    if (target.closest('[data-modded]')) { catalogModded = !catalogModded; render(); return; }
    const chain = target.closest('[data-chain]');
    if (chain) { showChain(chain.dataset.chain); return; }
    const gv = target.closest('[data-goalview]');
    if (gv) { goalView = gv.dataset.goalview; render(); return; }
    if (target.closest('[data-treefold]')) { prefs.treeFoldDone = !prefs.treeFoldDone; savePrefs(); render(); return; }
    const dq = target.closest('[data-dq]');
    if (dq && !target.closest('a')) {
      // light up what this quest needs and what needs it; the rest dims
      const wrap = dq.closest('.diagram');
      const id = dq.dataset.dq;
      const was = wrap.dataset.lit === id;
      wrap.querySelectorAll('.dq,path').forEach(el => el.classList.remove('on'));
      wrap.classList.toggle('lit', !was);
      wrap.dataset.lit = was ? '' : id;
      if (!was) {
        const paths = [...wrap.querySelectorAll('path')];
        const walk = (from, key, other) => {
          const seen = new Set([from]); const todo = [from];
          while (todo.length) {
            const at = todo.pop();
            for (const e of paths.filter(x => x.dataset[key] === at)) { e.classList.add('on'); const next = e.dataset[other]; if (!seen.has(next)) { seen.add(next); todo.push(next); } }
          }
          return seen;
        };
        const lit = new Set([...walk(id, 'to', 'from'), ...walk(id, 'from', 'to')]);
        lit.forEach(x => wrap.querySelector(`[data-dq="${x}"]`)?.classList.add('on'));
      }
      return;
    }
    const uk = target.closest('[data-unlockkind]');
    if (uk) { unlockKind = uk.dataset.unlockkind; render(); return; }
    const modf = target.closest('[data-modf]');
    if (modf) { const m = modf.dataset.modf; modFilter.has(m) ? modFilter.delete(m) : modFilter.add(m); render(); return; }
    if (target.closest('[data-compactmods]')) { prefs.compactMods = !prefs.compactMods; savePrefs(); render(); return; }
    const ovMore = target.closest('[data-ovmore]');
    if (ovMore) { const st = ovMore.dataset.ovmore; overviewOpen.has(st) ? overviewOpen.delete(st) : overviewOpen.add(st); render(); return; }
    if (target.closest('[data-hidelocked]')) { prefs.hideLockedLists = !prefs.hideLockedLists; savePrefs(); render(); return; }
    const achChip = target.closest('[data-achrarity]');
    if (achChip) { achRarity = achChip.dataset.achrarity; render(); return; }
    if (target.closest('[data-unused]')) { catalogUnused = !catalogUnused; render(); return; }
    if (target.closest('[data-moreitems]')) { catalogShown += 240; render(); return; }
    const more = target.closest('[data-more]');
    if (more) { $(more.dataset.more)?.classList.remove('short'); more.remove(); return; }
    const plan = target.closest('[data-plan]');
    if (plan) { planMap = plan.dataset.plan; view = { page: 'plan', id: planMap }; history.replaceState(null, '', hashFor(view)); render(); return; }
    const keep = target.closest('[data-keep]');
    if (keep) { keepFilter = +keep.dataset.keep; render(); return; }
    if (target.closest('[data-back]')) {
      if (history.length > 1) history.back(); else go({ page: 'overview', id: '' });
      return;
    }
    const goal = target.closest('[data-goal]');
    if (goal) { go({ page: 'goal', id: goal.dataset.goal }); return; }
    const goalGo = target.closest('[data-goalgo]');
    if (goalGo) {
      go({ page: 'goal', id: goalGo.dataset.goalgo === 'trader' ? `trader.${$('gTrader').value}.${$('gLevel').value}` : `hideout.${$('gArea').value}.${$('gAreaLevel').value}` });
      return;
    }
    const box = target.closest('[data-box]');
    if (box) {
      e.preventDefault();
      e.stopPropagation();
      $('searchResults').hidden = true;
      go({ page: 'box', id: box.dataset.box });
      return;
    }
    const wishAdd = target.closest('[data-wishadd]');
    if (wishAdd) { e.stopPropagation(); if (!wishAdd.disabled) wishDo('add', wishAdd.dataset.wishadd, wishGroup); return; }
    const wishDel = target.closest('[data-wishdel]');
    if (wishDel) { e.stopPropagation(); if (!wishDel.disabled) wishDo('remove', wishDel.dataset.wishdel, 0); return; }
    // a quest named on an item's card (a stock card's "Needs ..."): the quest, not the item
    const cardQuest = target.closest('[data-q]');
    if (cardQuest && cardQuest.closest('[data-item]')) {
      e.preventDefault();
      e.stopPropagation();
      go({ page: 'quest', id: cardQuest.dataset.q });
      return;
    }
    const item = target.closest('[data-item]');
    if (item) {
      e.preventDefault();
      e.stopPropagation();
      $('searchResults').hidden = true;
      go({ page: 'item', id: item.dataset.item });
      return;
    }
    const armorRow = target.closest('[data-armor]');
    if (armorRow) { const id = armorRow.dataset.armor; if (!armorOpen.delete(id)) armorOpen.add(id); render(); return; }
    const q = target.closest('[data-q]');
    if (q && !target.closest('a[href]')) {
      e.preventDefault();
      $('searchResults').hidden = true;
      go({ page: 'quest', id: q.dataset.q });
      return;
    }
    const page = target.closest('[data-page]');
    if (page) {
      $('searchResults').hidden = true;
      // a page picked from the menu bar (a section or its menu): the menu closes until the pointer leaves the bar
      if (page.closest('#nav')) $('nav').classList.add('shut');
      go({ page: page.dataset.page, id: page.dataset.id || '' });
      return;
    }
    const map = target.closest('[data-map]');
    if (map) { mapFilter = map.dataset.map; render(); return; }
    const flag = target.closest('[data-flag]');
    if (flag) { const f = +flag.dataset.flag; flagFilter = flagFilter === f ? 0 : f; render(); return; }
    if (target.closest('[data-rewardsall]')) { prefs.rewardsAll = !prefs.rewardsAll; savePrefs(); render(); return; }
    if (target.closest('[data-hide]')) { prefs.hideDone = !prefs.hideDone; savePrefs(); render(); return; }
    const sortBy = target.closest('[data-rsort]');
    if (sortBy) { const k = sortBy.dataset.rsort; ruleSort = ruleSort === k + ':d' ? k + ':a' : ruleSort === k + ':a' ? '' : k + ':d'; render(); return; }
    if (target.closest('[data-rsortclear]')) { ruleSort = ''; render(); return; }
    const questMap = target.closest('[data-qmap]');
    if (questMap) { const [q, m] = questMap.dataset.qmap.split('|'); questMapPick.set(q, m); render(); return; }
    const stashSell = target.closest('[data-stashsell]');
    if (stashSell) { e.stopPropagation(); if (!stashSell.disabled) stashSellOne(stashSell.dataset.stashsell); return; }
    const stashFix = target.closest('[data-stashrepair]');
    if (stashFix) { e.stopPropagation(); if (!stashFix.disabled) stashDo('repair', stashFix.dataset.stashrepair, `Repair at ${stashFix.closest('tr').children[1].textContent} for ${stashFix.closest('tr').children[2].textContent}`); return; }
    const shopAdd = target.closest('[data-shopadd]');
    if (shopAdd) { shopFind = ''; shopSet(shopAdd.dataset.shopadd, (shopList()[shopAdd.dataset.shopadd] || 0) + 1); return; }
    const shopN = target.closest('[data-shopn]');
    if (shopN) { const [id, step] = shopN.dataset.shopn.split('|'); shopSet(id, +step ? (shopList()[id] || 0) + +step : 0); return; }
    if (target.closest('[data-shopwish]')) {
      const list = { ...shopList() };
      for (const w of wishState()?.list?.items || []) if (!list[w.id]) list[w.id] = 1;
      prefs.shop = { ...(prefs.shop || {}), [profile.id]: list };
      savePrefs();
      render();
      return;
    }
    const stashQ = target.closest('[data-stashquick]');
    if (stashQ) { e.stopPropagation(); stashQuick(stashQ.dataset.stashquick); return; }
    const stashC = target.closest('[data-stashcat]');
    if (stashC) { stashCat = stashC.dataset.stashcat; render(); return; }
    const stashS = target.closest('[data-stashsort]');
    if (stashS) { const k = stashS.dataset.stashsort; const [was, dir] = stashSort.split(':'); stashSort = was === k ? `${k}:${dir === 'a' ? 'd' : 'a'}` : `${k}:${['name', 'where', 'cat'].includes(k) ? 'a' : 'd'}`; render(); return; }
    const stashBox = target.closest('[data-stashopen]');
    if (stashBox) { stashOpen = stashBox.dataset.stashopen; render(); return; }
    const rulePicked = target.closest('[data-rpick]');
    if (rulePicked) { rulePick = rulePicked.dataset.rpick; render(); return; }
    const botKindChip = target.closest('[data-botkind]');
    if (botKindChip) { botKind = botKindChip.dataset.botkind; render(); return; }
    const craftAreaChip = target.closest('[data-craftarea]');
    if (craftAreaChip) { craftArea = craftAreaChip.dataset.craftarea; render(); return; }
    if (target.closest('[data-craftnow]')) { craftNow = !craftNow; render(); return; }
    if (target.closest('[data-hidehave]')) { prefs.hideHave = !prefs.hideHave; savePrefs(); render(); return; }
    if (target.closest('[data-hideunavailable]')) { prefs.hideUnavailable = !prefs.hideUnavailable; savePrefs(); render(); return; }
    if (target.closest('#menuButton')) { $('menu').classList.toggle('open'); return; }
    if (!target.closest('#menu')) $('menu').classList.remove('open');
    if (!target.closest('.search')) $('searchResults').hidden = true;
  });

  // the item list's name filter redraws as you type, keeping the cursor in the box
  document.addEventListener('input', e => {
    if (e.target.id === 'gItem' || e.target.id === 'gQuest') { goalHits(e.target); return; }
    if (e.target.id !== 'catalogFilter' && e.target.id !== 'craftFilter' && e.target.id !== 'wishFind' && e.target.id !== 'ruleFind' && e.target.id !== 'stashFind' && e.target.id !== 'shopFind') return;
    if (e.target.id === 'craftFilter') craftFilter = e.target.value;
    else if (e.target.id === 'ruleFind') ruleFilter = e.target.value;
    else if (e.target.id === 'wishFind') wishFind = e.target.value;
    else if (e.target.id === 'stashFind') stashFind = e.target.value;
    else if (e.target.id === 'shopFind') shopFind = e.target.value;
    else { catalogFilter = e.target.value; catalogShown = 240; }
    const id = e.target.id;
    render();
    const box = $(id);
    box.focus();
    box.setSelectionRange(box.value.length, box.value.length);
  });

  // the quest lists' map drop-down
  document.addEventListener('change', e => {
    if (e.target.matches?.('[data-rfilter]')) { ruleFilter = e.target.value; render(); return; }
    if (e.target.matches?.('[data-armorwith]')) { armorWith = e.target.value; render(); return; }
    if (e.target.matches?.('[data-wishgroup]')) { wishDo('group', e.target.dataset.wishgroup, +e.target.value); return; }
    if (e.target.id === 'wishGroup') { wishGroup = +e.target.value; return; }
    if (e.target.matches?.('[data-qsort]')) { questSort = e.target.value; render(); return; }
    if (!e.target.matches?.('[data-mapselect]')) return;
    mapFilter = e.target.value;
    render();
  });

  document.documentElement.dataset.theme = 'dark';

  document.querySelectorAll('[data-pref]').forEach(box => {
    box.checked = !!prefs[box.dataset.pref];
    box.addEventListener('change', () => {
      prefs[box.dataset.pref] = box.checked;
      savePrefs();
      writeMarks();
      render();
    });
  });

  // Options › Password: My character, scrolled to its Password card
  // Options > Language: the languages this server has game text for; names, items and quests change together
  getJson('api/langs').then(info => {
    if (!info?.langs || info.langs.length < 2) return;
    $('langPick').innerHTML = info.langs.map(l => `<option value="${esc(l.id)}">${esc(l.name)}</option>`).join('');
    $('langPick').value = info.langs.some(l => l.id === prefs.lang) ? prefs.lang : info.current;
    $('langRow').hidden = false;
  }).catch(() => {});
  $('langPick').addEventListener('change', () => { prefs.lang = $('langPick').value; savePrefs(); location.reload(); });
  $('passwordLink').addEventListener('click', () => { $('menu').classList.remove('open'); if (profile) lockPopup(); });
  $('editsLink').addEventListener('click', () => {
    $('menu').classList.remove('open');
    if (!profile) return;
    if (editsOn()) { editsSet(profile.id, false); render(); } else editsAsk();
  });
  $('logoutLink').addEventListener('click', () => { $('menu').classList.remove('open'); if (profile) lockLogout(); });
  // the logo by keyboard, like a link
  const brand = document.querySelector('.brand');
  brand.addEventListener('click', e => { e.preventDefault(); e.stopPropagation(); $('searchResults').hidden = true; go({ page: 'overview', id: '' }); });
  brand.addEventListener('keydown', e => { if (e.key === 'Enter') brand.click(); });

  $('kindsLink').addEventListener('click', () => { $('menu').classList.remove('open'); go({ page: 'kinds', id: '' }); });
  $('reload').addEventListener('click', async () => {
    $('menu').classList.remove('open');
    await loadGraph(true);
    await loadProfile();
    render();
  });

  $('profile').addEventListener('change', async e => {
    prefs.profile = e.target.value;
    savePrefs();
    await loadProfile();
    render();
  });

  // progress changes while you play: read the profile again when the page comes back into view
  document.addEventListener('visibilitychange', async () => {
    if (document.visibilityState !== 'visible' || !prefs.profile || !data) return;
    await loadProfile();
    render();
  });

  const search = $('search');
  const results = $('searchResults');
  let hits = [];
  let active = -1;

  let itemHits = [];
  let itemTimer = 0;
  // the text the server last answered for: until it has, an empty list means still searching
  let itemsFor = null;

  // quests at once, items from the server a moment after typing stops
  search.addEventListener('input', () => {
    const text = search.value.trim().toLowerCase();
    hits = !text || !data ? [] : findQuests(text).slice(0, 12);
    itemHits = [];
    itemsFor = null;
    active = -1;
    showResults(text);
    clearTimeout(itemTimer);
    if (text.length >= 2) {
      itemTimer = setTimeout(async () => {
        try {
          const found = await getJson('api/items?q=' + encodeURIComponent(text));
          if (search.value.trim().toLowerCase() !== text) return;
          itemHits = found;
          itemsFor = text;
          found.forEach(i => { data.items[i.id] ||= i.name; });
          showResults(text);
        } catch { if (search.value.trim().toLowerCase() === text) { itemsFor = text; showResults(text); } }
      }, 200);
    }
  });

  function showResults(text) {
    const quests = hits.map(q => `<button data-q="${q.id}">${avatar(q.trader)}<span class="n">${esc(q.name)}<small>${esc(traderName(q.trader))}${q.level ? ' · Lv ' + q.level : ''}${q.liveName ? ` · “${esc(q.liveName)}” in live` : ''}</small>${tags(q)}</span></button>`);
    // traders by name, and containers (a duffle bag) to their own page of where they stand
    const found = text ? traders.filter(t => traderName(t).toLowerCase().includes(text)) : [];
    const people = found.map(t => `<button data-page="trader" data-id="${esc(t)}">${avatar(t)}<span class="n">${esc(traderName(t))}<small>Trader</small></span></button>`);
    const boxes = itemHits.filter(i => i.box).map(i => `<button data-box="${esc(i.box)}"><span class="ico sm box-ico"></span><span class="n">${esc(i.name)}<small>Container${i.maps ? ` · on ${i.maps} map${i.maps === 1 ? '' : 's'}` : ''}</small></span></button>`);
    const botList = text ? wiki('bots', 'api/bots') : null;
    const botHits = Array.isArray(botList) ? botList.filter(b => b.name.toLowerCase().includes(text) || b.roles.some(r => r.toLowerCase().includes(text.replace(/\s+/g, '')))
      || (text.length >= 3 && kindName(b).toLowerCase().startsWith(text))).slice(0, 8) : [];
    const botRows = botHits.map(b => `<button data-page="bot" data-id="${esc(b.id)}"><span class="ico sm box-ico"></span><span class="n">${esc(botTitle(b.name))}<small>${esc(kindName(b))}${b.mod ? ' · added by a mod' : ''}${b.maps.length ? ' · ' + esc(b.maps.slice(0, 3).join(', ')) : ''}</small></span></button>`);
    const mapList = text ? wiki('maps', 'api/maps') : null;
    const mapRows = (Array.isArray(mapList) ? oneFactory(mapList).filter(m => m.name.toLowerCase().includes(text)) : []).map(m => `<button data-page="map" data-id="${esc(m.id)}"><img class="map-hit" src="${mapBanner(m.id)}" alt="" onerror="this.remove()"><span class="n">${esc(m.name)}<small>Map</small></span></button>`);
    const items = itemHits.filter(i => !i.box).map(i => `<button data-item="${i.id}">${icon(i.id, 'sm')}<span class="n">${esc(i.name)}<small>${esc(i.category || 'Item')}${i.variant ? ' · ' + esc(i.variant) : ''}</small></span></button>`);
    const listRows = text.length >= 3 ? listHits(text).map(l => `<button data-page="items" data-id="${esc(l.id)}"><span class="ico sm box-ico"></span><span class="n">${esc(l.name)}<small>Item list</small></span></button>`) : [];
    results.innerHTML = (listRows.length ? `<div class="group">Lists</div>${listRows.join('')}` : '')
      + (mapRows.length ? `<div class="group">Maps</div>${mapRows.join('')}` : '')
      + (people.length ? `<div class="group">Traders</div>${people.join('')}` : '')
      + (botRows.length ? `<div class="group">Bots</div>${botRows.join('')}` : '')
      + (boxes.length ? `<div class="group">Containers</div>${boxes.join('')}` : '')
      + (quests.length ? `<div class="group">Quests</div>${quests.join('')}` : '')
      + (items.length ? `<div class="group">Items</div>${items.join('')}` : '')
      || (text.length >= 2 && itemsFor !== text ? '<div class="empty">Searching…</div>' : '<div class="empty">No results found</div>');
    results.hidden = !text;
  }

  // Item lists whose name fits the search: the handbook's categories, and the server's item-tree groups (plates).
  // Every word typed must start a word of the name; a plural matches its singular ("plates", "Armor Plate").
  function listHits(text) {
    const cat = wiki('catalog', 'api/catalog');
    const groups = wiki('catgroups', 'api/catalog/groups');
    const stem = w => w.length > 3 ? w.replace(/s$/, '') : w;
    const words = s => s.toLowerCase().split(/[^\p{L}\p{N}]+/u).filter(Boolean).map(stem);
    const want = words(text);
    const fits = name => { const have = words(name); return want.length && want.every(w => have.some(h => h.startsWith(w))); };
    const out = [];
    if (cat?.categories) {
      const counts = new Map();
      const parents = new Map(cat.categories.map(c => [c.id, c.parent]));
      for (const i of cat.items) if (!i.fixedPart && !i.unused) for (let at = i.category; at; at = parents.get(at)) counts.set(at, (counts.get(at) || 0) + 1);
      for (const c of cat.categories) if (counts.get(c.id) && fits(c.name)) out.push({ id: c.id, name: c.name, count: counts.get(c.id) });
    }
    if (Array.isArray(groups)) for (const g of groups) if (fits(g.name)) out.push(g);
    return out.slice(0, 6);
  }
  // the lists search looks through, asked for as soon as the search box is used
  search.addEventListener('focus', () => { wiki('catalog', 'api/catalog'); wiki('catgroups', 'api/catalog/groups'); });

  // a press anywhere outside the search closes its results, before any other click handling can stop it
  document.addEventListener('pointerdown', e => {
    if (!e.target.closest?.('.search')) results.hidden = true;
  }, true);
  document.addEventListener('focusin', e => {
    if (!e.target.closest?.('.search')) results.hidden = true;
  });

  // clicking back into the box brings the last results back
  search.addEventListener('click', () => {
    if (!search.value.trim() || !results.hidden) return;
    if (results.innerHTML) results.hidden = false;
    else search.dispatchEvent(new Event('input'));
  });

  search.addEventListener('keydown', e => {
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      active = Math.max(0, Math.min(results.querySelectorAll('button').length - 1, active + (e.key === 'ArrowDown' ? 1 : -1)));
      [...results.querySelectorAll('button')].forEach((b, i) => b.classList.toggle('active', i === active));
    } else if (e.key === 'Enter' && results.querySelector('button')) {
      results.hidden = true;
      const pick = results.querySelectorAll('button')[Math.max(0, active)];
      if (pick?.dataset.q) go({ page: 'quest', id: pick.dataset.q });
      else if (pick?.dataset.page) go({ page: pick.dataset.page, id: pick.dataset.id });
      else if (pick?.dataset.box) go({ page: 'box', id: pick.dataset.box });
      else if (pick?.dataset.item) go({ page: 'item', id: pick.dataset.item });
    } else if (e.key === 'Escape') {
      results.hidden = true;
    }
  });

  document.addEventListener('keydown', e => {
    if (e.key === '/' && document.activeElement !== search) { e.preventDefault(); search.focus(); }
  });

  function footer() {
    const built = new Date(data.builtAt).toLocaleTimeString();
    $('footer').innerHTML = `<span>Read from this server's quest data at ${built}, so quest mods are included.</span>
      <span>${data.keyEntries} key entries (tarkov.dev and keys.json). Item pictures from tarkov.dev.</span>
      <a id="footReload">Reload quest data</a>
      <span class="credit">Created by Fums</span>`;
    $('footReload').onclick = () => $('reload').click();
  }

  (async () => {
    writeMarks();
    await Promise.all([loadGraph(false), loadProfiles()]);
    if (!data) return;
    readHash();
    await loadProfile();
    render();
  })();
})();
