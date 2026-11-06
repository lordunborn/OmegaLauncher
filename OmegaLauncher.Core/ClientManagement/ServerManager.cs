using OmegaLauncher.Core.Models;

namespace OmegaLauncher.Core.ClientManagement
{
    public class ServerManager
    {
        private readonly List<ServerInfo> _servers = new();

        // Expose server list for serialization, but do not allow GUI implementations to access it.
        internal List<ServerInfo> ServerList { get => _servers; }

        public int ServerCount { get => _servers.Count; }

        public ServerManager()
        {
        }

        public List<ServerInfo>.Enumerator GetEnumerator()
        {
            return _servers.GetEnumerator();
        }

        public ServerInfo GetServer(int index)
        {
            if (index < 0 || index >= _servers.Count)
                return null;

            return _servers[index];
        }

        public void SetServerList(IEnumerable<ServerInfo> serverList)
        {
            _servers.Clear();

            if (serverList != null)
                _servers.AddRange(serverList);
        }

        /// <summary>
        /// Adds the servers of the deployment file that the player's list lacks (matched by SiteConfigUrl), so a launcher that first ran without
        /// the deployment file still gets the server once the file arrives. A list holding only the untouched placeholder entry is replaced.
        /// Returns true if the list changed.
        /// </summary>
        public bool MergeDeploymentServers(IEnumerable<ServerInfo> deploymentServers)
        {
            List<ServerInfo> wanted = deploymentServers?.Where(s => string.IsNullOrWhiteSpace(s?.SiteConfigUrl) == false).ToList();
            if (wanted == null || wanted.Count == 0)
                return false;

            bool changed = false;

            ServerInfo placeholder = new();
            if (_servers.Count > 0 && _servers.All(s => IsPlaceholder(s, placeholder)))
            {
                _servers.Clear();
                changed = true;
            }

            foreach (ServerInfo server in wanted)
            {
                if (_servers.Any(s => SameAddress(s.SiteConfigUrl, server.SiteConfigUrl)))
                    continue;

                _servers.Add(new ServerInfo { Name = server.Name, SiteConfigUrl = server.SiteConfigUrl, NewsFeedUrl = server.NewsFeedUrl });
                changed = true;
            }

            return changed;
        }

        private static bool IsPlaceholder(ServerInfo server, ServerInfo placeholder)
        {
            return server.Name == placeholder.Name && SameAddress(server.SiteConfigUrl, placeholder.SiteConfigUrl) && string.IsNullOrWhiteSpace(server.NewsFeedUrl);
        }

        private static bool SameAddress(string a, string b)
        {
            return string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        public ServerInfo AddServer()
        {
            ServerInfo serverInfo = new();
            _servers.Add(serverInfo);
            return serverInfo;
        }

        public bool RemoveServer(int index)
        {
            if (index < 0 || index >= _servers.Count)
                return false;

            _servers.RemoveAt(index);
            return true;
        }

        public bool RemoveServer(ServerInfo serverInfo)
        {
            int index = _servers.IndexOf(serverInfo);
            return RemoveServer(index);
        }
    }
}
