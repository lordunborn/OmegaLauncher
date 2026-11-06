using System.Text.Json;
using OmegaLauncher.Updates.Client;
using OmegaLauncher.Updates.Content;
using OmegaLauncher.Updates.Models;

namespace OmegaLauncher.Updates.Tests
{
    public sealed class BaseCheckTests : IDisposable
    {
        private readonly string _temp = Path.Combine(Path.GetTempPath(), "mho-basecheck-" + Guid.NewGuid().ToString("N"));

        public BaseCheckTests()
        {
            Directory.CreateDirectory(Path.Combine(_temp, "host"));
            Directory.CreateDirectory(Path.Combine(_temp, "client", "Data"));

            BaselineSizes sizes = new() { BaseClientVersion = "1.53.0.203" };
            sizes.Files.Add(new() { Path = "Data/real.bin", Size = 10 });
            sizes.Files.Add(new() { Path = "Data/empty.ini", Size = 0 });
            sizes.Files.Add(new() { Path = "Data/Localization/Properties.CZE", Size = 0 });
            File.WriteAllBytes(Path.Combine(_temp, "host", UpdateConstants.BaselineSizesFileName), JsonSerializer.SerializeToUtf8Bytes(sizes, UpdatesJsonContext.Default.BaselineSizes));
        }

        public void Dispose()
        {
            try { Directory.Delete(_temp, true); } catch (IOException) { }
        }

        private Task<BaseCheckResult> Run() =>
            BaseCheck.RunAsync(new DirectoryContentSource(Path.Combine(_temp, "host")), Path.Combine(_temp, "client"), new ClientManifest());

        [Fact]
        public async Task MissingEmptyFiles_AreNotFlagged()
        {
            File.WriteAllBytes(Path.Combine(_temp, "client", "Data", "real.bin"), new byte[10]);

            BaseCheckResult result = await Run();

            Assert.True(result.Ok, string.Join(",", result.Details));
            Assert.Equal(1, result.FilesChecked);
        }

        [Fact]
        public async Task MissingOrResizedRealFiles_StillFlagged()
        {
            File.WriteAllBytes(Path.Combine(_temp, "client", "Data", "real.bin"), new byte[3]);

            BaseCheckResult result = await Run();
            Assert.Equal(new[] { "Data/real.bin" }, result.WrongSize);

            File.Delete(Path.Combine(_temp, "client", "Data", "real.bin"));
            result = await Run();
            Assert.Equal(new[] { "Data/real.bin" }, result.Missing);
        }
    }
}
