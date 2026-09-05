using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using Shared.Companion;

namespace ClientPlugin.Companion;

public sealed partial class CompanionClient
{
    private bool profileSequence, sendingProfilePage;
    private Action profileStep, profileInterrupted;
    private long profileStepAt;

    private bool ProfilePageRequest(long anchor, long terminal, SharedScopeProfile snapshot, ProfileOperation operation, Action<CompanionMessage> completed)
    {
        sendingProfilePage = true;
        try { return Request(MessageKind.ProfileOperation, anchor, terminal, snapshot, ProfileCodec.Encode(operation), completed); }
        finally { sendingProfilePage = false; }
    }
    private void NextProfileStep(Action step)
    { profileStep = step; profileStepAt = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 1.2); }

    public bool ProfileRequest(MessageKind kind, long anchor, long terminal, SharedScopeProfile snapshot, byte[] body, Action<CompanionMessage> completed)
    {
        if (!Supports(CompanionCapabilities.ProfileOperations)) return Request(kind, anchor, terminal, snapshot, body, completed);
        if (Busy) return false;
        if (kind != MessageKind.GetProfile && kind != MessageKind.PublishProfile) return Request(kind, anchor, terminal, snapshot, body, completed);
        if (kind == MessageKind.PublishProfile && (body == null || body.Length == 0 || body.Length > ProfileCodec.MaxSettingsBytes)) return false;
        profileSequence = true;
        void Complete(CompanionMessage response)
        { profileSequence = false; profileStep = null; profileInterrupted = null; completed(response); }
        var pages = new List<byte[]>();
        var binding = snapshot;
        void Failed(ResultCode code) => Complete(new CompanionMessage { Kind = MessageKind.Result, Code = code });
        profileInterrupted = () => Failed(ResultCode.UnknownOutcome);
        void Receive(CompanionMessage response)
        {
            if (response.Code != ResultCode.Ok) { Complete(response); return; }
            try
            {
                var page = ProfileCodec.Decode<ProfilePage>(response.Body);
                if (page.Page != pages.Count || page.Pages < 1 || page.Pages > 16 || page.Data == null || page.Data.Length > ProfilePage.PageBytes ||
                    page.Page >= page.Pages || page.Page < page.Pages - 1 && page.Data.Length != ProfilePage.PageBytes)
                    throw new InvalidOperationException("Invalid profile page.");
                if (pages.Count > 0 && (binding.Id != response.ProfileId || binding.Revision != response.Revision))
                    throw new InvalidOperationException("Profile revision changed between pages.");
                binding = new SharedScopeProfile { Id = response.ProfileId, Revision = response.Revision };
                pages.Add(page.Data);
                if (pages.Count < page.Pages) NextProfileStep(() => FetchPage(pages.Count));
                else
                {
                    response.Body = pages.SelectMany(value => value).ToArray();
                    var decoded = ProfileCodec.DecodeDocument<SharedScopeProfile>(response.Body);
                    ProfileCodec.Validate(decoded.Settings);
                    if (decoded.Id != binding.Id || decoded.Revision != binding.Revision) throw new InvalidOperationException("Invalid profile identity.");
                    Complete(response);
                }
            }
            catch (Exception) { Failed(ResultCode.UnknownOutcome); }
        }
        void FetchPage(int index)
        {
            if (!ProfilePageRequest(anchor, terminal, index == 0 ? null : binding,
                new ProfileOperation { Operation = ProfileOperationKind.FetchPage, Page = index }, Receive))
                Failed(ResultCode.Unavailable);
        }
        if (kind == MessageKind.GetProfile) { FetchPage(0); return true; }
        var uploadId = Guid.NewGuid();
        var count = (body.Length + ProfilePage.PageBytes - 1) / ProfilePage.PageBytes;
        void UploadPage(int index)
        {
            var commit = index == count;
            var operation = new ProfileOperation
            {
                Operation = commit ? ProfileOperationKind.CommitUpload : ProfileOperationKind.UploadPage,
                UploadId = uploadId, Page = commit ? 0 : index, Pages = count,
                Data = commit ? Array.Empty<byte>() : body.Skip(index * ProfilePage.PageBytes).Take(ProfilePage.PageBytes).ToArray()
            };
            if (!ProfilePageRequest(anchor, terminal, snapshot, operation, response =>
            {
                if (response.Code != ResultCode.Ok) { Complete(response); return; }
                if (commit) Receive(response); else NextProfileStep(() => UploadPage(index + 1));
            })) Failed(ResultCode.UnknownOutcome);
        }
        UploadPage(0);
        return true;
    }
}
