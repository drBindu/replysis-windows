using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace InterviewCopilot
{
    /// <summary>
    /// The one screen that stands between a launch and the app, and only when
    /// the Store says an update is mandatory.
    ///
    /// It exists so the user never has to open the Microsoft Store, find the
    /// library, search for Replysis and press Update. They press one button
    /// here and the Store does the rest.
    ///
    /// It is shown before the main window is created, which is the only moment
    /// an update can be installed without interrupting anyone: a running copy is
    /// never asked to close, however old it is. See <see cref="StoreUpdateRules"/>.
    ///
    /// If the update cannot be downloaded the gate opens anyway. Being one
    /// version behind is a smaller problem than being locked out ten minutes
    /// before an interview.
    /// </summary>
    public partial class UpdateRequiredWindow : Window
    {
        /// <summary>True when the user may carry on into the app.</summary>
        internal bool MayContinue { get; private set; }

        private bool _busy;
        private readonly TaskCompletionSource<bool> _decided =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal UpdateRequiredWindow(string? version)
        {
            InitializeComponent();
            ShowVersion(version);
            Closed += (_, _) => _decided.TrySetResult(true);
        }

        /// <summary>
        /// The handle the Store needs to own its own dialogs. Forced into
        /// existence rather than waited for, because the check runs while the
        /// window is still invisible.
        /// </summary>
        internal IntPtr Handle => new WindowInteropHelper(this).EnsureHandle();

        /// <summary>Names the versions once the check knows what is waiting.</summary>
        internal void ShowVersion(string? version)
        {
            VersionLine.Text = string.IsNullOrWhiteSpace(version)
                ? $"Installed {AppUpdates.CurrentVersion}"
                : $"Installed {AppUpdates.CurrentVersion}, update {version} available";
        }

        /// <summary>Completes when the window closes, whatever the user chose.</summary>
        internal Task WaitForDecisionAsync() => _decided.Task;

        private async void UpdateBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _busy = true;

            UpdateBtn.IsEnabled = false;
            QuitBtn.IsEnabled = false;
            ErrorText.Visibility = Visibility.Collapsed;
            ProgressArea.Visibility = Visibility.Visible;

            var progress = new Progress<double>(fraction =>
            {
                Bar.Value = fraction;
                ProgressText.Text = fraction >= 0.999 ? "Installing" : $"Downloading {fraction:P0}";
            });

            // This usually does not return: the Store closes the app to swap the
            // package in. Reaching the next line means it could not be done.
            bool ok = await StoreUpdateService.DownloadAndInstallAsync(Handle, progress);

            if (ok)
            {
                // The package went in but this process survived it. Carrying on
                // would leave the user running the code they were just told they
                // could not use, so close instead and let them open the new one.
                MayContinue = false;
                Close();
                return;
            }

            ProgressArea.Visibility = Visibility.Collapsed;
            ErrorText.Text = "The update could not be installed. You can open the Microsoft Store "
                           + "to install it manually, or continue with this version for now.";
            ErrorText.Visibility = Visibility.Visible;
            StoreBtn.Visibility = Visibility.Visible;
            QuitBtn.IsEnabled = true;

            // The gate must not become a locked door.
            UpdateBtn.Content = "Continue";
            UpdateBtn.Click -= UpdateBtn_Click;
            UpdateBtn.Click += ContinueBtn_Click;
            UpdateBtn.IsEnabled = true;
            _busy = false;
        }

        private void ContinueBtn_Click(object sender, RoutedEventArgs e)
        {
            MayContinue = StoreUpdateRules.ContinueWhenUpdateCannotBeDone();
            Close();
        }

        private void StoreBtn_Click(object sender, RoutedEventArgs e) => StoreUpdateService.OpenStoreListing();

        private void QuitBtn_Click(object sender, RoutedEventArgs e)
        {
            MayContinue = false;
            Close();
        }
    }
}
