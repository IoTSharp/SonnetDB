using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SonnetDB.Graphs.Storage;
using SonnetDB.Kv;
using SonnetDB.Tables;

namespace SonnetDB.Graphs;

internal static class GraphOfflineAlgorithmRunner
{
    private const int EdgeRecordSize = sizeof(long) * 2;
    private const int VoteRecordSize = sizeof(long) * 3;
    private const int MaximumOpenVoteRuns = 32;
    private const string VertexIdsFileName = "vertices.bin";
    private const string VertexRecordsFileName = "vertex-records.bin";
    private const string EdgesFileName = "edges.bin";
    private const string InDegreeFileName = "in-degree.bin";
    private const string OutDegreeFileName = "out-degree.bin";
    private const string ComponentsFileName = "components.bin";
    private const string RankFilePrefix = "rank-";
    private const string CommunityFilePrefix = "community-";

    internal static GraphOfflineAlgorithmResult Run(
        GraphStore store,
        GraphOfflineAlgorithmRequest request,
        GraphOfflineAlgorithmOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ValidateOutput(request.Output);
        byte[] configurationHash = ComputeConfigurationHash(request.Output, options);
        string workspace = GetWorkspace(store, request.OperationId);
        string manifestPath = Path.Combine(workspace, GraphOfflineAlgorithmManifestCodec.FileName);
        Directory.CreateDirectory(workspace);

        GraphOfflineAlgorithmState? state = GraphOfflineAlgorithmManifestCodec.Load(manifestPath, store.StorageId);
        bool resumed = state is not null;
        if (state is null)
        {
            using GraphReadSession read = store.BeginRead();
            state = new GraphOfflineAlgorithmState
            {
                StorageId = store.StorageId,
                OperationId = request.OperationId,
                ConfigurationHash = configurationHash,
                Phase = GraphOfflineAlgorithmPhase.ScanVertices,
                SourceSequence = read.Sequence,
                MemoryBudgetBytes = options.MaxMemoryBytes,
            };
            GraphOfflineAlgorithmManifestCodec.Save(manifestPath, state);
        }
        else
        {
            if (state.OperationId != request.OperationId
                || !state.ConfigurationHash.AsSpan().SequenceEqual(configurationHash))
            {
                throw new InvalidOperationException(
                    "Graph offline algorithm operation ID 已绑定到不同的算法或输出配置。");
            }
        }

        if (state.Phase == GraphOfflineAlgorithmPhase.Completed)
        {
            CleanupCompletedWorkspace(workspace);
            return new GraphOfflineAlgorithmResult(state, resumed);
        }

        int completedWorkUnits = 0;
        while (completedWorkUnits < options.MaxWorkUnits
               && state.Phase != GraphOfflineAlgorithmPhase.Completed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecuteWorkUnit(store, request, options, workspace, state, cancellationToken);
            state.WorkUnits = checked(state.WorkUnits + 1);
            state.SpillBytes = Math.Max(state.SpillBytes, MeasureSpillBytes(workspace));
            GraphOfflineAlgorithmManifestCodec.Save(manifestPath, state);
            completedWorkUnits++;
        }

        if (state.Phase == GraphOfflineAlgorithmPhase.Completed)
            CleanupCompletedWorkspace(workspace);
        return new GraphOfflineAlgorithmResult(state, resumed);
    }

    internal static string CreateResultVersion(Guid operationId, long sourceSequence)
        => $"{operationId:N}@{sourceSequence}";

    private static void ExecuteWorkUnit(
        GraphStore store,
        GraphOfflineAlgorithmRequest request,
        GraphOfflineAlgorithmOptions options,
        string workspace,
        GraphOfflineAlgorithmState state,
        CancellationToken cancellationToken)
    {
        switch (state.Phase)
        {
            case GraphOfflineAlgorithmPhase.ScanVertices:
                ScanVertices(store, options, workspace, state, cancellationToken);
                break;
            case GraphOfflineAlgorithmPhase.ScanEdges:
                ScanEdges(store, options, workspace, state, cancellationToken);
                break;
            case GraphOfflineAlgorithmPhase.Degree:
                ComputeDegree(workspace, state, cancellationToken);
                state.Phase = GraphOfflineAlgorithmPhase.ConnectedComponents;
                break;
            case GraphOfflineAlgorithmPhase.ConnectedComponents:
                ComputeConnectedComponents(workspace, state, cancellationToken);
                state.Phase = GraphOfflineAlgorithmPhase.PageRank;
                break;
            case GraphOfflineAlgorithmPhase.PageRank:
                ComputePageRankWorkUnit(workspace, state, options, cancellationToken);
                break;
            case GraphOfflineAlgorithmPhase.Community:
                ComputeCommunityWorkUnit(workspace, state, options, cancellationToken);
                break;
            case GraphOfflineAlgorithmPhase.Publish:
                PublishWorkUnit(store, request, options, workspace, state, cancellationToken);
                break;
            default:
                throw new InvalidDataException($"未知 Graph offline algorithm phase {state.Phase}。");
        }
    }

    private static void ScanVertices(
        GraphStore store,
        GraphOfflineAlgorithmOptions options,
        string workspace,
        GraphOfflineAlgorithmState state,
        CancellationToken cancellationToken)
    {
        using GraphReadSession read = RequireSourceSnapshot(store, state);
        using KvReadSnapshot snapshot = read.AcquireTraversalSnapshot();
        using KvRangeCursor cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions
        {
            Prefix = GraphKeyCodec.VertexRecordPrefix(),
            AfterKey = state.AfterKey,
            PageSize = options.PageSize,
            MaxPageBytes = options.MaxPageBytes,
        });
        IReadOnlyList<KvEntry> page = cursor.ReadNextPage(cancellationToken);

        string idsPath = Path.Combine(workspace, VertexIdsFileName);
        string recordsPath = Path.Combine(workspace, VertexRecordsFileName);
        using var ids = OpenAppendFile(idsPath, checked(state.VertexCount * sizeof(long)));
        using var records = OpenAppendFile(recordsPath, state.VertexRecordsLength);
        Span<byte> integer = stackalloc byte[sizeof(long)];
        foreach (KvEntry entry in page)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GraphStorageKey key = GraphKeyCodec.Decode(entry.Key.Span);
            GraphVertexRecord vertex = GraphElementRecordCodec.DecodeVertex(entry.Value.Span);
            if (key.Kind != GraphKeyKind.VertexRecord || key.ElementId != vertex.Id)
                throw new InvalidDataException("Graph offline vertex key 与 record 不一致。");
            BinaryPrimitives.WriteInt64LittleEndian(integer, vertex.Id.Value);
            ids.Write(integer);
            WriteLengthPrefixed(records, entry.Value.Span);
            state.VertexCount = checked(state.VertexCount + 1);
        }
        ids.Flush(flushToDisk: true);
        records.Flush(flushToDisk: true);
        state.VertexRecordsLength = records.Length;

        if (page.Count > 0)
            state.AfterKey = page[^1].Key.ToArray();
        if (cursor.IsExhausted)
        {
            state.AfterKey = [];
            state.Phase = GraphOfflineAlgorithmPhase.ScanEdges;
        }
    }

    private static void ScanEdges(
        GraphStore store,
        GraphOfflineAlgorithmOptions options,
        string workspace,
        GraphOfflineAlgorithmState state,
        CancellationToken cancellationToken)
    {
        using GraphReadSession read = RequireSourceSnapshot(store, state);
        using KvReadSnapshot snapshot = read.AcquireTraversalSnapshot();
        using KvRangeCursor cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions
        {
            Prefix = GraphKeyCodec.EdgeRecordPrefix(),
            AfterKey = state.AfterKey,
            PageSize = options.PageSize,
            MaxPageBytes = options.MaxPageBytes,
        });
        IReadOnlyList<KvEntry> page = cursor.ReadNextPage(cancellationToken);

        string idsPath = Path.Combine(workspace, VertexIdsFileName);
        using GraphAlgorithmLongVector vertexIds = GraphAlgorithmLongVector.Open(
            idsPath,
            state.VertexCount,
            state.MemoryBudgetBytes);
        string edgesPath = Path.Combine(workspace, EdgesFileName);
        using var edges = OpenAppendFile(edgesPath, checked(state.EdgeCount * EdgeRecordSize));
        Span<byte> encoded = stackalloc byte[EdgeRecordSize];
        foreach (KvEntry entry in page)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GraphStorageKey key = GraphKeyCodec.Decode(entry.Key.Span);
            GraphEdgeRecord edge = GraphElementRecordCodec.DecodeEdge(entry.Value.Span);
            if (key.Kind != GraphKeyKind.EdgeRecord || key.ElementId != edge.Id)
                throw new InvalidDataException("Graph offline edge key 与 record 不一致。");
            long sourceIndex = vertexIds.BinarySearch(edge.SourceId.Value);
            long targetIndex = vertexIds.BinarySearch(edge.TargetId.Value);
            if (sourceIndex < 0 || targetIndex < 0)
                throw new InvalidDataException($"Graph edge {edge.Id} 存在 orphan endpoint。");
            BinaryPrimitives.WriteInt64LittleEndian(encoded, sourceIndex);
            BinaryPrimitives.WriteInt64LittleEndian(encoded[sizeof(long)..], targetIndex);
            edges.Write(encoded);
            state.EdgeCount = checked(state.EdgeCount + 1);
        }
        edges.Flush(flushToDisk: true);

        if (page.Count > 0)
            state.AfterKey = page[^1].Key.ToArray();
        if (cursor.IsExhausted)
        {
            state.AfterKey = [];
            state.Phase = GraphOfflineAlgorithmPhase.Degree;
        }
    }

    private static GraphReadSession RequireSourceSnapshot(GraphStore store, GraphOfflineAlgorithmState state)
    {
        GraphReadSession read = store.BeginRead();
        if (read.Sequence == state.SourceSequence)
            return read;
        long actual = read.Sequence;
        read.Dispose();
        throw new GraphOfflineAlgorithmSourceChangedException(state.SourceSequence, actual);
    }

    private static void ComputeDegree(
        string workspace,
        GraphOfflineAlgorithmState state,
        CancellationToken cancellationToken)
    {
        string inTemporary = Path.Combine(workspace, InDegreeFileName + ".tmp");
        string outTemporary = Path.Combine(workspace, OutDegreeFileName + ".tmp");
        TryDelete(inTemporary);
        TryDelete(outTemporary);
        long vectorBudget = Math.Max(1, state.MemoryBudgetBytes / 2);
        using (GraphAlgorithmLongVector inDegree = GraphAlgorithmLongVector.Create(
                   inTemporary, state.VertexCount, vectorBudget))
        using (GraphAlgorithmLongVector outDegree = GraphAlgorithmLongVector.Create(
                   outTemporary, state.VertexCount, vectorBudget))
        using (var edges = OpenEdgeReader(workspace, state.EdgeCount))
        {
            for (long index = 0; index < state.EdgeCount; index++)
            {
                if ((index & 1023) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                (long source, long target) = ReadEdge(edges);
                outDegree.Set(source, checked(outDegree.Get(source) + 1));
                inDegree.Set(target, checked(inDegree.Get(target) + 1));
            }
            inDegree.Flush();
            outDegree.Flush();
        }
        ReplaceFile(inTemporary, Path.Combine(workspace, InDegreeFileName));
        ReplaceFile(outTemporary, Path.Combine(workspace, OutDegreeFileName));
    }

    private static void ComputeConnectedComponents(
        string workspace,
        GraphOfflineAlgorithmState state,
        CancellationToken cancellationToken)
    {
        string parentPath = Path.Combine(workspace, "component-parent.tmp");
        string resultTemporary = Path.Combine(workspace, ComponentsFileName + ".tmp");
        TryDelete(parentPath);
        TryDelete(resultTemporary);
        long vectorBudget = Math.Max(1, state.MemoryBudgetBytes / 3);
        using (GraphAlgorithmLongVector parent = GraphAlgorithmLongVector.Create(
                   parentPath, state.VertexCount, vectorBudget))
        {
            for (long index = 0; index < state.VertexCount; index++)
                parent.Set(index, index);
            using (var edges = OpenEdgeReader(workspace, state.EdgeCount))
            {
                for (long index = 0; index < state.EdgeCount; index++)
                {
                    if ((index & 1023) == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    (long source, long target) = ReadEdge(edges);
                    long sourceRoot = FindRoot(parent, source);
                    long targetRoot = FindRoot(parent, target);
                    if (sourceRoot == targetRoot)
                        continue;
                    long lower = Math.Min(sourceRoot, targetRoot);
                    long higher = Math.Max(sourceRoot, targetRoot);
                    parent.Set(higher, lower);
                }
            }

            using GraphAlgorithmLongVector vertexIds = GraphAlgorithmLongVector.Open(
                Path.Combine(workspace, VertexIdsFileName), state.VertexCount, vectorBudget);
            using GraphAlgorithmLongVector components = GraphAlgorithmLongVector.Create(
                resultTemporary, state.VertexCount, vectorBudget);
            for (long index = 0; index < state.VertexCount; index++)
            {
                if ((index & 1023) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                long root = FindRoot(parent, index);
                components.Set(index, vertexIds.Get(root));
            }
            components.Flush();
        }
        TryDelete(parentPath);
        ReplaceFile(resultTemporary, Path.Combine(workspace, ComponentsFileName));
    }

    private static long FindRoot(GraphAlgorithmLongVector parent, long index)
    {
        long current = index;
        long steps = 0;
        while (true)
        {
            long next = parent.Get(current);
            if (next == current)
                break;
            current = next;
            if (++steps > parent.Count)
                throw new InvalidDataException("Graph connected-components parent state 包含环。");
        }
        long root = current;
        current = index;
        while (current != root)
        {
            long next = parent.Get(current);
            parent.Set(current, root);
            current = next;
        }
        return root;
    }

    private static void ComputePageRankWorkUnit(
        string workspace,
        GraphOfflineAlgorithmState state,
        GraphOfflineAlgorithmOptions options,
        CancellationToken cancellationToken)
    {
        if (!state.PageRankInitialized)
        {
            string path = GetRankPath(workspace, 0);
            TryDelete(path);
            using GraphAlgorithmLongVector rank = GraphAlgorithmLongVector.Create(
                path, state.VertexCount, state.MemoryBudgetBytes);
            double initial = state.VertexCount == 0 ? 0 : 1d / state.VertexCount;
            for (long index = 0; index < state.VertexCount; index++)
                rank.SetDouble(index, initial);
            rank.Flush();
            state.PageRankInitialized = true;
            state.PageRankGeneration = 0;
            if (state.VertexCount == 0)
            {
                state.PageRankConverged = true;
                state.Phase = GraphOfflineAlgorithmPhase.Community;
            }
            return;
        }

        int nextGeneration = 1 - state.PageRankGeneration;
        string nextPath = GetRankPath(workspace, nextGeneration);
        TryDelete(nextPath);
        long vectorBudget = Math.Max(1, state.MemoryBudgetBytes / 3);
        double delta = 0;
        using (GraphAlgorithmLongVector current = GraphAlgorithmLongVector.Open(
                   GetRankPath(workspace, state.PageRankGeneration), state.VertexCount, vectorBudget))
        using (GraphAlgorithmLongVector outDegree = GraphAlgorithmLongVector.Open(
                   Path.Combine(workspace, OutDegreeFileName), state.VertexCount, vectorBudget))
        using (GraphAlgorithmLongVector next = GraphAlgorithmLongVector.Create(
                   nextPath, state.VertexCount, vectorBudget))
        {
            double dangling = 0;
            for (long index = 0; index < state.VertexCount; index++)
            {
                if ((index & 1023) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                if (outDegree.Get(index) == 0)
                    dangling += current.GetDouble(index);
            }
            double vertexCount = state.VertexCount;
            double baseline = (1 - options.PageRankDampingFactor) / vertexCount
                + options.PageRankDampingFactor * dangling / vertexCount;
            for (long index = 0; index < state.VertexCount; index++)
                next.SetDouble(index, baseline);

            using (var edges = OpenEdgeReader(workspace, state.EdgeCount))
            {
                for (long index = 0; index < state.EdgeCount; index++)
                {
                    if ((index & 1023) == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    (long source, long target) = ReadEdge(edges);
                    long degree = outDegree.Get(source);
                    if (degree <= 0)
                        throw new InvalidDataException("Graph PageRank outgoing degree 与 edge spill 不一致。");
                    double contribution = options.PageRankDampingFactor * current.GetDouble(source) / degree;
                    next.SetDouble(target, next.GetDouble(target) + contribution);
                }
            }

            for (long index = 0; index < state.VertexCount; index++)
                delta += Math.Abs(next.GetDouble(index) - current.GetDouble(index));
            if (!double.IsFinite(delta))
                throw new InvalidDataException("Graph PageRank 产生了非有限收敛指标。");
            next.Flush();
        }

        state.PageRankGeneration = nextGeneration;
        state.PageRankIterations = checked(state.PageRankIterations + 1);
        state.PageRankConverged = delta <= options.PageRankTolerance;
        if (state.PageRankConverged || state.PageRankIterations >= options.MaxPageRankIterations)
            state.Phase = GraphOfflineAlgorithmPhase.Community;
    }

    private static void ComputeCommunityWorkUnit(
        string workspace,
        GraphOfflineAlgorithmState state,
        GraphOfflineAlgorithmOptions options,
        CancellationToken cancellationToken)
    {
        if (!state.CommunityInitialized)
        {
            string path = GetCommunityPath(workspace, 0);
            TryDelete(path);
            long vectorBudget = Math.Max(1, state.MemoryBudgetBytes / 2);
            using GraphAlgorithmLongVector vertexIds = GraphAlgorithmLongVector.Open(
                Path.Combine(workspace, VertexIdsFileName), state.VertexCount, vectorBudget);
            using GraphAlgorithmLongVector labels = GraphAlgorithmLongVector.Create(
                path, state.VertexCount, vectorBudget);
            for (long index = 0; index < state.VertexCount; index++)
                labels.Set(index, vertexIds.Get(index));
            labels.Flush();
            state.CommunityInitialized = true;
            state.CommunityGeneration = 0;
            if (state.VertexCount == 0)
            {
                state.CommunityConverged = true;
                state.Phase = GraphOfflineAlgorithmPhase.Publish;
            }
            return;
        }

        int nextGeneration = 1 - state.CommunityGeneration;
        string nextPath = GetCommunityPath(workspace, nextGeneration);
        bool changed = BuildCommunityIteration(
            workspace,
            state,
            GetCommunityPath(workspace, state.CommunityGeneration),
            nextPath,
            cancellationToken);
        state.CommunityGeneration = nextGeneration;
        state.CommunityIterations = checked(state.CommunityIterations + 1);
        state.CommunityConverged = !changed;
        if (state.CommunityConverged || state.CommunityIterations >= options.MaxCommunityIterations)
            state.Phase = GraphOfflineAlgorithmPhase.Publish;
    }

    private static bool BuildCommunityIteration(
        string workspace,
        GraphOfflineAlgorithmState state,
        string currentPath,
        string nextPath,
        CancellationToken cancellationToken)
    {
        string runDirectory = Path.Combine(workspace, "community-runs");
        ResetDirectory(runDirectory);
        TryDelete(nextPath);
        int capacity = checked((int)Math.Clamp(
            state.MemoryBudgetBytes / 64,
            1_024,
            1_000_000));
        var votes = new List<CommunityVote>(capacity);
        var runs = new List<string>();
        long vectorBudget = Math.Max(1, state.MemoryBudgetBytes / 4);
        try
        {
            using (GraphAlgorithmLongVector current = GraphAlgorithmLongVector.Open(
                       currentPath, state.VertexCount, vectorBudget))
            using (var edges = OpenEdgeReader(workspace, state.EdgeCount))
            {
                for (long index = 0; index < state.EdgeCount; index++)
                {
                    if ((index & 1023) == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    (long source, long target) = ReadEdge(edges);
                    votes.Add(new CommunityVote(source, current.Get(target)));
                    votes.Add(new CommunityVote(target, current.Get(source)));
                    if (votes.Count >= capacity)
                        FlushVoteRun(runDirectory, votes, runs);
                }
                FlushVoteRun(runDirectory, votes, runs);
            }

            string? mergedPath = MergeVoteRuns(runDirectory, runs, cancellationToken);
            bool changed = WriteCommunityLabels(
                currentPath,
                nextPath,
                mergedPath,
                state.VertexCount,
                vectorBudget,
                cancellationToken);
            return changed;
        }
        finally
        {
            TryDeleteDirectory(runDirectory);
        }
    }

    private static void FlushVoteRun(
        string runDirectory,
        List<CommunityVote> votes,
        List<string> runs)
    {
        if (votes.Count == 0)
            return;
        votes.Sort();
        string path = Path.Combine(runDirectory, $"run-{runs.Count:D6}.bin");
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024);
        CommunityVote previous = votes[0];
        long count = 1;
        for (int index = 1; index < votes.Count; index++)
        {
            CommunityVote current = votes[index];
            if (current == previous)
            {
                count = checked(count + 1);
                continue;
            }
            WriteVoteCount(output, previous.VertexIndex, previous.Label, count);
            previous = current;
            count = 1;
        }
        WriteVoteCount(output, previous.VertexIndex, previous.Label, count);
        output.Flush(flushToDisk: true);
        runs.Add(path);
        votes.Clear();
    }

    private static string? MergeVoteRuns(
        string runDirectory,
        List<string> runs,
        CancellationToken cancellationToken)
    {
        if (runs.Count == 0)
            return null;
        var current = runs.ToList();
        int pass = 0;
        while (current.Count > 1)
        {
            var next = new List<string>();
            for (int offset = 0; offset < current.Count; offset += MaximumOpenVoteRuns)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = Path.Combine(runDirectory, $"merge-{pass:D3}-{next.Count:D6}.bin");
                IReadOnlyList<string> batch = current
                    .Skip(offset)
                    .Take(MaximumOpenVoteRuns)
                    .ToArray();
                MergeVoteRunBatch(batch, path, cancellationToken);
                next.Add(path);
            }
            foreach (string path in current)
                TryDelete(path);
            current = next;
            pass++;
        }
        return current[0];
    }

    private static void MergeVoteRunBatch(
        IReadOnlyList<string> inputPaths,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var readers = new List<VoteRunReader>(inputPaths.Count);
        var queue = new PriorityQueue<VoteRunReader, VotePriority>();
        try
        {
            foreach (string path in inputPaths)
            {
                var reader = new VoteRunReader(path);
                readers.Add(reader);
                if (reader.MoveNext())
                    queue.Enqueue(reader, new VotePriority(reader.VertexIndex, reader.Label));
            }
            using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024);
            while (queue.TryDequeue(out VoteRunReader? reader, out VotePriority priority))
            {
                cancellationToken.ThrowIfCancellationRequested();
                long count = reader.Count;
                AdvanceVoteReader(reader, queue);
                while (queue.TryPeek(out _, out VotePriority nextPriority) && nextPriority == priority)
                {
                    _ = queue.TryDequeue(out VoteRunReader? duplicate, out _);
                    count = checked(count + duplicate!.Count);
                    AdvanceVoteReader(duplicate, queue);
                }
                WriteVoteCount(output, priority.VertexIndex, priority.Label, count);
            }
            output.Flush(flushToDisk: true);
        }
        finally
        {
            foreach (VoteRunReader reader in readers)
                reader.Dispose();
        }
    }

    private static void AdvanceVoteReader(
        VoteRunReader reader,
        PriorityQueue<VoteRunReader, VotePriority> queue)
    {
        if (reader.MoveNext())
            queue.Enqueue(reader, new VotePriority(reader.VertexIndex, reader.Label));
    }

    private static bool WriteCommunityLabels(
        string currentPath,
        string nextPath,
        string? votesPath,
        long vertexCount,
        long vectorBudget,
        CancellationToken cancellationToken)
    {
        using GraphAlgorithmLongVector current = GraphAlgorithmLongVector.Open(
            currentPath, vertexCount, vectorBudget);
        using GraphAlgorithmLongVector next = GraphAlgorithmLongVector.Create(
            nextPath, vertexCount, vectorBudget);
        using VoteRunReader? votes = votesPath is null ? null : new VoteRunReader(votesPath);
        bool hasVote = votes?.MoveNext() == true;
        bool changed = false;
        for (long vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
        {
            if ((vertexIndex & 1023) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            long currentLabel = current.Get(vertexIndex);
            long selectedLabel = currentLabel;
            long selectedCount = 0;
            while (hasVote && votes!.VertexIndex == vertexIndex)
            {
                long label = votes.Label;
                long count = votes.Count;
                if (count > selectedCount
                    || count == selectedCount
                    && (label == currentLabel || selectedLabel != currentLabel && label < selectedLabel))
                {
                    selectedLabel = label;
                    selectedCount = count;
                }
                hasVote = votes.MoveNext();
            }
            if (hasVote && votes!.VertexIndex < vertexIndex)
                throw new InvalidDataException("Graph community vote run 排序无效。");
            next.Set(vertexIndex, selectedLabel);
            changed |= selectedLabel != currentLabel;
        }
        if (hasVote)
            throw new InvalidDataException("Graph community vote 指向未知 vertex index。");
        next.Flush();
        return changed;
    }

    private static void PublishWorkUnit(
        GraphStore store,
        GraphOfflineAlgorithmRequest request,
        GraphOfflineAlgorithmOptions options,
        string workspace,
        GraphOfflineAlgorithmState state,
        CancellationToken cancellationToken)
    {
        if (state.PublishedVertices >= state.VertexCount)
        {
            state.Phase = GraphOfflineAlgorithmPhase.Completed;
            return;
        }
        long count = Math.Min(options.OutputBatchSize, state.VertexCount - state.PublishedVertices);
        switch (request.Output)
        {
            case GraphOfflineAlgorithmTableOutput tableOutput:
                PublishTableBatch(tableOutput.Table, workspace, state, count, cancellationToken);
                break;
            case GraphOfflineAlgorithmGraphOutput graphOutput:
                PublishGraphBatch(
                    store,
                    graphOutput,
                    workspace,
                    state,
                    count,
                    state.PublishedVertices / options.OutputBatchSize,
                    cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }
        state.PublishedVertices = checked(state.PublishedVertices + count);
        if (state.PublishedVertices >= state.VertexCount)
            state.Phase = GraphOfflineAlgorithmPhase.Completed;
    }

    private static void PublishTableBatch(
        TableStore table,
        string workspace,
        GraphOfflineAlgorithmState state,
        long count,
        CancellationToken cancellationToken)
    {
        long vectorBudget = Math.Max(1, state.MemoryBudgetBytes / 7);
        using GraphAlgorithmLongVector ids = OpenResultVector(workspace, VertexIdsFileName, state, vectorBudget);
        using GraphAlgorithmLongVector components = OpenResultVector(workspace, ComponentsFileName, state, vectorBudget);
        using GraphAlgorithmLongVector rank = OpenResultVector(
            workspace, $"{RankFilePrefix}{state.PageRankGeneration}.bin", state, vectorBudget);
        using GraphAlgorithmLongVector inDegree = OpenResultVector(workspace, InDegreeFileName, state, vectorBudget);
        using GraphAlgorithmLongVector outDegree = OpenResultVector(workspace, OutDegreeFileName, state, vectorBudget);
        using GraphAlgorithmLongVector community = OpenResultVector(
            workspace, $"{CommunityFilePrefix}{state.CommunityGeneration}.bin", state, vectorBudget);
        string operationId = state.OperationId.ToString("D");
        var mutations = new List<TableRowMutation>(checked((int)count));
        for (long offset = 0; offset < count; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long index = state.PublishedVertices + offset;
            long vertexId = ids.Get(index);
            long incoming = inDegree.Get(index);
            long outgoing = outDegree.Get(index);
            object?[] values =
            [
                operationId,
                vertexId,
                state.SourceSequence,
                components.Get(index),
                rank.GetDouble(index),
                incoming,
                outgoing,
                checked(incoming + outgoing),
                community.Get(index),
            ];
            mutations.Add(new TableRowMutation([operationId, vertexId], values));
        }
        _ = table.ApplyBatch(mutations);
    }

    private static void PublishGraphBatch(
        GraphStore store,
        GraphOfflineAlgorithmGraphOutput output,
        string workspace,
        GraphOfflineAlgorithmState state,
        long count,
        long batchIndex,
        CancellationToken cancellationToken)
    {
        long vectorBudget = Math.Max(1, state.MemoryBudgetBytes / 7);
        using GraphAlgorithmLongVector components = OpenResultVector(workspace, ComponentsFileName, state, vectorBudget);
        using GraphAlgorithmLongVector rank = OpenResultVector(
            workspace, $"{RankFilePrefix}{state.PageRankGeneration}.bin", state, vectorBudget);
        using GraphAlgorithmLongVector inDegree = OpenResultVector(workspace, InDegreeFileName, state, vectorBudget);
        using GraphAlgorithmLongVector outDegree = OpenResultVector(workspace, OutDegreeFileName, state, vectorBudget);
        using GraphAlgorithmLongVector community = OpenResultVector(
            workspace, $"{CommunityFilePrefix}{state.CommunityGeneration}.bin", state, vectorBudget);
        using var records = new FileStream(
            Path.Combine(workspace, VertexRecordsFileName),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        for (long index = 0; index < state.PublishedVertices; index++)
            _ = ReadLengthPrefixed(records);

        Guid requestId = CreateBatchRequestId(state.OperationId, batchIndex);
        var limits = new GraphTransactionLimits
        {
            MaxKvMutations = Math.Max(10_000, checked((int)count * 128)),
            MaxEncodedBytes = Math.Max(64L * 1024 * 1024, count * 2L * 1024 * 1024),
        };
        GraphTransaction transaction = store.BeginTransaction(requestId, limits);
        string resultVersion = CreateResultVersion(state.OperationId, state.SourceSequence);
        for (long offset = 0; offset < count; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long index = state.PublishedVertices + offset;
            GraphVertexRecord vertex = GraphElementRecordCodec.DecodeVertex(ReadLengthPrefixed(records));
            long incoming = inDegree.Get(index);
            long outgoing = outDegree.Get(index);
            var properties = vertex.Properties.ToDictionary(static property => property.PropertyId);
            properties[output.ComponentPropertyId] = new GraphProperty(
                output.ComponentPropertyId, GraphPropertyValue.FromInt64(components.Get(index)));
            properties[output.PageRankPropertyId] = new GraphProperty(
                output.PageRankPropertyId, GraphPropertyValue.FromFloat64(rank.GetDouble(index)));
            properties[output.InDegreePropertyId] = new GraphProperty(
                output.InDegreePropertyId, GraphPropertyValue.FromInt64(incoming));
            properties[output.OutDegreePropertyId] = new GraphProperty(
                output.OutDegreePropertyId, GraphPropertyValue.FromInt64(outgoing));
            properties[output.TotalDegreePropertyId] = new GraphProperty(
                output.TotalDegreePropertyId, GraphPropertyValue.FromInt64(checked(incoming + outgoing)));
            properties[output.CommunityPropertyId] = new GraphProperty(
                output.CommunityPropertyId, GraphPropertyValue.FromInt64(community.Get(index)));
            properties[output.ResultVersionPropertyId] = new GraphProperty(
                output.ResultVersionPropertyId, GraphPropertyValue.FromString(resultVersion));
            int[] uniquePropertyIds = ResolveUniquePropertyIds(output, vertex, properties);
            transaction.UpsertVertex(
                vertex.Id,
                vertex.ElementVersion,
                vertex.Labels,
                properties.Values.OrderBy(static property => property.PropertyId),
                uniquePropertyIds);
        }
        _ = transaction.Commit(cancellationToken);
    }

    private static int[] ResolveUniquePropertyIds(
        GraphOfflineAlgorithmGraphOutput output,
        GraphVertexRecord vertex,
        IReadOnlyDictionary<int, GraphProperty> properties)
    {
        var result = new List<int>();
        foreach (IGrouping<int, GraphUniqueIndexDefinition> group in output.UniqueIndexes
                     .Where(static definition => definition.ElementType == GraphElementType.Vertex)
                     .GroupBy(static definition => definition.PropertyId))
        {
            if (!properties.ContainsKey(group.Key))
                continue;
            int matchingLabels = vertex.Labels.Count(label => group.Any(definition => definition.LabelId == label));
            if (matchingLabels == 0)
                continue;
            if (matchingLabels != vertex.Labels.Count)
            {
                throw new NotSupportedException(
                    $"Graph offline property output 无法为多标签 vertex {vertex.Id} 精确保留 label-specific unique property {group.Key}；请改用 Table output。");
            }
            result.Add(group.Key);
        }
        result.Sort();
        return [.. result];
    }

    private static GraphAlgorithmLongVector OpenResultVector(
        string workspace,
        string fileName,
        GraphOfflineAlgorithmState state,
        long memoryBudget)
        => GraphAlgorithmLongVector.Open(
            Path.Combine(workspace, fileName), state.VertexCount, memoryBudget);

    private static Guid CreateBatchRequestId(Guid operationId, long batchIndex)
    {
        Span<byte> source = stackalloc byte[24];
        operationId.TryWriteBytes(source[..16]);
        BinaryPrimitives.WriteInt64LittleEndian(source[16..], batchIndex);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(source, hash);
        var result = new Guid(hash[..16]);
        return result == Guid.Empty ? new Guid(hash[16..]) : result;
    }

    private static void ValidateOutput(GraphOfflineAlgorithmOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        switch (output)
        {
            case GraphOfflineAlgorithmGraphOutput graph:
                ArgumentNullException.ThrowIfNull(graph.UniqueIndexes);
                int[] propertyIds =
                [
                    graph.ComponentPropertyId,
                    graph.PageRankPropertyId,
                    graph.InDegreePropertyId,
                    graph.OutDegreePropertyId,
                    graph.TotalDegreePropertyId,
                    graph.CommunityPropertyId,
                    graph.ResultVersionPropertyId,
                ];
                if (propertyIds.Any(static id => id <= 0)
                    || propertyIds.Distinct().Count() != propertyIds.Length)
                {
                    throw new ArgumentException("Graph offline output property ID 必须为互不相同的正数。", nameof(output));
                }
                var seen = new HashSet<GraphUniqueIndexDefinition>();
                foreach (GraphUniqueIndexDefinition definition in graph.UniqueIndexes)
                {
                    if (!Enum.IsDefined(definition.ElementType)
                        || definition.LabelId.Value <= 0
                        || definition.PropertyId <= 0
                        || !seen.Add(definition))
                    {
                        throw new ArgumentException("Graph offline output unique index 声明无效或重复。", nameof(output));
                    }
                    if (definition.ElementType == GraphElementType.Vertex
                        && propertyIds.Contains(definition.PropertyId))
                    {
                        throw new ArgumentException("Graph offline 结果属性不能声明为 unique。", nameof(output));
                    }
                }
                break;
            case GraphOfflineAlgorithmTableOutput table:
                ValidateTableSchema(table.Table.Schema);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(output));
        }
    }

    private static void ValidateTableSchema(TableSchema actual)
    {
        TableSchema expected = GraphOfflineAlgorithmTable.CreateSchema(actual.Name);
        if (!actual.PrimaryKey.SequenceEqual(expected.PrimaryKey, StringComparer.Ordinal)
            || actual.Columns.Count != expected.Columns.Count)
        {
            throw new ArgumentException("Graph offline algorithm 结果表 schema 不匹配。", nameof(actual));
        }
        for (int index = 0; index < expected.Columns.Count; index++)
        {
            TableColumn left = actual.Columns[index];
            TableColumn right = expected.Columns[index];
            if (left.Name != right.Name
                || left.DataType != right.DataType
                || left.IsNullable != right.IsNullable
                || left.IsPrimaryKey != right.IsPrimaryKey)
            {
                throw new ArgumentException("Graph offline algorithm 结果表 schema 不匹配。", nameof(actual));
            }
        }
    }

    private static byte[] ComputeConfigurationHash(
        GraphOfflineAlgorithmOutput output,
        GraphOfflineAlgorithmOptions options)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendInt32(hash, (int)output.Kind);
        AppendInt32(hash, options.MaxPageRankIterations);
        AppendInt64(hash, BitConverter.DoubleToInt64Bits(options.PageRankDampingFactor));
        AppendInt64(hash, BitConverter.DoubleToInt64Bits(options.PageRankTolerance));
        AppendInt32(hash, options.MaxCommunityIterations);
        AppendInt32(hash, options.OutputBatchSize);
        switch (output)
        {
            case GraphOfflineAlgorithmGraphOutput graph:
                AppendInt32(hash, graph.ComponentPropertyId);
                AppendInt32(hash, graph.PageRankPropertyId);
                AppendInt32(hash, graph.InDegreePropertyId);
                AppendInt32(hash, graph.OutDegreePropertyId);
                AppendInt32(hash, graph.TotalDegreePropertyId);
                AppendInt32(hash, graph.CommunityPropertyId);
                AppendInt32(hash, graph.ResultVersionPropertyId);
                foreach (GraphUniqueIndexDefinition definition in graph.UniqueIndexes
                             .OrderBy(static value => value.ElementType)
                             .ThenBy(static value => value.LabelId)
                             .ThenBy(static value => value.PropertyId))
                {
                    AppendInt32(hash, (int)definition.ElementType);
                    AppendInt32(hash, definition.LabelId.Value);
                    AppendInt32(hash, definition.PropertyId);
                }
                break;
            case GraphOfflineAlgorithmTableOutput table:
                AppendString(hash, table.Table.Schema.Name);
                foreach (TableColumn column in table.Table.Schema.Columns)
                {
                    AppendString(hash, column.Name);
                    AppendInt32(hash, (int)column.DataType);
                    AppendInt32(hash, column.IsNullable ? 1 : 0);
                    AppendInt32(hash, column.IsPrimaryKey ? 1 : 0);
                }
                break;
        }
        return hash.GetHashAndReset();
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> encoded = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(encoded, value);
        hash.AppendData(encoded);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> encoded = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(encoded, value);
        hash.AppendData(encoded);
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        byte[] encoded = Encoding.UTF8.GetBytes(value);
        AppendInt32(hash, encoded.Length);
        hash.AppendData(encoded);
    }

    private static FileStream OpenAppendFile(string path, long durableLength)
    {
        var stream = new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            64 * 1024,
            FileOptions.WriteThrough);
        if (stream.Length < durableLength)
        {
            stream.Dispose();
            throw new InvalidDataException($"Graph offline spill '{path}' 短于 durable continuation。");
        }
        stream.SetLength(durableLength);
        stream.Position = durableLength;
        return stream;
    }

    private static FileStream OpenEdgeReader(string workspace, long edgeCount)
    {
        string path = Path.Combine(workspace, EdgesFileName);
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length != checked(edgeCount * EdgeRecordSize))
        {
            stream.Dispose();
            throw new InvalidDataException("Graph offline edge spill 长度无效。");
        }
        return stream;
    }

    private static (long Source, long Target) ReadEdge(Stream stream)
    {
        Span<byte> encoded = stackalloc byte[EdgeRecordSize];
        stream.ReadExactly(encoded);
        return (
            BinaryPrimitives.ReadInt64LittleEndian(encoded),
            BinaryPrimitives.ReadInt64LittleEndian(encoded[sizeof(long)..]));
    }

    private static void WriteLengthPrefixed(Stream stream, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, value.Length);
        stream.Write(length);
        stream.Write(value);
    }

    private static byte[] ReadLengthPrefixed(Stream stream)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        stream.ReadExactly(length);
        int count = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (count is <= 0 or > GraphRecordEnvelopeCodec.MaxEncodedRecordBytes)
            throw new InvalidDataException("Graph offline vertex record spill 长度无效。");
        byte[] result = new byte[count];
        stream.ReadExactly(result);
        return result;
    }

    private static string GetWorkspace(GraphStore store, Guid operationId)
        => Path.Combine(store.OfflineAlgorithmRootDirectory, operationId.ToString("N"));

    private static string GetRankPath(string workspace, int generation)
        => Path.Combine(workspace, $"{RankFilePrefix}{generation}.bin");

    private static string GetCommunityPath(string workspace, int generation)
        => Path.Combine(workspace, $"{CommunityFilePrefix}{generation}.bin");

    private static void ReplaceFile(string temporaryPath, string destinationPath)
    {
        File.Move(temporaryPath, destinationPath, overwrite: true);
        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
            SonnetDB.Wal.DirectoryFsync.FlushRequired(directory);
    }

    private static long MeasureSpillBytes(string workspace)
    {
        long total = 0;
        foreach (string path in Directory.EnumerateFiles(workspace, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFileName(path), GraphOfflineAlgorithmManifestCodec.FileName, StringComparison.Ordinal)
                || path.EndsWith(".tmp", StringComparison.Ordinal))
            {
                continue;
            }
            total = checked(total + new FileInfo(path).Length);
        }
        return total;
    }

    private static void CleanupCompletedWorkspace(string workspace)
    {
        if (!Directory.Exists(workspace))
            return;
        foreach (string path in Directory.EnumerateFiles(workspace))
        {
            if (!string.Equals(Path.GetFileName(path), GraphOfflineAlgorithmManifestCodec.FileName, StringComparison.Ordinal))
                TryDelete(path);
        }
        foreach (string directory in Directory.EnumerateDirectories(workspace))
            TryDeleteDirectory(directory);
    }

    private static void ResetDirectory(string path)
    {
        TryDeleteDirectory(path);
        Directory.CreateDirectory(path);
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (FileNotFoundException)
        {
        }
    }

    private static void WriteVoteCount(Stream output, long vertexIndex, long label, long count)
    {
        Span<byte> encoded = stackalloc byte[VoteRecordSize];
        BinaryPrimitives.WriteInt64LittleEndian(encoded, vertexIndex);
        BinaryPrimitives.WriteInt64LittleEndian(encoded[8..], label);
        BinaryPrimitives.WriteInt64LittleEndian(encoded[16..], count);
        output.Write(encoded);
    }

    private readonly record struct CommunityVote(long VertexIndex, long Label) : IComparable<CommunityVote>
    {
        public int CompareTo(CommunityVote other)
        {
            int comparison = VertexIndex.CompareTo(other.VertexIndex);
            return comparison != 0 ? comparison : Label.CompareTo(other.Label);
        }
    }

    private readonly record struct VotePriority(long VertexIndex, long Label) : IComparable<VotePriority>
    {
        public int CompareTo(VotePriority other)
        {
            int comparison = VertexIndex.CompareTo(other.VertexIndex);
            return comparison != 0 ? comparison : Label.CompareTo(other.Label);
        }
    }

    private sealed class VoteRunReader : IDisposable
    {
        private readonly FileStream _stream;

        internal VoteRunReader(string path)
        {
            _stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            if (_stream.Length % VoteRecordSize != 0)
                throw new InvalidDataException("Graph community vote run 长度无效。");
        }

        internal long VertexIndex { get; private set; }

        internal long Label { get; private set; }

        internal long Count { get; private set; }

        internal bool MoveNext()
        {
            Span<byte> encoded = stackalloc byte[VoteRecordSize];
            int first = _stream.Read(encoded);
            if (first == 0)
                return false;
            while (first < encoded.Length)
            {
                int read = _stream.Read(encoded[first..]);
                if (read == 0)
                    throw new EndOfStreamException("Graph community vote run 被截断。");
                first += read;
            }
            VertexIndex = BinaryPrimitives.ReadInt64LittleEndian(encoded);
            Label = BinaryPrimitives.ReadInt64LittleEndian(encoded[8..]);
            Count = BinaryPrimitives.ReadInt64LittleEndian(encoded[16..]);
            if (VertexIndex < 0 || Label <= 0 || Count <= 0)
                throw new InvalidDataException("Graph community vote run 字段无效。");
            return true;
        }

        public void Dispose() => _stream.Dispose();
    }
}
