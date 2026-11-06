using System.Reflection;
using Microsoft.AspNetCore.Http;
using SPTarkov.Reflection.Patching;

namespace Tushonkapedia.Server;

// SPT writes "[Client Request] <path>" to the console for every request (logRequests in http.json). One page view
// asks for dozens of item pictures, map tiles, trader faces and quest pictures, and the game plugin asks the tunnel
// every 20 seconds, which buries everything else. This skips that line for the wiki's own paths, and for SPT's
// pictures when the wiki page asked for them (the browser says which page with Referer; the game never sends one).
// Every other request is still logged.
public class QuietRequests : AbstractPatch
{
    protected override MethodBase GetTargetMethod() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("SPTarkov.Server.Middleware.SptLoggerMiddleware", false))
            .FirstOrDefault(type => type != null)
            ?.GetMethod("LogRequest", BindingFlags.Instance | BindingFlags.Public)
        ?? throw new MissingMethodException("SptLoggerMiddleware", "LogRequest");

    [PatchPrefix]
    public static bool Prefix(HttpContext context) =>
        !IsWiki(context.Request.Path)
        && !(Uri.TryCreate(context.Request.Headers.Referer.ToString(), UriKind.Absolute, out var from) && IsWiki(new PathString(from.AbsolutePath)));

    private static bool IsWiki(PathString path) =>
        path.StartsWithSegments(WikiRoute.Root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments(WikiRoute.Alias, StringComparison.OrdinalIgnoreCase);
}
