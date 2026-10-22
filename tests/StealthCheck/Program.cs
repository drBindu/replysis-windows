// Proves StealthSelfTest tells a visible window from a hidden one on this machine.
using System.Windows;
using InterviewCopilot;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var lines = new System.Collections.Generic.List<string>();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int code = 0;
        app.Startup += async (_, _) =>
        {
            var visible = await StealthSelfTest.RunAsync(applyHiding: false);
            lines.Add("  " + StealthSelfTest.LastDetail);
            var hidden = await StealthSelfTest.RunAsync(applyHiding: true);
            lines.Add("  " + StealthSelfTest.LastDetail);
            lines.Add($"without hiding: {visible} (want Visible)");
            lines.Add($"with hiding:    {hidden} (want Hidden)");
            code = visible == StealthSelfTest.Result.Visible && hidden == StealthSelfTest.Result.Hidden ? 0 : 1;
            lines.Add(code == 0 ? "PASS" : "FAIL");
            // Diagnostic: a 60 px square at (120,120) DIPs, the region around it saved as a picture.
            var w = new Window { Width = 60, Height = 60, Left = 120, Top = 120, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, Topmost = true, Background = System.Windows.Media.Brushes.Magenta };
            w.Show();
            await System.Threading.Tasks.Task.Delay(600);
            var src = PresentationSource.FromVisual(w);
            double sc = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            lines.Add($"diag scale {sc}");
            int gx = (int)(100 * sc), gy = (int)(100 * sc), gs = (int)(100 * sc);
            byte[] px = ScreenAnalyzer.GrabPixels(gx, gy, gs, gs);
            var bmp = System.Windows.Media.Imaging.BitmapSource.Create(gs, gs, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, gs * 4);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using (var f = System.IO.File.Create(@"C:\Users\krish\Desktop\replysis-user-flow\stealth-diag.png")) enc.Save(f);
            w.Close();
            if (args.Length > 0) System.IO.File.WriteAllLines(args[0], lines); else lines.ForEach(Console.WriteLine);
            app.Shutdown();
        };
        app.Run();
        return code;
    }
}
