using System.Text;
using OmegaLauncher.Updates.Content;
using OmegaLauncher.Updates.Models;
using OmegaLauncher.Updates.Publishing;
using OmegaLauncher.Updates.Security;

namespace OmegaLauncher.Updates.Client
{
    public sealed class ManifestRejectedException : UpdateException
    {
        public ManifestRejectedException(string message) : base(message) { }
    }

    public sealed class UpdateCheckResult
    {
        public ClientManifest Manifest { get; init; }
        public UpdatePlan Plan { get; init; }
        public LauncherState State { get; init; }

        /// <summary>Result of the stock-client sanity check, or null if the host does not publish baseline sizes.</summary>
        public BaseCheckResult BaseCheck { get; init; }

        /// <summary>The published content needs a newer launcher than this one.</summary>
        public bool LauncherTooOld { get; init; }

        public int InstalledContentVersion => State.ContentVersion;
        public bool UpToDate => Plan.UpToDate;

        /// <summary>The player has to update before playing.</summary>
        public bool Required => Manifest.Force && UpToDate == false;
    }

    /// <summary>The launcher's entry points: check what is published against a client folder, apply it, or revert it.</summary>
    public static class UpdateService
    {
        /// <summary>How long the server gets to answer the "what is published" questions before the launcher gives up and lets the player continue.</summary>
        public static readonly TimeSpan DefaultServerTimeout = TimeSpan.FromSeconds(15);

        public static async Task<UpdateCheckResult> CheckAsync(IContentSource source, string publicKeyBase64, string clientRoot, int launcherVersion = int.MaxValue,
            bool ignoreCache = false, IProgress<PlanProgress> progress = null, CancellationToken cancellationToken = default, TimeSpan? serverTimeout = null)
        {
            TimeSpan timeout = serverTimeout ?? DefaultServerTimeout;
            byte[] manifestBytes;
            byte[] signatureBytes;

            using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(timeout);

                try
                {
                    manifestBytes = await source.GetBytesAsync(UpdateConstants.ManifestFileName, deadline.Token);
                    signatureBytes = await source.GetBytesAsync(UpdateConstants.SignatureFileName, deadline.Token);
                }
                catch (IOException ex)
                {
                    throw new UpdateException("The update server could not be reached. Check your connection and try again.", ex);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested == false)
                {
                    throw new UpdateException("The update server did not answer in time. Check your connection and try again.");
                }
            }

            if (manifestBytes == null || signatureBytes == null)
                throw new UpdateException("The update server has no published client files yet.");

            if (ManifestSigner.Verify(manifestBytes, Encoding.UTF8.GetString(signatureBytes), publicKeyBase64) == false)
                throw new ManifestRejectedException("The update information is not signed by the server's key. It was rejected, nothing was changed.");

            ClientManifest manifest = ManifestIO.Deserialize(manifestBytes);
            List<string> problems = ManifestIO.Validate(manifest);
            if (problems.Count > 0)
                throw new ManifestRejectedException("The update information is invalid: " + problems[0]);

            MhoFolders folders = new(clientRoot);
            LauncherState state = LauncherStateStore.Load(folders);

            UpdatePlan plan = UpdatePlanner.Plan(manifest, clientRoot, state, ignoreCache, progress, cancellationToken);

            // Remember what is already up to date, so the next check does not hash it again.
            UpdatePlanner.RememberUpToDate(plan, state);
            if (state.Files.Count > 0)
                LauncherStateStore.Save(folders, state);

            BaseCheckResult baseCheck = null;
            using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(timeout);

                try
                {
                    baseCheck = await BaseCheck.RunAsync(source, clientRoot, manifest, deadline.Token);
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
                {
                    // The base check is advice only.
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested == false)
                {
                    // Too slow: skip the advice rather than hold the player up.
                }
            }

            WriteBaseCheckReport(folders, baseCheck);

            return new UpdateCheckResult
            {
                Manifest = manifest,
                Plan = plan,
                State = state,
                BaseCheck = baseCheck,
                LauncherTooOld = manifest.MinLauncherVersion > launcherVersion
            };
        }

        /// <summary>Saves the full list of flagged stock files to .mho/stock-file-check.txt (and removes an old report once the files are fine), so a player can send it.</summary>
        private static void WriteBaseCheckReport(MhoFolders folders, BaseCheckResult baseCheck)
        {
            if (baseCheck == null)
                return;

            string reportPath = Path.Combine(folders.Mho, "stock-file-check.txt");

            try
            {
                if (baseCheck.Ok)
                {
                    File.Delete(reportPath);
                    return;
                }

                Directory.CreateDirectory(folders.Mho);
                File.WriteAllLines(reportPath, baseCheck.Details.Prepend($"{baseCheck.Details.Count} of {baseCheck.FilesChecked} stock files look wrong ({DateTime.Now:yyyy-MM-dd HH:mm})"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The report is a convenience only.
            }
        }

        public static Task<ApplyResult> ApplyAsync(UpdateCheckResult check, IContentSource source, string clientRoot, IProgress<UpdateProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (check.LauncherTooOld)
                throw new UpdateException("This update needs a newer launcher. Update the launcher first.");

            return UpdateApplier.ApplyAsync(check.Plan, source, clientRoot, check.State, progress, cancellationToken);
        }

        public static int Revert(string clientRoot, IProgress<PlanProgress> progress = null, CancellationToken cancellationToken = default)
        {
            MhoFolders folders = new(clientRoot);
            LauncherState state = LauncherStateStore.Load(folders);
            return UpdateReverter.RevertAll(clientRoot, state, progress, cancellationToken);
        }
    }
}
