using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Kura.Core.Models;

namespace Kura.App.ViewModels;

public sealed record TagGroup(string Title, TagCategory Category, IReadOnlyList<Tag> Tags);

/// <summary>A post as the grid, info panel and viewer see it.</summary>
public sealed partial class PostItemViewModel : ObservableObject
{
    private static readonly (TagCategory Cat, string Title)[] GroupOrder =
    [
        (TagCategory.Artist, "Artist"), (TagCategory.Copyright, "Copyright"), (TagCategory.Character, "Character"),
        (TagCategory.Species, "Species"), (TagCategory.General, "General"), (TagCategory.Lore, "Lore"),
        (TagCategory.Meta, "Meta"), (TagCategory.Invalid, "Invalid"),
    ];

    private IReadOnlyList<TagGroup>? _groups;

    public PostItemViewModel(Post post, int index, int pageNumber, string serverName, IReadOnlyList<Tag> resolvedTags)
    {
        Post = post;
        Index = index;
        PageNumber = pageNumber;
        ServerName = serverName;
        Tags = resolvedTags;
    }

    public Post Post { get; }
    public int Index { get; }
    public int PageNumber { get; }
    public string ServerName { get; }
    public IReadOnlyList<Tag> Tags { get; }

    [ObservableProperty] private bool _isFavorite;

    public string RemoteId => Post.RemoteId;
    public double Aspect => Post.AspectRatio;
    public string? Referer => Post.PostPageReferer;

    /// <summary>Best thumbnail source: preview, else sample, else the file itself when it's a still image.</summary>
    public string? ThumbUrl =>
        Post.Preview?.Url ?? Post.Sample?.Url ?? (Post.File.Kind is MediaKind.Image or MediaKind.Gif ? Post.File.Url : null);

    public bool IsVideo => Post.Kind == MediaKind.Video;
    public bool IsGif => Post.Kind == MediaKind.Gif;
    public bool IsAnimated => IsVideo || IsGif;

    /// <summary>Badge text for non-JPEG/PNG formats (GIF, WEBM, MP4…), null otherwise.</summary>
    public string? FormatBadge => Post.File.Extension switch
    {
        "jpg" or "jpeg" or "png" or "" => null,
        var e => e.ToUpperInvariant(),
    };

    public string DimensionsText
    {
        get
        {
            var (w, h) = Post.Dimensions;
            return w > 1 ? $"{w} × {h}" : "Unknown size";
        }
    }

    public string RatingText => Ratings.Display(Post.Rating);
    public string ScoreText => Post.Score.ToString("N0", CultureInfo.CurrentCulture);
    public string? CreatedText => Post.CreatedAt?.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture);
    public IReadOnlyList<string> Sources => SourceLinks.Split(Post.Source);
    public string Title => $"#{Post.RemoteId}";
    public string? PostTitle => string.IsNullOrWhiteSpace(Post.Title) ? null : Post.Title;

    public IReadOnlyList<TagGroup> TagGroups => _groups ??= GroupOrder
        .Select(g => new TagGroup(g.Title, g.Cat, Tags.Where(t => Normalize(t.Category) == g.Cat).ToList()))
        .Where(g => g.Tags.Count > 0)
        .ToList();

    private static TagCategory Normalize(TagCategory c) => c == TagCategory.Unknown ? TagCategory.General : c;
}
