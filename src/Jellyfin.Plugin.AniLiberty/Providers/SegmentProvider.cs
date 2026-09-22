using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AniLiberty.Api;
using Jellyfin.Plugin.AniLiberty.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;

namespace Jellyfin.Plugin.AniLiberty.Providers;

/// <summary>
/// Intro/outro segments from AniLiberty's opening/ending marks, so clients offer "Skip intro".
/// Enable "AniLiberty" under the library's media segment providers.
/// The marks are made on AniLiberty's own encode; torrents of the same release match it.
/// </summary>
public sealed class SegmentProvider : IMediaSegmentProvider
{
    private readonly ILibraryManager _library;
    private readonly AniLibertyClient _client;

    public SegmentProvider(ILibraryManager library, AniLibertyClient client)
    {
        _library = library;
        _client = client;
    }

    public string Name => Plugin.ProviderName;

    public ValueTask<bool> Supports(BaseItem item) => ValueTask.FromResult(item is Episode or Movie);

    public async Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(MediaSegmentGenerationRequest request, CancellationToken cancellationToken)
    {
        var result = new List<MediaSegmentDto>();
        if (_library.GetItemById(request.ItemId) is not Video video)
        {
            return result;
        }

        // Alternate versions carry no provider ids of their own: resolve through the primary version.
        var primary = video.PrimaryVersionId is { } primaryId && _library.GetItemById(primaryId) is Video p ? p : video;
        if (LibraryIndex.ReleaseId(primary) is not { } releaseId)
        {
            return result;
        }

        var release = await _client.GetReleaseAsync(releaseId, cancellationToken).ConfigureAwait(false);
        var episodes = release?.Episodes;
        if (episodes is not { Count: > 0 })
        {
            return result;
        }

        ReleaseEpisode? episode;
        if (primary is Movie)
        {
            episode = episodes.Count == 1 ? episodes[0] : null;
        }
        else
        {
            var number = NameNormalizer.BracketEpisodeNumber(video.Path) ?? video.IndexNumber ?? primary.IndexNumber;
            episode = number is { } n ? episodes.FirstOrDefault(e => e.Ordinal is { } o && Math.Abs(o - n) < 0.01) : null;
        }

        if (episode is null)
        {
            return result;
        }

        var runtime = video.RunTimeTicks ?? primary.RunTimeTicks ?? long.MaxValue;
        Add(result, request.ItemId, MediaSegmentType.Intro, episode.Opening, runtime);
        Add(result, request.ItemId, MediaSegmentType.Outro, episode.Ending, runtime);
        return result;
    }

    public Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken) => Task.CompletedTask;

    private static void Add(List<MediaSegmentDto> result, Guid itemId, MediaSegmentType type, SegmentMark? mark, long runtime)
    {
        if (mark?.Start is not { } start || mark.Stop is not { } stop || stop <= start || start < 0)
        {
            return;
        }

        var startTicks = (long)(start * TimeSpan.TicksPerSecond);
        var endTicks = Math.Min((long)(stop * TimeSpan.TicksPerSecond), runtime);
        if (endTicks - startTicks < TimeSpan.TicksPerSecond * 5)
        {
            // Marks shorter than a few seconds are noise.
            return;
        }

        result.Add(new MediaSegmentDto { ItemId = itemId, Type = type, StartTicks = startTicks, EndTicks = endTicks });
    }
}
