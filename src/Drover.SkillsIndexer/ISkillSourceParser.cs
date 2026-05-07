namespace Drover.SkillsIndexer;

// One implementation per source whose layout we know. The dispatcher in
// Program.cs picks a parser by `CanParse(string source)` (typically a literal
// "owner/repo" match) and falls back to DefaultSkillMdParser for everything
// else. Add a new file in this folder + register it in ParserRegistry — no
// changes to Program.cs needed.
public interface ISkillSourceParser
{
    string Name { get; }
    bool CanParse(string source);
    IReadOnlyList<IndexedSkill> Parse(string repoRoot);
}
