using ErsatzTV.Core.Interfaces.Navidrome;
using ErsatzTV.Core.Navidrome;

namespace ErsatzTV.Application.Navidrome;

public class GetNavidromeSecretsHandler : IRequestHandler<GetNavidromeSecrets, NavidromeSecrets>
{
    private readonly INavidromeSecretStore _navidromeSecretStore;

    public GetNavidromeSecretsHandler(INavidromeSecretStore navidromeSecretStore) =>
        _navidromeSecretStore = navidromeSecretStore;

    public Task<NavidromeSecrets> Handle(GetNavidromeSecrets request, CancellationToken cancellationToken) =>
        _navidromeSecretStore.ReadSecrets();
}
