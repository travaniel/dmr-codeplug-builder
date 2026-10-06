using System;
using System.Collections.Generic;
using System.IO;
using CodeplugBuilder.Core;

namespace CodeplugBuilder.App
{
    /// <summary>The open project, where it lives on disk, and the CPS format in use.</summary>
    sealed class Session
    {
        public Project Project { get; private set; } = new Project();
        public string FilePath { get; private set; }
        public bool Dirty { get; private set; }
        public CpsFormat Format { get; set; }

        /// <summary>Something in the project changed (any field, any page).</summary>
        public event EventHandler Changed;
        /// <summary>A different project was loaded or created; every page should rebind.</summary>
        public event EventHandler Replaced;
        /// <summary>The talkgroup list changed (names, IDs, add/remove).</summary>
        public event EventHandler TalkgroupsChanged;

        public string DisplayName => FilePath == null ? "Untitled" : Path.GetFileNameWithoutExtension(FilePath);

        public void MarkDirty()
        {
            Dirty = true;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void NotifyTalkgroupsChanged()
        {
            TalkgroupsChanged?.Invoke(this, EventArgs.Empty);
            MarkDirty();
        }

        public void Replace(Project project, string path, bool dirty)
        {
            project.Normalize();
            Project = project;
            FilePath = path;
            Dirty = dirty;
            Replaced?.Invoke(this, EventArgs.Empty);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Save(string path)
        {
            Project.SyncZones();
            ProjectStore.Save(Project, path);
            FilePath = path;
            Dirty = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Small key=value settings file in %APPDATA%\DMR Codeplug Builder.</summary>
    static class AppSettings
    {
        /// <summary>%APPDATA%\DMR Codeplug Builder, or CODEPLUGBUILDER_SETTINGS when set (tests use a throwaway folder).</summary>
        public static string Folder =>
            Environment.GetEnvironmentVariable("CODEPLUGBUILDER_SETTINGS") is string custom && custom.Length > 0
                ? custom
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DMR Codeplug Builder");
        public static string FormatFolder => Path.Combine(Folder, "CPS Format");
        static string FilePath => Path.Combine(Folder, "settings.txt");

        public static string DocumentsFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DMR Codeplug Builder");

        static Dictionary<string, string> Read()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(FilePath))
                    foreach (var line in File.ReadAllLines(FilePath))
                    {
                        int i = line.IndexOf('=');
                        if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                    }
            }
            catch { }
            return d;
        }

        public static string Get(string key)
        {
            return Read().TryGetValue(key, out string v) ? v : null;
        }

        public static void Set(string key, string value)
        {
            try
            {
                var d = Read();
                if (value == null) d.Remove(key); else d[key] = value;
                Directory.CreateDirectory(Folder);
                var lines = new List<string>();
                foreach (var kv in d) lines.Add(kv.Key + "=" + kv.Value);
                File.WriteAllLines(FilePath, lines);
            }
            catch { }
        }

        /// <summary>The saved custom CPS format if there is one, else the built-in 6X2 Pro layout.</summary>
        public static CpsFormat LoadFormat()
        {
            try
            {
                if (File.Exists(Path.Combine(FormatFolder, CpsFormat.ChannelFile)))
                    return CpsFormat.FromFolder(FormatFolder);
            }
            catch { }
            return CpsFormat.BuiltIn();
        }
    }
}
