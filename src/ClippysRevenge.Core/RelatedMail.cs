namespace ClippysRevenge.Core;

/// <summary>One index row offered as related mail (SPEC 25.3). Body is the stored body's first
/// RelatedMail.BodyFetchChars characters. ToString() shows only "RelatedRow" (no mail data in logs).</summary>
public sealed record RelatedRow(string EntryId, string? InternetMessageId, string? ConversationTopic, string Folder,
    DateTimeOffset EventTime, string? SenderName, string? SenderEmail, string? To, string? Cc, string? Subject,
    string? Body)
{
    public override string ToString() => nameof(RelatedRow);
}

/// <summary>TermHits in bm25 order (best first); PeopleHits newest first. Each at most RelatedMail.MaxPerQuery.</summary>
public sealed record RelatedHits(IReadOnlyList<RelatedRow> TermHits, IReadOnlyList<RelatedRow> PeopleHits)
{
    public static RelatedHits None { get; } = new([], []);
}

/// <param name="AnchorEntryId">The email being answered (or the revisit's reply target).</param>
/// <param name="AnchorImid">Its internet message id, or null.</param>
/// <param name="AnchorTopic">Its conversation topic (the thread to exclude), or null.</param>
/// <param name="SenderEmail">Address for the people query; null/blank = no people query.</param>
/// <param name="Terms">From RelatedMail.Terms; empty = no terms query.</param>
/// <param name="Since">Window start (now - 12 months).</param>
/// <param name="Until">The anchor's event time: only rows at or before it.</param>
public sealed record RelatedQuery(string AnchorEntryId, string? AnchorImid, string? AnchorTopic, string? SenderEmail,
    IReadOnlyList<string> Terms, DateTimeOffset Since, DateTimeOffset Until);

/// <summary>Related earlier mail for drafting prompts (SPEC 25.3): search terms, scoring and selection. Pure logic.</summary>
public static class RelatedMail
{
    public const int WindowMonths = 12;
    public const int MaxPerQuery = 50;
    public const int MaxMessages = 5;
    public const int MaxTerms = 8;
    public const int MinTermLength = 3;
    public const int MinSubjectTerms = 2;
    public const int BodyTermMinLength = 5;
    public const int BodyTermChars = 2_000;
    public const int MaxMessageChars = 800;
    public const int BudgetChars = 4_000;
    public const int MinMessageChars = 200;
    public const int BodyFetchChars = 8_000;

    private const string StopText =
        "about after again all also and any are back been before being best but can could dear did does done email " +
        "for from get got had has have hello her here him his how info into its just kind let like mail many may " +
        "more most much need new not now one only other our out over please regards see she should some such than " +
        "thank thanks that the their them then there these they this those today too two update use very want was " +
        "way week were what when where which who why will with would yes you your " +
        "aber alle als auch auf aus bei beim beste besten bin bis bitte dank danke dann das dass daß dem den der des " +
        "die dies diese dieser doch ein eine einem einen einer eines euch frau freundlichen für fuer geehrte geehrter " +
        "gibt gruss gruß grüße gruesse grüßen gruessen habe haben hallo hat herr hier ich ihnen ihr ist jetzt kann " +
        "kein keine liebe lieber mfg mich mir mit nach nicht noch nur oder schon sehr sich sie sind über ueber und uns " +
        "unter viele vielen vom von vor war was weil wenn wer wie wir wird zum zur " +
        "aux avec avez avoir bien bonjour bonne cela ces cette cher chère comme cordialement dans des donc elle elles " +
        "est été être ils les leur leurs madame mais merci mes monsieur nos notre nous par pas peut plus pour que quel " +
        "quelle qui salut ses son sont sur tous tout toutes très une vos votre vous";

    /// <summary>Lower-case stop words (SPEC 25.3), Ordinal.</summary>
    public static IReadOnlySet<string> StopWords { get; } =
        new HashSet<string>(StopText.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    /// <summary>Search terms from the subject (prefixes stripped) and, when the subject gives fewer than
    /// MinSubjectTerms, the most frequent long words of the own text. Never throws; null inputs count as "".
    /// Lower-case, distinct, at most MaxTerms, in order. Only letters and digits, so nothing can reach FTS syntax.</summary>
    public static IReadOnlyList<string> Terms(string? subject, string? ownText)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var token in Tokens(ReplySubject.StripPrefixes(subject)))
        {
            if (result.Count >= MaxTerms) break;
            if (Usable(token, MinTermLength) && seen.Add(token)) result.Add(token);
        }

        if (result.Count >= MinSubjectTerms || string.IsNullOrWhiteSpace(ownText)) return result;

        var body = ownText.Length > BodyTermChars ? ownText[..BodyTermChars] : ownText;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var token in Tokens(body))
        {
            if (!Usable(token, BodyTermMinLength) || seen.Contains(token)) continue;
            if (counts.TryGetValue(token, out var n)) counts[token] = n + 1;
            else
            {
                counts[token] = 1;
                order.Add(token);
            }
        }
        var ranked = order.Select((t, i) => (t, i)).OrderByDescending(x => counts[x.t]).ThenBy(x => x.i);
        foreach (var (t, _) in ranked)
        {
            if (result.Count >= MaxTerms) break;
            result.Add(t);
        }
        return result;
    }

    private static bool Usable(string token, int minLength)
    {
        if (token.Length < minLength || StopWords.Contains(token)) return false;
        return !(token.Length <= 4 && token.All(char.IsDigit));
    }

    /// <summary>Maximal runs of letters and digits, lower-cased.</summary>
    private static IEnumerable<string> Tokens(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var isToken = i < text.Length && char.IsLetterOrDigit(text[i]);
            if (isToken)
            {
                if (start < 0) start = i;
            }
            else if (start >= 0)
            {
                yield return text[start..i].ToLowerInvariant();
                start = -1;
            }
        }
    }

    /// <summary>(termRank is int i ? 2 - i/50.0 : 0) + (peopleHit ? 1.5 : 0) + 0.5 x max(0, 1 - ageDays/365),
    /// ageDays = max(0, (now - eventTime).TotalDays).</summary>
    public static double Score(int? termRank, bool peopleHit, DateTimeOffset eventTime, DateTimeOffset now)
    {
        var age = Math.Max(0, (now - eventTime).TotalDays);
        return (termRank is { } i ? 2 - i / 50.0 : 0) + (peopleHit ? 1.5 : 0) + 0.5 * Math.Max(0, 1 - age / 365);
    }

    private sealed record Scored(RelatedRow Row, double Score, bool People);

    /// <summary>At most MaxMessages rows, oldest first (SPEC 25.3 ranking). Pure.</summary>
    /// <exception cref="ArgumentNullException">hits or one of its lists is null.</exception>
    public static IReadOnlyList<RelatedRow> Select(RelatedHits hits, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(hits);
        ArgumentNullException.ThrowIfNull(hits.TermHits);
        ArgumentNullException.ThrowIfNull(hits.PeopleHits);

        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        var rows = new Dictionary<string, RelatedRow>(StringComparer.Ordinal);
        for (var i = 0; i < hits.TermHits.Count; i++)
        {
            var r = hits.TermHits[i];
            if (rows.TryAdd(r.EntryId, r)) rank[r.EntryId] = i;
        }
        var people = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in hits.PeopleHits)
        {
            rows.TryAdd(r.EntryId, r);
            people.Add(r.EntryId);
        }

        var scored = rows.Values
            .Select(r => new Scored(r, Score(rank.TryGetValue(r.EntryId, out var k) ? k : null, people.Contains(r.EntryId),
                r.EventTime, now), people.Contains(r.EntryId)))
            .OrderByDescending(s => s.Score).ThenByDescending(s => s.Row.EventTime)
            .ThenBy(s => s.Row.EntryId, StringComparer.Ordinal)
            .ToList();

        var held = new HashSet<string>(StringComparer.Ordinal);
        var deduped = new List<Scored>();
        foreach (var s in scored)
        {
            var keys = Keys(s.Row);
            if (keys.Any(held.Contains)) continue;
            foreach (var k in keys) held.Add(k);
            deduped.Add(s);
        }

        var kept = deduped.Take(MaxMessages).ToList();
        if (!kept.Any(s => IsSent(s.Row)))
        {
            var slot = deduped.FirstOrDefault(s => s.People && IsSent(s.Row) && !kept.Contains(s));
            if (slot is not null)
            {
                if (kept.Count >= MaxMessages) kept[MaxMessages - 1] = slot;
                else kept.Add(slot);
            }
        }

        return kept.Select(s => s.Row).OrderBy(r => r.EventTime).ThenBy(r => r.EntryId, StringComparer.Ordinal).ToList();
    }

    private static bool IsSent(RelatedRow r) =>
        string.Equals((r.Folder ?? "").Split('\\')[0], "Sent Items", StringComparison.OrdinalIgnoreCase);

    // Keys carry a type prefix; the held set is Ordinal, so topic and subject keys are lower-cased (OrdinalIgnoreCase).
    private static List<string> Keys(RelatedRow r)
    {
        var keys = new List<string>(3);
        var imid = r.InternetMessageId?.Trim();
        if (!string.IsNullOrEmpty(imid)) keys.Add("i\u0001" + imid);
        var topic = r.ConversationTopic?.Trim();
        if (!string.IsNullOrEmpty(topic)) keys.Add("t\u0001" + topic.ToLowerInvariant());
        var subject = string.Join(' ', ReplySubject.StripPrefixes(r.Subject).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
        var sender = (string.IsNullOrWhiteSpace(r.SenderEmail) ? r.SenderName : r.SenderEmail)?.Trim().ToLowerInvariant() ?? "";
        keys.Add("s\u0001" + subject + "\u0001" + sender + "\u0001" + r.EventTime.ToUnixTimeSeconds() / 60);
        return keys;
    }

    /// <summary>max(MinMessageChars, min(MaxMessageChars, BudgetChars / count)); count &lt;= 0 gives MaxMessageChars.</summary>
    public static int PerMessageChars(int count) =>
        count <= 0 ? MaxMessageChars : Math.Max(MinMessageChars, Math.Min(MaxMessageChars, BudgetChars / count));
}

public static class RelatedMailTexts
{
    public const string Notice =
        "AI drafts now also send up to 5 related earlier emails from your mailbox to Claude, to write better replies. You can turn this off in Settings → AI drafts.";
}
