using ErsatzTV.Core.Audiobookshelf;
using ErsatzTV.Core.Interfaces.Audiobookshelf;

namespace ErsatzTV.Application.Audiobookshelf;

public class GetAudiobookshelfSecretsHandler : IRequestHandler<GetAudiobookshelfSecrets, AudiobookshelfSecrets>
{
    private readonly IAudiobookshelfSecretStore _audiobookshelfSecretStore;

    public GetAudiobookshelfSecretsHandler(IAudiobookshelfSecretStore audiobookshelfSecretStore) =>
        _audiobookshelfSecretStore = audiobookshelfSecretStore;

    public Task<AudiobookshelfSecrets> Handle(GetAudiobookshelfSecrets request, CancellationToken cancellationToken) =>
        _audiobookshelfSecretStore.ReadSecrets();
}
