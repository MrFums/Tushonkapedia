using System.Text.Json;
using System.Text.Json.Nodes;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;

namespace Tushonkapedia.Server;

// What players call each kind of bot, all from the server it runs on:
//   1. the locale's own name for the role, as kill objectives use it ("Reshala", "Cultist Priest");
//   2. the name the server's quests use when one kill objective asks for just that role: the words its objectives
//      share, less the wording other kill objectives share ("Locate and neutralize Kaban" gives "Kaban");
//   3. the locale's role label ("Raider", "Rogue"); the regular Scav, which every map's waves spawn most, by the
//      locale's name for Scavs as a kill target;
//   4. a boss's escort, as that boss's guards (from the maps' boss spawns);
//   5. the role's own name split into words, each word as the locale spells it ("pmcUSEC" gives "PMC USEC").
public static class BotRoles
{
    private static Func<IEnumerable<Quest>>? _quests;
    private static Func<IEnumerable<Location?>>? _locations;
    private static Func<IReadOnlyDictionary<string, BotType?>>? _types;
    // learned once per language: the server may hand out a fresh copy of a locale each time, so it is told apart by
    // its size and a few of its own labels rather than by the copy
    private static readonly Dictionary<string, Learned> Cache = [];

    private static Learned For(IReadOnlyDictionary<string, string> locale)
    {
        var key = $"{locale.Count}|{locale.GetValueOrDefault("Pmc")}|{locale.GetValueOrDefault("ScavRole/Boss")}|{locale.GetValueOrDefault("Scav")}";
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out var learned))
            {
                Cache[key] = learned = Learn(locale);
            }

            return learned;
        }
    }

    private sealed class Learned
    {
        public readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> Rank = new(StringComparer.OrdinalIgnoreCase);
        // each role as the game spells it ("crazyAssaultEvent"); the bot table's keys are all lower case
        public readonly Dictionary<string, string> Casing = new(StringComparer.OrdinalIgnoreCase);
        // roles that lead spawns of their own on the maps
        public readonly HashSet<string> Bosses = new(StringComparer.OrdinalIgnoreCase);
    }

    // where the names come from; called before any page is built
    public static void Use(Func<IEnumerable<Quest>> quests, Func<IEnumerable<Location?>> locations, Func<IReadOnlyDictionary<string, BotType?>> types)
    {
        _quests ??= quests;
        _locations ??= locations;
        _types ??= types;
    }

    // bots that don't fight you: the server's own bot settings make them neutral to both kinds of PMC
    public static bool IsEnemy(string role) => !Neutral().Contains(role);

    private static HashSet<string>? _neutral;

    private static HashSet<string> Neutral()
    {
        if (_neutral != null || _types == null)
        {
            return _neutral ?? [];
        }

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (role, type) in _types())
        {
            try
            {
                if (type?.BotDifficulty is not { } diff || JsonSerializer.SerializeToNode(diff, diff.GetType()) is not JsonObject all)
                {
                    continue;
                }

                // every difficulty's mind: neutral to both sides in all of them
                var minds = all.Select(d => d.Value?["Mind"] ?? d.Value?["mind"]).Where(m => m != null).ToList();
                if (minds.Count > 0 && minds.All(m => Neutralish(m!["DEFAULT_USEC_BEHAVIOUR"]) && Neutralish(m!["DEFAULT_BEAR_BEHAVIOUR"])))
                {
                    set.Add(role);
                }
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
                // a bot type SPT's types can't write as JSON: counted as an enemy
            }
        }

        return _neutral = set;

        static bool Neutralish(JsonNode? v) => v?.ToString() is { } s && s.Equals("Neutral", StringComparison.OrdinalIgnoreCase);
    }

    public static string Name(string role, IReadOnlyDictionary<string, string> locale)
    {
        var learned = For(locale);
        lock (learned)
        {
            if (!learned.Names.TryGetValue(role, out var name))
            {
                learned.Names[role] = name = Fallback(role, learned, locale);
            }

            return name;
        }
    }

    // the order bots are listed in: the ones the maps' waves spawn most first, then the rest by name
    public static int Order(string name, IReadOnlyDictionary<string, string> locale) =>
        For(locale) is var learned && learned.Rank.TryGetValue(name, out var rank) ? rank : int.MaxValue;

    private static Learned Learn(IReadOnlyDictionary<string, string> locale)
    {
        var learned = new Learned();
        var names = learned.Names;

        // 1. the locale's name for the role as a kill target
        const string killRole = "QuestCondition/Elimination/Kill/BotRole/";
        foreach (var (key, value) in locale)
        {
            if (key.StartsWith(killRole, StringComparison.Ordinal) && key.Length > killRole.Length && !string.IsNullOrWhiteSpace(value))
            {
                names.TryAdd(key[killRole.Length..], value.Trim());
            }
        }

        // the roles the locale names outright: nothing below renames them
        var fromLocale = new HashSet<string>(names.Keys, StringComparer.OrdinalIgnoreCase);

        // 2. the quests' own words for it
        var fromQuests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (role, name) in FromQuests(locale))
        {
            if (names.TryAdd(role, name))
            {
                fromQuests.Add(role);
            }
        }

        // 2b. the one name the bot file gives every bot of the role, in the locale's own letters
        var pooled = FromNamePools(locale, names, learned);
        foreach (var (role, name) in pooled)
        {
            names.TryAdd(role, name);
        }

        // 3. the locale's role labels; the waves' most common role as the locale's Scavs
        const string scavRole = "ScavRole/";
        foreach (var (key, value) in locale)
        {
            if (key.StartsWith(scavRole, StringComparison.Ordinal) && key.Length > scavRole.Length && value is { Length: > 0 } && value.Any(char.IsLetter))
            {
                names.TryAdd(key[scavRole.Length..], value.Trim());
            }
        }

        var waves = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var pairs = new Dictionary<(string escort, string boss), int>();
        var bosses = learned.Bosses;
        var casing = learned.Casing;
        foreach (var value in Enum.GetNames<WildSpawnType>())
        {
            casing.TryAdd(value, value);
        }
        foreach (var location in _locations?.Invoke() ?? [])
        {
            foreach (var wave in location?.Base?.Waves ?? [])
            {
                if (wave?.WildSpawnType?.ToString() is { Length: > 0 } w)
                {
                    waves[w] = waves.GetValueOrDefault(w) + 1;
                    if (w.Any(char.IsUpper))
                    {
                        casing.TryAdd(w, w);
                    }
                }
            }

            foreach (var spawn in location?.Base?.BossLocationSpawn ?? [])
            {
                if (string.IsNullOrEmpty(spawn?.BossName))
                {
                    continue;
                }

                bosses.Add(spawn.BossName);
                // each escort with how many come: a spawn may name an escort type and send none of it
                foreach (var (escort, amount) in new[] { (spawn.BossName, "1"), (spawn.BossEscortType, spawn.BossEscortAmount?.ToString()) }
                             .Concat((spawn.Supports ?? []).Select(s => (s?.BossEscortType, s?.BossEscortAmount?.ToString()))))
                {
                    if (!string.IsNullOrEmpty(escort) && escort.Any(char.IsUpper))
                    {
                        casing.TryAdd(escort, escort);
                    }

                    var some = (amount ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Any(a => double.TryParse(a, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0);
                    if (some && !string.IsNullOrEmpty(escort) && !escort.Equals(spawn.BossName, StringComparison.OrdinalIgnoreCase))
                    {
                        var key = (escort.ToLowerInvariant(), spawn.BossName);
                        pairs[key] = pairs.GetValueOrDefault(key) + 1;
                    }
                }
            }
        }

        if (waves.Count > 0 && Text(locale, "QuestCondition/Elimination/Kill/Target/Savage") is { } scavs)
        {
            names.TryAdd(waves.MaxBy(w => w.Value).Key, scavs);
        }

        // 4. a boss's escort: the boss it comes with most often. A role that leads spawns of its own isn't anyone's guard.
        // The quests' words for a guard ("Reshala's") give way to this, so every boss's guards read the same.
        foreach (var group in pairs.GroupBy(p => p.Key.escort).Where(g => !bosses.Contains(g.Key)))
        {
            var boss = group.MaxBy(p => p.Value).Key.boss;
            // a guard with a name of its own (the locale's, or the one its bot file gives it: "Birdeye") keeps it
            if (!names.ContainsKey(group.Key) || fromQuests.Contains(group.Key) && !fromLocale.Contains(group.Key) && !pooled.ContainsKey(group.Key))
            {
                names[group.Key] = $"{names.GetValueOrDefault(boss) ?? Fallback(boss, learned, locale)}'s guards";
            }
        }

        // the order: wave roles by how often the maps spawn them, then the rest
        var rank = 0;
        foreach (var (role, _) in waves.OrderByDescending(w => w.Value))
        {
            learned.Rank.TryAdd(names.GetValueOrDefault(role) ?? Fallback(role, learned, locale), rank++);
        }

        return learned;
    }

    // A bot file that gives every bot of its role the same name ("Vengeful" "Killa"), written in the same letters as
    // the locale's own role names (a Russian name pool doesn't name a bot on an English page). A name that ends on a
    // small joining word ("Shadow" "of") is finished with the name of the role it's a version of (bossTagilla for
    // bossTagillaAgro).
    private static Dictionary<string, string> FromNamePools(IReadOnlyDictionary<string, string> locale, Dictionary<string, string> names, Learned learned)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var types = _types?.Invoke();
        if (types == null)
        {
            return found;
        }

        var sample = locale.GetValueOrDefault("ScavRole/Boss") ?? locale.GetValueOrDefault("Scav") ?? "";
        bool SameLetters(string text) => text.Where(char.IsLetter).All(c => sample.Where(char.IsLetter).Any(l => SameScript(l, c)));
        foreach (var (role, type) in types)
        {
            var first = type?.FirstNames?.ToList() ?? [];
            var last = type?.LastNames?.ToList() ?? [];
            if (first.Count != 1 || last.Count > 1 || string.IsNullOrWhiteSpace(first[0]) || !first[0].Any(char.IsLetter))
            {
                continue;
            }

            var name = string.Join(' ', new[] { first[0] }.Concat(last).Select(w => w.Trim()).Where(w => w.Length > 0));
            if (!SameLetters(name))
            {
                continue;
            }

            var words = name.Split(' ');
            if (words.Length > 1 && words[^1].All(char.IsLower))
            {
                var parent = types.Keys.Where(k => !k.Equals(role, StringComparison.OrdinalIgnoreCase) && role.StartsWith(k, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(k => k.Length).FirstOrDefault();
                if (parent == null)
                {
                    continue;
                }

                name += " " + (names.GetValueOrDefault(parent) ?? Fallback(parent, learned, locale));
            }

            found[role] = name;
        }

        return found;

        static bool SameScript(char a, char b) => (a < 0x250) == (b < 0x250) && (a < 0x250 || Math.Abs(a - b) < 0x100);
    }

    private static string? Text(IReadOnlyDictionary<string, string> locale, string key) =>
        locale.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    // Each kill objective that asks for exactly one role names it in its text. The name is the words all that role's
    // objectives share, less the run of words at the start and the end that other roles' objectives share too.
    private static Dictionary<string, string> FromQuests(IReadOnlyDictionary<string, string> locale)
    {
        var groups = new Dictionary<string, List<string[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (var quest in _quests?.Invoke() ?? [])
        {
            foreach (var condition in quest?.Conditions?.AvailableForFinish ?? [])
            {
                var roles = (condition?.Counter?.Conditions ?? [])
                    .Where(c => c?.ConditionType == "Kills" && c.SavageRole is { Count: > 0 })
                    .SelectMany(c => c!.SavageRole!).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (roles.Count == 0 || Text(locale, condition!.Id.ToString()) is not { } text)
                {
                    continue;
                }

                var key = string.Join(",", roles.OrderBy(r => r, StringComparer.OrdinalIgnoreCase));
                if (!groups.TryGetValue(key, out var list))
                {
                    groups[key] = list = [];
                }

                // numbers ("Eliminate 5 Scavs") aren't part of the name
                list.Add(text.TrimEnd('.', '!').Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !w.All(char.IsDigit)).ToArray());
            }
        }

        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, texts) in groups)
        {
            if (key.Contains(','))
            {
                continue;
            }

            // first the words every one of the role's objectives shares, less the shared start and end
            if (Trimmed(texts[0].Where(w => texts.All(t => t.Contains(w))).ToArray(), key, groups) is { } all)
            {
                found[key] = all;
                continue;
            }

            // else each objective's own candidate: its words less the run at the start and the end other roles'
            // objectives share; the role's name is the candidate most of its objectives give
            var counts = new Dictionary<string, int>();
            foreach (var words in texts)
            {
                int lead = 0, tail = 0;
                foreach (var (other, otherTexts) in Others(groups, key))
                {
                    foreach (var t in otherTexts)
                    {
                        var i = 0;
                        while (i < words.Length && i < t.Length && words[i] == t[i])
                        {
                            i++;
                        }

                        lead = Math.Max(lead, i);
                    }
                }

                foreach (var (other, otherTexts) in Others(groups, key))
                {
                    foreach (var t in otherTexts)
                    {
                        var j = 0;
                        while (j < words.Length - lead - 1 && j < t.Length && words[^(j + 1)] == t[^(j + 1)])
                        {
                            j++;
                        }

                        tail = Math.Max(tail, j);
                    }
                }

                if (lead >= words.Length)
                {
                    continue;
                }

                var name = words[lead..^tail];
                // a name, not a phrase: a few words, starting with a capital, nothing in brackets or quotes
                if (name.Length is > 0 and <= 3 && char.IsUpper(name[0][0]) && name.All(w => w.All(c => char.IsLetter(c) || c is '-' or '\'')))
                {
                    var n = string.Join(' ', name);
                    counts[n] = counts.GetValueOrDefault(n) + 1;
                }
            }

            if (counts.Count > 0)
            {
                found[key] = counts.MaxBy(c => c.Value).Key;
            }
        }

        return found;
    }

    // objectives that ask for other bots: a group that takes this role too names it the same way, so it doesn't count
    private static IEnumerable<KeyValuePair<string, List<string[]>>> Others(Dictionary<string, List<string[]>> groups, string key) =>
        groups.Where(g => !g.Key.Split(',').Contains(key, StringComparer.OrdinalIgnoreCase));

    private static string? Trimmed(string[] words, string key, Dictionary<string, List<string[]>> groups)
    {
        int lead = 0, tail = 0;
        foreach (var (other, otherTexts) in Others(groups, key))
        {
            foreach (var t in otherTexts)
            {
                var i = 0;
                while (i < words.Length && i < t.Length && words[i] == t[i])
                {
                    i++;
                }

                lead = Math.Max(lead, i);
            }
        }

        foreach (var (other, otherTexts) in Others(groups, key))
        {
            foreach (var t in otherTexts)
            {
                var j = 0;
                while (j < words.Length - lead - 1 && j < t.Length && words[^(j + 1)] == t[^(j + 1)])
                {
                    j++;
                }

                tail = Math.Max(tail, j);
            }
        }

        if (lead >= words.Length)
        {
            return null;
        }

        var name = words[lead..^tail];
        // a name, not a phrase: a few words, starting with a capital, nothing in brackets or quotes
        return name.Length is > 0 and <= 3 && char.IsUpper(name[0][0]) && name.All(w => w.All(c => char.IsLetter(c) || c is '-' or (char)39))
            ? string.Join(' ', name) : null;
    }

    // "blackDivAssault" as "Black Div Assault"; "bossWedge" as "Wedge". Each word as the locale spells it, when it
    // has the word on its own ("pmcUSEC" gives "PMC USEC").
    private static string Words(string role, IReadOnlyDictionary<string, string> locale)
    {
        var bare = System.Text.RegularExpressions.Regex.Replace(role, "^(boss|follower)(?=[A-Z])", "");
        var words = System.Text.RegularExpressions.Regex.Split(bare, "(?<=[a-z])(?=[A-Z])").Where(w => w.Length > 0)
            .Select(w => locale.FirstOrDefault(kv => kv.Key.Equals(w, StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } v
                         && v.Equals(w, StringComparison.OrdinalIgnoreCase) ? v : w)
            .ToList();
        var spaced = string.Join(' ', words);
        return spaced.Length > 0 ? char.ToUpperInvariant(spaced[0]) + spaced[1..] : role;
    }

    // 5. a role nothing names. Spelt all in lower case ("blackdivlead"), it is split by the words other roles are
    // spelt with ("blackDivAssault"). A role that carries on a boss's name ("bossBoarSniper" after "bossBoar") takes
    // that boss's name for its first part ("Kaban Sniper"). Otherwise its own words.
    private static string Fallback(string role, Learned learned, IReadOnlyDictionary<string, string> locale)
    {
        var cased = learned.Casing.GetValueOrDefault(role) ?? Split(role, learned);
        var bare = Bare(cased);
        var lead = learned.Bosses
            .Where(b => !b.Equals(role, StringComparison.OrdinalIgnoreCase) && learned.Names.ContainsKey(b))
            .Select(b => (Boss: b, Bare: Bare(learned.Casing.GetValueOrDefault(b) ?? b)))
            .Where(b => b.Bare.Length > 0 && bare.Length > b.Bare.Length && char.IsUpper(bare[b.Bare.Length])
                        && bare.StartsWith(b.Bare, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(b => b.Bare.Length)
            .FirstOrDefault();
        return lead.Boss != null ? $"{learned.Names[lead.Boss]} {Words(bare[lead.Bare.Length..], locale)}" : Words(cased, locale);

        static string Bare(string r) => System.Text.RegularExpressions.Regex.Replace(r, "^(boss|follower)(?=[A-Z])", "");
    }

    // a role in lower case, cut into the longest words other roles are spelt with; the rest stays one word
    private static string Split(string role, Learned learned)
    {
        if (role.Any(char.IsUpper))
        {
            return role;
        }

        var known = learned.Casing.Values
            .SelectMany(c => System.Text.RegularExpressions.Regex.Split(c, "(?<=[a-z])(?=[A-Z])"))
            .Where(w => w.Length >= 3).Select(w => w.ToLowerInvariant()).ToHashSet();
        var parts = new List<string>();
        var at = 0;
        while (at < role.Length)
        {
            var word = known.Where(w => role.AsSpan(at).StartsWith(w)).MaxBy(w => w.Length);
            if (word == null)
            {
                break;
            }

            parts.Add(word);
            at += word.Length;
        }

        if (parts.Count == 0)
        {
            return role;
        }

        if (at < role.Length)
        {
            parts.Add(role[at..]);
        }

        return string.Concat(parts.Select((p, i) => i == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));
    }

    // kept for callers that only want a role's spaced name
    public static string Pretty(string role) => Words(role, new Dictionary<string, string>());
}
