using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;

namespace Tushonkapedia.Server;

// The page's data, worked out from the quest table the server has in memory (after every mod has changed it).
// Nothing here writes back to the quests: every value is copied out.
public sealed class QuestGraph
{
    // SPT's own names for the quests that end the Kappa and Lightkeeper lines, and for the Lightkeeper
    public static readonly string Collector = QuestTpl.COLLECTOR.ToString();
    public static readonly string KnockKnock = QuestTpl.KNOCK_KNOCK.ToString();
    public static readonly string LightkeeperTrader = SPTarkov.Server.Core.Models.Enums.Traders.LIGHTHOUSEKEEPER.ToString();

    // same values as Quest Glow's flags
    public const int Kappa = 1;
    public const int Lightkeeper = 2;
    public const int Lockout = 4;

    public string Version { get; init; } = "";
    public string Lang { get; init; } = "en";
    public DateTime BuiltAt { get; init; }
    public Dictionary<string, TraderOut> Traders { get; init; } = [];
    public List<string> Maps { get; init; } = [];
    public List<QuestNode> Quests { get; init; } = [];
    public List<QuestEdge> Edges { get; init; } = [];
    // item id -> name, for every item the quests mention
    public Dictionary<string, string> Items { get; init; } = [];
    public int KeyEntries { get; set; }
    // the server's money: item id -> short name, each currency tag ("USD") -> its item, and the base currency
    public Dictionary<string, string> Currencies { get; set; } = [];
    public Dictionary<string, string> CurrencyTags { get; set; } = [];
    public string BaseCurrency { get; set; } = "";
    // the game's own words for body zones, body parts and injuries, by their keys ("ParietalHead", "Chest", "Fracture")
    public Dictionary<string, string> Words { get; set; } = [];
    // the quest and trader ids above, so the page needs none of its own
    public string CollectorQuest => Collector;
    public string KnockKnockQuest => KnockKnock;
    public string LightkeeperId => LightkeeperTrader;
    public string FenceId => SPTarkov.Server.Core.Models.Enums.Traders.FENCE.ToString();
    public string TherapistId => SPTarkov.Server.Core.Models.Enums.Traders.THERAPIST.ToString();

    internal Dictionary<string, QuestNode> ById { get; } = [];

    // Everything a quest needs before it can be started, all the way back. A link that only accepts the
    // earlier quest failed is met by finishing a rival that fails it, so those rivals are on the way too.
    // Quests can link both ways, so each quest is visited once.
    public HashSet<string> Chain(string root)
    {
        var seen = new HashSet<string>();
        var todo = new Stack<string>();
        todo.Push(root);

        while (todo.Count > 0)
        {
            var id = todo.Pop();
            if (!ById.TryGetValue(id, out var quest) || !seen.Add(id))
            {
                continue;
            }

            foreach (var link in quest.Start.Where(c => c.Kind == "Quest"))
            {
                foreach (var target in link.Quests)
                {
                    todo.Push(target);
                    if (link.Status.Contains("Fail") && ById.TryGetValue(target, out var linked))
                    {
                        foreach (var rival in linked.FailsWhen.Where(c => c.Kind == "Quest").SelectMany(c => c.Quests))
                        {
                            todo.Push(rival);
                        }
                    }
                }
            }
        }

        return seen;
    }

    // The chain as an indented tree, for the verify script. A quest already printed is shown once more by
    // name only, so shared prerequisites don't repeat their whole tree.
    public string ChainText(string root)
    {
        var text = new StringBuilder();
        if (!ById.TryGetValue(root, out var quest))
        {
            return $"No quest {root} on this server.\n";
        }

        text.AppendLine($"{quest.Name} [{quest.Id}]  giver: {TraderName(quest.Trader)}");
        var quests = quest.Start.Count(c => c.Kind == "Quest");
        var others = quest.Start.Where(c => c.Kind != "Quest").GroupBy(c => c.Kind).Select(g => $"{g.Count()} {g.Key}");
        text.AppendLine($"AvailableForStart: {quest.Start.Count} conditions ({string.Join(", ", others.Prepend($"{quests} Quest"))})");
        foreach (var condition in quest.Start)
        {
            text.AppendLine($"  - {condition.Kind}: {condition.Text}");
        }

        foreach (var need in quest.Needs)
        {
            text.AppendLine($"  needs to finish ({need.Kind}): {need.Text}");
        }

        var chain = Chain(root);
        text.AppendLine($"Prerequisite chain: {chain.Count - 1} quests");
        Tree(root, "", "", [], [], text);
        return text.ToString();
    }

    private string TraderName(string id) => Traders.TryGetValue(id, out var trader) ? trader.Name : id;

    private void Tree(string id, string indent, string how, HashSet<string> printed, HashSet<string> path, StringBuilder text)
    {
        var name = ById.TryGetValue(id, out var quest) ? quest.Name : "(missing quest)";
        var label = $"{indent}{how}{name} [{id}]";
        if (path.Contains(id))
        {
            text.AppendLine(label + "  (loop, stops here)");
            return;
        }

        if (quest == null || !printed.Add(id))
        {
            text.AppendLine(quest == null ? label : label + "  (see above)");
            return;
        }

        var needs = quest.Start.Where(c => c.Kind != "Quest").Select(c => c.Text).ToList();
        text.AppendLine(needs.Count > 0 ? $"{label}  needs: {string.Join("; ", needs)}" : label);

        path.Add(id);
        foreach (var link in quest.Start.Where(c => c.Kind == "Quest"))
        {
            foreach (var target in link.Quests)
            {
                Tree(target, indent + "    ", $"<- {string.Join("/", link.Status.Select(StatusWord))}: ", printed, path, text);
                if (link.Status.Contains("Fail") && ById.TryGetValue(target, out var linked))
                {
                    foreach (var rival in linked.FailsWhen.Where(c => c.Kind == "Quest").SelectMany(c => c.Quests))
                    {
                        Tree(rival, indent + "        ", "<- rival, finishing it fails the above: ", printed, path, text);
                    }
                }
            }
        }

        path.Remove(id);
    }

    public static string StatusWord(string status)
    {
        return status switch
        {
            "Success" => "finished",
            "Started" => "started",
            "Fail" => "failed",
            "AvailableForFinish" => "ready to hand in",
            _ => status
        };
    }
}

public sealed class TraderOut
{
    public string Name { get; init; } = "";
    public string? Avatar { get; init; }
    public bool UnlockedByDefault { get; init; } = true;
    // quests whose reward unlocks this trader
    public List<string> UnlockedBy { get; init; } = [];
    // how many loyalty levels the trader has (the BTR driver has one)
    public int Levels { get; init; } = 4;
}

public sealed class QuestNode
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Trader { get; init; } = "";
    public List<string> Maps { get; init; } = [];
    public int? Level { get; set; }
    public string? Side { get; init; }
    public string? Type { get; init; }
    public bool Restartable { get; init; }
    // the quest's picture, served by SPT itself
    public string? Image { get; init; }
    // the trader's briefing
    public string? Description { get; init; }
    // Kappa 1, Lightkeeper 2, lock-out 4, as in Quest Glow
    public int Flags { get; set; }
    public string? Wiki { get; init; }
    // what the live game (Tarkov 1.0, the wiki) calls this quest, when that's a different name: BSG renamed and
    // renumbered many quests after SPT 4.1's data, so the wiki page can look like a different quest
    public string? LiveName { get; init; }
    // added by a mod: the mod's folder name, or "" when which one isn't known; null for SPT's own quests
    public string? Mod { get; set; }
    // how much the rewards are worth having, 0 to 1, and the rewards worth pointing out
    public double RewardScore { get; init; }
    public List<string> Highlights { get; init; } = [];
    // the item each highlight is about (same order), for its picture; null for a trader, stash rows ...
    public List<string?> HighlightItems { get; init; } = [];
    // the trader value of the items given (roubles)
    public double RewardValue { get; init; }
    // longest run of prerequisites before this quest
    public int Depth { get; set; }
    // SPT doesn't offer it right now: an event quest outside its event (the event's name)
    public string? Hidden { get; set; }
    // only offered to this side ("bear" or "usec"), or only to / never to these game editions
    public string? OnlySide { get; set; }
    public List<string>? Editions { get; set; }
    public List<string>? NotEditions { get; set; }
    public List<ConditionInfo> Start { get; init; } = [];
    public List<ConditionInfo> FailsWhen { get; init; } = [];
    public List<ObjectiveInfo> Objectives { get; init; } = [];
    // a mod changed this quest from SPT's own version of it
    public bool Changed { get; set; }
    public List<NeedInfo> Needs { get; init; } = [];
    public List<RewardInfo> Rewards { get; init; } = [];
}

// One condition in AvailableForStart or Fail. Kind is the server's conditionType, so condition types this mod
// doesn't know about are still shown with a generic text.
public sealed class ConditionInfo
{
    public string Kind { get; init; } = "";
    public string Text { get; init; } = "";
    public List<string> Quests { get; init; } = [];
    public List<string> Status { get; init; } = [];
    public string? Trader { get; init; }
    public string? Compare { get; init; }
    public double? Value { get; init; }
    public int? AvailableAfter { get; init; }
}

public sealed class ObjectiveInfo
{
    public List<string> ZoneMaps { get; init; } = [];
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Text { get; init; } = "";
    public List<string> Maps { get; init; } = [];
    // item ids to find, hand over or place, with the count
    public List<string> Items { get; init; } = [];
    public double? Count { get; init; }
    public bool Fir { get; init; }
    // a quest item that lies at a set spot in raid: the page can show where
    public bool QuestItem { get; init; }
    // the places in raid it asks for (go to, stand in, place an item at, kill inside): their ids in the map scenes,
    // so the page can show where they are
    public List<string> Zones { get; init; } = [];
    // what the objective's own text leaves out, spelled out from the condition: the body part, the weapon, the gear,
    // where the bots spawn. Nothing it already says (the map, the count, the item, extracting).
    public List<string> Steps { get; init; } = [];
    // 1, 2 ... when this is one of several objectives where any one will do; 0 when it's required on its own.
    // SPT's quest data has no such groups, so the server leaves it 0: every objective shows as required.
    public int Any { get; set; }
    // how many objectives are in that group
    public int AnyOf { get; set; }
    // not known to be a group, but it looks like one (a mod quest listing parallel objectives of the same kind)
    public bool MaybeAny { get; set; }
}

// An item the player needs to finish the quest. Kind: handover, find, place, build, sell, wear, weapon,
// mods, caliber, given (handed over when the quest starts) or key (from keys.json, not from the quest data).
public sealed class NeedInfo
{
    public string Kind { get; init; } = "";
    public string Text { get; init; } = "";
    public List<string> Items { get; init; } = [];
    // item ids, when the need is one item (any of them will do)
    public List<string> Ids { get; init; } = [];
    public double? Count { get; init; }
    public bool Fir { get; init; }
    public string? Note { get; init; }
    // where it came from when not the quest data: "tarkov.dev", "keys.json" or "map" (a lock near the objective)
    public string? Source { get; init; }
    // a listed key: whether this server's map has one of its locks near the objective; null when there's no place to check
    public bool? OnMap { get; init; }
    public string? Objective { get; set; }
    // when the objective it comes from is one of a group where any one will do: the group's number and size
    public int Any { get; set; }
    public int AnyOf { get; set; }
}

public sealed class RewardInfo
{
    public string When { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Text { get; init; } = "";
    // the trader a TraderUnlock reward opens, a TraderStanding reward raises, or an AssortmentUnlock sells at
    public string? Trader { get; init; }
    // the standing, XP or skill points given
    public double? Value { get; init; }
    // the item given or unlocked, with the stack size
    public string? Item { get; init; }
    public double? Count { get; init; }
}

// Kind "requires": the earlier quest opens the later one. Kind "rival": the later quest needs the earlier one
// failed, so it's no prerequisite. Kind "fails": finishing the earlier quest fails the later one.
public sealed class QuestEdge
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string Kind { get; init; } = "";
    public List<string> Status { get; init; } = [];
}

// keys.json: quest id -> keys you need to get to its objectives. The quest data has no such list, so these are
// filled in by hand or by tools\update-keys.ps1.
public sealed class KeyEntry
{
    public List<string> Keys { get; init; } = [];
    public string? Map { get; init; }
    public string? Note { get; init; }
    // the objective (condition id) the key is for, when known
    public string? Objective { get; init; }
}

public sealed record GraphSources(
    IReadOnlyDictionary<string, string> Locale,
    IReadOnlyDictionary<string, string> MapIds,
    IReadOnlyDictionary<string, (string? Avatar, bool UnlockedByDefault, int Levels)> Traders,
    IReadOnlyDictionary<string, List<KeyEntry>> Keys,
    IReadOnlyDictionary<string, List<KeyEntry>> UserKeys,
    IReadOnlyDictionary<string, string> Wiki,
    Func<string, bool> IsQuestItem,
    Func<string, double> ItemPrice,
    Func<string, int> CaseSpace,
    // where a kind of bot spawns: map name and spawn chance (percent), for bosses, cultists, raiders and rogues
    Func<string, List<(string Map, double Chance)>> BotSpawns,
    // the keys whose locks are close to these quest places, nearest first, with how many metres away
    Func<IReadOnlyList<string>, List<(string Key, double Metres)>> LocksNear,
    // whether a quest's places differ from SPT's own version of it
    Func<string, IEnumerable<string>, bool> QuestMoved,
    // the maps these quest places are on, by the server's map ids
    Func<IEnumerable<string>, List<string>> ZoneMaps);

public static partial class QuestGraphBuilder
{
    private const string AnyMap = "Any";
    // locks near one objective worth naming
    private const int MostLocksNear = 3;
    // a "use one of these" list longer than this is cut short in the text (all names stay in Items)
    private const int ListedWeapons = 6;

    public static QuestGraph Build(IEnumerable<Quest> source, GraphSources sources, string lang, string version)
    {
        var quests = source.Where(q => q != null).ToList();
        var names = new Names(sources.Locale, sources.MapIds, quests);
        var graph = new QuestGraph { Lang = lang, Version = version, BuiltAt = DateTime.UtcNow };

        foreach (var quest in quests)
        {
            var node = Node(quest, names, sources);
            graph.Quests.Add(node);
            graph.ById[node.Id] = node;
        }

        foreach (var node in graph.Quests)
        {
            foreach (var link in node.Start.Where(c => c.Kind == "Quest"))
            {
                var rival = link.Status.Count > 0 && link.Status.All(s => s == "Fail");
                foreach (var target in link.Quests.Where(graph.ById.ContainsKey))
                {
                    graph.Edges.Add(new QuestEdge { From = target, To = node.Id, Kind = rival ? "rival" : "requires", Status = link.Status });
                }
            }

            foreach (var link in node.FailsWhen.Where(c => c.Kind == "Quest"))
            {
                foreach (var target in link.Quests.Where(graph.ById.ContainsKey))
                {
                    graph.Edges.Add(new QuestEdge { From = target, To = node.Id, Kind = "fails", Status = link.Status });
                }
            }
        }

        Flag(graph);
        Depths(graph);

        foreach (var trader in graph.Quests.Select(q => q.Trader).Distinct())
        {
            var info = sources.Traders.TryGetValue(trader, out var found) ? found : (null, true, 4);
            graph.Traders[trader] = new TraderOut
            {
                Name = names.Trader(trader),
                Avatar = info.Avatar,
                UnlockedByDefault = info.UnlockedByDefault,
                Levels = info.Levels,
                // only a real unlock: standing and purchase rewards name the trader too
                UnlockedBy = graph.Quests.Where(q => q.Rewards.Any(r => r.Kind == "TraderUnlock" && r.Trader == trader && r.When == "Success")).Select(q => q.Id).ToList()
            };
        }

        foreach (var (item, name) in names.Items)
        {
            graph.Items[item] = name;
        }

        graph.KeyEntries = graph.Quests.Sum(q => q.Needs.Count(n => n.Kind == "key"));
        graph.Maps.AddRange(graph.Quests.SelectMany(q => q.Maps).Distinct().OrderBy(m => m == AnyMap ? "" : m));
        graph.Quests.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return graph;
    }

    // Kappa and Lightkeeper: everything on the way to Collector and Knock-Knock. Lock-out: a quest that fails for
    // good when a rival is finished, and that rival. Same rules as Quest Glow.
    private static void Flag(QuestGraph graph)
    {
        foreach (var id in graph.Chain(QuestGraph.Collector))
        {
            graph.ById[id].Flags |= QuestGraph.Kappa;
        }

        foreach (var id in graph.Chain(QuestGraph.KnockKnock))
        {
            graph.ById[id].Flags |= QuestGraph.Lightkeeper;
        }

        foreach (var edge in graph.Edges.Where(e => e.Kind == "fails"))
        {
            graph.ById[edge.From].Flags |= QuestGraph.Lockout;
            graph.ById[edge.To].Flags |= QuestGraph.Lockout;
        }
    }

    // Longest prerequisite run. A link back into a quest still being worked out (a loop) is skipped.
    private static void Depths(QuestGraph graph)
    {
        var preds = graph.Edges.Where(e => e.Kind == "requires" && e.From != e.To).ToLookup(e => e.To, e => e.From);
        var done = new Dictionary<string, int>();
        var busy = new HashSet<string>();

        int DepthOf(string id)
        {
            if (done.TryGetValue(id, out var known))
            {
                return known;
            }

            if (!busy.Add(id))
            {
                return -1;
            }

            var depth = 0;
            foreach (var pred in preds[id])
            {
                var d = DepthOf(pred);
                if (d >= 0)
                {
                    depth = Math.Max(depth, d + 1);
                }
            }

            busy.Remove(id);
            done[id] = depth;
            return depth;
        }

        foreach (var node in graph.Quests)
        {
            node.Depth = DepthOf(node.Id);
        }
    }

    private static QuestNode Node(Quest quest, Names names, GraphSources sources)
    {
        var id = quest.Id.ToString();
        var start = (quest.Conditions?.AvailableForStart ?? []).Where(c => c != null).Select(c => Condition(c, names)).ToList();
        var finish = (quest.Conditions?.AvailableForFinish ?? []).Where(c => c != null).ToList();
        var maps = new List<string>();
        if (!string.IsNullOrEmpty(quest.Location) && !quest.Location.Equals("any", StringComparison.OrdinalIgnoreCase))
        {
            maps.Add(names.Map(quest.Location));
        }

        var objectives = finish.Select(c => Objective(c, names, sources, maps.FirstOrDefault(), names.Trader(quest.TraderId.ToString()))).ToList();

        maps.AddRange(objectives.SelectMany(o => o.Maps));
        maps = maps.Distinct().ToList();
        if (maps.Count == 0)
        {
            maps.Add(AnyMap);
        }

        var level = start.Where(c => c.Kind == "Level" && c.Compare is ">=" or ">" or "=" or "==").Select(c => (int?)c.Value).Max();
        var needs = Needs(quest, finish, names, sources);
        foreach (var need in needs.Where(n => n.Objective != null))
        {
            var from = objectives.FirstOrDefault(o => o.Id == need.Objective);
            need.Any = from?.Any ?? 0;
            need.AnyOf = from?.AnyOf ?? 0;
        }

        // your keys.json replaces tarkov.dev's list for a quest it names
        var user = sources.UserKeys.TryGetValue(id, out var own);
        var entries = user ? own! : sources.Keys.GetValueOrDefault(id) ?? [];
        // the quest data doesn't say which keys a quest needs, but the server's maps say where each lock is: what's close
        // to an objective's places backs a listed key up, and anything else close is worth knowing about
        var near = objectives.Where(o => o.Zones.Count > 0).Select(o => (Objective: o.Id, Locks: sources.LocksNear(o.Zones))).ToList();
        var nearKeys = near.SelectMany(n => n.Locks.Select(l => l.Key)).ToHashSet();
        var listed = entries.SelectMany(e => e.Keys).ToHashSet();
        // tarkov.dev is kept unless the quest has changed here and none of its keys open anything near the objective:
        // then the closest lock's key takes over
        var closest = near.SelectMany(n => n.Locks).OrderBy(l => l.Metres).Select(l => l.Key).FirstOrDefault();
        var moved = !user && closest != null && sources.QuestMoved(id, objectives.SelectMany(o => o.Zones));
        bool Replaced(KeyEntry entry) => moved && !entry.Keys.Any(nearKeys.Contains);
        listed.UnionWith(entries.Where(Replaced).Select(_ => closest!));
        needs.AddRange(near.SelectMany(n => n.Locks.Where(l => !listed.Contains(l.Key)).Take(MostLocksNear).Select(l => new NeedInfo
        {
            Kind = "lock",
            Items = [names.Item(l.Key)],
            Ids = [l.Key],
            Text = names.Item(l.Key),
            Note = $"opens a lock {Number(Math.Round(l.Metres))} m from the objective",
            Objective = n.Objective,
            Source = "map"
        })).GroupBy(n => n.Ids[0]).Select(g => g.First()));
        needs.InsertRange(0, entries.Select(entry =>
        {
            var swapped = Replaced(entry);
            var ids = swapped ? [closest!] : entry.Keys;
            var keys = ids.Select(names.Item).ToList();
            return new NeedInfo
            {
                Kind = "key",
                Items = keys,
                Ids = ids,
                Text = string.Join(" or ", keys) + (entry.Map != null ? $" ({names.Map(entry.Map)})" : ""),
                Note = swapped ? $"This quest is different on this server, so this replaces {string.Join(" or ", entry.Keys.Select(names.Item))} from tarkov.dev's list." : entry.Note,
                Objective = entry.Objective,
                Source = swapped ? "map" : user ? "keys.json" : "tarkov.dev",
                OnMap = near.Count == 0 ? null : swapped || entry.Keys.Any(nearKeys.Contains)
            };
        }).GroupBy(n => n.Text).Select(g => g.First()));

        var rewards = Rewards(quest, names);
        var (score, highlights, value) = RewardWorth(rewards, sources, names);

        return new QuestNode
        {
            RewardScore = score,
            Highlights = highlights.Select(h => h.Text).ToList(),
            HighlightItems = highlights.Select(h => h.Item).ToList(),
            RewardValue = value,
            Id = id,
            Name = names.Quest(id),
            Trader = quest.TraderId.ToString(),
            Maps = maps,
            Level = level,
            Side = quest.Side,
            Type = quest.Type.ToString(),
            Restartable = quest.Restartable,
            Wiki = sources.Wiki.GetValueOrDefault(id),
            LiveName = LiveName(names.Quest(id), sources.Wiki.GetValueOrDefault(id)),
            Image = string.IsNullOrEmpty(quest.Image) ? null : quest.Image,
            Description = names.Text(quest.Description ?? "") ?? names.Text(id + " description"),
            Start = start,
            FailsWhen = (quest.Conditions?.Fail ?? []).Where(c => c != null).Select(c => Condition(c, names)).ToList(),
            Objectives = objectives,
            Needs = needs,
            Rewards = rewards
        };
    }

    // What the player has to bring, wear or use, read from the objectives. Keys for locked rooms aren't in the
    // quest data; those come from keys.json.
    private static List<NeedInfo> Needs(Quest quest, List<QuestCondition> finish, Names names, GraphSources sources)
    {
        var needs = new List<NeedInfo>();
        var handedOver = finish.Where(c => c.ConditionType == "HandoverItem").Select(c => string.Join(",", Targets(c.Target).Order())).ToHashSet();

        foreach (var condition in finish)
        {
            var targets = Targets(condition.Target);
            var count = Number(condition.Value);
            var fir = condition.OnlyFoundInRaid == true ? ", found in raid" : "";
            switch (condition.ConditionType)
            {
                case "HandoverItem":
                case "FindItem" when !handedOver.Contains(string.Join(",", targets.Order())):
                {
                    var questItem = targets.Any(sources.IsQuestItem);
                    var verb = condition.ConditionType == "HandoverItem" ? "handover" : "find";
                    needs.Add(Need(verb, targets, names, $"{count} ×", questItem ? " (quest item)" : fir, condition.Value, condition.OnlyFoundInRaid == true, condition.Id.ToString()));
                    break;
                }
                case "LeaveItemAtLocation":
                case "PlaceBeacon":
                    needs.Add(Need("place", targets, names, $"{count} ×", "", condition.Value, false, condition.Id.ToString()));
                    break;
                case "WeaponAssembly":
                    needs.Add(Need("build", targets, names, "", ""));
                    break;
                case "SellItemToTrader":
                    needs.Add(Need("sell", targets, names, $"{count} ×", "", condition.Value, false, condition.Id.ToString()));
                    break;
            }

            foreach (var counter in condition.Counter?.Conditions ?? [])
            {
                if (counter == null)
                {
                    continue;
                }

                // each list is one way to meet it (any one of them will do); inside a list, every item is needed
                if (Alternatives("wear", counter.EquipmentInclusive, names) is { } wear)
                {
                    needs.Add(wear);
                }

                if (counter.Weapon is { Count: > 0 } weapons)
                {
                    needs.Add(Need("weapon", weapons.ToList(), names, "", ""));
                }

                if (Alternatives("mods", counter.WeaponModsInclusive, names) is { } mods)
                {
                    needs.Add(mods);
                }

                if (counter.WeaponCaliber is { Count: > 0 } calibers)
                {
                    var list = calibers.Select(Caliber).ToList();
                    needs.Add(new NeedInfo { Kind = "caliber", Items = list, Text = string.Join(" or ", list) });
                }
            }
        }

        foreach (var reward in quest.Rewards?.GetValueOrDefault("Started") ?? [])
        {
            if (reward?.Type == RewardType.Item)
            {
                var item = ItemStack(reward, names);
                var root = RootItem(reward);
                needs.Add(new NeedInfo { Kind = "given", Items = [item], Ids = root == null ? [] : [root.Template.ToString()], Text = item });
            }
        }

        // the same "wear" or "weapon" list often repeats across objectives
        return needs.GroupBy(n => n.Kind + "|" + n.Text).Select(g => g.First()).ToList();
    }

    private static NeedInfo Need(string kind, List<string> targets, Names names, string before, string after, double? count = null, bool fir = false, string? objective = null)
    {
        var items = targets.Select(names.Item).Distinct().ToList();
        var shown = kind == "weapon" && items.Count > ListedWeapons
            ? string.Join(" or ", items.Take(ListedWeapons)) + $" (+{items.Count - ListedWeapons} more)"
            : string.Join(" or ", items);
        return new NeedInfo { Kind = kind, Items = items, Ids = targets, Count = count, Fir = fir, Objective = objective, Text = $"{before} {shown}{after}".Trim() };
    }

    private static NeedInfo? Alternatives(string kind, IEnumerable<List<string>>? sets, Names names)
    {
        var ways = (sets ?? []).Where(set => set is { Count: > 0 })
            .Select(set => string.Join(" + ", set.Select(names.Item).Distinct()))
            .Distinct()
            .ToList();
        if (ways.Count == 0)
        {
            return null;
        }

        var shown = string.Join(" or ", ways.Take(ListedWeapons)) + (ways.Count > ListedWeapons ? $" (+{ways.Count - ListedWeapons} more)" : "");
        return new NeedInfo { Kind = kind, Items = ways, Text = shown };
    }

    // "Caliber556x45NATO" -> "5.56x45 NATO"
    internal static string Caliber(string caliber)
    {
        var match = CaliberPattern().Match(caliber);
        if (!match.Success)
        {
            return caliber.Replace("Caliber", "");
        }

        var bore = match.Groups[1].Value;
        bore = bore.Length switch
        {
            >= 3 => bore[0] == '1' ? bore[..2] + "." + bore[2..] : bore[..1] + "." + bore[1..],
            2 when bore is "46" or "57" or "86" or "93" => bore[..1] + "." + bore[1..],
            _ => bore
        };
        var rest = match.Groups[3].Value;
        return $"{bore}x{match.Groups[2].Value}{(rest.Length > 0 ? " " + rest : "")}";
    }

    [GeneratedRegex(@"^Caliber(\d+)x(\d+)(.*)$")]
    private static partial Regex CaliberPattern();

    private static ConditionInfo Condition(QuestCondition condition, Names names)
    {
        var kind = condition.ConditionType ?? "Unknown";
        var targets = Targets(condition.Target);
        var compare = condition.CompareMethod;
        var value = condition.Value;

        switch (kind)
        {
            case "Quest":
            {
                var status = (condition.Status ?? []).OrderBy(s => (int)s).Select(s => s.ToString()).ToList();
                var words = status.Count > 0 ? string.Join(" or ", status.Select(QuestGraph.StatusWord)) : "any status";
                var delay = condition.AvailableAfter is > 0 ? $", then wait {Duration(condition.AvailableAfter.Value)}" : "";
                return new ConditionInfo
                {
                    Kind = kind,
                    Quests = targets,
                    Status = status,
                    AvailableAfter = condition.AvailableAfter is > 0 ? condition.AvailableAfter : null,
                    Text = $"{string.Join(" / ", targets.Select(names.Quest))}: {words}{delay}"
                };
            }
            case "Level":
                return new ConditionInfo { Kind = kind, Compare = compare, Value = value, Text = $"Level {compare} {Number(value)}" };
            case "TraderLoyalty":
            case "TraderStanding":
            {
                var trader = targets.FirstOrDefault() ?? condition.TraderId ?? "";
                var what = kind == "TraderLoyalty" ? "loyalty level" : "standing";
                return new ConditionInfo
                {
                    Kind = kind, Trader = trader, Compare = compare, Value = value,
                    Text = $"{names.Trader(trader)} {what} {compare} {Number(value)}"
                };
            }
            default:
                return new ConditionInfo { Kind = kind, Compare = compare, Value = value, Quests = targets.Where(names.IsQuest).ToList(), Text = Generic(condition, kind, targets, names) };
        }
    }

    // Anything else is shown by its own locale text when it has one, else by type, targets and value.
    private static string Generic(QuestCondition condition, string kind, List<string> targets, Names names)
    {
        var own = names.Text(condition.Id.ToString());
        if (own != null)
        {
            return own;
        }

        var text = new StringBuilder(kind);
        if (targets.Count > 0)
        {
            text.Append(": ").Append(string.Join(", ", targets.Select(names.Any)));
        }

        if (condition.Value != null)
        {
            text.Append($" {condition.CompareMethod ?? ""} {Number(condition.Value)}".TrimEnd());
        }

        return text.ToString();
    }

    private static ObjectiveInfo Objective(QuestCondition condition, Names names, GraphSources sources, string? questMap, string trader)
    {
        var kind = condition.ConditionType ?? "Unknown";
        var targets = Targets(condition.Target);
        var text = names.Text(condition.Id.ToString());

        if (kind is "HandoverItem" or "FindItem" or "LeaveItemAtLocation" or "SellItemToTrader")
        {
            // found in raid shows as the FIR badge, so it isn't written in the text as well
            var stack = $"{Number(condition.Value)} × {string.Join(" / ", targets.Select(names.Item))}";
            // the locale text often says it all already ("Hand over the found in raid item: Antique axe")
            // the text names the item already: only a count it doesn't say is added ("× 4"); found in raid is a badge
            var named = text != null && targets.All(t => text.Contains(names.Item(t), StringComparison.OrdinalIgnoreCase));
            var counted = condition.Value is null or 1 || (text?.Contains(Number(condition.Value)) ?? false);
            text = text == null ? $"{kind}: {stack}" : named ? (counted ? text : $"{text} (× {Number(condition.Value)})") : $"{text} [{stack}]";
        }

        text ??= Generic(condition, kind, targets, names);

        var maps = new List<string>();
        foreach (var counter in condition.Counter?.Conditions ?? [])
        {
            if (counter?.ConditionType == "Location")
            {
                maps.AddRange(Targets(counter.Target).Select(names.Map));
            }
        }

        // "sell any of these 60 items" isn't a reason to keep any one of them
        var items = kind is "HandoverItem" or "FindItem" or "LeaveItemAtLocation" or "PlaceBeacon" or "WeaponAssembly"
            || (kind == "SellItemToTrader" && targets.Count <= 5) ? targets : [];
        items.ForEach(t => names.Item(t));
        return new ObjectiveInfo
        {
            Id = condition.Id.ToString(), Kind = kind, Text = text, Maps = maps.Distinct().ToList(),
            Items = items, Count = items.Count > 0 || condition.Value > 1 ? condition.Value : null, Fir = condition.OnlyFoundInRaid == true,
            QuestItem = kind == "FindItem" && targets.Count > 0 && targets.All(sources.IsQuestItem),
            Zones = ZonesOf(condition),
            ZoneMaps = sources.ZoneMaps(ZonesOf(condition)),
            Steps = Steps(condition, names, sources, questMap, text) is { Count: > 0 } steps ? steps : ItemSteps(condition, names, sources, questMap, trader, text)
        };
    }


    private static readonly Dictionary<string, string> ExitStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Survived"] = "survive", ["Runner"] = "run through", ["Transit"] = "take a transit", ["Killed"] = "die", ["Left"] = "leave the raid",
        ["MissingInAction"] = "go missing in action"
    };

    // The ids of the places an objective asks for, as the map scenes name their triggers.
    private static List<string> ZonesOf(QuestCondition condition)
    {
        var zones = new List<string>();
        if (!string.IsNullOrEmpty(condition.ZoneId))
        {
            zones.Add(condition.ZoneId);
        }

        foreach (var c in condition.Counter?.Conditions ?? [])
        {
            if (c == null)
            {
                continue;
            }

            if (c.ConditionType == "VisitPlace")
            {
                zones.AddRange(Targets(c.Target));
            }

            zones.AddRange(c.Zones ?? []);
        }

        return zones.Where(z => !string.IsNullOrWhiteSpace(z)).Distinct().ToList();
    }

    // What a counter objective asks beyond what its text says: "with the killing shot to the head, using ...", "all
    // in one raid", where the bots it wants spawn. A kill with no such terms adds no line of its own.
    private static List<string> Steps(QuestCondition condition, Names names, GraphSources sources, string? questMap, string text)
    {
        var steps = new List<string>();
        var counters = (condition.Counter?.Conditions ?? []).Where(c => c != null).Select(c => c!).ToList();
        if (counters.Count == 0)
        {
            return steps;
        }

        var maps = counters.Where(c => c.ConditionType == "Location").SelectMany(c => Targets(c.Target)).Select(names.Map).Distinct().ToList();
        // no map in the objective: the quest's own map, when it has one
        if (maps.Count == 0 && questMap != null)
        {
            maps.Add(questMap);
        }
        var count = condition.Value is > 1 ? Number(condition.Value) + " " : "";
        var roles = new List<string>();
        foreach (var c in counters)
        {
            switch (c.ConditionType)
            {
                case "Kills":
                case "Shots":
                {
                    roles.AddRange(c.SavageRole ?? []);
                    var line = new StringBuilder();
                    // a mod's quest can list a blank entry (""), which the game ignores
                    if (c.BodyPart?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() is { Count: > 0 } parts)
                    {
                        line.Append($", with the {(c.ConditionType == "Shots" ? "hit" : "killing shot")} to the {string.Join(" or ", parts.Select(p => names.Text("QuestCondition/Elimination/Kill/BodyPart/" + p) ?? Regex.Replace(p, "([a-z])([A-Z])", "$1 $2").ToLowerInvariant()))}");
                    }

                    if (c.Distance?.Value is > 0 and var metres)
                    {
                        var closer = c.Distance.CompareMethod is "<=" or "<";
                        line.Append($", from {(closer ? "no more than" : "at least")} {Number(metres)} m away");
                    }

                    if (c.Weapon?.Where(w => !string.IsNullOrWhiteSpace(w.ToString())).ToList() is { Count: > 0 } weapons)
                    {
                        var list = weapons.Select(names.Item).Distinct().ToList();
                        line.Append($", using {string.Join(" or ", list.Take(4))}{(list.Count > 4 ? $" (or one of {list.Count - 4} more)" : "")}");
                    }

                    if (c.WeaponCaliber?.Where(w => !string.IsNullOrWhiteSpace(w)).ToList() is { Count: > 0 } calibers)
                    {
                        line.Append($", with {string.Join(" or ", calibers.Select(Caliber))} ammo");
                    }

                    if (c.Daytime is { } time && (time.From ?? 0) != (time.To ?? 0))
                    {
                        line.Append($", between {time.From:00}:00 and {time.To:00}:00 in-raid time");
                    }

                    if (c.Zones is { Count: > 0 })
                    {
                        line.Append(", inside the quest's area (see the map below)");
                    }

                    if (c.EnemyEquipmentInclusive?.Any(e => e is { Count: > 0 }) == true)
                    {
                        var gear = c.EnemyEquipmentInclusive.Where(e => e is { Count: > 0 }).Select(e => string.Join(" + ", e.Select(names.Item))).Distinct().Take(3);
                        line.Append($", while they wear {string.Join(" or ", gear)}");
                    }

                    if (c.EnemyHealthEffects is { Count: > 0 })
                    {
                        line.Append(", while they're hurt in the way the quest asks (see its text)");
                    }

                    // only the terms: "Kill 15 enemies" is the objective's own text
                    if (line.Length > 0)
                    {
                        steps.Add(char.ToUpperInvariant(line[2]) + line.ToString()[3..] + ".");
                    }

                    break;
                }
                case "ExitStatus":
                {
                    // surviving is what extracting means; only a raid that may end another way says more
                    var how = (c.Status ?? []).Where(s => s != "Survived").Select(s => ExitStatuses.GetValueOrDefault(s) ?? s).Distinct().ToList();
                    if (how.Count > 0)
                    {
                        steps.Add($"{char.ToUpperInvariant(how[0][0])}{string.Join(" or ", how)[1..]} counts too.");
                    }

                    break;
                }
                case "ExitName":
                    if (!string.IsNullOrEmpty(c.ExitName) && (names.Text(c.ExitName) ?? c.ExitName) is var exit && !text.Contains(exit, StringComparison.OrdinalIgnoreCase))
                    {
                        steps.Add($"Leave through {exit}.");
                    }

                    break;
                case "Equipment":
                    if (c.EquipmentInclusive?.Any(e => e is { Count: > 0 }) == true)
                    {
                        var gear = c.EquipmentInclusive.Where(e => e is { Count: > 0 }).Select(e => string.Join(" + ", e.Select(names.Item))).Distinct().ToList();
                        steps.Add($"Wear {string.Join(" or ", gear.Take(4))}{(gear.Count > 4 ? $" (or one of {gear.Count - 4} more sets)" : "")} while you do it.");
                    }

                    break;
                case "UseItem":
                {
                    var used = Targets(c.Target).Select(names.Item).Distinct().Where(u => !text.Contains(u, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (used.Count > 0)
                    {
                        steps.Add($"Use {string.Join(" or ", used.Take(4))}.");
                    }

                    break;
                }
            }
        }

        if (condition.OneSessionOnly == true)
        {
            steps.Add("All in one raid: if you die or leave first, it starts over.");
        }

        // where these bots turn up, so you know where to go: the best chance on each map of any of them
        var spawns = roles.Distinct().SelectMany(sources.BotSpawns)
            .Where(s => maps.Count == 0 || maps.Contains(s.Map))
            .GroupBy(s => s.Map).Select(g => (Map: g.Key, Chance: g.Max(s => s.Chance)))
            .OrderByDescending(s => s.Chance).ThenBy(s => s.Map).ToList();
        if (spawns.Count > 0)
        {
            var who = string.Join(" and ", roles.Select(r => BotRoles.Name(r, names.Locale)).Distinct());
            steps.Add($"Where {who} spawn: {string.Join(", ", spawns.Select(s => $"{s.Map} ({Number(s.Chance)}% a raid)"))}.");
        }

        if (roles.Any(r => r.StartsWith("sectant", StringComparison.OrdinalIgnoreCase)))
        {
            steps.Add("Cultists only come out at night, in-raid time.");
        }

        return steps;
    }

    // Item objectives as instructions: what to bring or pick up, whether it must be found in raid, and where.
    private static List<string> ItemSteps(QuestCondition condition, Names names, GraphSources sources, string? map, string trader, string text)
    {
        var targets = Targets(condition.Target);
        if (targets.Count == 0 || condition.ConditionType is not ("FindItem" or "HandoverItem" or "LeaveItemAtLocation" or "PlaceBeacon"))
        {
            return [];
        }

        var what = $"{Number(condition.Value ?? 1)} × {string.Join(" or ", targets.Take(4).Select(names.Item))}{(targets.Count > 4 ? $" (or one of {targets.Count - 4} more)" : "")}";
        var fir = condition.OnlyFoundInRaid == true;
        var questItem = targets.All(sources.IsQuestItem);
        var limits = new List<string>();
        if (condition.MinDurability is > 0)
        {
            limits.Add($"at least {Number(condition.MinDurability)}% durability");
        }

        if (condition.MaxDurability is > 0 and < 100)
        {
            limits.Add($"at most {Number(condition.MaxDurability)}% durability");
        }

        if (condition.DogtagLevel is > 0)
        {
            limits.Add($"from a level {condition.DogtagLevel}+ PMC");
        }

        var steps = new List<string>();
        // a quest item: the page shows where it lies; the rest the objective says itself
        if (questItem)
        {
            return steps;
        }

        if (limits.Count > 0)
        {
            steps.Add($"Only counts with {string.Join(", ", limits)}.");
        }

        switch (condition.ConditionType)
        {
            case "FindItem" or "HandoverItem":
                // found in raid or not shows as the FIR badge; a hint only when the objective's text says found in raid
                // and the quest doesn't check it (a mod's wording), as the server goes by the check
                if (!fir && text.Contains("found in raid", StringComparison.OrdinalIgnoreCase))
                {
                    steps.Add("The text says found in raid, but this server doesn't check it: bought ones count too.");
                }

                break;
            case "LeaveItemAtLocation":
            case "PlaceBeacon":
                steps.Add($"At the spot (see the map below), look at it, pick the place action and hold still{(condition.PlantTime is > 0 ? $" for {Number(condition.PlantTime)} s" : "")}.");
                break;
        }

        return steps;
    }

    // How much a quest's rewards are worth having, and which to point out: a big item case, a new trader, stash
    // rows, the chance to buy something valuable, or a large pile of loot. XP and money add a little. The sum is
    // squashed to 0..1, so a quest with one great reward and a quest with five good ones both score high.
    private static (double Score, List<(string Text, string? Item)> Highlights, double Value) RewardWorth(List<RewardInfo> rewards, GraphSources sources, Names names)
    {
        var found = new List<(double Weight, string Text, string? Item)>();
        double value = 0, raw = 0;
        foreach (var reward in rewards.Where(r => r.When == "Success"))
        {
            var price = reward.Item != null ? sources.ItemPrice(reward.Item) : 0;
            // a case is worth what it adds to the stash: a T H I C C item case far more than a documents case
            var space = reward.Item != null ? sources.CaseSpace(reward.Item) : 0;
            switch (reward.Kind)
            {
                case "Experience":
                    raw += Math.Min(0.2, (ParseNumber(reward.Text) ?? 0) / 200000);
                    break;
                case "Item":
                    value += price * (reward.Count ?? 1);
                    if (space >= 40)
                    {
                        found.Add((1, $"Gives {names.Item(reward.Item!)}", reward.Item));
                    }
                    else if (space >= 12)
                    {
                        found.Add((0.4, $"Gives {names.Item(reward.Item!)}", reward.Item));
                    }

                    break;
                case "AssortmentUnlock":
                    if (space >= 40)
                    {
                        found.Add((1, $"Unlocks buying {names.Item(reward.Item!)}", reward.Item));
                    }
                    else if (price >= 250000)
                    {
                        found.Add((0.7, $"Unlocks buying {names.Item(reward.Item!)}", reward.Item));
                    }
                    else if (price >= 80000 || space >= 12)
                    {
                        found.Add((0.3, $"Unlocks buying {names.Item(reward.Item!)}", reward.Item));
                    }
                    else
                    {
                        raw += 0.04;
                    }

                    break;
                case "ProductionScheme":
                    found.Add((space >= 40 ? 0.9 : 0.25, $"Unlocks crafting {names.Item(reward.Item ?? "")}", reward.Item));
                    break;
                case "TraderUnlock":
                    found.Add((1, reward.Text, null));
                    break;
                case "StashRows":
                case "Pockets":
                    found.Add((0.9, reward.Text, null));
                    break;
            }
        }

        raw += Math.Min(0.3, value / 3000000);
        if (value >= 2000000)
        {
            found.Add((0.6, $"Items worth about {value / 1000000:0.#}M", null));
        }

        raw += found.Sum(f => f.Weight);
        var highlights = found.Where(f => f.Weight >= 0.6).OrderByDescending(f => f.Weight).DistinctBy(f => f.Text).Take(3).Select(f => (f.Text, f.Item)).ToList();
        return (Math.Round(raw / (raw + 0.6), 3), highlights, Math.Round(value));
    }

    // The wiki page's title, when it isn't just this name with "[PVE ZONE]" or "- Part 1" dropped.
    private static string? LiveName(string name, string? wiki)
    {
        if (string.IsNullOrEmpty(wiki))
        {
            return null;
        }

        var live = Uri.UnescapeDataString(wiki[(wiki.LastIndexOf('/') + 1)..]).Replace('_', ' ').Trim();
        static string Plain(string text) => Regex.Replace(Regex.Replace(text.ToLowerInvariant(), @"\[pve zone\]|\(quest\)|- part 1$|^a ", ""), "[^a-z0-9]", "");
        return live.Length == 0 || Plain(live) == Plain(name) ? null : live;
    }

    private static double? ParseNumber(string text)
    {
        var digits = new string(text.Where(c => char.IsDigit(c) || c == '.').ToArray());
        return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static List<RewardInfo> Rewards(Quest quest, Names names)
    {
        var list = new List<RewardInfo>();
        foreach (var (when, rewards) in quest.Rewards ?? [])
        {
            foreach (var reward in rewards ?? [])
            {
                if (reward == null)
                {
                    continue;
                }

                var kind = reward.Type?.ToString() ?? "Unknown";
                var root = RootItem(reward);
                list.Add(new RewardInfo
                {
                    When = when, Kind = kind, Text = RewardText(reward, kind, names),
                    Trader = reward.Type is RewardType.TraderUnlock or RewardType.TraderStanding ? reward.Target
                        : reward.Type == RewardType.AssortmentUnlock && reward.TraderId?.IsString == true ? reward.TraderId.String : null,
                    Value = reward.Value,
                    Item = root?.Template.ToString(),
                    Count = root == null ? null : Copies(reward, root)
                });
            }
        }

        return list;
    }

    private static string RewardText(Reward reward, string kind, Names names)
    {
        var target = reward.Target ?? "";
        var traderId = reward.TraderId?.IsString == true ? reward.TraderId.String : reward.TraderId?.Int?.ToString(CultureInfo.InvariantCulture);
        var fir = reward.FindInRaid == true ? " (found in raid)" : "";

        switch (reward.Type)
        {
            case RewardType.Experience:
                return $"+{Number(reward.Value)} XP";
            case RewardType.TraderStanding:
                return $"{names.Trader(target)} standing {(reward.Value >= 0 ? "+" : "")}{Number(reward.Value)}";
            case RewardType.TraderUnlock:
                return $"Unlocks trader {names.Trader(target)}";
            case RewardType.Skill:
                return $"{names.Text(target) ?? target} skill +{Number(reward.Value)}";
            case RewardType.Item:
                return $"{ItemStack(reward, names)}{fir}";
            case RewardType.AssortmentUnlock:
                return $"Unlocks purchase of {ItemStack(reward, names, false)} at {names.Trader(traderId ?? "")}" +
                       (reward.LoyaltyLevel != null ? $" LL{reward.LoyaltyLevel}" : "");
            case RewardType.ProductionScheme:
                return $"Unlocks crafting {ItemStack(reward, names)}" + (traderId != null ? $" ({names.Text($"hideout_area_{traderId}_name") ?? "hideout area " + traderId})" : "");
            case RewardType.StashRows:
                return $"+{Number(reward.Value)} stash rows";
            case RewardType.Pockets:
                return "Expanded pockets";
            case RewardType.Achievement:
                return $"Achievement: {names.Text(target + " name") ?? target}";
            case RewardType.Quest:
                return $"Quest: {names.Quest(target)}";
            default:
                var parts = new List<string> { kind };
                if (reward.Items is { Count: > 0 })
                {
                    parts.Add(ItemStack(reward, names));
                }
                else if (target != "")
                {
                    parts.Add(names.Any(target));
                }

                if (reward.Value != null)
                {
                    parts.Add(Number(reward.Value));
                }

                return string.Join(" ", parts);
        }
    }

    private static Item? RootItem(Reward reward)
    {
        var items = reward.Items ?? [];
        return items.FirstOrDefault(i => i.Id.ToString() == reward.Target) ?? items.FirstOrDefault();
    }

    // How many of the reward's top item it gives: its stack, plus any separate copies alongside it (a reward of
    // 2 × Pile of meds can be two items of one each). Parts attached to a top item aren't counted.
    private static double Copies(Reward reward, Item root)
    {
        var items = reward.Items ?? [];
        var ids = items.Select(i => i.Id.ToString()).ToHashSet();
        return items.Where(i => i.Template == root.Template && (i.ParentId == null || !ids.Contains(i.ParentId)))
            .Sum(i => i.Upd?.StackObjectsCount ?? 1);
    }

    // The reward's top item (its Target) with its stack size; attachments under it aren't listed.
    private static string ItemStack(Reward reward, Names names, bool count = true)
    {
        var root = RootItem(reward);
        if (root == null)
        {
            return names.Any(reward.Target ?? "?");
        }

        var stack = root.Upd?.StackObjectsCount == null && reward.Items?.Count == 1 ? reward.Value ?? 1 : Copies(reward, root);
        var name = names.Item(root.Template.ToString());
        return count && stack != 1 ? $"{Number(stack)} × {name}" : name;
    }

    private static List<string> Targets(SPTarkov.Server.Core.Utils.Json.ListOrT<string>? target)
    {
        if (target?.IsList == true)
        {
            return target.List!.Where(t => !string.IsNullOrEmpty(t)).ToList();
        }

        return target?.Item is { Length: > 0 } item ? [item] : [];
    }

    private static string Number(double? value)
    {
        return value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "?";
    }

    private static string Duration(int seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1 ? $"{span.TotalHours:0.#} h" : $"{span.TotalMinutes:0} min";
    }

    // Locale lookups with the id as the fallback, so quests added by mods without locale entries still show.
    private sealed class Names(IReadOnlyDictionary<string, string> locale, IReadOnlyDictionary<string, string> mapIds, List<Quest> quests)
    {
        private readonly Dictionary<string, string> _questNames = quests
            .GroupBy(q => q.Id.ToString())
            .ToDictionary(g => g.Key, g => g.First().QuestName ?? g.Key);

        public bool IsQuest(string id) => _questNames.ContainsKey(id);

        public IReadOnlyDictionary<string, string> Locale => locale;

        public string? Text(string key) => locale.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

        public string Quest(string id) => Text(id + " name") ?? _questNames.GetValueOrDefault(id) ?? id;

        public string Trader(string id) => Text(id + " Nickname") ?? id;

        // every item looked up, so the page can name it (and show its icon) wherever it appears
        public Dictionary<string, string> Items { get; } = [];

        public string Item(string id) => Items[id] = Text(id + " Name") ?? id;

        // a location id ("56f40101d2720b2a4d8b45d6") or a map key ("bigmap"); both give the map's display name
        // a variant with no name of its own ("Sandbox_start") goes by its map's ("Sandbox")
        public string Map(string id) => Text((mapIds.GetValueOrDefault(id) ?? id) + " Name") ?? Text(id)
            ?? (id.IndexOf('_') is > 0 and var cut ? Text(id[..cut]) : null) ?? id;

        public string Any(string id) => _questNames.ContainsKey(id) ? Quest(id) : Text(id + " Nickname") ?? Text(id + " Name") ?? Text(id + " name") ?? Text(id) ?? id;
    }
}
