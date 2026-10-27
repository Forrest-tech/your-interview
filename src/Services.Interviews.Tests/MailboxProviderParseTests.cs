using System.Text.Json;
using FluentAssertions;
using YourInterview.Services.Interviews.Application;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// 票 240：Gmail format=full / Graph message JSON 解析测试（公开静态解析入口，227 同款先例）。
/// 纯解析，不发网络请求。夹具全合成（虚构发件人、主题、链接，不含任何真实邮箱/会议链接）。
/// </summary>
public sealed class MailboxProviderParseTests
{
    [Fact]
    public void GmailFull_ParsesHeaders_Body_Attachments()
    {
        // 正文 "Interview on Oct 7" 的 base64url。
        const string json = """
        {
          "id": "msg-1", "threadId": "thread-1", "internalDate": "1760000000000",
          "payload": {
            "mimeType": "multipart/mixed",
            "headers": [
              { "name": "From", "value": "Recruiter <recruiter@examplecorp.com>" },
              { "name": "To", "value": "me@example.com" },
              { "name": "Subject", "value": "Interview invitation" },
              { "name": "Date", "value": "Mon, 06 Oct 2026 10:00:00 +0000" },
              { "name": "Message-ID", "value": "<abc@examplecorp.com>" }
            ],
            "parts": [
              { "mimeType": "text/plain", "filename": "",
                "body": { "data": "SW50ZXJ2aWV3IG9uIE9jdCA3", "size": 19 } },
              { "mimeType": "application/pdf", "filename": "agenda.pdf",
                "body": { "attachmentId": "ATT-1", "size": 1234 } }
            ]
          }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var m = GmailMailboxProvider.ParseFullMessage(doc.RootElement);
        m.Should().NotBeNull();
        m!.ProviderMessageId.Should().Be("msg-1");
        m.ProviderThreadId.Should().Be("thread-1");
        m.FromEmail.Should().Be("recruiter@examplecorp.com");
        m.FromName.Should().Be("Recruiter");
        m.Subject.Should().Be("Interview invitation");
        m.BodyText.Should().Be("Interview on Oct 7");
        m.MessageIdHeader.Should().Be("<abc@examplecorp.com>");
        m.Attachments.Should().HaveCount(1);
        m.Attachments[0].FileName.Should().Be("agenda.pdf");
        m.Attachments[0].ProviderAttachmentId.Should().Be("ATT-1");
    }

    [Fact]
    public void GmailFull_MissingId_ReturnsNull()
    {
        using var doc = JsonDocument.Parse("""{ "payload": { "headers": [] } }""");
        GmailMailboxProvider.ParseFullMessage(doc.RootElement).Should().BeNull();
    }

    [Fact]
    public void GraphMessage_ParsesFields_AndStripsHtml()
    {
        const string json = """
        {
          "id": "gmsg-1", "conversationId": "conv-1",
          "from": { "emailAddress": { "address": "recruiter@examplecorp.com", "name": "Recruiter" } },
          "toRecipients": [{ "emailAddress": { "address": "me@example.com", "name": "Me" } }],
          "ccRecipients": [],
          "subject": "Next steps",
          "receivedDateTime": "2026-10-06T10:00:00Z",
          "body": { "contentType": "html", "content": "<p>Hello<br>see you soon</p>" },
          "hasAttachments": false,
          "internetMessageId": "<g1@examplecorp.com>"
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var m = GraphMailboxProvider.ParseGraphMessage(doc.RootElement);
        m.Should().NotBeNull();
        m!.ProviderMessageId.Should().Be("gmsg-1");
        m.ProviderThreadId.Should().Be("conv-1");
        m.FromEmail.Should().Be("recruiter@examplecorp.com");
        m.Subject.Should().Be("Next steps");
        m.BodyText.Should().Contain("Hello");
        m.BodyText.Should().Contain("see you soon");
        m.BodyHtml.Should().Contain("<p>Hello");
        m.MessageIdHeader.Should().Be("<g1@examplecorp.com>");
    }

    [Fact]
    public void GraphMessage_MissingId_ReturnsNull()
    {
        using var doc = JsonDocument.Parse("""{ "subject": "x" }""");
        GraphMailboxProvider.ParseGraphMessage(doc.RootElement).Should().BeNull();
    }
}
