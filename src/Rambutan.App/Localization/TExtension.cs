using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;

namespace Rambutan.App.Localization;

/// <summary>
/// XAML access to <see cref="Strings"/>: <c>Text="{l:T settings.title}"</c>, with
/// <c>xmlns:l="using:Rambutan.App.Localization"</c>. Also works in style setters.
/// <para>
/// The result is a binding to an observable, not a string, so a language change
/// in Settings re-renders every localized string at once. No reflection: the
/// observable is plain C#, and <see cref="Arg"/> takes a compiled binding.
/// </para>
/// <para>
/// One bound argument fills a placeholder: <c>{l:T common.goTo, Arg={Binding
/// Album}}</c> with <c>"common.goTo": "Go to {value}"</c>. The placeholder is
/// <c>{value}</c> unless <see cref="ArgName"/> says otherwise.
/// </para>
/// </summary>
public sealed class TExtension : MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string key) => Key = key;

    public string Key { get; set; } = "";

    /// <summary>A binding whose value fills <see cref="ArgName"/>.</summary>
    [AssignBinding]
    public BindingBase? Arg { get; set; }

    public string ArgName { get; set; } = "value";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var language = new LanguageObservable().ToBinding();
        if (Arg is null)
            return new MultiBinding
            {
                Bindings = { language },
                Converter = new KeyConverter(Key, null),
            };

        return new MultiBinding
        {
            Bindings = { language, Arg },
            Converter = new KeyConverter(Key, ArgName),
        };
    }

    private sealed class KeyConverter(string key, string? argName) : IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (argName is null)
                return Strings.Get(key);

            var arg = values.Count > 1 ? values[1] : null;
            if (arg is UnsetValueType or BindingNotification)
                arg = "";
            return Strings.FormatCore(key, [new KeyValuePair<string, object?>(argName, arg)]);
        }
    }
}

/// <summary>
/// Emits <see cref="Strings.Version"/> now and after every language change.
/// Observers are held weakly: a binding lives as long as its control, and pages
/// are rebuilt on every navigation, so a strong static subscription would keep
/// every page ever shown alive. Boxed (<c>object?</c>) so Avalonia subscribes its
/// binding expression directly rather than through a wrapper only this list holds.
/// </summary>
internal sealed class LanguageObservable : IObservable<object?>
{
    private static readonly List<WeakReference<IObserver<object?>>> Observers = [];

    static LanguageObservable() => Strings.Changed += Publish;

    public IDisposable Subscribe(IObserver<object?> observer)
    {
        var entry = new WeakReference<IObserver<object?>>(observer);
        lock (Observers)
        {
            // Dead entries otherwise wait for a language change to be swept.
            if (Observers.Count % 256 == 255)
                Observers.RemoveAll(o => !o.TryGetTarget(out _));
            Observers.Add(entry);
        }

        observer.OnNext(Strings.Version);
        return new Unsubscriber(entry);
    }

    internal static int LiveCount
    {
        get
        {
            lock (Observers)
                return Observers.Count(o => o.TryGetTarget(out _));
        }
    }

    private static void Publish()
    {
        IObserver<object?>[] live;
        lock (Observers)
        {
            Observers.RemoveAll(o => !o.TryGetTarget(out _));
            live = [.. Observers.Select(o => o.TryGetTarget(out var t) ? t : null).OfType<IObserver<object?>>()];
        }

        foreach (var observer in live)
            observer.OnNext(Strings.Version);
    }

    private sealed class Unsubscriber(WeakReference<IObserver<object?>> entry) : IDisposable
    {
        public void Dispose()
        {
            lock (Observers)
                Observers.Remove(entry);
        }
    }
}
