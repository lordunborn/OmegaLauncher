using System.Net;
using System.Text.Json;
using OmegaLauncher.Updates;
using OmegaLauncher.Updates.Client;
using OmegaLauncher.Updates.Content;
using OmegaLauncher.Updates.Models;
using OmegaLauncher.Updates.Publishing;
using OmegaLauncher.Updates.Security;

namespace OmegaLauncher.Publisher
{
    /// <summary>
    /// Command line tool for whoever publishes client updates (not shipped to players).
    ///   keygen   &lt;dir&gt;                      make the signing key pair
    ///   baseline &lt;pristine client&gt;           hash the stock client once
    ///   publish  &lt;edited client&gt;             chunk the managed files and write the host folder
    ///   verify   &lt;url or folder&gt;             check a published folder the way a launcher would
    ///   serve    &lt;folder&gt;                    tiny local web server for testing
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] is "help" or "-h" or "--help")
            {
                Console.WriteLine(Usage);
                return args.Length == 0 ? 1 : 0;
            }

            try
            {
                Options options = Options.Parse(args.Skip(1).ToArray());

                return args[0].ToLowerInvariant() switch
                {
                    "keygen" => KeyGen(options),
                    "baseline" => Baseline(options),
                    "publish" => Publish(options),
                    "verify" => Verify(options).GetAwaiter().GetResult(),
                    "serve" => Serve(options),
                    "publish-launcher" => PublishLauncher(options),
                    "player-check" => PlayerCheck(options, apply: false).GetAwaiter().GetResult(),
                    "player-update" => PlayerCheck(options, apply: true).GetAwaiter().GetResult(),
                    "player-revert" => PlayerRevert(options),
                    _ => Fail($"Unknown command: {args[0]}")
                };
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return Fail(ex.Message);
            }
        }

        private const string Usage = @"OmegaLauncher.Publisher

  keygen   <dir>                              Make the signing key pair (private PEM stays here, public key goes into the launcher).
  baseline <pristine client> [--version 1.53.0.203] [--out baseline.json]
  publish  <edited client> --baseline baseline.json --key private.pem [--out host] [--notes ""text""]
           [--exclude pattern]... [--exclude-file file] [--cache hashes.json] [--min-launcher N] [--optional] [--new-version] [--content-version N]
  verify   <url or folder> --pubkey <file or base64> [--deep]
  publish-launcher <built program> --platform win-x64 --version 1.0.1 --key private.pem [--out host] [--notes ""text""]
  serve    <folder> [--port 8099]

Developer commands that act like a player's launcher (use a COPY of a client, never your working one):
  player-check  <client folder> --host <url or folder> --pubkey <file or base64> [--full]
  player-update <client folder> --host <url or folder> --pubkey <file or base64> [--full]
  player-revert <client folder>
";

        private static int Fail(string message)
        {
            Console.Error.WriteLine("Error: " + message);
            return 1;
        }

        private static int KeyGen(Options o)
        {
            string dir = o.Positional(0, "output folder");
            Directory.CreateDirectory(dir);
            string privatePath = Path.Combine(dir, "mho-private.pem");
            string publicPath = Path.Combine(dir, "mho-public.key");

            if (File.Exists(privatePath))
                throw new IOException($"{privatePath} already exists; refusing to overwrite a signing key.");

            var (privatePem, publicKey) = ManifestSigner.GenerateKeyPair();
            File.WriteAllText(privatePath, privatePem);
            File.WriteAllText(publicPath, publicKey);

            Console.WriteLine($"Private key: {privatePath}   (KEEP SECRET, back it up, never commit it)");
            Console.WriteLine($"Public key:  {publicPath}");
            Console.WriteLine(publicKey);
            return 0;
        }

        private static int Baseline(Options o)
        {
            string root = o.Positional(0, "pristine client folder");
            string version = o.Get("version") ?? "1.53.0.203";
            string output = o.Get("out") ?? "baseline.json";

            ClientBaseline baseline = BaselineBuilder.Build(root, version, Console.WriteLine);
            File.WriteAllBytes(output, JsonSerializer.SerializeToUtf8Bytes(baseline, UpdatesJsonContext.Default.ClientBaseline));
            Console.WriteLine($"Wrote {output}: {baseline.Files.Count} files, {baseline.Files.Sum(f => f.Size) / 1073741824.0:F1} GB.");
            return 0;
        }

        private static int PublishLauncher(Options o)
        {
            LauncherRelease release = LauncherPublisher.Publish(
                o.Positional(0, "built launcher program"),
                o.Require("platform"),
                o.Require("version"),
                o.Require("key"),
                o.Get("out") ?? "host",
                o.Get("notes") ?? string.Empty);

            Console.WriteLine($"Launcher {release.Version}: {string.Join(", ", release.Files.Select(f => f.Platform))}");
            Console.WriteLine($"Upload the CONTENTS of '{o.Get("out") ?? "host"}/launcher' to the host's client/launcher folder (program files first, latest.sig and latest.json last).");
            return 0;
        }

        private static int Publish(Options o)
        {
            PublishOptions options = new()
            {
                ClientRoot = o.Positional(0, "edited client folder"),
                BaselinePath = o.Require("baseline"),
                PrivateKeyPemPath = o.Require("key"),
                OutputDirectory = o.Get("out") ?? "host",
                Notes = o.Get("notes") ?? string.Empty,
                HashCachePath = o.Get("cache"),
                MinLauncherVersion = int.TryParse(o.Get("min-launcher"), out int minLauncher) ? minLauncher : 0,
                Force = o.Flag("optional") == false,
                ForceNewVersion = o.Flag("new-version"),
                ContentVersionOverride = int.TryParse(o.Get("content-version"), out int contentVersion) ? contentVersion : 0,
                Log = Console.WriteLine
            };

            options.ExcludePatterns.AddRange(o.GetAll("exclude"));
            string excludeFile = o.Get("exclude-file");
            if (excludeFile != null)
                options.ExcludePatterns.AddRange(File.ReadAllLines(excludeFile));

            PublishResult result = ClientPublisher.Publish(options);

            if (result.NoChange)
                return 0;

            Console.WriteLine();
            Console.WriteLine($"Content version {result.ContentVersion}");
            Console.WriteLine($"  added:   {result.Added.Count}");
            Console.WriteLine($"  changed: {result.Changed.Count}");
            Console.WriteLine($"  removed: {result.Removed.Count}");
            Console.WriteLine($"Upload the CONTENTS of '{options.OutputDirectory}' to the host's client folder (chunks first, manifest.json last).");
            return 0;
        }

        private static async Task<int> Verify(Options o)
        {
            string target = o.Positional(0, "url or folder");
            string key = o.Require("pubkey");
            if (File.Exists(key))
                key = File.ReadAllText(key).Trim();

            IContentSource source = target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? new HttpContentSource(target)
                : new DirectoryContentSource(target);

            VerifyResult result = await HostVerifier.VerifyAsync(source, key, o.Flag("deep"), Console.WriteLine);

            if (result.Ok)
            {
                Console.WriteLine($"OK: content version {result.ContentVersion}, {result.Files} files, {result.ChunksChecked} chunks ({result.BytesChecked / 1048576.0:F1} MB) verified.");
                return 0;
            }

            foreach (string error in result.Errors.Take(50))
                Console.Error.WriteLine("  " + error);
            Console.Error.WriteLine($"FAILED: {result.Errors.Count} problem(s).");
            return 2;
        }

        private static IContentSource OpenSource(string target) =>
            target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? new HttpContentSource(target)
                : new DirectoryContentSource(target);

        private static async Task<int> PlayerCheck(Options o, bool apply)
        {
            string client = o.Positional(0, "client folder");
            string key = o.Require("pubkey");
            if (File.Exists(key))
                key = File.ReadAllText(key).Trim();

            IContentSource source = OpenSource(o.Require("host"));
            string root = ClientInspector.FindClientRoot(client) ?? throw new ArgumentException("Not a Marvel Heroes folder.");

            ClientInfo info = ClientInspector.Inspect(root);
            Console.WriteLine($"Client: {root}");
            Console.WriteLine($"Version: {info.Version ?? "unknown"}");

            Progress<PlanProgress> planProgress = new(p => { if (p.CurrentFile != null) Console.Write($"\r  checking {p.FilesDone + 1}/{p.FilesTotal}  ".PadRight(40)); });
            UpdateCheckResult check = await UpdateService.CheckAsync(source, key, root, ignoreCache: o.Flag("full"), progress: planProgress);
            Console.WriteLine();

            Console.WriteLine($"Installed content version: {check.InstalledContentVersion}; published: {check.Manifest.ContentVersion} ({(check.Manifest.Force ? "required" : "optional")})");
            if (check.BaseCheck != null)
                Console.WriteLine($"Stock files checked: {check.BaseCheck.FilesChecked}, missing {check.BaseCheck.Missing.Count}, wrong size {check.BaseCheck.WrongSize.Count}");

            if (check.UpToDate)
            {
                Console.WriteLine("Up to date.");
                return 0;
            }

            Console.WriteLine($"To update: {check.Plan.FileCount} files, download up to {check.Plan.DownloadBytes / 1048576.0:F1} MB (rebuilding {check.Plan.RebuildBytes / 1048576.0:F1} MB), remove {check.Plan.Removed.Count}.");
            foreach (PlannedFile f in check.Plan.FilesToUpdate)
                Console.WriteLine($"  {f.Manifest.Path}  {(f.Exists ? "update" : "new")}  {f.DownloadBytes / 1048576.0:F1} MB to download");

            if (apply == false)
                return 0;

            DateTime lastPrint = DateTime.MinValue;
            Progress<UpdateProgress> progress = new(p =>
            {
                if ((DateTime.UtcNow - lastPrint).TotalMilliseconds < 500) return;
                lastPrint = DateTime.UtcNow;
                Console.Write($"\r  [{p.FilesDone}/{p.FilesTotal}] {p.BytesDownloaded / 1048576.0:F1}/{p.BytesToDownload / 1048576.0:F1} MB  {p.CurrentFile}".PadRight(150)[..150]);
            });

            ApplyResult result = await UpdateService.ApplyAsync(check, source, root, progress);
            Console.WriteLine();
            Console.WriteLine($"Done: {result.FilesUpdated} files updated, {result.FilesRemoved} removed, {result.BytesDownloaded / 1048576.0:F1} MB downloaded.");
            return 0;
        }

        private static int PlayerRevert(Options o)
        {
            string root = ClientInspector.FindClientRoot(o.Positional(0, "client folder")) ?? throw new ArgumentException("Not a Marvel Heroes folder.");
            int reverted = UpdateService.Revert(root);
            Console.WriteLine($"Reverted {reverted} files to stock.");
            return 0;
        }

        private static int Serve(Options o)
        {
            string folder = Path.GetFullPath(o.Positional(0, "folder"));
            int port = int.TryParse(o.Get("port"), out int p) ? p : 8099;

            using HttpListener listener = new();
            listener.Prefixes.Add($"http://localhost:{port}/");
            listener.Start();
            Console.WriteLine($"Serving {folder} at http://localhost:{port}/  (Ctrl+C to stop)");

            while (true)
            {
                HttpListenerContext context = listener.GetContext();
                try
                {
                    string relative = Uri.UnescapeDataString(context.Request.Url.AbsolutePath.TrimStart('/'));
                    string full = Path.GetFullPath(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)));

                    if (full.StartsWith(folder, StringComparison.OrdinalIgnoreCase) == false || File.Exists(full) == false)
                    {
                        context.Response.StatusCode = 404;
                        context.Response.Close();
                        continue;
                    }

                    byte[] data = File.ReadAllBytes(full);
                    context.Response.ContentType = "application/octet-stream";
                    context.Response.ContentLength64 = data.Length;
                    if (context.Request.HttpMethod != "HEAD")
                        context.Response.OutputStream.Write(data, 0, data.Length);
                    context.Response.Close();
                }
                catch (Exception ex) when (ex is HttpListenerException or IOException)
                {
                    // Client went away mid-response.
                }
            }
        }
    }

    internal sealed class Options
    {
        private readonly List<string> _positional = new();
        private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> FlagNames = new(StringComparer.OrdinalIgnoreCase) { "deep", "optional", "new-version", "full" };

        public static Options Parse(string[] args)
        {
            Options options = new();

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--") == false)
                {
                    options._positional.Add(args[i]);
                    continue;
                }

                string name = args[i][2..];
                if (FlagNames.Contains(name))
                {
                    options._flags.Add(name);
                    continue;
                }

                if (i + 1 >= args.Length)
                    throw new ArgumentException($"Missing value for --{name}");

                if (options._values.TryGetValue(name, out List<string> list) == false)
                    options._values[name] = list = new List<string>();

                list.Add(args[++i]);
            }

            return options;
        }

        public string Positional(int index, string description) =>
            index < _positional.Count ? _positional[index] : throw new ArgumentException($"Missing argument: {description}");

        public string Get(string name) => _values.TryGetValue(name, out List<string> list) ? list[^1] : null;
        public IEnumerable<string> GetAll(string name) => _values.TryGetValue(name, out List<string> list) ? list : Enumerable.Empty<string>();
        public string Require(string name) => Get(name) ?? throw new ArgumentException($"Missing option --{name}");
        public bool Flag(string name) => _flags.Contains(name);
    }
}
