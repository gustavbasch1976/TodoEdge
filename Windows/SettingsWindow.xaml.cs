using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Navigation;

namespace TodoEdge
{
    public partial class SettingsWindow : Window
    {
        private readonly AppSettings _settings;

        public SettingsWindow(AppSettings settings)
        {
            InitializeComponent();
            _settings = settings;

            KeyBox.Password = settings.ApiKey ?? "";
            ModelBox.Text = settings.Model;
            TagsBox.Text = string.Join(", ", settings.Tags);
            StartupBox.IsChecked = StartupHelper.IsEnabled;
            HoverBox.IsChecked = settings.OpenOnHover;
            NameBox.Text = settings.MyName;

            Loaded += (s, e) =>
            {
                Activate();
                if (KeyBox.Password.Length == 0) KeyBox.Focus();
            };
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            string key = KeyBox.Password.Trim();
            if (key.Length > 0 && !key.StartsWith("sk-ant-", StringComparison.Ordinal))
            {
                var answer = MessageBox.Show(this, "That does not look like a Claude API key (they start with sk-ant-). Save anyway?",
                    "TodoEdge", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return;
            }

            _settings.ApiKey = key.Length > 0 ? key : null;
            _settings.OpenOnHover = HoverBox.IsChecked == true;
            _settings.MyName = NameBox.Text.Trim();
            string model = ModelBox.Text.Trim();
            _settings.Model = model.Length > 0 ? model : "claude-haiku-5-5";
            _settings.Tags = TagsBox.Text.Split(',')
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            try
            {
                StartupHelper.Set(StartupBox.IsChecked == true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not change the startup setting: " + ex.Message, "TodoEdge");
            }

            DialogResult = true;
        }

        private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }
    }
}
