using System.Text;
using Xunit;
using static YourInterview.Services.Assessment.Tests.DictionaryTestHarness;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票 316 回归：ECDICT 载入不得随行数累积栈占用。
/// 旧实现在逐行循环里 stackalloc，每行 64 B 直到方法返回才释放；
/// 20 万行 ≈ 12.8 MB，超过 Windows 默认 1 MB 与 Linux 默认 8 MB 线程栈，两个平台都会
/// Stack overflow 使测试宿主崩溃（不可捕获，表现为整个测试运行中断）。修复后栈占用为常量。
/// 夹具为合成词（zorblax 系），不含真实词典条目。
/// </summary>
public class DictionaryLoadStackTests
{
    private const int Rows = 200_000;

    private static string BigEcdictTsv()
    {
        var sb = new StringBuilder(Rows * 40);
        for (var i = 0; i < Rows; i++)
        {
            // 8 列：word phonetic definition translation exchange collins oxford tag
            sb.Append("zorblax").Append(i).Append("\tz\tn. glorp\t合\t");
            if (i % 2 == 1) sb.Append("0:zorblax").Append(i - 1);
            sb.Append("\t\t\t\n");
        }
        return sb.ToString();
    }

    [Fact]
    public void Load_ManyRows_DoesNotOverflowStack_AndKeepsEveryRow()
    {
        var store = BuildOpenStore(BigEcdictTsv(), WordNetJsonl());

        Assert.Equal(Rows, store.Ecdict.Count);
        Assert.Equal("n. glorp", store.Ecdict["zorblax0"].Definition);
        Assert.Equal("zorblax199998", store.LemmaByForm["zorblax199999"]);
        Assert.Equal(Rows / 2, store.LemmaByForm.Count);
    }
}
