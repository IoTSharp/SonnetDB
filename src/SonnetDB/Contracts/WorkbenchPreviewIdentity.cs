namespace SonnetDB.Contracts;

/// <summary>独立工作台包的版本与源码身份，不包含凭据或数据。</summary>
internal sealed record WorkbenchPreviewIdentity(string Candidate, string Version, string SourceCommit,
    string Profile, string ScopeRevision, string Rid);
