using System.Text.RegularExpressions;

namespace ContainerForest.DiscordBot.Commands;

/// <summary>Discord markdown for text that comes from users, the aggregator or Docker.</summary>
public static partial class Markdown
{
    /// <summary>Makes <paramref name="text"/> show literally instead of formatting or breaking the message.</summary>
    public static string Escape(string text) => SpecialCharacters().Replace(text, @"\$1");

    /// <summary>An inline code span; backticks cannot be escaped inside one, so they are dropped.</summary>
    public static string Code(string text) => "`" + text.Replace("`", "") + "`";

    [GeneratedRegex(@"([\\*_~`|>\[\]()#])")]
    private static partial Regex SpecialCharacters();
}
