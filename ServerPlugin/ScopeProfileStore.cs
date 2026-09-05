using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Serialization;
using ClientPlugin.Profiles;
using PluginSdk.Logging;
using Shared.Companion;

namespace ServerPlugin;

public sealed class ServerProfileDocument
{
    public int Version { get; set; } = 1;
    public List<SharedScopeProfile> Profiles { get; set; } = new();
    public List<OrphanedProfile> Orphans { get; set; } = new();
}

public sealed class OrphanedProfile
{
    public Guid Id { get; set; }
    public DateTime MissingSinceUtc { get; set; }
}

internal sealed class ScopeProfileStore
{
    private static readonly XmlSerializer Serializer = new(typeof(ServerProfileDocument));
    private readonly string path;
    private readonly Logger log;
    private ServerProfileDocument document = new();
    private DateTime flushAt;
    private bool dirty;
    public bool Available { get; private set; } = true;
    public IReadOnlyList<SharedScopeProfile> Profiles => document.Profiles;

    public ScopeProfileStore(string worldPath, Logger log)
    {
        this.log = log;
        path = Path.Combine(worldPath, "Storage", "UnifiedStorage.server-profiles.xml");
        if (!File.Exists(path)) return;
        try
        {
            using var reader = XmlReader.Create(path, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 80 * 1024 * 1024
            });
            var loaded = (ServerProfileDocument)Serializer.Deserialize(reader);
            if (loaded.Version != 1 || loaded.Profiles == null || loaded.Profiles.Count > 256 || loaded.Orphans == null || loaded.Orphans.Count > 256 ||
                loaded.Orphans.Any(orphan => orphan == null || orphan.Id == Guid.Empty || orphan.MissingSinceUtc == default) ||
                loaded.Orphans.Select(orphan => orphan.Id).Distinct().Count() != loaded.Orphans.Count)
                throw new InvalidDataException("Unsupported server profile document.");
            var ids = new HashSet<Guid>();
            var anchors = new HashSet<long>();
            foreach (var profile in loaded.Profiles)
            {
                if (profile == null || profile.SchemaVersion != 1 || profile.Id == Guid.Empty || !ids.Add(profile.Id) ||
                    profile.AnchorEntityId == 0 || !anchors.Add(profile.AnchorEntityId) || profile.OwnerIdentityId == 0 || profile.Revision <= 0 ||
                    (profile.Automation & ~AutomationManifest.Modes) != 0)
                    throw new InvalidDataException("Invalid server profile identity.");
                ProfileCodec.Validate(profile.Settings);
                InventoryGroupRecord.Migrate(profile.Settings);
            }
            document = loaded;
        }
        catch (Exception exception)
        {
            Available = false;
            log.Error("Profile load failed; shared profiles disabled to preserve the existing file", exception);
        }
    }

    public SharedScopeProfile[] InScope(HashSet<long> gridIds) =>
        document.Profiles.Where(profile => gridIds.Contains(profile.AnchorEntityId)).ToArray();

    public void Put(SharedScopeProfile value)
    {
        if (!Available) throw new InvalidOperationException("Profile store unavailable.");
        var index = document.Profiles.FindIndex(profile => profile.Id == value.Id);
        if (index < 0) document.Profiles.Add(value); else document.Profiles[index] = value;
        document.Orphans.RemoveAll(orphan => orphan.Id == value.Id);
        if (!dirty) flushAt = DateTime.UtcNow.AddSeconds(2);
        dirty = true;
    }

    public void Update() { if (dirty && DateTime.UtcNow >= flushAt) Flush(); }

    public void Remove(Guid id)
    {
        if (!Available) throw new InvalidOperationException("Profile store unavailable.");
        var removed = document.Profiles.FirstOrDefault(profile => profile.Id == id);
        if (removed == null) return;
        // An explicit deletion has a separate recovery archive; subsequent normal saves cannot overwrite it.
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var archive = path + ".before-delete-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + id + ".xml";
        using (var writer = XmlWriter.Create(archive, new XmlWriterSettings { Indent = true }))
            Serializer.Serialize(writer, new ServerProfileDocument { Profiles = new() { removed } });
        document.Profiles.RemoveAll(profile => profile.Id == id);
        document.Orphans.RemoveAll(orphan => orphan.Id == id);
        if (!dirty) flushAt = DateTime.UtcNow.AddSeconds(2);
        dirty = true;
    }

    public void Flush()
    {
        if (!Available || !dirty) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + ".tmp";
            using (var writer = XmlWriter.Create(temporary, new XmlWriterSettings { Indent = true }))
                Serializer.Serialize(writer, document);
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
            dirty = false;
        }
        catch (Exception exception)
        {
            Available = false;
            log.Error("Profile save failed; further shared profile changes disabled", exception);
        }
    }

    public void AuditOrphans(Func<long, bool> anchorExists, int retentionDays)
    {
        if (!Available) return;
        var changed = false;
        foreach (var profile in document.Profiles.ToArray())
        {
            var orphan = document.Orphans.FirstOrDefault(value => value.Id == profile.Id);
            if (anchorExists(profile.AnchorEntityId))
            { if (orphan != null) { document.Orphans.Remove(orphan); changed = true; } continue; }
            if (orphan == null)
            {
                document.Orphans.Add(new OrphanedProfile { Id = profile.Id, MissingSinceUtc = DateTime.UtcNow });
                changed = true;
            }
            else if (retentionDays > 0 && DateTime.UtcNow - orphan.MissingSinceUtc >= TimeSpan.FromDays(Math.Min(3650, retentionDays)))
            { Remove(profile.Id); break; } // At most one archived expiry per audit.
        }
        if (!changed) return;
        if (!dirty) flushAt = DateTime.UtcNow.AddSeconds(2);
        dirty = true;
    }
}
