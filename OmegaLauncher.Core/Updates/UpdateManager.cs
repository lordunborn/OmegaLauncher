using System.Text;
using OmegaLauncher.Updates.Client;
using OmegaLauncher.Updates.Content;

namespace OmegaLauncher.Core.Updates
{
    /// <summary>Everything the launcher windows need from the update system, for one client folder.</summary>
    public sealed class UpdateManager : IDisposable
    {
        private readonly DeploymentConfig _deployment;
        private HttpContentSource _source;

        public string ClientRoot { get; }
        public bool Configured => _deployment.UpdatesConfigured;
        public UpdateCheckResult LastCheck { get; private set; }

        public UpdateManager(DeploymentConfig deployment, string clientRoot)
        {
            _deployment = deployment;
            ClientRoot = clientRoot;
        }

        private HttpContentSource Source => _source ??= new(_deployment.UpdateBaseUrl);

        /// <summary>Compares the client folder with what the server publishes. With <paramref name="full"/> every managed file is hashed again.</summary>
        public async Task<UpdateCheckResult> CheckAsync(bool full = false, IProgress<PlanProgress> progress = null, CancellationToken cancellationToken = default)
        {
            // Hashing the managed files is synchronous work: keep it off the caller's (UI) thread.
            LastCheck = await Task.Run(() => UpdateService.CheckAsync(Source, _deployment.UpdatePublicKey, ClientRoot, DeploymentConfig.LauncherVersion, full, progress, cancellationToken), cancellationToken);
            return LastCheck;
        }

        /// <summary>Downloads and installs what the last check found, then checks again.</summary>
        public async Task<ApplyResult> ApplyAsync(IProgress<UpdateProgress> progress = null, CancellationToken cancellationToken = default)
        {
            if (LastCheck == null)
                throw new UpdateException("Check for updates first.");

            UpdateCheckResult check = LastCheck;
            ApplyResult result = await Task.Run(() => UpdateService.ApplyAsync(check, Source, ClientRoot, progress, cancellationToken), cancellationToken);
            await CheckAsync(false, null, cancellationToken);
            return result;
        }

        public int Revert(IProgress<PlanProgress> progress = null, CancellationToken cancellationToken = default)
        {
            int reverted = UpdateService.Revert(ClientRoot, progress, cancellationToken);
            LastCheck = null;
            return reverted;
        }

        /// <summary>The version of the game executable in the client folder, or null.</summary>
        public string GetClientVersion() => ClientInspector.Inspect(ClientRoot).Version;

        public void Dispose()
        {
            _source?.Dispose();
        }

        public static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
        }

        /// <summary>One line for the main window.</summary>
        public static string DescribeShort(UpdateCheckResult check)
        {
            if (check.LauncherTooOld)
                return "This server needs a newer launcher. Download the latest launcher.";

            if (check.UpToDate)
                return $"Client files up to date (version {check.Manifest.ContentVersion})";

            string kind = check.Required ? "Update required" : "Update available";
            return $"{kind}: {check.Plan.FileCount} file(s), {FormatBytes(check.Plan.DownloadBytes)} to download";
        }

        /// <summary>Several lines for the Updates window.</summary>
        public static string DescribeLong(UpdateCheckResult check, string clientVersion)
        {
            StringBuilder sb = new();
            sb.AppendLine($"Published version: {check.Manifest.ContentVersion}    Installed version: {check.InstalledContentVersion}");
            sb.AppendLine(DescribeShort(check));

            if (string.IsNullOrWhiteSpace(check.Manifest.Notes) == false)
                sb.AppendLine("Notes: " + check.Manifest.Notes);

            if (HasWrongBaseVersion(check, clientVersion))
                sb.AppendLine($"Warning: the game is version {clientVersion} but the server needs {check.Manifest.BaseClientVersion}.");

            if (check.BaseCheck != null && check.BaseCheck.Ok == false)
            {
                sb.AppendLine($"Warning: {check.BaseCheck.Missing.Count} stock file(s) are missing and {check.BaseCheck.WrongSize.Count} have an unexpected size. " +
                              "Verify the game files in Steam, then check again.");

                const int maxListed = 30;
                foreach (string detail in check.BaseCheck.Details.Take(maxListed))
                    sb.AppendLine("  " + detail);

                if (check.BaseCheck.Details.Count > maxListed)
                    sb.AppendLine($"  ... and {check.BaseCheck.Details.Count - maxListed} more. The full list is in .mho\\stock-file-check.txt in your game folder.");
            }

            return sb.ToString().TrimEnd();
        }

        public static bool HasWrongBaseVersion(UpdateCheckResult check, string clientVersion)
        {
            return clientVersion != null && string.IsNullOrWhiteSpace(check.Manifest.BaseClientVersion) == false && clientVersion != check.Manifest.BaseClientVersion;
        }
    }
}
