using System.Text.Json;
using OmegaLauncher.Updates.Chunking;
using OmegaLauncher.Updates.Models;
using OmegaLauncher.Updates.Security;

namespace OmegaLauncher.Updates.Publishing
{
    public class PublishOptions
    {
        /// <summary>The edited client to publish (the working copy).</summary>
        public string ClientRoot { get; set; }

        /// <summary>baseline.json made from the pristine client.</summary>
        public string BaselinePath { get; set; }

        /// <summary>Folder that becomes the host's client folder (manifest, signature, chunks).</summary>
        public string OutputDirectory { get; set; }

        public string PrivateKeyPemPath { get; set; }

        /// <summary>Extra exclude patterns on top of <see cref="ClientPublisher.DefaultExcludes"/>.</summary>
        public List<string> ExcludePatterns { get; set; } = new();

        public string Notes { get; set; } = string.Empty;
        public bool Force { get; set; } = true;
        public int MinLauncherVersion { get; set; }

        /// <summary>Publish a new content version even if nothing changed.</summary>
        public bool ForceNewVersion { get; set; }

        /// <summary>Publish under this exact content version instead of previous + 1 (0 = automatic). Must be higher than the previous version; implies a new version even if no file changed.</summary>
        public int ContentVersionOverride { get; set; }

        /// <summary>Optional path of the publisher's file hash cache (speeds up repeated publishes).</summary>
        public string HashCachePath { get; set; }

        public Action<string> Log { get; set; }
        public CancellationToken CancellationToken { get; set; }
    }

    public class PublishResult
    {
        public bool NoChange { get; set; }
        public int ContentVersion { get; set; }
        public List<string> Added { get; } = new();
        public List<string> Changed { get; } = new();
        public List<string> Removed { get; } = new();
        public long ManagedBytes { get; set; }
        public int NewChunks { get; set; }
        public long NewChunkBytes { get; set; }
        public int ReusedChunks { get; set; }
    }

    /// <summary>
    /// Builds the content folder for the host. The managed set is every file of the edited client that differs from, or is missing in, the pristine
    /// baseline, minus the excludes: nothing has to be listed by hand, so an edited file cannot be forgotten.
    /// </summary>
    public static class ClientPublisher
    {
        public static readonly string[] DefaultExcludes =
        {
            "*.bak*", "*.premerge", "*.dmp", "*.log", "*.part", "*.tmp",
            "Logs/**", "UnrealEngine3/MarvelGame/Logs/**",
            ".mho/**", ".mho-backup/**", "launcher-state.json",
            "BiFrost/**", "Bifrost.*", "OmegaLauncher/**", "OmegaLauncher.*", "Mho.Deployment.json",
        };

        public static PublishResult Publish(PublishOptions options)
        {
            Action<string> log = options.Log ?? (_ => { });
            string root = Path.GetFullPath(options.ClientRoot);
            string output = Path.GetFullPath(options.OutputDirectory);

            ClientBaseline baseline = JsonSerializer.Deserialize(File.ReadAllBytes(options.BaselinePath), UpdatesJsonContext.Default.ClientBaseline)
                ?? throw new InvalidDataException("Baseline could not be read.");
            Dictionary<string, BaselineFile> baselineByPath = baseline.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);

            GlobMatcher excludes = new(DefaultExcludes.Concat(options.ExcludePatterns));

            HashCache cache = null;
            if (string.IsNullOrEmpty(options.HashCachePath) == false && File.Exists(options.HashCachePath))
                cache = JsonSerializer.Deserialize(File.ReadAllBytes(options.HashCachePath), UpdatesJsonContext.Default.HashCache);
            cache ??= new HashCache();

            // 1. Which files are managed?
            List<string> localPaths = BaselineBuilder.EnumerateRelativePaths(root).Where(p => excludes.IsMatch(p) == false).ToList();
            log($"Comparing {localPaths.Count} files with the baseline...");

            PublishResult result = new();
            List<(string Path, string CanonicalPath)> managed = new();
            object gate = new();
            int compared = 0;

            Parallel.ForEach(localPaths, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2), CancellationToken = options.CancellationToken }, path =>
            {
                string full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
                long size = new FileInfo(full).Length;

                if (baselineByPath.TryGetValue(path, out BaselineFile stock) == false)
                {
                    lock (gate) { managed.Add((path, path)); result.Added.Add(path); }
                }
                else if (stock.Size != size)
                {
                    lock (gate) { managed.Add((path, stock.Path)); result.Changed.Add(stock.Path); }
                }
                else if (BaselineBuilder.HashFileCached(root, path, cache) != stock.Sha256)
                {
                    lock (gate) { managed.Add((path, stock.Path)); result.Changed.Add(stock.Path); }
                }

                int n = Interlocked.Increment(ref compared);
                if (n % 2000 == 0)
                    log($"  {n} / {localPaths.Count}");
            });

            managed = managed.OrderBy(m => m.CanonicalPath, StringComparer.Ordinal).ToList();
            result.Added.Sort(StringComparer.Ordinal);
            result.Changed.Sort(StringComparer.Ordinal);
            log($"Managed files: {managed.Count} ({result.Added.Count} added, {result.Changed.Count} changed)");

            if (string.IsNullOrEmpty(options.HashCachePath) == false)
                ManifestIO.WriteAtomic(options.HashCachePath, JsonSerializer.SerializeToUtf8Bytes(cache, UpdatesJsonContext.Default.HashCache));

            // 2. Chunk and store.
            Directory.CreateDirectory(output);
            ManifestFile[] entries = new ManifestFile[managed.Count];
            int newChunks = 0, reusedChunks = 0;
            long newChunkBytes = 0;
            int filesDone = 0;

            Parallel.For(0, managed.Count, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = options.CancellationToken }, i =>
            {
                string full = Path.Combine(root, managed[i].Path.Replace('/', Path.DirectorySeparatorChar));
                List<ChunkRef> chunks = new();

                using FileStream fs = new(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
                FastCdc.Result chunked = FastCdc.Chunk(fs, (offset, data, sha) =>
                {
                    chunks.Add(new ChunkRef { Sha256 = sha, Size = data.Length });

                    string dest = ChunkFilePath(output, sha);
                    if (File.Exists(dest))
                    {
                        Interlocked.Increment(ref reusedChunks);
                        return;
                    }

                    byte[] stored = ChunkCodec.Encode(data);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    string temp = dest + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllBytes(temp, stored);

                    try { File.Move(temp, dest, false); }
                    catch (IOException) { File.Delete(temp); }   // another file produced the same chunk first

                    Interlocked.Increment(ref newChunks);
                    Interlocked.Add(ref newChunkBytes, stored.Length);
                }, options.CancellationToken);

                entries[i] = new ManifestFile { Path = managed[i].CanonicalPath, Size = chunked.Size, Sha256 = chunked.Sha256, Chunks = chunks };

                int n = Interlocked.Increment(ref filesDone);
                log($"  [{n}/{managed.Count}] {managed[i].CanonicalPath} ({chunked.Size / 1048576.0:F1} MB, {chunked.ChunkCount} chunks)");
            });

            result.NewChunks = newChunks;
            result.NewChunkBytes = newChunkBytes;
            result.ReusedChunks = reusedChunks;
            result.ManagedBytes = entries.Sum(e => e.Size);

            // 3. Compare with the previous manifest: version number, removed files, no-change detection.
            string manifestPath = Path.Combine(output, UpdateConstants.ManifestFileName);
            ClientManifest previous = File.Exists(manifestPath) ? ManifestIO.Deserialize(File.ReadAllBytes(manifestPath)) : null;

            HashSet<string> currentPaths = new(entries.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
            List<string> removed = new();
            if (previous != null)
            {
                removed.AddRange(previous.Removed);
                removed.AddRange(previous.Files.Select(f => f.Path));
                removed = removed.Where(p => currentPaths.Contains(p) == false).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.Ordinal).ToList();
                result.Removed.AddRange(previous.Files.Select(f => f.Path).Where(p => currentPaths.Contains(p) == false));

                bool same = previous.Files.Count == entries.Length
                    && previous.Files.Zip(entries).All(pair => pair.First.Path == pair.Second.Path && pair.First.Sha256 == pair.Second.Sha256)
                    && previous.Removed.SequenceEqual(removed);

                if (options.ContentVersionOverride > 0 && options.ContentVersionOverride <= previous.ContentVersion)
                    throw new ArgumentException($"--content-version {options.ContentVersionOverride} must be higher than the already published version {previous.ContentVersion}.");

                if (same && options.ForceNewVersion == false && options.ContentVersionOverride <= 0)
                {
                    result.NoChange = true;
                    result.ContentVersion = previous.ContentVersion;
                    log($"Nothing changed since content version {previous.ContentVersion}; nothing published.");
                    return result;
                }
            }

            // 4. Manifest last: a reader never sees a manifest whose chunks are not on the host yet.
            ClientManifest manifest = new()
            {
                ContentVersion = options.ContentVersionOverride > 0 ? options.ContentVersionOverride : (previous?.ContentVersion ?? 0) + 1,
                MinLauncherVersion = options.MinLauncherVersion,
                BaseClientVersion = baseline.BaseClientVersion,
                Force = options.Force,
                Notes = options.Notes ?? string.Empty,
                PublishedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                Files = entries.ToList(),
                Removed = removed
            };

            byte[] manifestBytes = ManifestIO.Serialize(manifest);
            string signature = ManifestSigner.Sign(manifestBytes, File.ReadAllText(options.PrivateKeyPemPath));

            BaselineSizes sizes = new() { BaseClientVersion = baseline.BaseClientVersion, Files = baseline.Files.Select(f => new BaselineSizeEntry { Path = f.Path, Size = f.Size }).ToList() };
            ManifestIO.WriteAtomic(Path.Combine(output, UpdateConstants.BaselineSizesFileName), JsonSerializer.SerializeToUtf8Bytes(sizes, UpdatesJsonContext.Default.BaselineSizes));
            ManifestIO.WriteAtomic(Path.Combine(output, UpdateConstants.SignatureFileName), signature);
            ManifestIO.WriteAtomic(manifestPath, manifestBytes);

            result.ContentVersion = manifest.ContentVersion;
            log($"Published content version {manifest.ContentVersion}: {entries.Length} files, {result.ManagedBytes / 1048576.0:F1} MB managed, {newChunks} new chunks ({newChunkBytes / 1048576.0:F1} MB stored), {reusedChunks} reused.");
            return result;
        }

        public static string ChunkFilePath(string outputDirectory, string sha256) =>
            Path.Combine(outputDirectory, UpdateConstants.ChunkDirectory, sha256[..2], sha256);
    }
}
