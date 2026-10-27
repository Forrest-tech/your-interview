using FluentAssertions;
using YourInterview.Services.Interviews.Application;
using YourInterview.Services.Interviews.Domain;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// 票253:列表 DTO 补齐 Interviewers/Location(与详情 DTO 同源,仅暴露已有数据)。
/// 断言 ToListItemDto 透传两字段;缺失时保持 null,前端条件渲染,绝不编造占位。
/// </summary>
public sealed class InterviewListItemMappingTests
{
    private static readonly Guid Owner = Guid.NewGuid();

    [Fact]
    public void ToListItemDto_CarriesInterviewersAndLocation()
    {
        var e = new InterviewEntry(Guid.NewGuid(), "Acme", "Senior .NET Developer", userId: Owner);
        e.ClearDomainEvents();
        e.UpdateBasicInfo("Acme", "Senior .NET Developer", null, null, null,
            1, null, "Video", "Ada Lovelace", "Toronto, ON", null, null);

        var dto = e.ToListItemDto();

        dto.Interviewers.Should().Be("Ada Lovelace");
        dto.Location.Should().Be("Toronto, ON");
    }

    [Fact]
    public void ToListItemDto_NullInterviewersAndLocationStayNull()
    {
        var e = new InterviewEntry(Guid.NewGuid(), "Acme", "Senior .NET Developer", userId: Owner);
        e.ClearDomainEvents();

        var dto = e.ToListItemDto();

        dto.Interviewers.Should().BeNull();
        dto.Location.Should().BeNull();
    }
}
