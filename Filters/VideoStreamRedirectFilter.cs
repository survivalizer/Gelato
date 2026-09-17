using Gelato.Config;
using Gelato.Services;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

/// <summary>
/// Redirects /Videos/{id}/stream to the real stream URL for items Gelato owns.
///
/// The URL handed out in the PlaybackInfo response is only a suggestion: MediaSourceInfo.Path
/// tells a client it *may* fetch the stream itself, and a client is free to ignore it and ask
/// the server for /Videos/{id}/stream anyway. Jellyfin then relays every byte from the debrid
/// host through itself, so the server's uplink - not the debrid host's - sets the ceiling for
/// playback, and a stream that needs more than that uplink can carry buffers forever. No
/// transcode is involved, so this costs no CPU and shows up nowhere in the logs.
///
/// Following a redirect, by contrast, is not optional for an HTTP client. Answering the
/// streaming endpoint with a 307 puts the bytes on the direct path whatever the client would
/// have chosen, which is how the same setup behaves with proxying turned off in Remux.
///
/// Anything unexpected falls through to Jellyfin's own handler: this must never be the reason
/// a stream stops playing.
/// </summary>
public sealed class VideoStreamRedirectFilter(
    ILibraryManager library,
    GelatoManager manager,
    IUserManager userManager,
    IMediaSourceManager mediaSourceManager,
    ILogger<VideoStreamRedirectFilter> log
) : IAsyncActionFilter, IOrderedFilter
{
    /// <summary>
    /// After PlaybackInfoFilter (3), which is what puts MediaSourceId into HttpContext.Items.
    /// </summary>
    public int Order { get; init; } = 4;

    private static readonly string[] StreamActionNames =
    [
        "GetVideoStream",
        "GetVideoStreamByContainer",
    ];

    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        var url = TryResolveRedirectUrl(ctx);

        if (url is null)
        {
            await next();
            return;
        }

        log.LogDebug(
            "Redirecting stream request for {Item} to the origin host",
            ctx.GetActionName()
        );

        // 307 preserves the method, so a client probing with HEAD stays on HEAD.
        ctx.Result = new RedirectResult(url, permanent: false, preserveMethod: true);
    }

    /// <summary>
    /// The real URL to send the client to, or null to let Jellyfin serve the request. Every
    /// failure path returns null - a broken lookup here must not break playback of media
    /// Gelato does not own.
    /// </summary>
    private string? TryResolveRedirectUrl(ActionExecutingContext ctx)
    {
        try
        {
            if (!IsVideoStreamAction(ctx.GetActionName()))
                return null;

            if (!IsStaticRequest(ctx.HttpContext.Request.Query))
                return null;

            if (!ctx.TryGetRouteGuid(out var guid) || !ctx.TryGetUserId(out var userId))
                return null;

            var user = userManager.GetUserById(userId);
            if (user is null)
                return null;

            var item = ResolveItem(ctx, guid, user);
            if (item is null || !manager.IsStremio(item))
                return null;

            var path = ResolvePath(item, user);

            return ShouldRedirect(
                ctx.GetActionName(),
                isStatic: true,
                GelatoPlugin.Instance?.Configuration,
                userId,
                path
            )
                ? path
                : null;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Stream redirect check failed, falling through to Jellyfin");
            return null;
        }
    }

    private Video? ResolveItem(ActionExecutingContext ctx, Guid routeGuid, User user)
    {
        // Clients that pick a specific version send MediaSourceId; the stream row it names is
        // the item actually being played, not the route id (which is the parent).
        var raw = ctx.HttpContext.Items["MediaSourceId"] as string;
        var id = DownloadFilter.TryGetMediaSourceId(raw, out var mediaSourceId)
            ? mediaSourceId
            : routeGuid;

        try
        {
            return library.GetItemById<Video>(id, user);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string? ResolvePath(Video item, User user)
    {
        if (item.IsStream())
            return item.Path;

        // Clients that send no MediaSourceId land on the parent item, whose own path is a
        // placeholder; its first media source is the stream they would have been given.
        var sources = mediaSourceManager.GetStaticMediaSources(item, true, user);

        return sources.Count > 0 ? sources[0].Path : null;
    }

    public static bool IsVideoStreamAction(string? actionName) =>
        actionName is not null && StreamActionNames.Contains(actionName, StringComparer.Ordinal);

    /// <summary>
    /// Whether the client asked for the file untouched. Without static=true it is asking the
    /// server to transcode or remux, and handing back the original would return exactly the
    /// thing it just said it cannot play.
    /// </summary>
    public static bool IsStaticRequest(IQueryCollection query)
    {
        var raw = query["static"].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(raw))
            return false;

        return raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1";
    }

    /// <summary>
    /// The whole decision in one place: a static read of a stream endpoint, for a path this
    /// user is allowed to direct play and a client could actually reach.
    /// </summary>
    public static bool ShouldRedirect(
        string? actionName,
        bool isStatic,
        PluginConfiguration? cfg,
        Guid userId,
        string? path
    ) =>
        IsVideoStreamAction(actionName)
        && isStatic
        && DirectPlayPolicy.IsAllowed(cfg, userId, path);
}
