using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace CodeplugBuilder.Core
{
    /// <summary>Saves and loads projects as readable, indented JSON (.cpb files).</summary>
    public static class ProjectStore
    {
        public const string Extension = ".cpb";
        public const string FileFilter = "W6OZZ CPS project (*.cpb)|*.cpb|All files (*.*)|*.*";

        static DataContractJsonSerializer Serializer()
        {
            return new DataContractJsonSerializer(typeof(Project));
        }

        public static string ToJson(Project project)
        {
            using (var ms = new MemoryStream())
            {
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(ms, Encoding.UTF8, false, true, "  "))
                {
                    Serializer().WriteObject(writer, project);
                    writer.Flush();
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        public static Project FromJson(string json)
        {
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                var p = (Project)Serializer().ReadObject(ms);
                p.Normalize();
                return p;
            }
        }

        public static void Save(Project project, string path)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // Write to a temp file first so a crash mid-save can't destroy the project.
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, ToJson(project), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public static Project Load(string path)
        {
            return FromJson(File.ReadAllText(path, Encoding.UTF8));
        }
    }
}
