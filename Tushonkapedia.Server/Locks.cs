using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tushonkapedia.Server;

// Passwords that lock a character against changes from the wiki: selling, repairing, crafts, upgrades and the
// wishlist. Only a salted hash is kept (PBKDF2, SHA-256), in locks.json in the mod's folder. Unlocking hands the
// browser a key that's signed with the server's secret and the password's hash, so it lasts 30 days, survives a
// restart and stops working when the password changes or goes.
//
// Forgot a password: stop the server, delete that character's entry from locks.json, start it again.
internal sealed class Locks
{
    private const int Rounds = 210_000;
    private static readonly TimeSpan KeyLife = TimeSpan.FromDays(30);

    private sealed class Entry
    {
        public string Salt { get; set; } = "";
        public string Hash { get; set; } = "";
    }

    private sealed class Store
    {
        public string Secret { get; set; } = "";
        public Dictionary<string, Entry> Profiles { get; set; } = [];
    }

    private readonly string _file;
    private readonly object _lock = new();
    private Store? _store;
    // failed tries per character, so a password can't be guessed quickly
    private readonly Dictionary<string, (int Count, DateTime Until)> _fails = [];

    public Locks(string folder)
    {
        _file = Path.Combine(folder, "locks.json");
    }

    private Store Data
    {
        get
        {
            if (_store != null)
            {
                return _store;
            }

            try
            {
                _store = File.Exists(_file) ? JsonSerializer.Deserialize<Store>(File.ReadAllText(_file)) : null;
            }
            catch (Exception)
            {
                _store = null;
            }

            _store ??= new Store();
            if (_store.Secret.Length == 0)
            {
                _store.Secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                Save();
            }

            return _store;
        }
    }

    private void Save()
    {
        var temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_store, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _file, true);
    }

    public bool IsLocked(string profile)
    {
        lock (_lock)
        {
            return Data.Profiles.ContainsKey(profile);
        }
    }

    private static byte[] HashOf(string password, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(password, salt, Rounds, HashAlgorithmName.SHA256, 32);

    // whether the key from the browser opens this character
    public bool KeyFits(string profile, string? key)
    {
        lock (_lock)
        {
            if (!Data.Profiles.TryGetValue(profile, out var entry))
            {
                return true;
            }

            var parts = (key ?? "").Split('.');
            if (parts.Length != 2 || !long.TryParse(parts[0], out var until) || DateTimeOffset.FromUnixTimeSeconds(until) < DateTimeOffset.UtcNow)
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(profile, until, entry)), Encoding.ASCII.GetBytes(parts[1]));
        }
    }

    private string Sign(string profile, long until, Entry entry)
    {
        using var mac = new HMACSHA256(Convert.FromBase64String(Data.Secret));
        return Convert.ToHexString(mac.ComputeHash(Encoding.UTF8.GetBytes($"{profile}|{until}|{entry.Hash}")));
    }

    private string KeyFor(string profile, Entry entry)
    {
        var until = DateTimeOffset.UtcNow.Add(KeyLife).ToUnixTimeSeconds();
        return $"{until}.{Sign(profile, until, entry)}";
    }

    // null when the password is right, otherwise why not
    private string? Check(string profile, string password)
    {
        if (!Data.Profiles.TryGetValue(profile, out var entry))
        {
            return null;
        }

        if (_fails.TryGetValue(profile, out var fail) && fail.Until > DateTime.UtcNow)
        {
            return $"Too many wrong tries. Wait {Math.Ceiling((fail.Until - DateTime.UtcNow).TotalSeconds)} seconds.";
        }

        if (CryptographicOperations.FixedTimeEquals(HashOf(password, Convert.FromBase64String(entry.Salt)), Convert.FromBase64String(entry.Hash)))
        {
            _fails.Remove(profile);
            return null;
        }

        // after 3 wrong tries, a wait that doubles each time, up to 10 minutes
        var count = fail.Count + 1;
        _fails[profile] = (count, count < 3 ? DateTime.MinValue : DateTime.UtcNow.AddSeconds(Math.Min(600, 15 * Math.Pow(2, count - 3))));
        return "Wrong password.";
    }

    public object Unlock(string profile, string password)
    {
        lock (_lock)
        {
            if (!Data.Profiles.TryGetValue(profile, out var entry))
            {
                return new { ok = true, key = "" };
            }

            return Check(profile, password) is { } why ? new { ok = false, why } : new { ok = true, key = KeyFor(profile, entry) };
        }
    }

    // sets or changes the password; changing needs the old one
    public object Set(string profile, string current, string password)
    {
        if (password.Length < 4 || password.Length > 200)
        {
            return new { ok = false, why = "Use 4 to 200 characters." };
        }

        lock (_lock)
        {
            if (Check(profile, current) is { } why)
            {
                return new { ok = false, why };
            }

            var salt = RandomNumberGenerator.GetBytes(16);
            var entry = new Entry { Salt = Convert.ToBase64String(salt), Hash = Convert.ToBase64String(HashOf(password, salt)) };
            Data.Profiles[profile] = entry;
            Save();
            return new { ok = true, key = KeyFor(profile, entry) };
        }
    }

    public object Clear(string profile, string current)
    {
        lock (_lock)
        {
            if (Check(profile, current) is { } why)
            {
                return new { ok = false, why };
            }

            if (Data.Profiles.Remove(profile))
            {
                Save();
            }

            return new { ok = true };
        }
    }
}
