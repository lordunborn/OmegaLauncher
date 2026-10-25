using System.Diagnostics;
using OmegaLauncher.Core.Models;
using OmegaLauncher.Core.News;
using OmegaLauncher.Core.Platform;
using OmegaLauncher.Core.Updates;
using OmegaLauncher.Core.Serialization;

namespace OmegaLauncher.Core.ClientManagement
{
    public enum ClientLauncherInitializationResult
    {
        Success,
        ClientNotFound,
        ExecutableNotFound
    }

    public class ClientLauncher
    {
        private const string ServerListFileName = "OmegaLauncher.ServerList.json";
        private const string LaunchConfigFileName = "OmegaLauncher.LaunchConfig.json";
        private const string GuiConfigFileName = "OmegaLauncher.GuiConfig.json";

        private readonly ClientDirectory _clientDirectory = new();

        public ServerManager ServerManager { get; } = new();
        public NewsFeed NewsFeed { get; } = new();

        public LaunchConfig LaunchConfig { get; private set; }
        public GuiConfig GuiConfig { get; private set; }
        public DeploymentConfig Deployment { get; private set; }

        public ClientMetadata ClientMetadata { get => _clientDirectory.ClientMetadata; }
        public string[] Locales { get => _clientDirectory.Locales; }
        public string ClientRoot { get => _clientDirectory.RootPath; }

        public ClientLauncher()
        {
        }

        public ClientLauncherInitializationResult Initialize(string clientPath)
        {
            // Settings are read from disk only the first time. Initialize runs again when the player picks another game folder,
            // and reloading here would throw that new folder away (it is only written to disk by SaveData).
            if (GuiConfig == null)
            {
                LaunchConfig = JsonHelper.LoadOrCreate(LaunchConfigFileName, JsonContext.Default.LaunchConfig);
                GuiConfig = JsonHelper.LoadOrCreate(GuiConfigFileName, JsonContext.Default.GuiConfig);

                // ServerList
                List<ServerInfo> serverList = JsonHelper.LoadOrCreate(ServerListFileName, JsonContext.Default.ListServerInfo);
                ServerManager.SetServerList(serverList);

                // A fresh install takes the servers from the deployment file that came with the launcher.
                Deployment = DeploymentConfig.Load();
                if (ServerManager.ServerCount == 0)
                {
                    foreach (ServerInfo server in Deployment.Servers)
                        ServerManager.ServerList.Add(server);
                }

                if (ServerManager.ServerCount == 0)
                    ServerManager.AddServer();
            }

            // ClientDirectory
            ClientLauncherInitializationResult directoryResult = _clientDirectory.Initialize(clientPath);
            if (directoryResult != ClientLauncherInitializationResult.Success)
                return directoryResult;

            return ClientLauncherInitializationResult.Success;
        }

        public void RefreshNewsFeedSources()
        {
            NewsFeed.Clear();

            if (GuiConfig.NewsCategoryFilter.HasFlag(NewsFeedSourceCategories.Default))
            {
                string defaultNewsFeedUrl = GuiConfig.DefaultNewsFeedUrl;
                if (string.IsNullOrWhiteSpace(defaultNewsFeedUrl) == false)
                    NewsFeed.AddSource(defaultNewsFeedUrl, "MHServerEmu", NewsFeedSourceCategories.Default);
            }

            if (GuiConfig.NewsCategoryFilter.HasFlag(NewsFeedSourceCategories.Server))
            {
                foreach (ServerInfo server in ServerManager)
                {
                    string serverNewsFeedUrl = server.NewsFeedUrl;
                    if (string.IsNullOrWhiteSpace(serverNewsFeedUrl))
                        continue;

                    NewsFeed.AddSource(serverNewsFeedUrl, server.Name, NewsFeedSourceCategories.Server);
                }
            }
        }

        public void SaveData()
        {
            JsonHelper.Save(ServerManager.ServerList, ServerListFileName, JsonContext.Default.ListServerInfo);
            JsonHelper.Save(LaunchConfig, LaunchConfigFileName, JsonContext.Default.LaunchConfig);
            JsonHelper.Save(GuiConfig, GuiConfigFileName, JsonContext.Default.GuiConfig);
        }

        /// <summary>Why the last <see cref="Launch"/> or <see cref="BuildLaunchCommand"/> failed, in words for the player.</summary>
        public string LastLaunchError { get; private set; } = string.Empty;

        /// <summary>Everything this machine offers for running the Windows game (Linux only), best first, ignoring the player's choice.</summary>
        public List<GameRunner> DetectRunners()
        {
            if (OperatingSystem.IsLinux() == false)
                return new();

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string[] pathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator);
            return LinuxRunner.Detect(home, pathDirectories);
        }

        /// <summary>The runners to try, best first: the player's choice (environment variable or setting) and then everything detected.</summary>
        public List<GameRunner> FindRunners()
        {
            List<GameRunner> runners = new();
            if (OperatingSystem.IsLinux() == false)
                return runners;

            // An explicit choice (setting or environment variable) always goes first.
            foreach (string configured in new[] { Environment.GetEnvironmentVariable(LinuxRunner.RunnerEnvironmentVariable), GuiConfig?.RunnerPath })
            {
                GameRunner chosen = LinuxRunner.FromPath(configured);
                if (chosen != null)
                    runners.Add(chosen);
            }

            runners.AddRange(DetectRunners());

            // The player's choice is usually one of the detected ones: list each program once, keeping the first (best) position.
            return runners.DistinctBy(runner => runner.Path).ToList();
        }

        private void AddContentToken(List<string> args)
        {
            string secret = Deployment?.ContentTokenSecret;
            if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrEmpty(ClientRoot))
                return;

            // The client sends -twofactorauthname= to the server as the free-text "twofactorname" login field. Measured: -serveroverride never arrives,
            // and -webtoken makes the client log in with a token and no password.
            if (args.Any(arg => arg.StartsWith("-twofactorauthname", StringComparison.OrdinalIgnoreCase)))
                return;

            int installedVersion = OmegaLauncher.Updates.Client.LauncherStateStore.Load(new OmegaLauncher.Updates.Client.MhoFolders(ClientRoot)).ContentVersion;
            args.Add("-twofactorauthname=" + ContentToken.Create(installedVersion, secret));
        }

        public LaunchCommand BuildLaunchCommand()
        {
            LastLaunchError = string.Empty;

            // Use the specified server if index is within range
            ServerInfo serverInfo = ServerManager.GetServer(LaunchConfig.ServerIndex);
            if (serverInfo == null)
            {
                LastLaunchError = "No server is selected.";
                return null;
            }

            // Prefer Win64 if supported and Win32 is not forced. Otherwise launch Win32.
            string executablePath = _clientDirectory.Supports64
                ? LaunchConfig.Force32Bit ? _clientDirectory.ExecutablePath32 : _clientDirectory.ExecutablePath64
                : _clientDirectory.ExecutablePath32;

            if (string.IsNullOrWhiteSpace(executablePath))
            {
                LastLaunchError = "The game executable was not found.";
                return null;
            }

            List<string> args = [.. LaunchConfig.ToLaunchArguments(serverInfo, _clientDirectory.ClientMetadata.Flags)];

            // Tell the server which version of the launcher-managed files this client has (it can refuse clients that are out of date).
            AddContentToken(args);

            // On a Steam Deck the game starts at the Deck's screen size, unless the player picked a resolution or switched this off.
            if (GuiConfig.DeckResolution && SteamDeck.IsSteamDeck() && LaunchConfig.ForceCustomResolution == false &&
                args.Any(arg => arg.StartsWith("-ResX", StringComparison.OrdinalIgnoreCase)) == false)
            {
                SteamDeck.AddResolutionArguments(args);
            }

            if (OperatingSystem.IsLinux() == false)
                return new LaunchCommand { FileName = executablePath, Arguments = [.. args] };

            // Linux: the game is a Windows program, so it goes through Proton, umu or Wine.
            GameRunner runner = FindRunners().FirstOrDefault();
            if (runner == null)
            {
                LastLaunchError = "No way to run the game was found. Install Proton-GE (ProtonUp-Qt), umu-launcher or Wine, or set the runner in Options.";
                return null;
            }

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string prefix = string.IsNullOrWhiteSpace(GuiConfig.PrefixPath) ? LinuxRunner.DefaultPrefix(home) : GuiConfig.PrefixPath;
            string steamRoot = LinuxRunner.SteamRoots(home).FirstOrDefault();

            if (runner.Kind == RunnerKind.Proton && steamRoot == null)
            {
                LastLaunchError = "Proton needs a Steam installation to run, but Steam was not found.";
                return null;
            }

            return LinuxRunner.Build(runner, executablePath, args, prefix, steamRoot);
        }

        public bool Launch()
        {
            LaunchCommand command = BuildLaunchCommand();
            if (command == null)
                return false;

            try
            {
                if (OperatingSystem.IsLinux())
                    Directory.CreateDirectory(command.Environment.GetValueOrDefault("STEAM_COMPAT_DATA_PATH") ?? command.Environment.GetValueOrDefault("WINEPREFIX") ?? ".");

                Process.Start(command.ToStartInfo());
                return true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
            {
                LastLaunchError = "The game could not be started: " + ex.Message;
                return false;
            }
        }

        public string GetClientDebugInfo()
        {
            return _clientDirectory.GetVersionDebugInfo();
        }

        public static string GetInitializationResultText(ClientLauncherInitializationResult result)
        {
            return result switch
            {
                ClientLauncherInitializationResult.Success              => "Game directory initialized successfully.",
                ClientLauncherInitializationResult.ClientNotFound       => "Marvel Heroes not found.",
                ClientLauncherInitializationResult.ExecutableNotFound   => "Marvel Heroes executable not found.",
                _                                                       => "Unknown error.",
            };
        }
    }
}
