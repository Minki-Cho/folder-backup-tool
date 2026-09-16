using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace DdalggakBackup
{
    public static class SettingsStore
    {
        public static string DataDirectory
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DdalggakBackup"); }
        }
        public static AppSettings Defaults()
        {
            return new AppSettings
            {
                SelectedProfile = 0, LastName = "",
                Profiles = new List<MachineProfile>
                {
                    new MachineProfile { Name = "1호기", SourceRoot = @"", BackupRoot = @"", ItemNames = PathSafety.ValidateItemNames(null) },
                    new MachineProfile { Name = "2호기", SourceRoot = @"", BackupRoot = @"", ItemNames = PathSafety.ValidateItemNames(null) }
                }
            };
        }
        public static AppSettings Load(out string warning)
        {
            return LoadFrom(DataDirectory, out warning);
        }
        public static AppSettings LoadFrom(string directory, out string warning)
        {
            warning = null;
            string path = Path.Combine(directory, "settings.xml");
            if (!File.Exists(path)) return Defaults();
            try
            {
                var xmlSettings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using (XmlReader reader = XmlReader.Create(path, xmlSettings))
                {
                    var settings = (AppSettings)new XmlSerializer(typeof(AppSettings)).Deserialize(reader);
                    if (settings == null || settings.Profiles == null || settings.Profiles.Count != 2 || settings.Profiles[0] == null || settings.Profiles[1] == null)
                        throw new InvalidDataException("두 호기의 경로 설정을 읽을 수 없습니다.");
                    if (settings.SelectedProfile < 0 || settings.SelectedProfile >= settings.Profiles.Count) settings.SelectedProfile = 0;
                    foreach (MachineProfile profile in settings.Profiles)
                        profile.ItemNames = PathSafety.ValidateItemNames(profile.ItemNames);
                    return settings;
                }
            }
            catch (Exception error)
            {
                warning = "설정 파일을 읽을 수 없습니다. 경로·항목 설정에서 확인 후 저장해 주세요. " + error.Message;
                var settings = Defaults();
                // A corrupt saved configuration must not silently target a different machine.
                foreach (var profile in settings.Profiles) { profile.SourceRoot = ""; profile.BackupRoot = ""; }
                return settings;
            }
        }
        public static void Save(AppSettings settings) { SaveTo(settings, DataDirectory); }
        public static void SaveTo(AppSettings settings, string directory)
        {
            if (settings == null || settings.Profiles == null || settings.Profiles.Count != 2 || settings.Profiles[0] == null || settings.Profiles[1] == null)
                throw new ArgumentException("두 호기의 경로·항목 설정이 필요합니다.");
            foreach (MachineProfile profile in settings.Profiles)
                profile.ItemNames = PathSafety.ValidateItemNames(profile.ItemNames);
            EnsureSettingsOutsideSources(settings, directory);
            Directory.CreateDirectory(directory);
            string destination = Path.Combine(directory, "settings.xml");
            string temp = Path.Combine(directory, "settings-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                EnsureSettingsOutsideSources(settings, temp);
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
                    new XmlSerializer(typeof(AppSettings)).Serialize(writer, settings);
                EnsureSettingsOutsideSources(settings, destination);
                if (File.Exists(destination)) File.Replace(temp, destination, null);
                else File.Move(temp, destination);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        private static void EnsureSettingsOutsideSources(AppSettings settings, string path)
        {
            string target = PathSafety.Normalize(path);
            string actualTarget = PathSafety.Canonicalize(target);
            foreach (MachineProfile profile in settings.Profiles)
            {
                if (String.IsNullOrWhiteSpace(profile.SourceRoot)) continue;
                string source = PathSafety.Normalize(profile.SourceRoot);
                // Settings must remain saveable while equipment UNC shares are offline.
                // Check their explicit namespace without opening the remote shares.
                // Resolve local source aliases as well as the settings destination.
                bool resolveLocal = !source.StartsWith(@"\\", StringComparison.Ordinal);
                if (resolveLocal)
                {
                    DriveType type = new DriveInfo(Path.GetPathRoot(source)).DriveType;
                    resolveLocal = type != DriveType.Network && type != DriveType.NoRootDirectory;
                }
                var names = new HashSet<string>(profile.ItemNames, StringComparer.OrdinalIgnoreCase);
                names.UnionWith(Catalog.Items);
                foreach (string item in names)
                {
                    string sourceItem = Path.Combine(source, item);
                    bool inside = PathSafety.IsWithin(target, sourceItem) || PathSafety.IsWithin(actualTarget, sourceItem);
                    if (!inside && resolveLocal)
                        inside = PathSafety.IsWithin(actualTarget, PathSafety.Canonicalize(sourceItem));
                    if (inside)
                        throw new IOException("설정 저장 위치가 원본 항목 안에 있습니다. 원본 보호를 위해 원본 경로나 항목 이름을 수정해 주세요: " + sourceItem);
                }
            }
        }
    }
}
