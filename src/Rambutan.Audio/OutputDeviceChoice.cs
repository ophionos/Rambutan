namespace Rambutan.Audio;

/// <summary>Which present device a choice by name means.</summary>
public static class OutputDeviceChoice
{
    /// <summary>The index of the device named <paramref name="preferred"/>, or null
    /// when no device is chosen or the chosen one isn't present: then the output
    /// follows the system default. Names match exactly; they are what the OS
    /// shows, and ids aren't stable across runs.</summary>
    public static int? Find(IEnumerable<string?> names, string? preferred)
    {
        if (string.IsNullOrEmpty(preferred))
            return null;
        var i = 0;
        foreach (var name in names)
        {
            if (string.Equals(name, preferred, StringComparison.Ordinal))
                return i;
            i++;
        }
        return null;
    }
}
