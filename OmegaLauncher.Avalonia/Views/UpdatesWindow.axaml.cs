using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OmegaLauncher.Avalonia.Views.Dialogs;
using OmegaLauncher.Core.ClientManagement;
using OmegaLauncher.Core.Updates;
using OmegaLauncher.Updates.Client;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OmegaLauncher.Avalonia.Views
{
    /// <summary>Details and manual controls for the client updates: check, update, full verify, restore the original files, pick the game folder.</summary>
    public partial class UpdatesWindow : Window
    {
        private readonly ClientLauncher _clientLauncher;
        private readonly UpdateManager _updateManager;

        private CancellationTokenSource _cts;
        private bool _busy;

        /// <summary>The player chose a different game folder; the main window has to start over with it.</summary>
        public bool ClientFolderChanged { get; private set; }

        public UpdatesWindow()
        {
            InitializeComponent();
        }

        public UpdatesWindow(ClientLauncher clientLauncher, UpdateManager updateManager) : this()
        {
            _clientLauncher = clientLauncher;
            _updateManager = updateManager;

            FolderTextBlock.Text = clientLauncher.ClientRoot;

            if (updateManager == null)
            {
                StatusTextBlock.Text = "Updates are not set up for this launcher (no Mho.Deployment.json next to it).";
                SetButtons(false);
                return;
            }

            if (updateManager.LastCheck != null)
                ShowCheck(updateManager.LastCheck);

            SetButtons(false);
        }

        private void ShowCheck(UpdateCheckResult check)
        {
            StatusTextBlock.Text = UpdateManager.DescribeLong(check, _updateManager.GetClientVersion());
        }

        private void SetButtons(bool busy)
        {
            bool canAct = busy == false && _updateManager != null;
            CheckButton.IsEnabled = canAct;
            VerifyButton.IsEnabled = canAct;
            RevertButton.IsEnabled = canAct;
            ChangeFolderButton.IsEnabled = busy == false;
            UpdateButton.IsEnabled = canAct && _updateManager.LastCheck != null && _updateManager.LastCheck.UpToDate == false && _updateManager.LastCheck.LauncherTooOld == false;
            CloseButton.Content = busy ? "Cancel" : "Close";
        }

        private async Task RunAsync(Func<CancellationToken, Task> work)
        {
            if (_busy)
                return;

            _busy = true;
            _cts = new();
            UpdateProgressBar.IsVisible = true;
            UpdateProgressBar.Value = 0;
            SetButtons(true);

            try
            {
                await work(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                StatusTextBlock.Text = "Cancelled. Nothing was left half-installed.";
            }
            catch (UpdateException ex)
            {
                StatusTextBlock.Text = ex.Message;
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = "Something went wrong: " + ex.Message;
            }
            finally
            {
                _busy = false;
                _cts.Dispose();
                _cts = null;
                UpdateProgressBar.IsVisible = false;
                SetButtons(false);
            }
        }

        private async Task CheckAsync(bool full, CancellationToken cancellationToken)
        {
            StatusTextBlock.Text = full ? "Checking every file..." : "Checking for updates...";

            Progress<PlanProgress> progress = new(p =>
            {
                UpdateProgressBar.Value = p.FilesTotal > 0 ? 100.0 * p.FilesDone / p.FilesTotal : 0;
                StatusTextBlock.Text = $"Checking {p.CurrentFile} ({p.FilesDone}/{p.FilesTotal})";
            });

            UpdateCheckResult check = await _updateManager.CheckAsync(full, progress, cancellationToken);
            ShowCheck(check);
        }

        private async Task UpdateAsync(CancellationToken cancellationToken)
        {
            Progress<UpdateProgress> progress = new(p =>
            {
                UpdateProgressBar.Value = p.BytesToDownload > 0 ? 100.0 * p.BytesDownloaded / p.BytesToDownload : 0;
                StatusTextBlock.Text = $"Updating {p.CurrentFile} ({p.FilesDone}/{p.FilesTotal}), " +
                                       $"{UpdateManager.FormatBytes(p.BytesDownloaded)} of {UpdateManager.FormatBytes(p.BytesToDownload)}";
            });

            ApplyResult result = await _updateManager.ApplyAsync(progress, cancellationToken);
            ShowCheck(_updateManager.LastCheck);
            StatusTextBlock.Text = $"Updated {result.FilesUpdated} file(s), downloaded {UpdateManager.FormatBytes(result.BytesDownloaded)}.\n\n" + StatusTextBlock.Text;
        }

        #region Event Handlers

        private async void CheckButton_Click(object sender, RoutedEventArgs e)
        {
            await RunAsync(ct => CheckAsync(false, ct));
        }

        private async void UpdateButton_Click(object sender, RoutedEventArgs e)
        {
            await RunAsync(UpdateAsync);
        }

        private async void VerifyButton_Click(object sender, RoutedEventArgs e)
        {
            await RunAsync(ct => CheckAsync(true, ct));
        }

        private async void RevertButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult confirm = await MessageBoxWindow.Show(this,
                "This puts the original game files back and removes the files the launcher added. You will have to update again before you can play on the server.",
                "Restore original files", MessageBoxType.OKCancel);

            if (confirm != DialogResult.OK)
                return;

            await RunAsync(async ct =>
            {
                StatusTextBlock.Text = "Restoring original files...";
                Progress<PlanProgress> progress = new(p => UpdateProgressBar.Value = p.FilesTotal > 0 ? 100.0 * p.FilesDone / p.FilesTotal : 0);
                int reverted = await Task.Run(() => _updateManager.Revert(progress, ct), ct);
                await CheckAsync(false, ct);
                StatusTextBlock.Text = $"Restored {reverted} file(s).\n\n" + StatusTextBlock.Text;
            });
        }

        private async void ChangeFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select your Marvel Heroes folder", AllowMultiple = false });
            string picked = folders.FirstOrDefault()?.TryGetLocalPath();
            if (picked == null)
                return;

            string root = ClientHelper.ResolveRoot(picked);
            if (root == null)
            {
                await MessageBoxWindow.Show(this, "That is not a Marvel Heroes folder. Pick the one that contains UnrealEngine3 and Data.", "Game folder");
                return;
            }

            _clientLauncher.GuiConfig.ClientPath = root;
            _clientLauncher.SaveData();
            ClientFolderChanged = true;
            Close();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                _cts?.Cancel();
            else
                Close();
        }

        private void Window_Closing(object sender, WindowClosingEventArgs e)
        {
            // Never close in the middle of an update: ask it to stop and let the player close again.
            if (_busy)
            {
                e.Cancel = true;
                _cts?.Cancel();
            }
        }

        #endregion
    }
}
