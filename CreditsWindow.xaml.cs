using System;
using System.Diagnostics;
using System.Windows;

namespace InterviewCopilot
{
    public partial class CreditsWindow : Window
    {
        public CreditsWindow()
        {
            InitializeComponent();
            try { WindowStealth.SetStealthMode(this, SettingsWindow.GetStealthMode()); } catch { }
            RefreshFromSession();
        }

        public void RefreshFromSession()
        {

            string plan = string.IsNullOrWhiteSpace(UserSession.Plan) ? "guest" : UserSession.Plan;
            string planLabel = char.ToUpperInvariant(plan[0]) + plan[1..] + " plan";

            if (UserSession.IsUnlimited)
            {
                CreditsAmountText.Text = "Unlimited";
                AllowanceText.Text = "Unlimited access";
                ResetText.Text = "Not required";
            }
            else
            {
                CreditsAmountText.Text = $"{UserSession.Credits:N0} credits";
                // Must match PLAN_MONTHLY_CREDITS in the website's
                // app/api/stt/tokens/route.ts. lifetime/teams are retired
                // plans, kept only so an existing account still shows its
                // real allowance instead of silently falling to the free tier.
                AllowanceText.Text = plan switch
                {
                    "pro" => "2,000 each month",
                    "max" or "lifetime" => "5,000 each month",
                    "teams" => "10,000 each month",
                    _ => "100 each month"
                };
                ResetText.Text = NextMonthlyReset().ToString("MMM d, yyyy");
            }

            PlanText.Text = UserSession.IsLoggedIn ? planLabel : "Guest plan";
            DeviceNoteText.Visibility = UserSession.IsLoggedIn ? Visibility.Collapsed : Visibility.Visible;
        }

        private static DateTime NextMonthlyReset()
        {
            DateTime now = DateTime.Now;
            return new DateTime(now.Year, now.Month, 1).AddMonths(1);
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        private void PricingBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://replysis.com/pricing")
                {
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }
}
