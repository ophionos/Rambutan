#if WINDOWS
using Rambutan.Core.Diagnostics;
using Velopack.Locators;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Rambutan.Integrations.Notifications;

/// <summary>
/// Track notifications as Windows toasts. An unpackaged app's toasts show only
/// under the AUMID of a Start menu shortcut, and the one Velopack installs
/// carries it, so only an installed build shows them.
/// </summary>
/// <param name="appUserModelId">The installed shortcut's AUMID; null where
/// there is none, and nothing shows.</param>
public sealed class ToastTrackNotifier(string? appUserModelId) : ITrackNotifier
{
    private const string Tag = "track";
    private const string Group = "now-playing";

    // It describes a song that is over in minutes; Action Center needn't keep it.
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private ToastNotifier? _notifier;
    private ToastNotification? _shown;

    /// <summary>For the installed build's shortcut; one that can't show in a
    /// dev or portable run.</summary>
    public static ToastTrackNotifier ForInstalledApp() => new(InstalledAppUserModelId());

    private static string? InstalledAppUserModelId()
    {
        try
        {
            var locator = VelopackLocator.Current;
            return locator.CurrentlyInstalledVersion is null ? null : locator.AppUserModelId;
        }
        catch (Exception ex)
        {
            AppLog.Write("notify", $"no installed app id: {ex.Message}");
            return null;
        }
    }

    public bool CanShow => appUserModelId is not null;

    public void Show(TrackNotification notification)
    {
        if (appUserModelId is null)
            return;
        try
        {
            _notifier ??= ToastNotificationManager.CreateToastNotifier(appUserModelId);
            if (_notifier.Setting is not NotificationSetting.Enabled)
            {
                AppLog.Write("notify", $"Windows doesn't show Rambutan's notifications ({_notifier.Setting})");
                return;
            }
            Close();
            // The same tag and group replace the last one in Action Center too.
            var toast = new ToastNotification(Build(notification))
            {
                Tag = Tag,
                Group = Group,
                ExpirationTime = DateTimeOffset.Now + Lifetime,
            };
            _notifier.Show(toast);
            _shown = toast;
        }
        catch (Exception ex)
        {
            AppLog.Write("notify", $"toast failed: {ex.Message}");
        }
    }

    public void Close()
    {
        if (_shown is not { } shown || _notifier is null)
            return;
        _shown = null;
        try
        {
            _notifier.Hide(shown);
        }
        catch (Exception ex)
        {
            AppLog.Write("notify", $"couldn't hide the toast: {ex.Message}");
        }
    }

    /// <summary>The toast's XML. Text goes in as text nodes, so a title with
    /// <c>&amp;</c> or <c>&lt;</c> can't break it; silent, as a song is already playing.</summary>
    internal static XmlDocument Build(TrackNotification notification)
    {
        var xml = new XmlDocument();
        xml.LoadXml("<toast><visual><binding template=\"ToastGeneric\"><text/><text/></binding></visual><audio silent=\"true\"/></toast>");
        var texts = xml.GetElementsByTagName("text");
        texts[0].InnerText = notification.Title;
        texts[1].InnerText = notification.Body;
        if (notification.CoverFile is { } cover)
        {
            var image = xml.CreateElement("image");
            image.SetAttribute("placement", "appLogoOverride");
            image.SetAttribute("src", new Uri(cover).AbsoluteUri);
            xml.GetElementsByTagName("binding")[0].AppendChild(image);
        }
        return xml;
    }

    public void Dispose() => Close();
}
#endif
