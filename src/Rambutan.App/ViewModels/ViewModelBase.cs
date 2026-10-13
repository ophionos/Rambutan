using CommunityToolkit.Mvvm.ComponentModel;

namespace Rambutan.App.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    /// <summary>
    /// Vertical scroll position of this page's main list, written by
    /// <see cref="Rambutan.App.Controls.ScrollMemory"/> when the page's view is
    /// swapped out and read back when it returns. Views are
    /// rebuilt from their DataTemplate on Back, so the view can't hold it.
    /// </summary>
    public double SavedScrollOffset { get; set; }
}
