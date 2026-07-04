using ErsatzTV.Core;
using ErsatzTV.Core.Interfaces.Navidrome;
using ErsatzTV.Core.Navidrome;
using Newtonsoft.Json;

namespace ErsatzTV.Infrastructure.Navidrome;

public class NavidromeSecretStore : INavidromeSecretStore
{
    public Task<Unit> DeleteAll() => SaveSecrets(new NavidromeSecrets());

    public Task<NavidromeSecrets> ReadSecrets() =>
        File.ReadAllTextAsync(FileSystemLayout.NavidromeSecretsPath)
            .Map(JsonConvert.DeserializeObject<NavidromeSecrets>)
            .Map(s => Optional(s).IfNone(new NavidromeSecrets()));

    public Task<Unit> SaveSecrets(NavidromeSecrets secrets) =>
        Some(JsonConvert.SerializeObject(secrets)).Match(
            s => File.WriteAllTextAsync(FileSystemLayout.NavidromeSecretsPath, s).ToUnit(),
            Task.FromResult(Unit.Default));
}
