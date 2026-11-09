using System;
using System.IO;

namespace TodoEdge
{
    internal static class Paths
    {
        public static string Root { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TodoEdge");

        public static string Sources => Path.Combine(Root, "sources");
        public static string TasksFile => Path.Combine(Root, "tasks.json");
        public static string SettingsFile => Path.Combine(Root, "settings.json");

        public static void Ensure()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Sources);
        }

        /// <summary>Write via a temp file so a crash never leaves a half-written file.</summary>
        public static void WriteAllTextSafe(string path, string contents)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, contents);
            File.Move(tmp, path, overwrite: true);
        }
    }
}
