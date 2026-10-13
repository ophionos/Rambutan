namespace Rambutan.Core.Links;

/// <summary>
/// What a <c>rambutan://</c> link (or a music.youtube.com URL given on the
/// command line) asks for. Every kind only reads and navigates: a web page can
/// fire these links, so none of them deletes, signs out or changes a setting.
/// </summary>
public abstract record DeepLink;

/// <summary><c>rambutan://</c> or <c>rambutan://open</c>: bring the window forward.</summary>
public sealed record OpenAppLink : DeepLink;

public sealed record PlayLink(string VideoId) : DeepLink;

/// <summary><c>rambutan://album/&lt;browseId&gt;</c> (MPRE…).</summary>
public sealed record AlbumLink(string BrowseId) : DeepLink;

/// <summary><c>rambutan://playlist/&lt;playlistId&gt;</c>; the id carries no VL prefix.</summary>
public sealed record PlaylistLink(string PlaylistId) : DeepLink;

/// <summary><c>rambutan://artist/&lt;channelId&gt;</c> (UC…).</summary>
public sealed record ArtistLink(string ChannelId) : DeepLink;

public sealed record SearchLink(string Query) : DeepLink;

/// <summary>Why a link was refused.</summary>
public enum DeepLinkError
{
    /// <summary>Not a link at all, or broken: bad characters, a bad id, too long.</summary>
    Malformed,

    /// <summary>Well formed, but the action is one this version doesn't know.</summary>
    UnknownAction,
}

/// <summary>A parsed link, or why it was refused. <see cref="Action"/> names the
/// refused action for <see cref="DeepLinkError.UnknownAction"/>.</summary>
public sealed record DeepLinkResult(DeepLink? Link, DeepLinkError? Error, string? Action = null)
{
    public bool IsValid => Link is not null;

    internal static DeepLinkResult Ok(DeepLink link) => new(link, null);
    internal static DeepLinkResult Malformed() => new(null, DeepLinkError.Malformed);
    internal static DeepLinkResult Unknown(string action) => new(null, DeepLinkError.UnknownAction, action);
}
