using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace TodoEdge
{
    public class TodoItem : INotifyPropertyChanged
    {
        private string _title = "";
        private string? _person;
        private DateTime? _due;
        private string? _tag;
        private bool _isEditing;

        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Title
        {
            get => _title;
            set { _title = value; OnChanged(); }
        }

        public string? Notes { get; set; }

        public string? Person
        {
            get => _person;
            set { _person = value; OnChanged(); Refresh(); }
        }

        public DateTime? Due
        {
            get => _due;
            set { _due = value?.Date; OnChanged(); Refresh(); }
        }

        public string? Tag
        {
            get => _tag;
            set { _tag = string.IsNullOrWhiteSpace(value) ? null : value; OnChanged(); OnChanged(nameof(TagBrush)); Refresh(); }
        }

        public string? SourceText { get; set; }
        public string? SourceImage { get; set; }
        public DateTime Created { get; set; } = DateTime.Now;
        public DateTime? Completed { get; set; }

        [JsonIgnore]
        public bool IsEditing
        {
            get => _isEditing;
            set { _isEditing = value; OnChanged(); }
        }

        [JsonIgnore]
        public bool IsOverdue => Due.HasValue && Due.Value.Date < DateTime.Today && Completed == null;

        [JsonIgnore]
        public bool HasSource => !string.IsNullOrEmpty(SourceImage) || !string.IsNullOrEmpty(SourceText);

        [JsonIgnore]
        public Brush TagBrush => TagPalette.BrushFor(Tag);

        [JsonIgnore]
        public string Subtitle
        {
            get
            {
                var parts = new List<string>();
                if (Due.HasValue)
                {
                    var d = Due.Value.Date;
                    if (d == DateTime.Today) parts.Add("Due today");
                    else if (d == DateTime.Today.AddDays(1)) parts.Add("Due tomorrow");
                    else if (d < DateTime.Today) parts.Add("Overdue " + d.ToString("dd/MM/yyyy"));
                    else parts.Add("Due " + d.ToString("ddd dd/MM/yyyy"));
                }
                if (!string.IsNullOrWhiteSpace(Person)) parts.Add(Person!);
                if (!string.IsNullOrWhiteSpace(Tag)) parts.Add(Tag!);
                return string.Join("  ·  ", parts);
            }
        }

        [JsonIgnore]
        public bool HasSubtitle => Subtitle.Length > 0;

        public void Refresh()
        {
            OnChanged(nameof(Subtitle));
            OnChanged(nameof(HasSubtitle));
            OnChanged(nameof(IsOverdue));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    internal static class TagPalette
    {
        private static readonly Color[] Colours =
        {
            Color.FromRgb(0x25, 0x63, 0xEB), // blue
            Color.FromRgb(0x16, 0xA3, 0x4A), // green
            Color.FromRgb(0xD9, 0x77, 0x06), // amber
            Color.FromRgb(0x93, 0x33, 0xEA), // purple
            Color.FromRgb(0xDB, 0x27, 0x77), // pink
            Color.FromRgb(0x0D, 0x94, 0x88), // teal
        };

        public static List<string> Tags { get; set; } = new List<string>();

        public static Brush BrushFor(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return Brushes.Transparent;
            int idx = Tags.FindIndex(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) idx = tag.Sum(c => (int)c) % Colours.Length; // stable across runs
            var b = new SolidColorBrush(Colours[idx % Colours.Length]);
            b.Freeze();
            return b;
        }
    }

    public class TaskStore
    {
        private const int MaxCompleted = 300;

        public ObservableCollection<TodoItem> Open { get; } = new ObservableCollection<TodoItem>();
        public ObservableCollection<TodoItem> Done { get; } = new ObservableCollection<TodoItem>();

        public event Action? Changed;

        private class FileModel
        {
            public List<TodoItem> Open { get; set; } = new List<TodoItem>();
            public List<TodoItem> Done { get; set; } = new List<TodoItem>();
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };

        public void Load()
        {
            Open.Clear();
            Done.Clear();
            if (!File.Exists(Paths.TasksFile)) return;
            try
            {
                var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(Paths.TasksFile));
                if (model == null) return;
                foreach (var i in model.Open) Open.Add(i);
                foreach (var i in model.Done) Done.Add(i);
            }
            catch
            {
                // Keep a copy of an unreadable file rather than overwrite it.
                File.Copy(Paths.TasksFile, Paths.TasksFile + ".bad-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
            }
        }

        public void Save()
        {
            Paths.Ensure();
            var model = new FileModel { Open = Open.ToList(), Done = Done.ToList() };
            Paths.WriteAllTextSafe(Paths.TasksFile, JsonSerializer.Serialize(model, JsonOptions));
            Changed?.Invoke();
        }

        public void Add(TodoItem item)
        {
            Open.Add(item);
            Save();
        }

        /// <summary>Marks an item done and returns its old position, for undo.</summary>
        public int Complete(TodoItem item)
        {
            int index = Open.IndexOf(item);
            Open.Remove(item);
            item.Completed = DateTime.Now;
            item.IsEditing = false;
            Done.Insert(0, item);
            while (Done.Count > MaxCompleted) Done.RemoveAt(Done.Count - 1);
            item.Refresh();
            Save();
            return index;
        }

        public void Restore(TodoItem item, int index = -1)
        {
            Done.Remove(item);
            item.Completed = null;
            if (index >= 0 && index <= Open.Count) Open.Insert(index, item);
            else Open.Add(item);
            item.Refresh();
            Save();
        }

        public void MoveToTop(TodoItem item)
        {
            int index = Open.IndexOf(item);
            if (index > 0) Open.Move(index, 0);
            Save();
        }

        public void Delete(TodoItem item)
        {
            Open.Remove(item);
            Done.Remove(item);
            Save();
        }

        public void ClearDone()
        {
            Done.Clear();
            Save();
        }

        public void RefreshAll()
        {
            foreach (var i in Open) i.Refresh();
            Changed?.Invoke();
        }
    }
}
