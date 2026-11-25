namespace Kura.Core.Models;

public enum MediaKind { Image, Gif, Video, Archive, Unknown }

public enum TagCategory
{
    General = 0,
    Artist = 1,
    Copyright = 3,
    Character = 4,
    Meta = 5,
    Species = 6,
    Lore = 7,
    Invalid = 8,
    Unknown = -1,
}

public sealed record Tag(string Name, TagCategory Category)
{
    public string DisplayName => Name.Replace('_', ' ');
}

public sealed record PostImage(string Url, int Width, int Height)
{
    public string Extension => MediaTypes.ExtensionOf(Url);
    public MediaKind Kind => MediaTypes.KindOf(Extension);
    public bool IsAnimated => Kind is MediaKind.Gif or MediaKind.Video;
}

/// <summary>Engine-neutral post model.</summary>
public sealed class Post
{
    public required long ServerId { get; init; }
    public required string RemoteId { get; init; }
    public string? PostUrl { get; init; }
    /// <summary>Post title where the site has one (Kemono), shown in the info panel.</summary>
    public string? Title { get; init; }
    public string? Md5 { get; init; }
    public string? Rating { get; init; }
    public int Score { get; init; }
    public string? Source { get; init; }
    public string? Author { get; init; }
    public string? ParentId { get; init; }
    public bool HasChildren { get; init; }
    public bool HasNotes { get; init; }
    public bool HasComments { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }

    public required PostImage File { get; init; }
    public PostImage? Sample { get; init; }
    public PostImage? Preview { get; init; }

    public IReadOnlyList<Tag> Tags { get; init; } = [];
    public bool IsFavorite { get; set; }

    /// <summary>Best-known pixel dimensions for layout (file dims, falling back to sample/preview).</summary>
    public (int Width, int Height) Dimensions
    {
        get
        {
            if (File.Width > 0 && File.Height > 0) return (File.Width, File.Height);
            if (Sample is { Width: > 0, Height: > 0 }) return (Sample.Width, Sample.Height);
            if (Preview is { Width: > 0, Height: > 0 }) return (Preview.Width, Preview.Height);
            return (1, 1);
        }
    }

    public double AspectRatio
    {
        get
        {
            var (w, h) = Dimensions;
            var r = (double)w / h;
            return Math.Clamp(r, 0.25, 4.0); // keep extreme panoramas from wrecking a row
        }
    }

    public MediaKind Kind => File.Kind;

    /// <summary>
    /// What the viewer should show. Animated media always uses the file; otherwise the sample unless
    /// the original is enforced (SPEC §1.7 "Visible version").
    /// </summary>
    public PostImage VisibleImage(bool forceOriginal)
    {
        if (forceOriginal || Sample is null || File.IsAnimated) return File;
        return Sample;
    }

    public string? PostPageReferer =>
        PostUrl is not null && Uri.TryCreate(PostUrl, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Host}/" : null;
}
