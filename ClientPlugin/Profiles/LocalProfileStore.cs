using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    private LocalProfileDocument document;
    private bool loadFailed;
    private DateTime displayOrderSaveAt;

    public LocalProfileStore(IPluginLogger log, string path = null)
    {
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.path = path ?? Path.Combine(MyFileSystem.UserDataPath, "Storage", "UnifiedStorage.profiles.xml");
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
        InventoryGroupRecord.Migrate(profile);
        return profile;
    }

    public void Save()
    {
        if (loadFailed)
            throw new InvalidOperationException("Local profile loading failed; refusing to overwrite the existing file. Restore or repair it first.");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var serializer = new XmlSerializer(typeof(LocalProfileDocument));
        var temporary = path + ".tmp";
        using (var writer = File.CreateText(temporary))
            serializer.Serialize(writer, document);
        if (File.Exists(path))
            File.Replace(temporary, path, path + ".bak");
        else
            File.Move(temporary, path);
        displayOrderSaveAt = default;
    }

    public void BackupBeforeAdoption()
    {
        Save();
        File.Copy(path, path + ".before-adoption." + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "." + Guid.NewGuid().ToString("N") + ".xml");
    }

    private LocalProfileDocument Load()
    {
        if (!File.Exists(path))
            return new LocalProfileDocument();
        try
        {
            var serializer = new XmlSerializer(typeof(LocalProfileDocument));
            using var reader = File.OpenText(path);
            var loaded = serializer.Deserialize(reader) as LocalProfileDocument ?? new LocalProfileDocument();
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
