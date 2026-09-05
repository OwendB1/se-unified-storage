using System;
using System.Collections.Generic;
using ClientPlugin.Profiles;

namespace Shared.Companion;

public enum ProfileOperationKind { FetchPage, UploadPage, CommitUpload, Patch, ListOwned, Rebind, Delete }
[Flags]
public enum ProfileFields { None = 0, Policy = 1, Groups = 2, Loadouts = 4, Components = 8, Refineries = 16, Exclusions = 32, All = 63 }

public sealed class ProfileOperation
{
    public ProfileOperationKind Operation { get; set; }
    public Guid UploadId { get; set; }
    public int Page { get; set; }
    public int Pages { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public ProfileFields Fields { get; set; }
    public ScopeProfile Settings { get; set; }
}

public sealed class ProfilePage
{
    public const int PageBytes = 16 * 1024;
    public int Page { get; set; }
    public int Pages { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
}

public sealed class OwnedProfileList
{
    public List<OwnedProfileInfo> Profiles { get; set; } = new();
}
public sealed class OwnedProfileInfo
{
    public Guid Id { get; set; }
    public long Revision { get; set; }
    public long Anchor { get; set; }
    public bool AnchorMissing { get; set; }
}
