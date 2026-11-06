using System.Text.Json;
using OmegaLauncher.Updates.Content;
using OmegaLauncher.Updates.Models;
using OmegaLauncher.Updates.Publishing;

namespace OmegaLauncher.Updates.Client
{
    public sealed class BaseCheckResult
    {
        public int FilesChecked { get; set; }
        public List<string> Missing { get; } = new();
        public List<string> WrongSize { get; } = new();

        /// <summary>One readable line per flagged file (path plus expected and found size), so a player can say exactly which files look wrong.</summary>
        public List<string> Details { get; } = new();

        public bool Ok => Missing.Count == 0 && WrongSize.Count == 0;
    }

    /// <summary>
    /// Fast sanity check of a player's stock client against the published list of stock file sizes (existence and size only, no hashing of 24 GB).
    /// Files the launcher manages are skipped, because they are supposed to differ from stock, and so are missing empty (0 byte) files.
    /// </summary>
    public static class BaseCheck
    {
        // Files only the Steam install has: a client obtained another way (depot download) legitimately lacks them.
        private static readonly GlobMatcher Ignored = new(new[] { "*.vdf", "EmptySteamDepot/**", "InstallDependencies/**", "licenses.pdf", "*.dmp" });

        public static async Task<BaseCheckResult> RunAsync(IContentSource source, string clientRoot, ClientManifest manifest, CancellationToken cancellationToken = default)
        {
            byte[] data = await source.GetBytesAsync(UpdateConstants.BaselineSizesFileName, cancellationToken);
            if (data == null)
                return null;

            BaselineSizes sizes = JsonSerializer.Deserialize(data, UpdatesJsonContext.Default.BaselineSizes);
            HashSet<string> managed = new(manifest.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            foreach (string removed in manifest.Removed)
                managed.Add(removed);

            BaseCheckResult result = new();

            foreach (BaselineSizeEntry entry in sizes.Files)
            {
                if (managed.Contains(entry.Path) || Ignored.IsMatch(entry.Path))
                    continue;

                cancellationToken.ThrowIfCancellationRequested();
                result.FilesChecked++;

                string path = ClientPathResolver.Resolve(clientRoot, entry.Path);
                FileInfo info = new(path);
                if (info.Exists == false && entry.Size == 0)
                {
                    // Empty stock files (placeholder configs, empty localization files) are routinely dropped by zip tools, cloud sync and depot
                    // downloaders, and nothing depends on them, so a missing one is not a problem.
                    result.FilesChecked--;
                }
                else if (info.Exists == false)
                {
                    result.Missing.Add(entry.Path);
                    result.Details.Add($"MISSING    {entry.Path} (expected {entry.Size} bytes)");
                }
                else if (info.Length != entry.Size)
                {
                    result.WrongSize.Add(entry.Path);
                    result.Details.Add($"WRONG SIZE {entry.Path} (expected {entry.Size} bytes, found {info.Length})");
                }
            }

            return result;
        }
    }
}
