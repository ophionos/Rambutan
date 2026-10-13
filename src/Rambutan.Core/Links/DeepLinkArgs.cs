namespace Rambutan.Core.Links;

/// <summary>
/// Finds a link on the command line. The OS hands a link over as
/// <c>"&lt;exe&gt;" --deep-link "%1"</c> (see the Windows and Linux registrations); a
/// bare link anywhere on the command line also works, for manual runs. Options
/// this class doesn't know are left alone, so other options can sit beside it.
/// </summary>
public static class DeepLinkArgs
{
    public const string Option = "--deep-link";

    /// <summary>
    /// A launch from a link keeps only the link. The caller of <c>%1</c> may not
    /// escape quotes, so a link could otherwise smuggle in options of its own;
    /// everything after the link is dropped before anything reads the arguments.
    /// Other launches are returned as they are.
    /// </summary>
    public static string[] Sanitize(string[] args) =>
        args.Length > 0 && args[0] == Option
            ? args.Length > 1 ? [Option, args[1]] : [Option]
            : args;

    /// <summary>
    /// The link these arguments carry, still to be parsed; null when they carry
    /// none (a plain launch). A <c>--deep-link</c> with nothing after it yields an
    /// empty string, which parses as malformed rather than vanishing.
    /// </summary>
    public static string? Find(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == Option)
                return i + 1 < args.Count ? args[i + 1] : "";
            if (DeepLinkParser.LooksLikeLink(args[i]))
                return args[i];
        }
        return null;
    }
}
