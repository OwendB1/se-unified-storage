using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;
using ClientPlugin.Inventory;
using Sandbox.Game.World;
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
    private readonly string directory;
    private readonly Dictionary<(string World, long Anchor), string> files = new();
    private LocalProfileDocument document;
    private bool loadFailed;
    private DateTime displayOrderSaveAt;

    public LocalProfileStore(IPluginLogger log, string path = null)
    {
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        directory = path ?? Path.Combine(MyFileSystem.UserDataPath, "Storage", "UnifiedStorage", "Profiles");
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
        files[(profile.WorldId, anchor)] = Path.Combine(WorldDirectory(profile), NormalizeName(scope.DisplayName, "Grid") + "-" + FileName(profile));
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
                files[key] = destination = Path.Combine(WorldDirectory(profile), FileName(profile));
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            Write(destination, new LocalProfileDocument
            {
                Profiles = new List<ScopeProfile> { profile },
                DisplayOrders = document.DisplayOrders.Where(order => order.WorldId == profile.WorldId &&
                    order.Anchor == profile.ScopeAnchorEntityId).ToList()
            });
        }
        displayOrderSaveAt = default;
    }

    private string WorldDirectory(ScopeProfile profile)
    {
        var existing = files.FirstOrDefault(pair => pair.Key.World == profile.WorldId).Value;
        if (existing != null) return Path.GetDirectoryName(existing);
        var name = profile.WorldId == ProfileIdentity.CurrentWorld ? MySession.Static?.Name : null;
        return Path.Combine(directory, WorldHash(profile) + "-" + NormalizeName(name, "World"));
    }

    private static string NormalizeName(string name, string fallback)
    {
        var safe = new string((name ?? "").Select(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-' ? c : '_')
            .Take(60).ToArray()).Trim();
        return safe.Length == 0 ? fallback : safe;
    }

    private static string FileName(ScopeProfile profile) => profile.ScopeAnchorEntityId + ".xml";

    private static string WorldHash(ScopeProfile profile)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(profile.WorldId ?? ""))).Replace("-", "").Substring(0, 16);
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
        foreach (var profile in document.Profiles)
        {
            var worldBackup = Path.Combine(backup, Path.GetFileName(WorldDirectory(profile)));
            Directory.CreateDirectory(worldBackup);
            File.Copy(files[(profile.WorldId, profile.ScopeAnchorEntityId)], Path.Combine(worldBackup,
                Path.GetFileName(files[(profile.WorldId, profile.ScopeAnchorEntityId)])));
        }
    }

    private LocalProfileDocument Load()
    {
        var loaded = new LocalProfileDocument();
        try
        {
            var serializer = new XmlSerializer(typeof(LocalProfileDocument));
            foreach (var file in Directory.Exists(directory) ? Directory.GetFiles(directory, "*.xml", SearchOption.AllDirectories) : Array.Empty<string>())
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
            log.Error(exception, "Failed to load local profile store: {0}", directory);
            return new LocalProfileDocument();
        }
    }
}
