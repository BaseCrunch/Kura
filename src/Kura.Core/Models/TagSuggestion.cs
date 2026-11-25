namespace Kura.Core.Models;

public sealed record TagSuggestion(string Name, TagCategory Category, long PostCount, bool FromHistory = false)
{
    public string DisplayName => Name.Replace('_', ' ');
}
