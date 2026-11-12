using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;

namespace TodoEdge
{
    public class ReviewItem
    {
        public const string NoTag = "(none)";

        public bool Include { get; set; } = true;
        public string Title { get; set; } = "";
        public string DueText { get; set; } = "";
        public string? Person { get; set; }
        public string? Notes { get; set; }
        public string Tag { get; set; } = NoTag;
        public int PriorityIndex { get; set; } = 1; // 0 = 1 high, 1 = 2 medium, 2 = 3 low
        public List<string> PriorityOptions { get; } = new List<string> { "1 High", "2 Medium", "3 Low" };
        public List<string> TagOptions { get; set; } = new List<string>();

        public string Info
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(Person)) parts.Add("Person: " + Person);
                if (!string.IsNullOrWhiteSpace(Notes)) parts.Add(Notes!);
                return string.Join("   ", parts);
            }
        }

        public bool HasInfo => Info.Length > 0;

        public DateTime? ParseDue()
        {
            string t = DueText.Trim();
            if (t.Length == 0) return null;
            string[] formats = { "dd/MM/yyyy", "d/M/yyyy", "dd/MM/yy", "d/M/yy", "yyyy-MM-dd", "dd-MM-yyyy", "d MMM yyyy" };
            if (DateTime.TryParseExact(t, formats, CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.None, out var d))
                return d.Date;
            return null;
        }
    }

    public partial class ReviewWindow : Window
    {
        private readonly List<ReviewItem> _items;

        public List<ReviewItem> Selected { get; private set; } = new List<ReviewItem>();

        public ReviewWindow(List<ProposedTask> tasks, byte[]? png, List<string> tags, string? sourceName = null)
        {
            InitializeComponent();

            var options = new List<string> { ReviewItem.NoTag };
            options.AddRange(tags);

            _items = tasks.Select(t => new ReviewItem
            {
                Title = t.Title,
                DueText = t.Due?.ToString("dd/MM/yyyy") ?? "",
                Person = t.Person,
                Notes = t.Notes,
                PriorityIndex = t.Priority - 1,
                TagOptions = options
            }).ToList();

            TaskList.ItemsSource = _items;
            Header.Text = (_items.Count == 1 ? "Claude found 1 to-do item" : $"Claude found {_items.Count} to-do items")
                          + (sourceName != null ? " in " + sourceName : "");

            if (png != null)
                Preview.Source = ImageUtil.ToBitmapImage(png);
            else
                PreviewBorder.Visibility = Visibility.Collapsed;

            Loaded += (s, e) => Activate();
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            foreach (var i in _items.Where(i => i.Include))
            {
                if (i.DueText.Trim().Length > 0 && i.ParseDue() == null)
                {
                    MessageBox.Show(this, $"\"{i.DueText}\" is not a date. Use DD/MM/YYYY or leave it blank.",
                        "TodoEdge", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }
            Selected = _items.Where(i => i.Include && i.Title.Trim().Length > 0).ToList();
            DialogResult = true;
        }
    }
}
