using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Localization;

namespace Rambutan.App.ViewModels;

public enum ToastKind { Info, Error }

/// <summary>One outcome message: text, an optional single action (Undo, Retry), a close button.</summary>
public sealed partial class Toast : ObservableObject
{
    private readonly ToastHostViewModel _host;
    private readonly Func<Task>? _action;

    internal Toast(
        ToastHostViewModel host, string message, ToastKind kind, string? actionLabel, Func<Task>? action)
    {
        _host = host;
        _action = action;
        Message = message;
        Kind = kind;
        ActionLabel = actionLabel;
    }

    public string Message { get; }
    public ToastKind Kind { get; }
    public string? ActionLabel { get; }

    public bool IsError => Kind is ToastKind.Error;
    public bool HasAction => _action is not null;

    /// <summary>What a screen reader announces: the kind, so an error isn't read as news.</summary>
    public string AutomationName => IsError ? Strings.Format("toast.errorName", ("message", Message)) : Message;

    /// <summary>Pause counter: pointer over the toast, keyboard focus in it, or its action running.</summary>
    internal int Holds { get; set; }
    internal IDisposable? Expiry { get; set; }
    internal bool IsClosed { get; set; }

    /// <summary>Runs once the toast has gone, however it went: expired, closed,
    /// pushed out by newer ones, or after its action ran.</summary>
    internal Action? Closed { get; init; }

    /// <summary>Runs the action once, then closes. The toast stays up while the action
    /// runs, so an Undo in flight can't expire out from under the click.</summary>
    [RelayCommand]
    private async Task ActAsync()
    {
        if (_action is null || IsClosed)
            return;

        _host.Hold(this);
        try
        {
            await _action();
        }
        catch (Exception ex)
        {
            // Actions report their own failures; this is the backstop for one that didn't.
            _host.Show(Strings.Format("toast.actionFailed", ("action", ActionLabel), ("error", ex.Message)), ToastKind.Error);
        }
        finally
        {
            _host.Dismiss(this);
        }
    }

    [RelayCommand]
    private void Close() => _host.Dismiss(this);
}

/// <summary>
/// Outcome toasts: the answer to "did that work?". Each toast
/// expires on its own, stacks at most <see cref="MaxVisible"/> deep with the oldest
/// giving way, and pauses while the pointer or keyboard focus is on it. The status
/// strip keeps only standing state (sidecars, session); outcomes never go there.
/// </summary>
public sealed class ToastHostViewModel : ViewModelBase
{
    /// <summary>How long a plain outcome stays up.</summary>
    public static readonly TimeSpan PlainLifetime = TimeSpan.FromSeconds(4);

    /// <summary>Errors, toasts with an action and long messages get longer:
    /// there's more to read, or something to decide.</summary>
    public static readonly TimeSpan LongLifetime = TimeSpan.FromSeconds(8);

    /// <summary>Past this many characters a plain message is two lines, more than
    /// <see cref="PlainLifetime"/> gives time to read.</summary>
    public const int LongMessageLength = 60;

    public const int MaxVisible = 3;

    private readonly Func<TimeSpan, Action, IDisposable> _schedule;

    /// <summary>Newest last: the host stacks upward from the bottom edge.</summary>
    public ObservableCollection<Toast> Toasts { get; } = [];

    public ToastHostViewModel() : this(static (delay, action) => DispatcherTimer.RunOnce(action, delay)) { }

    /// <param name="schedule">Runs a callback once after a delay; disposing cancels it.
    /// Tests pass a manual clock.</param>
    public ToastHostViewModel(Func<TimeSpan, Action, IDisposable> schedule)
    {
        _schedule = schedule;
    }

    /// <summary>Shows an outcome. The same plain message as the newest toast restarts
    /// that toast's timer instead of stacking a copy (a skip burst, a double click).</summary>
    public Toast Show(string message, ToastKind kind = ToastKind.Info)
    {
        if (Toasts.Count > 0 && Toasts[^1] is { HasAction: false } newest &&
            newest.Message == message && newest.Kind == kind)
        {
            if (newest.Holds == 0)
                Arm(newest);
            return newest;
        }

        return Add(new Toast(this, message, kind, null, null));
    }

    /// <summary>Shows an outcome with one action, typically Undo.</summary>
    /// <param name="closed">Runs once the toast has gone, however it went; an
    /// outcome held back for Undo commits here.</param>
    public Toast Offer(string message, string actionLabel, Func<Task> action, Action? closed = null) =>
        Add(new Toast(this, message, ToastKind.Info, actionLabel, action) { Closed = closed });

    public void Dismiss(Toast toast)
    {
        if (toast.IsClosed)
            return;
        toast.IsClosed = true;
        toast.Expiry?.Dispose();
        toast.Expiry = null;
        Toasts.Remove(toast);
        toast.Closed?.Invoke();
    }

    /// <summary>Stops the clock (pointer over it, focus inside it, action running).</summary>
    public void Hold(Toast toast)
    {
        toast.Holds++;
        toast.Expiry?.Dispose();
        toast.Expiry = null;
    }

    /// <summary>Restarts the full lifetime once the last hold lets go: someone who
    /// just moved away from a toast gets the whole time to come back, not a sliver.</summary>
    public void Release(Toast toast)
    {
        if (toast.Holds == 0)
            return;
        toast.Holds--;
        if (toast.Holds == 0 && !toast.IsClosed)
            Arm(toast);
    }

    private Toast Add(Toast toast)
    {
        Toasts.Add(toast);
        while (Toasts.Count > MaxVisible)
            Dismiss(Toasts[0]);
        Arm(toast);
        return toast;
    }

    private void Arm(Toast toast)
    {
        toast.Expiry?.Dispose();
        var lifetime = toast.HasAction || toast.IsError || toast.Message.Length > LongMessageLength
            ? LongLifetime
            : PlainLifetime;
        toast.Expiry = _schedule(lifetime, () => Dismiss(toast));
    }
}
