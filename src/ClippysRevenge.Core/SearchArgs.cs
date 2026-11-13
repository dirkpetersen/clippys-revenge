using ModelContextProtocol;

namespace ClippysRevenge.Core;

/// <summary>Tool string arguments for the search filters (SPEC §11.1), mapped to a <see cref="SearchFilter"/>.</summary>
public sealed record SearchArgs
{
    public string? Query { get; init; }
    public string? Sender { get; init; }
    public string? Recipient { get; init; }
    public string? Subject { get; init; }
    public string? DateFrom { get; init; }
    public string? DateTo { get; init; }
    public string? Folder { get; init; }
    public bool? HasAttachment { get; init; }
    public string? AttachmentName { get; init; }
    public string? AttachmentExt { get; init; }
    public bool? Unread { get; init; }
    public string? Mailbox { get; init; }
    public int Limit { get; init; } = SearchFilter.DefaultLimit;
    public int Offset { get; init; }

    /// <summary>Dates via SearchDates (FormatException becomes McpException). Limit clamped, negative Offset becomes 0.</summary>
    public SearchFilter ToFilter(TimeZoneInfo tz)
    {
        ArgumentNullException.ThrowIfNull(tz);
        try
        {
            return new SearchFilter
            {
                Query = Query,
                Sender = Sender,
                Recipient = Recipient,
                Subject = Subject,
                DateFrom = SearchDates.ParseFrom(DateFrom, tz),
                DateTo = SearchDates.ParseTo(DateTo, tz),
                Folder = Folder,
                HasAttachment = HasAttachment,
                AttachmentName = AttachmentName,
                AttachmentExt = AttachmentExt,
                Unread = Unread,
                Limit = Math.Clamp(Limit, 1, SearchFilter.MaxLimit),
                Offset = Math.Max(Offset, 0),
            };
        }
        catch (FormatException ex)
        {
            throw new McpException(ex.Message);
        }
    }
}
