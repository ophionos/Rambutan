using System;
using System.Threading.Tasks;
using Rambutan.App.Localization;
using Rambutan.Integrations;

namespace Rambutan.App.Services;

/// <summary>
/// Restart now, from the status strip or Settings. Velopack's updater closes
/// every process running from the install, so while another instance runs
/// (another profile or data folder) it asks first; those instances close
/// without saving. Declining leaves the update to install when the last
/// instance quits.
/// </summary>
public sealed class UpdateRestart
{
    private readonly UpdateService _updates;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly Action _restart;

    /// <param name="confirm">Asks the message with the accept button's label;
    /// true on accept.</param>
    /// <param name="restart">Ends the run normally, then installs and starts
    /// the app again.</param>
    public UpdateRestart(UpdateService updates, Func<string, string, Task<bool>> confirm, Action restart)
    {
        _updates = updates;
        _confirm = confirm;
        _restart = restart;
    }

    public async Task RunAsync()
    {
        if (_updates.OtherInstancesRunning()
            && !await _confirm(Strings.Get("update.restartOthers"), Strings.Get("settings.updates.restart")))
        {
            return;
        }
        _restart();
    }
}
