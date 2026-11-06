namespace Tushonkapedia.Server;

// Work handed to one game at a time. With several players on one server (Fika), every game asks for the same
// missing pictures and map places; without this, each would draw all of them at once and send the same pictures
// several times. A game gets what nobody else holds, and holds it while it keeps sending (each send renews it);
// what a game leaves undone (it closed, or a raid started) goes to the next one that asks once the hold runs out.
internal sealed class Claims(TimeSpan hold)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (string Session, DateTime Until)> _held = new(StringComparer.Ordinal);

    // the keys this session may work on, now held by it
    public List<string> Take(string session, IEnumerable<string> keys)
    {
        var now = DateTime.UtcNow;
        var mine = new List<string>();
        lock (_lock)
        {
            foreach (var key in keys)
            {
                if (_held.TryGetValue(key, out var held) && held.Session != session && held.Until > now)
                {
                    continue;
                }

                _held[key] = (session, now + hold);
                mine.Add(key);
            }
        }

        return mine;
    }

    // the session is still at it: everything it holds is held for longer
    public void Renew(string session)
    {
        var until = DateTime.UtcNow + hold;
        lock (_lock)
        {
            foreach (var key in _held.Where(kv => kv.Value.Session == session).Select(kv => kv.Key).ToList())
            {
                _held[key] = (session, until);
            }
        }
    }
}
