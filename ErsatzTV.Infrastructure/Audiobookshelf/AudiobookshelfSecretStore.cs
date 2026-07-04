using ErsatzTV.Core;
using ErsatzTV.Core.Audiobookshelf;
using ErsatzTV.Core.Interfaces.Audiobookshelf;
using Newtonsoft.Json;

namespace ErsatzTV.Infrastructure.Audiobookshelf;

public class AudiobookshelfSecretStore : IAudiobookshelfSecretStore
{
    public Task<Unit> DeleteAll() => SaveSecrets(new AudiobookshelfSecrets());

    public Task<AudiobookshelfSecrets> ReadSecrets() =>
        File.ReadAllTextAsync(FileSystemLayout.AudiobookshelfSecretsPath)
            .Map(JsonConvert.DeserializeObject<AudiobookshelfSecrets>)
            .Map(s => Optional(s).IfNone(new AudiobookshelfSecrets()));

    public Task<Unit> SaveSecrets(AudiobookshelfSecrets secrets) =>
        Some(JsonConvert.SerializeObject(secrets)).Match(
            s => File.WriteAllTextAsync(FileSystemLayout.AudiobookshelfSecretsPath, s).ToUnit(),
            Task.FromResult(Unit.Default));
}
