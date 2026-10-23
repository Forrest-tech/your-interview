namespace YourInterview.Services.Assessment.Infrastructure.Dictionary;

/// <summary>
/// WordNet Morphy 规则屈折归元（SPEC §3.2/§4.1 第 3 步）：按词性的后缀替换表生成候选词元，
/// 候选须经「结构层有该词元」验证后才采用（验证在 DictionaryService）。
/// 规则表 = WordNet morphy 经典替换集；另加 ed/ing 词干双写辅音的去双写候选
/// （stopped→stop 一类，经典实现靠词表验证兜底，本实现同理）。
/// </summary>
public static class Morphy
{
    // (suffix, replacement) —— WordNet morphy 原表
    private static readonly (string Suffix, string Replacement)[] NounRules =
    [
        ("s", ""), ("ses", "s"), ("xes", "x"), ("zes", "z"),
        ("ches", "ch"), ("shes", "sh"), ("men", "man"), ("ies", "y"),
    ];

    private static readonly (string Suffix, string Replacement)[] VerbRules =
    [
        ("s", ""), ("ies", "y"), ("es", ""), ("es", "e"),
        ("ed", ""), ("ed", "e"), ("ing", ""), ("ing", "e"),
    ];

    private static readonly (string Suffix, string Replacement)[] AdjRules =
    [
        ("er", ""), ("er", "e"), ("est", ""), ("est", "e"),
        ("ier", "y"), ("iest", "y"),
    ];

    /// <summary>生成全部候选词元（去重、保序、不含原词本身）。</summary>
    public static IReadOnlyList<string> GenerateCandidates(string word)
    {
        var seen = new HashSet<string>();
        var output = new List<string>();
        void Add(string? candidate)
        {
            if (!string.IsNullOrEmpty(candidate) && candidate != word && seen.Add(candidate))
                output.Add(candidate);
        }

        foreach (var rules in new[] { NounRules, VerbRules, AdjRules })
        {
            foreach (var (suffix, replacement) in rules)
            {
                if (word.Length > suffix.Length && word.EndsWith(suffix, StringComparison.Ordinal))
                {
                    var stem = word[..^suffix.Length] + replacement;
                    Add(stem);
                    // ed/ing 去双写：stopp→stop、stopping→stop
                    if ((suffix is "ed" or "ing") && replacement == "" && stem.Length >= 3
                        && stem[^1] == stem[^2] && !IsVowel(stem[^1]))
                        Add(stem[..^1]);
                    if ((suffix is "ed" or "ing") && replacement == "e" && stem.Length >= 4
                        && stem[^2] == stem[^3] && !IsVowel(stem[^2]))
                        Add(stem[..^2] + "e");
                }
            }
        }
        return output;
    }

    private static bool IsVowel(char c) => c is 'a' or 'e' or 'i' or 'o' or 'u';
}
