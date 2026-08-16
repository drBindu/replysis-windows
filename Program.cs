using System;
using Velopack;

namespace InterviewCopilot
{
    /// <summary>
    /// The process entry point, written by hand rather than generated from
    /// App.xaml, for one reason: the updater's hooks have to run before anything
    /// else in the process does.
    ///
    /// Windows starts this same executable to install, to update, and to
    /// uninstall Replysis. In those runs the first call below does the work and
    /// ends the process. If any of the app's own startup ran ahead of it, that
    /// code would be running during an install or an uninstall, where it has no
    /// business running at all.
    /// </summary>
    internal static class Program
    {
        [STAThread]
        internal static void Main()
        {
            // Written out here rather than behind a helper on purpose: Velopack
            // checks at package time that this call sits in the entry point, and
            // moving it anywhere else fails that check.
            try
            {
                VelopackApp.Build().Run();
            }
            catch
            {
                // A broken updater must never stop the app from starting. Nothing
                // is logged: the app, and its logging, do not exist yet.
            }

            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
    }
}
