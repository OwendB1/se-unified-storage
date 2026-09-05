using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;
using ClientPlugin.Inventory;
using Shared.Logging;
using VRage.FileSystem;

namespace ClientPlugin.Profiles;

[XmlRoot("UnifiedStorageProfiles")]
public sealed class LocalProfileDocument
{
    public List<ScopeProfile> Profiles { get; set; } = new();
    public List<DisplayOrderRecord> DisplayOrders { get; set; } = new();
}

public sealed class LocalProfileStore
{
    private readonly IPluginLogger log;
    private readonly string path;
    private readonly string directory;
    private readonly Dictionary<(string World, long Anchor), string> files = new();
    private LocalProfileDocument document;
    private bool loadFailed;
    private DateTime displayOrderSaveAt;

    public LocalProfileStore(IPluginLogger log, string path = null)
    {
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.path = path ?? Path.Combine(MyFileSystem.UserDataPath, "Storage", "UnifiedStorage.profiles.xml");
        directory = Path.Combine(Path.GetDirectoryName(this.path), "UnifiedStorage", "Profiles");
        document = Load();
    }

    public IReadOnlyList<ScopeProfile> Profiles => document.Profiles;
    public List<DisplayOrderRecord> DisplayOrders => document.DisplayOrders;

    public void DisplayOrderChanged()
    {
        if (displayOrderSaveAt == default) displayOrderSaveAt = DateTime.UtcNow.AddSeconds(2);
    }

    public void FlushDisplayOrders()
    {
        if (displayOrderSaveAt == default || DateTime.UtcNow < displayOrderSaveAt || loadFailed) return;
        try { Save(); }
        catch (Exception exception)
        {
            // A private presentation preference must not disable transfers on a full/read-only disk.
            displayOrderSaveAt = DateTime.UtcNow.AddSeconds(30);
            log.Error(exception, "Could not save inventory display order; will retry later");
        }
    }

    public ScopeProfile GetOrCreate(string worldId, MechanicalInventoryScope scope)
    {
        if (scope == null)
            throw new ArgumentNullException(nameof(scope));
        var anchor = scope.Grids.Min(grid => grid.EntityId);
        var profile = document.Profiles.FirstOrDefault(candidate =>
            string.Equals(candidate.WorldId, worldId, StringComparison.Ordinal) &&
            candidate.ScopeAnchorEntityId == anchor);
        if (profile != null)
        {
            InventoryGroupRecord.Migrate(profile);
            return profile;
        }
        profile = new ScopeProfile
        {
            WorldId = worldId ?? string.Empty,
            ScopeAnchorEntityId = anchor,
            Policy = Config.Current.DefaultPolicy
        };
        document.Profiles.Add(profile);
        var name = scope.AnchorGrid.DisplayName ?? "Grid";
        var safe = new string(name.Select(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-' ? c : '_').Take(60).ToArray()).Trim();
        files[(profile.WorldId, anchor)] = Path.Combine(directory, (safe.Length == 0 ? "Grid" : safe) + "-" + FileName(profile));
        InventoryGroupRecord.Migrate(profile);
        return profile;
    }

    public void Save()
    {
        if (loadFailed)
            throw new InvalidOperationException("Local profile loading failed; refusing to overwrite the existing file. Restore or repair it first.");
        Directory.CreateDirectory(directory);
        foreach (var profile in document.Profiles)
        {
            var key = (profile.WorldId, profile.ScopeAnchorEntityId);
            if (!files.TryGetValue(key, out var destination))
                files[key] = destination = Path.Combine(directory, FileName(profile));
            Write(destination, new LocalProfileDocument
            {
                Profiles = new List<ScopeProfile> { profile },
                DisplayOrders = document.DisplayOrders.Where(order => order.WorldId == profile.WorldId &&
                    order.Anchor == profile.ScopeAnchorEntityId).ToList()
            });
        }
        // Keep the monolithic source as a recovery copy, but stop importing it after
        // every grid has been written successfully. Interrupted migration is retryable.
        var legacyFiles = Directory.GetFiles(Path.GetDirectoryName(path), Path.GetFileName(path) + "*")
            .Where(file => file == path || file == path + ".bak" || file.StartsWith(path + ".migrated.", StringComparison.Ordinal) ||
                file.StartsWith(path + ".before-adoption.", StringComparison.Ordinal)).ToArray();
        if (legacyFiles.Length > 0)
        {
            var backups = Path.Combine(Path.GetDirectoryName(directory), "Backups");
            Directory.CreateDirectory(backups);
            foreach (var file in legacyFiles)
                File.Move(file, Path.Combine(backups, Path.GetFileName(file) + "." + Guid.NewGuid().ToString("N") + ".bak"));
        }
        displayOrderSaveAt = default;
    }

    private static string FileName(ScopeProfile profile)
    {
        using var sha = SHA256.Create();
        var world = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(profile.WorldId ?? ""))).Replace("-", "").Substring(0, 16);
        return profile.ScopeAnchorEntityId + "-" + world + ".xml";
    }

    private static void Write(string path, LocalProfileDocument value)
    {
        var serializer = new XmlSerializer(typeof(LocalProfileDocument));
        using var buffer = new StringWriter();
        serializer.Serialize(buffer, value);
        var text = buffer.ToString();
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        var temporary = path + ".tmp";
        // StringWriter emits an UTF-16 declaration, so use the matching encoding.
        File.WriteAllText(temporary, text, Encoding.Unicode);
        if (File.Exists(path))
            File.Replace(temporary, path, path + ".bak");
        else
            File.Move(temporary, path);
    }

    public void BackupBeforeAdoption()
    {
        Save();
        var backup = Path.Combine(Path.GetDirectoryName(directory), "Backups", "before-adoption-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        foreach (var file in files.Values) File.Copy(file, Path.Combine(backup, Path.GetFileName(file)));
    }

    private LocalProfileDocument Load()
    {
        var loaded = new LocalProfileDocument();
        try
        {
            var serializer = new XmlSerializer(typeof(LocalProfileDocument));
            foreach (var file in Directory.Exists(directory) ? Directory.GetFiles(directory, "*.xml") : Array.Empty<string>())
            {
                using var reader = File.OpenText(file);
                var grid = (LocalProfileDocument)serializer.Deserialize(reader);
                if (grid?.Profiles?.Count != 1) throw new InvalidDataException("Each grid file must contain exactly one profile: " + file);
                var profile = grid.Profiles[0];
                var key = (profile.WorldId, profile.ScopeAnchorEntityId);
                if (files.ContainsKey(key)) throw new InvalidDataException("Duplicate grid profile: " + file);
                files.Add(key, file);
                loaded.Profiles.Add(profile);
                loaded.DisplayOrders.AddRange((grid.DisplayOrders ?? new()).Where(order => order != null &&
                    order.WorldId == profile.WorldId && order.Anchor == profile.ScopeAnchorEntityId));
            }
            if (File.Exists(path))
            {
                using var reader = File.OpenText(path);
                var legacy = (LocalProfileDocument)serializer.Deserialize(reader);
                foreach (var profile in legacy.Profiles.Where(profile => !files.ContainsKey((profile.WorldId, profile.ScopeAnchorEntityId))))
                {
                    loaded.Profiles.Add(profile);
                    loaded.DisplayOrders.AddRange((legacy.DisplayOrders ?? new()).Where(order => order != null &&
                        order.WorldId == profile.WorldId && order.Anchor == profile.ScopeAnchorEntityId));
                }
            }
            loaded.DisplayOrders ??= new();
            loaded.DisplayOrders.RemoveAll(order => order == null);
            foreach (var order in loaded.DisplayOrders)
                order.Keys = (order.Keys ?? new()).Where(key => !string.IsNullOrEmpty(key)).Distinct().ToList();
            foreach (var profile in loaded.Profiles)
                InventoryGroupRecord.Migrate(profile);
            return loaded;
        }
        catch (Exception exception)
        {
            loadFailed = true;
            log.Error(exception, "Failed to load local profile store: {0}", path);
            return new LocalProfileDocument();
        }
    }
}
