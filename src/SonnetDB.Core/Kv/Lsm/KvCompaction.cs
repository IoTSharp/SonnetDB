namespace SonnetDB.Kv.Lsm;

/// <summary>leveled compaction 选项。</summary>
internal sealed record KvCompactionOptions
{
    /// <summary>默认选项。</summary>
    internal static KvCompactionOptions Default { get; } = new();

    /// <summary>L0 文件数达到该值时触发 L0→L1 compaction。</summary>
    internal int Level0CompactionTrigger { get; init; } = 4;

    /// <summary>L1 目标总字节数。</summary>
    internal long MaxBytesForLevelBase { get; init; } = 64L * 1024 * 1024;

    /// <summary>相邻层目标字节数倍率。</summary>
    internal int LevelSizeMultiplier { get; init; } = 10;

    /// <summary>compaction 输出文件的目标字节数（只在 user key 边界切分）。</summary>
    internal long TargetFileSizeBytes { get; init; } = 64L * 1024 * 1024;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Level0CompactionTrigger, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBytesForLevelBase, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(LevelSizeMultiplier, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(TargetFileSizeBytes, 1024);
    }

    /// <summary>第 <paramref name="level"/> 层（≥1）的目标总字节数。</summary>
    internal long TargetBytes(int level)
    {
        double target = MaxBytesForLevelBase * Math.Pow(LevelSizeMultiplier, level - 1);
        return target >= long.MaxValue ? long.MaxValue : (long)target;
    }
}

/// <summary>一次 compaction 的输入与输出层。</summary>
/// <param name="InputLevel">输入层；全量 compaction 为 -1。</param>
/// <param name="OutputLevel">输出层。</param>
/// <param name="Inputs">需要删除的全部输入文件（含输出层中重叠的文件）。</param>
/// <param name="IsTrivialMove">单文件且下层无重叠时，只在 manifest 中改层号，不重写数据。</param>
internal sealed record KvCompactionJob(int InputLevel, int OutputLevel, IReadOnlyList<KvLsmFileMetadata> Inputs, bool IsTrivialMove);

/// <summary>compaction 选择：L0 按文件数，L1+ 按“实际字节 / 目标字节”评分，取评分最高且大于 1 的层。</summary>
internal static class KvCompactionPicker
{
    /// <summary>选择下一次自动 compaction；无需 compaction 时返回 null。</summary>
    /// <param name="version">当前版本。</param>
    /// <param name="options">compaction 选项。</param>
    /// <param name="compactPointers">各层上次 compaction 的最大 key，用于轮转选择文件；会被更新。</param>
    internal static KvCompactionJob? Pick(KvVersion version, KvCompactionOptions options, byte[]?[] compactPointers)
    {
        double bestScore = (double)version.Files(0).Count / options.Level0CompactionTrigger;
        int bestLevel = 0;
        for (int level = 1; level < KvVersion.LevelCount - 1; level++)
        {
            long bytes = version.Files(level).Sum(static f => f.FileSize);
            double score = (double)bytes / options.TargetBytes(level);
            if (score > bestScore)
            {
                bestScore = score;
                bestLevel = level;
            }
        }
        if (bestScore < 1)
            return null;

        if (bestLevel == 0)
        {
            // L0 文件范围可重叠：一次取全部 L0，使剩余（之后刷盘的）L0 一定比 L1 新。
            IReadOnlyList<KvLsmFileMetadata> level0 = version.Files(0);
            (byte[] min, byte[] max) = UserRange(level0);
            var inputs = new List<KvLsmFileMetadata>(level0);
            inputs.AddRange(Overlapping(version.Files(1), min, max));
            return new KvCompactionJob(0, 1, inputs, IsTrivialMove: false);
        }

        IReadOnlyList<KvLsmFileMetadata> files = version.Files(bestLevel);
        byte[]? pointer = compactPointers[bestLevel];
        KvLsmFileMetadata chosen = files[0];
        if (pointer is not null)
        {
            foreach (KvLsmFileMetadata file in files)
            {
                if (KvInternalKey.Compare(file.SmallestKey, pointer) > 0)
                {
                    chosen = file;
                    break;
                }
            }
        }
        compactPointers[bestLevel] = chosen.LargestKey;

        byte[] low = KvInternalKey.UserKey(chosen.SmallestKey).ToArray();
        byte[] high = KvInternalKey.UserKey(chosen.LargestKey).ToArray();
        List<KvLsmFileMetadata> below = Overlapping(version.Files(bestLevel + 1), low, high);
        var all = new List<KvLsmFileMetadata> { chosen };
        all.AddRange(below);
        return new KvCompactionJob(bestLevel, bestLevel + 1, all, IsTrivialMove: below.Count == 0);
    }

    /// <summary>把全部文件合并到最底层（手动全量 compaction）；没有文件时返回 null。</summary>
    internal static KvCompactionJob? Full(KvVersion version)
    {
        var inputs = version.AllFiles.ToList();
        return inputs.Count == 0 ? null : new KvCompactionJob(-1, KvVersion.LevelCount - 1, inputs, IsTrivialMove: false);
    }

    internal static List<KvLsmFileMetadata> Overlapping(IReadOnlyList<KvLsmFileMetadata> files, ReadOnlySpan<byte> minUser, ReadOnlySpan<byte> maxUser)
    {
        var result = new List<KvLsmFileMetadata>();
        foreach (KvLsmFileMetadata file in files)
        {
            if (KvInternalKey.UserKey(file.LargestKey).SequenceCompareTo(minUser) >= 0
                && KvInternalKey.UserKey(file.SmallestKey).SequenceCompareTo(maxUser) <= 0)
            {
                result.Add(file);
            }
        }
        return result;
    }

    private static (byte[] Min, byte[] Max) UserRange(IReadOnlyList<KvLsmFileMetadata> files)
    {
        byte[] min = KvInternalKey.UserKey(files[0].SmallestKey).ToArray();
        byte[] max = KvInternalKey.UserKey(files[0].LargestKey).ToArray();
        foreach (KvLsmFileMetadata file in files)
        {
            ReadOnlySpan<byte> smallest = KvInternalKey.UserKey(file.SmallestKey);
            ReadOnlySpan<byte> largest = KvInternalKey.UserKey(file.LargestKey);
            if (smallest.SequenceCompareTo(min) < 0)
                min = smallest.ToArray();
            if (largest.SequenceCompareTo(max) > 0)
                max = largest.ToArray();
        }
        return (min, max);
    }
}

/// <summary>
/// 执行一次 compaction：合并输入、按回收规则过滤，并在 user key 边界按目标大小切分输出。
/// </summary>
/// <remarks>
/// <para>
/// 回收规则（快照持有自己的版本引用，旧文件在快照释放前不会删除，因此 compaction 不必为存活快照保留旧版本）：
/// 同一 user key 只保留最新版本；最新版本为墓碑、或带 TTL 且已在 <c>ttlHorizonTicks</c> 之前到期时，
/// 若更低的层不再含该 key 则整体丢弃，否则写为墓碑以继续遮蔽下层旧版本。
/// </para>
/// <para>输出文件绝不在同一 user key 的多个版本之间切分，保证 L1+ 按 user key 互不重叠。</para>
/// </remarks>
internal static class KvCompactionExecutor
{
    /// <summary>执行 compaction 并返回待提交的编辑（尚未 <see cref="KvVersionSet.LogAndApply"/>）。</summary>
    internal static KvVersionEdit Run(
        KvCompactionJob job,
        KvVersion version,
        KvVersionSet versions,
        Func<KvLsmFileMetadata, IKvInternalIterator> open,
        KvSstWriterOptions writerOptions,
        KvCompactionOptions options,
        long ttlHorizonTicks,
        CancellationToken cancellationToken)
    {
        var edit = new KvVersionEdit();
        foreach (KvLsmFileMetadata input in job.Inputs)
            edit.DeletedFiles.Add((input.Level, input.FileNumber));

        if (job.IsTrivialMove)
        {
            KvLsmFileMetadata file = job.Inputs[0];
            edit.AddedFiles.Add(file with { Level = job.OutputLevel });
            return edit;
        }

        var children = new List<IKvInternalIterator>();
        foreach (KvLsmFileMetadata file in job.Inputs.Where(static f => f.Level == 0).OrderByDescending(static f => f.FileNumber))
            children.Add(open(file));
        for (int level = 1; level < KvVersion.LevelCount; level++)
        {
            var levelFiles = job.Inputs.Where(f => f.Level == level).ToList();
            if (levelFiles.Count > 0)
                children.Add(new KvLevelIterator(levelFiles, open));
        }

        var outputs = new List<long>();
        KvSstWriter? writer = null;
        long writerNumber = 0;
        try
        {
            using var merged = new KvMergingIterator(children);
            byte[] currentUser = new byte[64];
            int currentUserLength = -1;
            long processed = 0;

            for (merged.SeekToFirst(); merged.Valid; merged.MoveNext())
            {
                if ((++processed & 1023) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                ReadOnlySpan<byte> key = merged.Key;
                ReadOnlySpan<byte> user = KvInternalKey.UserKey(key);
                bool newUser = currentUserLength < 0 || !user.SequenceEqual(currentUser.AsSpan(0, currentUserLength));
                if (newUser)
                {
                    if (currentUser.Length < user.Length)
                        currentUser = new byte[Math.Max(user.Length, currentUser.Length * 2)];
                    user.CopyTo(currentUser);
                    currentUserLength = user.Length;

                    if (writer is not null && writer.EstimatedFileSize >= options.TargetFileSizeBytes)
                    {
                        FinishWriter(writer, writerNumber, job.OutputLevel, versions, edit);
                        writer.Dispose();
                        writer = null;
                    }
                }
                else
                {
                    // 同一 user key 的更新版本已经处理：旧版本对所有将来的快照都不可见。
                    continue;
                }

                KvEntryKind kind = KvInternalKey.Kind(key);
                long expires = merged.ExpiresAtUtcTicks;
                bool expired = kind == KvEntryKind.Put && expires != 0 && expires <= ttlHorizonTicks;
                bool deleteLike = kind == KvEntryKind.Delete || expired;
                if (deleteLike && IsBaseLevelForKey(version, job.OutputLevel, user))
                    continue;

                if (writer is null)
                {
                    writerNumber = versions.NewFileNumber();
                    outputs.Add(writerNumber);
                    writer = new KvSstWriter(
                        KvLsmPaths.SstPath(versions.RootDirectory, writerNumber),
                        writerOptions with { Generation = versions.Generation });
                }

                if (deleteLike)
                    writer.Add(KvInternalKey.Encode(user, KvInternalKey.Sequence(key), KvEntryKind.Delete), []);
                else
                    writer.Add(key, merged.Value, expires);
            }

            if (writer is not null)
            {
                FinishWriter(writer, writerNumber, job.OutputLevel, versions, edit);
                writer.Dispose();
                writer = null;
            }
            return edit;
        }
        catch
        {
            writer?.Dispose();
            foreach (long number in outputs)
                versions.ReleasePendingOutput(number);
            throw;
        }
    }

    /// <summary>输出层之下的各层都不含 <paramref name="userKey"/> 时返回 true，此时墓碑可以丢弃。</summary>
    internal static bool IsBaseLevelForKey(KvVersion version, int outputLevel, ReadOnlySpan<byte> userKey)
    {
        for (int level = outputLevel + 1; level < KvVersion.LevelCount; level++)
        {
            foreach (KvLsmFileMetadata file in version.Files(level))
            {
                if (userKey.SequenceCompareTo(KvInternalKey.UserKey(file.SmallestKey)) >= 0
                    && userKey.SequenceCompareTo(KvInternalKey.UserKey(file.LargestKey)) <= 0)
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static void FinishWriter(KvSstWriter writer, long number, int level, KvVersionSet versions, KvVersionEdit edit)
    {
        KvSstProperties properties = writer.Finish();
        long size = new FileInfo(KvLsmPaths.SstPath(versions.RootDirectory, number)).Length;
        edit.AddedFiles.Add(KvLsmFileMetadata.FromProperties(number, level, size, properties));
    }
}
