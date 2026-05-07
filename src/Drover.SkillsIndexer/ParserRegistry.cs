using Drover.SkillsIndexer.Parsers;

namespace Drover.SkillsIndexer;

internal static class ParserRegistry
{
    // Order matters: the first parser whose CanParse() returns true wins.
    // The default parser sits last and matches everything.
    private static readonly ISkillSourceParser[] Parsers =
    {
        new AlirezaRezvaniMarketplaceParser(),
        new DefaultSkillMdParser(),
    };

    public static ISkillSourceParser Resolve(string source)
    {
        foreach (var p in Parsers)
            if (p.CanParse(source)) return p;
        return Parsers[^1];
    }
}
