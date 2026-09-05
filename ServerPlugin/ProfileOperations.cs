using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClientPlugin.Profiles;
using Sandbox.Game.Entities;
using Shared.Companion;

namespace ServerPlugin;

internal sealed class ProfileOperations
{
    private readonly ScopeProfileStore store;
    private readonly CompanionConfig config;
    private readonly Dictionary<(ulong Sender, Guid Id), Upload> uploads = new();
    public ProfileOperations(ScopeProfileStore store, CompanionConfig config) { this.store = store; this.config = config; }

    public void Execute(ulong sender, long identity, MyCubeGrid anchor, HashSet<long> grids, CompanionMessage request, CompanionMessage response)
    {
        var operation = ProfileCodec.Decode<ProfileOperation>(request.Body);
        if (!Enum.IsDefined(typeof(ProfileOperationKind), operation.Operation) || operation.Data == null || operation.Data.Length > ProfilePage.PageBytes ||
            operation.Page < 0 || operation.Page >= 16 || operation.Pages < 0 || operation.Pages > 16)
            throw new InvalidDataException("Invalid profile operation.");
        foreach (var key in uploads.Where(pair => pair.Value.Expires < DateTime.UtcNow).Select(pair => pair.Key).ToArray()) uploads.Remove(key);
        if (operation.Operation == ProfileOperationKind.ListOwned)
        {
            response.Body = ProfileCodec.Encode(new OwnedProfileList
            {
                Profiles = store.Profiles.Where(profile => profile.OwnerIdentityId == identity).Skip(operation.Page * 16).Take(16)
                    .Select(profile => new OwnedProfileInfo { Id = profile.Id, Revision = profile.Revision, Anchor = profile.AnchorEntityId,
                        AnchorMissing = !MyEntities.TryGetEntityById<MyCubeGrid>(profile.AnchorEntityId, out var found) || found.MarkedForClose }).ToList()
            });
            response.Code = ResultCode.Ok; return;
        }
        var matches = store.InScope(grids);
        if (matches.Length > 1 && operation.Operation is not (ProfileOperationKind.Delete or ProfileOperationKind.Rebind))
        { response.Code = ResultCode.Conflict; return; }
        var existing = operation.Operation is ProfileOperationKind.Rebind or ProfileOperationKind.Delete ?
            store.Profiles.FirstOrDefault(profile => profile.Id == request.ProfileId) : matches.SingleOrDefault();
        if (operation.Operation == ProfileOperationKind.FetchPage)
        {
            if (existing == null) { response.Code = ResultCode.NotFound; return; }
            if (!ProfilePermissions.CanRead(existing, identity, config.AllowFactionRead)) { response.Code = ResultCode.Denied; return; }
            if (operation.Page != 0 && (existing.Id != request.ProfileId || existing.Revision != request.Revision))
            { response.Code = ResultCode.Conflict; return; }
            Page(response, existing, operation.Page); return;
        }
        if (!anchor.BigOwners.Contains(identity) || existing != null && existing.OwnerIdentityId != identity)
        { response.Code = ResultCode.Denied; return; }
        if (existing != null && operation.Operation is not (ProfileOperationKind.Delete or ProfileOperationKind.Rebind) &&
            !ProfilePermissions.CanRead(existing, identity, false)) { response.Code = ResultCode.Denied; return; }
        if (existing == null ? request.ProfileId != Guid.Empty || request.Revision != 0 :
            existing.Id != request.ProfileId || existing.Revision != request.Revision)
        { response.Code = ResultCode.Conflict; return; }
        if (operation.Operation is ProfileOperationKind.Delete or ProfileOperationKind.Rebind)
        {
            if (existing == null) { response.Code = ResultCode.NotFound; return; }
            if (operation.Operation == ProfileOperationKind.Delete)
            { store.Remove(existing.Id); response.Code = ResultCode.Ok; return; }
            if (matches.Any(profile => profile.Id != existing.Id)) { response.Code = ResultCode.Conflict; return; }
            // A moved profile never silently starts automation on a different ship.
            var moved = ProfileCodec.DecodeDocument<SharedScopeProfile>(ProfileCodec.EncodeDocument(existing));
            moved.AnchorEntityId = anchor.EntityId; moved.Settings.ScopeAnchorEntityId = anchor.EntityId;
            moved.Automation = CompanionCapabilities.None; moved.Revision = checked(moved.Revision + 1);
            Page(response, moved, 0); store.Put(moved); return;
        }
        if (existing == null && store.Profiles.Count >= Math.Max(1, Math.Min(256, config.MaxProfiles)))
        { response.Code = ResultCode.Busy; return; }
        ScopeProfile settings;
        var faction = existing?.FactionShared ?? false;
        if (operation.Operation == ProfileOperationKind.Patch)
        {
            if (existing == null) { response.Code = ResultCode.NotFound; return; }
            if (operation.Fields == ProfileFields.None || (operation.Fields & ~ProfileFields.All) != 0) throw new InvalidDataException("Invalid patch fields.");
            ProfileCodec.Validate(operation.Settings);
            InventoryGroupRecord.Migrate(operation.Settings);
            settings = ProfileCodec.Clone(existing.Settings);
            InventoryGroupRecord.Migrate(settings);
            var value = operation.Settings;
            if ((operation.Fields & ProfileFields.Policy) != 0) settings.Policy = value.Policy;
            if ((operation.Fields & ProfileFields.Groups) != 0) settings.Groups = value.Groups;
            if ((operation.Fields & ProfileFields.Loadouts) != 0) settings.Loadouts = value.Loadouts;
            if ((operation.Fields & ProfileFields.Components) != 0)
            { settings.ComponentTargets = value.ComponentTargets; settings.ComponentStartThreshold = value.ComponentStartThreshold; settings.MaintainComponentTargets = value.MaintainComponentTargets; }
            if ((operation.Fields & ProfileFields.Refineries) != 0) settings.RefineryPriority = value.RefineryPriority;
            if ((operation.Fields & ProfileFields.Exclusions) != 0) settings.InventoryManagement = value.InventoryManagement;
        }
        else
        {
            if (operation.UploadId == Guid.Empty) throw new InvalidDataException("Missing upload ID.");
            var key = (sender, operation.UploadId);
            if (operation.Operation == ProfileOperationKind.UploadPage)
            {
                if (operation.Pages < 1 || operation.Page >= operation.Pages || operation.Data.Length == 0 ||
                    operation.Page < operation.Pages - 1 && operation.Data.Length != ProfilePage.PageBytes) throw new InvalidDataException("Invalid page.");
                if (!uploads.TryGetValue(key, out var upload))
                {
                    if (operation.Page != 0 || uploads.Count >= 16 || uploads.Keys.Any(item => item.Sender == sender))
                    { response.Code = ResultCode.Busy; return; }
                    uploads[key] = upload = new Upload { Anchor = anchor.EntityId, Profile = request.ProfileId, Revision = request.Revision,
                        Pages = new byte[operation.Pages][], Expires = DateTime.UtcNow.AddMinutes(2) };
                }
                if (upload.Anchor != anchor.EntityId || upload.Profile != request.ProfileId || upload.Revision != request.Revision || upload.Pages.Length != operation.Pages)
                    throw new InvalidDataException("Upload binding changed.");
                if (upload.Pages[operation.Page] != null && !upload.Pages[operation.Page].SequenceEqual(operation.Data)) throw new InvalidDataException("Page changed.");
                upload.Pages[operation.Page] = operation.Data;
                response.Code = ResultCode.Ok; return;
            }
            if (operation.Operation != ProfileOperationKind.CommitUpload || !uploads.TryGetValue(key, out var complete) ||
                complete.Anchor != anchor.EntityId || complete.Profile != request.ProfileId || complete.Revision != request.Revision ||
                complete.Pages.Any(page => page == null)) throw new InvalidDataException("Incomplete upload.");
            var submitted = ProfileCodec.DecodeDocument<SharedScopeProfile>(complete.Pages.SelectMany(page => page).ToArray());
            if (submitted.SchemaVersion != 1) throw new InvalidDataException("Unsupported schema.");
            settings = submitted.Settings; faction = config.AllowFactionRead && submitted.FactionShared;
            uploads.Remove(key);
        }
        ProfileCodec.Validate(settings);
        InventoryGroupRecord.Migrate(settings);
        settings.WorldId = string.Empty; settings.ScopeAnchorEntityId = existing?.AnchorEntityId ?? anchor.EntityId;
        var updated = new SharedScopeProfile
        {
            Id = existing?.Id ?? Guid.NewGuid(), Revision = checked((existing?.Revision ?? 0) + 1), AnchorEntityId = settings.ScopeAnchorEntityId,
            OwnerIdentityId = identity, Settings = settings, FactionShared = faction, Automation = existing?.Automation ?? CompanionCapabilities.None
        };
        Page(response, updated, 0); store.Put(updated);
    }

    private static void Page(CompanionMessage response, SharedScopeProfile profile, int page)
    {
        var bytes = ProfileCodec.EncodeDocument(profile);
        var pages = (bytes.Length + ProfilePage.PageBytes - 1) / ProfilePage.PageBytes;
        if (page >= pages) throw new InvalidDataException("No such profile page.");
        response.ProfileId = profile.Id; response.Revision = profile.Revision;
        response.Body = ProfileCodec.Encode(new ProfilePage { Page = page, Pages = pages, Data = bytes.Skip(page * ProfilePage.PageBytes).Take(ProfilePage.PageBytes).ToArray() });
        response.Code = ResultCode.Ok;
    }
    private sealed class Upload
    { public long Anchor, Revision; public Guid Profile; public byte[][] Pages; public DateTime Expires; }
}
