using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Model.IO;

namespace Jellyfin.Plugin.AniLiberty.Providers;

/// <summary>
/// The AniLibria archive mixes series folders with single-file movies/OVAs at its root. In a TV library those
/// root files become orphan episodes; they belong to the movie library (built from symlinks), so skip them here.
/// </summary>
public sealed class RootFileIgnoreRule : IResolverIgnoreRule
{
    public bool ShouldIgnore(FileSystemMetadata fileInfo, BaseItem? parent)
    {
        if (fileInfo.IsDirectory
            || parent is not CollectionFolder { CollectionType: CollectionType.tvshows } library
            || !fileInfo.Name.Contains("anili", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var dir = Path.GetDirectoryName(fileInfo.FullName)?.TrimEnd('/');
        return library.PhysicalLocations.Any(p => string.Equals(p.TrimEnd('/'), dir, StringComparison.Ordinal));
    }
}
