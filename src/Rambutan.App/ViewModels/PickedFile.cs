namespace Rambutan.App.ViewModels;

/// <summary>A file the user picked. <see cref="LocalPath"/> is null when the
/// picker gave no path on this computer (a portal or a cloud folder that
/// doesn't map to one).</summary>
public sealed record PickedFile(string? LocalPath);
