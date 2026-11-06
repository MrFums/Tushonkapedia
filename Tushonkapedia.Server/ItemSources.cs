using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Common;

namespace Tushonkapedia.Server;

// Ways to get an item besides loot spots, traders and crafts: bots that can carry it (on their gear, in their
// pockets, bags and vests, or on their guns) and the kinds of airdrop that can hold it.
public sealed partial class ItemWiki
{
    private Dictionary<string, HashSet<string>>? _carriers;

    // the bot roles whose gear can include each item
    private Dictionary<string, HashSet<string>> Carriers()
    {
        if (_carriers != null)
        {
            return _carriers;
        }

        var carriers = new Dictionary<string, HashSet<string>>();
        void Add(string item, string role)
        {
            if (!carriers.TryGetValue(item, out var roles))
            {
                carriers[item] = roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            roles.Add(role);
        }

        foreach (var (role, type) in bots.Types)
        {
            var inventory = type?.BotInventory;
            if (inventory == null || !BotRoles.IsEnemy(role))
            {
                continue;
            }

            foreach (var pool in (inventory.Equipment ?? []).Values)
            {
                foreach (var item in pool.Keys)
                {
                    Add(item.ToString(), role);
                }
            }

            var items = inventory.Items;
            foreach (var pool in new[] { items?.Backpack, items?.Pockets, items?.TacticalVest, items?.SpecialLoot })
            {
                foreach (var item in (pool ?? []).Keys)
                {
                    Add(item.ToString(), role);
                }
            }

            foreach (var (holder, slots) in inventory.Mods ?? [])
            {
                Add(holder.ToString(), role);
                foreach (var part in slots.Values.SelectMany(p => p))
                {
                    Add(part.ToString(), role);
                }
            }
        }

        return _carriers = carriers;
    }

    // "Scavs", "PMCs", "Killa" ...: who can carry it, the common bots first
    public List<string> CarriedBy(string id, IReadOnlyDictionary<string, string> locale)
    {
        if (!Carriers().TryGetValue(id, out var roles))
        {
            return [];
        }

        var names = roles.Select(r => BotName(r, locale)).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
        // both kinds of PMC are just PMCs: two names the same but for the side, by the locale's names for the sides
        // the PMC sides are the ones SPT gives dogtags for
        var sides = Enum.GetNames<DogtagSide>().Select(k => locale.GetValueOrDefault(k.ToUpperInvariant()) ?? k).ToList();
        string Sideless(string n) => string.Join(' ', n.Split(' ').Where(w => !sides.Contains(w, StringComparer.OrdinalIgnoreCase)));
        foreach (var pair in names.Where(n => Sideless(n) != n).GroupBy(Sideless).Where(g => g.Key.Length > 0 && g.Count() == sides.Count).ToList())
        {
            names = names.Except(pair).Append(pair.Key).ToList();
        }

        return names.Distinct().OrderBy(n => BotRoles.Order(n, locale)).ThenBy(n => n).ToList();
    }

    // an airdrop kind by the server's own key for it, split into words ("weaponArmor" as "weapon armor")
    private static string AirdropName(string key) => Spaced(key).ToLowerInvariant();

    // The kinds of airdrop whose loot can include this item, the way SPT's own airdrop code picks it: an item whose
    // direct kind the airdrop lists, unless SPT's item settings blacklist it (all loot, rewards, boss items, reward
    // kinds), the airdrop's own list does, it is a seasonal item whose event is off, or it is a quest item; guns and armour come as whole presets; and any item
    // the airdrop is set to always carry.
    public List<string> Airdrops(string id)
    {
        if (!MongoId.IsValidMongoId(id) || !templates.Items.TryGetValue(new MongoId(id), out var item))
        {
            return [];
        }

        var lineage = Lineage(id).Select(x => new MongoId(x)).ToHashSet();
        var settings = ItemSettings;
        var barred = (settings != null && (settings.Blacklist.Contains(item.Id) || settings.RewardItemBlacklist.Contains(item.Id)
            || settings.BossItems.Contains(item.Id) || settings.RewardItemTypeBlacklist.Overlaps(lineage)))
            || OutOfSeason?.Invoke().Contains(id) == true;
        var armorClass = item.Properties?.ArmorClass;
        // guns and armour that takes plates come as whole presets, counted apart from the other loot
        var weapon = IsWeapon(id);
        var armour = armorClass is > 0 && item.Properties?.Slots?.Any() == true;
        return airdrops.Loot
            .Where(l => airdrops.AirdropTypeWeightings.Any(w => w.Key.ToString().Equals(l.Key, StringComparison.OrdinalIgnoreCase) && w.Value > 0))
            .Where(l => (l.Value.UseForcedLoot && l.Value.ForcedLoot?.ContainsKey(item.Id) == true)
                        || (!barred && !l.Value.ItemBlacklist.Contains(item.Id) && item.Properties?.QuestItem != true
                            && ((l.Value.ItemCount?.Max > 0 && l.Value.ItemTypeWhitelist.Contains(item.Parent))
                                || (weapon && l.Value.WeaponPresetCount?.Max > 0)
                                || (armour && l.Value.ArmorPresetCount?.Max > 0
                                    && (l.Value.ArmorLevelWhitelist == null || l.Value.ArmorLevelWhitelist.Contains((int)armorClass!))))))
            .Select(l => AirdropName(l.Key))
            .ToList();
    }
}
