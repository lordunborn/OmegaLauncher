using OmegaLauncher.Core.ClientManagement;
using OmegaLauncher.Core.Models;

namespace OmegaLauncher.Updates.Tests
{
    public class ServerListMergeTests
    {
        private static ServerInfo Vps() => new() { Name = "Renaissance", SiteConfigUrl = "game.example.org/SiteConfig.xml", NewsFeedUrl = "http://game.example.org/news" };

        private static ServerManager WithList(params ServerInfo[] servers)
        {
            ServerManager manager = new();
            manager.SetServerList(servers);
            return manager;
        }

        [Fact]
        public void Merge_ListOnlyHoldsThePlaceholder_ReplacesItWithTheDeploymentServer()
        {
            ServerManager manager = WithList(new ServerInfo());

            Assert.True(manager.MergeDeploymentServers(new[] { Vps() }));

            Assert.Equal(1, manager.ServerCount);
            Assert.Equal("game.example.org/SiteConfig.xml", manager.GetServer(0).SiteConfigUrl);
        }

        [Fact]
        public void Merge_PlayerHasOtherServers_AppendsAndKeepsTheirs()
        {
            ServerInfo mine = new() { Name = "Mine", SiteConfigUrl = "10.0.0.5/SiteConfig.xml" };
            ServerManager manager = WithList(mine);

            Assert.True(manager.MergeDeploymentServers(new[] { Vps() }));

            Assert.Equal(2, manager.ServerCount);
            Assert.Same(mine, manager.GetServer(0));
            Assert.Equal("Renaissance", manager.GetServer(1).Name);
        }

        [Fact]
        public void Merge_ServerAlreadyListed_ChangesNothing()
        {
            ServerInfo existing = new() { Name = "Renamed by player", SiteConfigUrl = " game.example.org/SITECONFIG.xml" };
            ServerManager manager = WithList(existing);

            Assert.False(manager.MergeDeploymentServers(new[] { Vps() }));

            Assert.Equal(1, manager.ServerCount);
            Assert.Equal("Renamed by player", manager.GetServer(0).Name);
        }

        [Fact]
        public void Merge_EmptyList_FillsFromTheDeployment()
        {
            ServerManager manager = new();

            Assert.True(manager.MergeDeploymentServers(new[] { Vps() }));

            Assert.Equal(1, manager.ServerCount);
        }

        [Fact]
        public void Merge_NoDeploymentServers_LeavesThePlaceholderAlone()
        {
            ServerManager manager = WithList(new ServerInfo());

            Assert.False(manager.MergeDeploymentServers(new List<ServerInfo>()));
            Assert.False(manager.MergeDeploymentServers(null));

            Assert.Equal(1, manager.ServerCount);
        }
    }
}
