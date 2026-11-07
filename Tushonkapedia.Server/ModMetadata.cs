using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Web;

namespace Tushonkapedia.Server;

public record ModMetadata : IModMetadata, IModBlazorMetadata
{
    public string ModGuid { get; init; } = "com.fums.tushonkapedia.server";
    public string Name { get; init; } = "Tushonkapedia";
    public string Author { get; init; } = "Fums";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("1.0.1");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");
    public bool HasPrepatcher { get; init; } = false;
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; } = "https://github.com/MrFums/Tushonkapedia";
    public bool? IsBundleMod { get; init; } = false;
    public string License { get; init; } = "CC-BY-NC-ND-4.0";

    // Lists the wiki under Mod Pages in the launcher and on SPT's web page.
    public string? WWWRootUrl { get; init; }
    public string? HomePage { get; init; } = "/wiki";
    public string? HomePageDescription { get; init; } = "The wiki for this server: quests, items, traders, maps and more.";
}
