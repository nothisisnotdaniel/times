namespace Times.Tests;

/// <summary>An in-memory token store: switchable to unsupported or to throwing, and counting every call.</summary>
internal sealed class FakeTokenStore : ITokenStore
{
    public const string Failure = "The test store failed.";

    public FakeTokenStore(bool supported = true, (string Organization, string Pat)? stored = null)
    {
        IsSupported = supported;
        Stored = stored;
    }

    public bool IsSupported { get; set; }

    /// <summary>What <see cref="ITokenStore.WriteRefusal"/> returns; null lets writes go ahead.</summary>
    public string? WriteRefusal { get; set; }

    public (string Organization, string Pat)? Stored { get; set; }

    /// <summary>Makes <see cref="Read"/>, <see cref="Write"/> and <see cref="Delete"/> throw a <see cref="TokenStoreException"/>.</summary>
    public bool Throws { get; set; }

    public int Reads { get; private set; }

    public List<(string Organization, string Pat)> Writes { get; } = [];

    public int Deletes { get; private set; }

    public int Calls => Reads + Writes.Count + Deletes;

    public (string Organization, string Pat)? Read()
    {
        Reads++;
        ThrowIfSet();
        return Stored;
    }

    public void Write(string organization, string pat)
    {
        Writes.Add((organization, pat));
        ThrowIfSet();
        Stored = (organization, pat);
    }

    public bool Delete()
    {
        Deletes++;
        ThrowIfSet();
        bool had = Stored is not null;
        Stored = null;
        return had;
    }

    private void ThrowIfSet()
    {
        if (Throws)
        {
            throw new TokenStoreException(Failure);
        }
    }
}

/// <summary>
/// An in-memory Azure DevOps: work items with Completed Work and a revision. Records every call;
/// a work item in <see cref="GetFailures"/> or <see cref="SetFailures"/> throws that reason.
/// </summary>
internal sealed class FakeDevOpsClient : IDevOpsClient, IDisposable
{
    public Dictionary<int, (double? Completed, int Rev)> Items { get; } = [];

    public Dictionary<int, string> GetFailures { get; } = [];

    public Dictionary<int, string> SetFailures { get; } = [];

    public List<int> Gets { get; } = [];

    public List<(int Id, int Rev, double Value)> Sets { get; } = [];

    /// <summary>The credentials the app made a client for.</summary>
    public List<DevOpsCredential> Credentials { get; } = [];

    /// <summary>Runs after a successful set, before the app marks the checkpoints booked.</summary>
    public Action<int>? AfterSet { get; set; }

    public int Disposed { get; private set; }

    /// <summary>What <see cref="GetAssignedWorkItemsAsync"/> returns.</summary>
    public List<(int Id, string? Title)> Assigned { get; } = [];

    /// <summary>Makes <see cref="GetAssignedWorkItemsAsync"/> throw a <see cref="DevOpsException"/> with this reason.</summary>
    public string? AssignedFailure { get; set; }

    public int AssignedCalls { get; private set; }

    public void Dispose() => Disposed++;

    public IDevOpsClient For(DevOpsCredential credential)
    {
        Credentials.Add(credential);
        return this;
    }

    public Task<(double? Completed, int Rev)> GetCompletedWorkAsync(int id)
    {
        Gets.Add(id);
        if (GetFailures.TryGetValue(id, out string? reason))
        {
            throw new DevOpsException(reason);
        }

        return Task.FromResult(Items.TryGetValue(id, out (double? Completed, int Rev) item) ? item : ((double?)null, 1));
    }

    public Task SetCompletedWorkAsync(int id, int rev, double value)
    {
        Sets.Add((id, rev, value));
        if (SetFailures.TryGetValue(id, out string? reason))
        {
            throw new DevOpsException(reason);
        }

        Items[id] = (value, rev + 1);
        AfterSet?.Invoke(id);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<(int Id, string? Title)>> GetAssignedWorkItemsAsync()
    {
        AssignedCalls++;
        if (AssignedFailure is { } reason)
        {
            throw new DevOpsException(reason);
        }

        return Task.FromResult<IReadOnlyList<(int Id, string? Title)>>(Assigned.ToList());
    }
}
