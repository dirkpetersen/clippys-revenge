using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClippysRevenge.Core;

/// <summary>How create_meeting_draft behaves (SPEC §5.2, §22.1). DraftOnly is the default (0).</summary>
public enum MeetingInviteMode
{
    DraftOnly = 0,
    Disabled = 1,
}

/// <summary>
/// Reads settings.json values leniently (SPEC §22.1 migration): "Disabled" → Disabled; any other string
/// (including the pre-M15 "Send", "" and unknown names) and null → DraftOnly. Writes the name.
/// </summary>
public sealed class MeetingInviteModeJsonConverter : JsonConverter<MeetingInviteMode>
{
    public override bool HandleNull => true;

    /// <exception cref="JsonException">The token is not a string, number or null (object, array, bool).</exception>
    public override MeetingInviteMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return string.Equals(reader.GetString()?.Trim(), "Disabled", StringComparison.OrdinalIgnoreCase)
                    ? MeetingInviteMode.Disabled
                    : MeetingInviteMode.DraftOnly;
            case JsonTokenType.Null:
                return MeetingInviteMode.DraftOnly;
            case JsonTokenType.Number:
                // Pre-M15 ordinals: 2 was Disabled; 0 (Send) and 1 (DraftOnly) both become DraftOnly.
                return reader.TryGetInt32(out var n) && n == 2 ? MeetingInviteMode.Disabled : MeetingInviteMode.DraftOnly;
            default:
                throw new JsonException("MeetingInviteMode must be a string.");
        }
    }

    public override void Write(Utf8JsonWriter writer, MeetingInviteMode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value == MeetingInviteMode.Disabled ? "Disabled" : "DraftOnly");
}
