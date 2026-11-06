using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Enums;

namespace Tushonkapedia.Server;

// What a part or a round fits: every gun and part with a slot, chamber or magazine that takes it, from the slots of
// every item in the server's table (a slot names items, or whole branches of the item tree such as "all muzzle
// devices", less the ones it leaves out). Worked out once, the first time it's asked for; mod guns and parts are
// in it like any other.
public sealed partial class ItemWiki
{
    // weapon parts (magazines among them) hang under this node in the item tree
    private static readonly string ModNode = BaseClasses.MOD.ToString();

    private Dictionary<string, List<string>>? _fits;

    private Dictionary<string, List<string>> Fits()
    {
        if (_fits != null)
        {
            return _fits;
        }

        // the item tree: each node's children, to open a branch into its items
        var children = new Dictionary<string, List<string>>();
        foreach (var (id, item) in templates.Items)
        {
            var parent = item.Parent.ToString();
            if (!children.TryGetValue(parent, out var list))
            {
                children[parent] = list = [];
            }

            list.Add(id.ToString());
        }

        IEnumerable<string> Expand(string id)
        {
            var stack = new Stack<string>([id]);
            var seen = new HashSet<string>();
            while (stack.Count > 0)
            {
                var at = stack.Pop();
                if (!seen.Add(at))
                {
                    continue;
                }

                if (templates.Items.TryGetValue(new MongoId(at), out var t) && t.Type == "Item")
                {
                    yield return at;
                }

                foreach (var child in children.GetValueOrDefault(at) ?? [])
                {
                    stack.Push(child);
                }
            }
        }

        var fits = new Dictionary<string, HashSet<string>>();
        foreach (var (holderId, holder) in templates.Items)
        {
            var props = holder.Properties;
            if (holder.Type != "Item" || props == null)
            {
                continue;
            }

            var holderKey = holderId.ToString();
            var slots = (props.Slots ?? []).Concat(props.Chambers ?? []).Concat(props.Cartridges ?? []);
            foreach (var slot in slots)
            {
                foreach (var filter in slot?.Properties?.Filters ?? [])
                {
                    foreach (var allowed in filter?.Filter ?? [])
                    {
                        foreach (var item in Expand(allowed.ToString()))
                        {
                            if (!fits.TryGetValue(item, out var on))
                            {
                                fits[item] = on = [];
                            }

                            on.Add(holderKey);
                        }
                    }
                }
            }
        }

        return _fits = fits.ToDictionary(f => f.Key, f => f.Value.ToList());
    }

    // The guns and the parts this item fits, each by name. A round lists the guns that chamber it and the magazines
    // that hold it.
    public object? FitsOn(string id, IReadOnlyDictionary<string, string> locale, Dictionary<string, string> names)
    {
        if (!Fits().TryGetValue(id, out var on) || on.Count == 0)
        {
            return null;
        }

        string Name(string item) => names[item] = locale.GetValueOrDefault(item + " Name") is { Length: > 0 } n ? n
            : templates.Items.TryGetValue(new MongoId(item), out var t) ? t.Name ?? item : item;
        // guns and weapon parts only: a backpack or the character's own kit holds a gun too, which says nothing
        var guns = on.Where(IsWeapon).OrderBy(Name).ToList();
        var parts = on.Where(h => !IsWeapon(h) && IsUnder(h, ModNode)).OrderBy(Name).ToList();
        if (guns.Count + parts.Count == 0)
        {
            return null;
        }
        return new { guns, parts };
    }
}
