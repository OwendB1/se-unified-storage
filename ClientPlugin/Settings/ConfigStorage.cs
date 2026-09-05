using System;
using System.IO;
using System.Xml.Serialization;
using VRage.FileSystem;
using VRage.Utils;

namespace ClientPlugin.Settings;

public static class ConfigStorage
{
    private static readonly string ConfigFileName = string.Concat(Plugin.Name, ".cfg");
    private static string ConfigFilePath => Path.Combine(MyFileSystem.UserDataPath, "Storage", "UnifiedStorage", "Config.xml");

    public static void Save(Config config)
    {
        var path = ConfigFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var temporary = path + ".tmp";
        using (var text = File.CreateText(temporary))
            new XmlSerializer(typeof(Config)).Serialize(text, config);
        if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
        else File.Move(temporary, path);
        var legacy = Path.Combine(MyFileSystem.UserDataPath, "Storage", ConfigFileName);
        if (File.Exists(legacy))
        {
            var backups = Path.Combine(Path.GetDirectoryName(path), "Backups");
            Directory.CreateDirectory(backups);
            File.Move(legacy, Path.Combine(backups, ConfigFileName + "." + Guid.NewGuid().ToString("N") + ".bak"));
        }
    }

    public static Config Load()
    {
        var path = ConfigFilePath;
        if (!File.Exists(path)) path = Path.Combine(MyFileSystem.UserDataPath, "Storage", ConfigFileName);
        if (!File.Exists(path))
        {
            return new Config();
        }

        var xmlSerializer = new XmlSerializer(typeof(Config));
        try
        {
            Config loaded;
            using (var streamReader = File.OpenText(path))
                loaded = (Config)xmlSerializer.Deserialize(streamReader) ?? new Config();
            if (path != ConfigFilePath)
            {
                try { Save(loaded); }
                catch (Exception) { MyLog.Default.Warning("UnifiedStorage: could not migrate config; using the legacy settings without changing them."); }
            }
            return loaded;
        }
        catch (Exception)
        {
            MyLog.Default.Warning($"{ConfigFileName}: Failed to read config file: {ConfigFilePath}");
        }
            
        return new Config();
    }
        
}
