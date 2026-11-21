using System.Text.RegularExpressions;

namespace Missum.Core.Memory;

/// <summary>
/// Cheap first pass over one visible user message. It never decides what is stored; it only
/// answers whether the sentence is worth an AI review at all, and whether that review has to
/// happen before the model answers. Everything here is deterministic CPU work, so the bulk of a
/// chat history can be screened without touching the GPU.
/// </summary>
public static class UserMemoryIntentScanner
{
    public const int MinimumCharacters = 10;
    public const int MaximumCharacters = 12_000;

    private static readonly Regex Permanence = new(
        @"\b(k[üu]nftig|zuk[üu]nftig|ab\s+jetzt|ab\s+sofort|fortan|von\s+nun\s+an|f[üu]r\s+(die\s+)?zukunft|immer|stets|grunds[äa]tzlich|generell|standardm[äa](?:ß|ss)ig|dauerhaft|permanent|from\s+now\s+on|always|never|going\s+forward)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Word stems instead of full forms: a user writes "Antworten", "antworten" or "antworte".
    private static readonly Regex Directive = new(
        @"\b(bitte|soll|antwor|nutz|verwend|schreib|erkl[äa]r|fasse|pr[üu]f|stell|formulier|erl[äa]uter|verzicht|arbeit|lass|halte|benutz|korrig|ersetz|use|write|reply|keep|avoid)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Forget = new(
        @"(?:^|[.!?\n])\s*(?:bitte\s+|please\s+)?\b(vergiss|vergesse|forget)\b(?!\s+(?:nicht|to)\b)"
        + @"|\b(nicht\s+mehr\s+merken|aus\s+(deinem|dem)\s+ged[äa]chtnis)\b"
        + @"|\b(l[öo]sch\w*|loesch\w*|entfern\w*|delete|remove)\b[^.?!\n]{0,70}\b(gespeicherte[nrms]?\s+\w+|erinnerung\w*|ged[äa]chtnis|memory|gespeicherten?\s+vorgabe\w*)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Remember = new(
        @"(?:^|[.!?\n])\s*(?:bitte\s+|please\s+)?(?:merk(?:e)?\s+dir|remember\s+(?:this|that|my)|speicher(?:e)?\s+(?:dir|als\s+vorgabe))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PersonalPreference = new(
        @"\b(ich\s+bevorzuge|meine\s+pr[äa]ferenz\s+ist|ich\s+arbeite\s+am\s+liebsten|i\s+prefer|my\s+preference\s+is)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex GlobalScope = new(
        @"\b(global|in\s+allen\s+(projekten|sitzungen|chats)|projekt[üu]bergreifend|across\s+(all\s+)?(projects|sessions|chats))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AsksForProfile = new(
        @"\bwas\s+hast\s+du\b[^.?!\n]{0,60}?(gemerkt|gespeichert|notiert)|\bwelche\s+((gespeicherten|meiner)\s+(vorgaben|regeln)|erinnerungen)\b|\b(zeig(?:e)?(?:\s+mir)?|was\s+steht\s+in)\s+mein(?:em)?\s+profil\b|\bwas\s+wei[ßs]t\s+du\s+[üu]ber\s+(mich|mir)\b|\bwhat\s+do\s+you\s+remember\b|\bshow\s+my\s+(memory|preferences)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ProjectOnly = new(
        @"\b(nur|only|ausschlie[ßs]lich)\s+(f[üu]r|in|inside)\s+(das|dieses|den|dem|this)\s+(projekt|repo|repository|workspace|projektordner)\b"
        + @"|\b(in|f[üu]r)\s+dies(?:em|es)\s+(projekt|repo|repository|workspace|projektordner)\b|\b(?:hier\s+im|in\s+this)\s+(projekt|project|repo|repository|workspace)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TemporaryOnly = new(
        @"\b(nur\s+diesmal|einmalig|nur\s+f[üu]r\s+(diese|den)\s+(aufgabe|anfrage|sitzung|runde)|heute\s+nur|dieses\s+eine\s+mal|just\s+this\s+once|only\s+(for|this)\s+this|one\s+time\s+only)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Correction = new(
        @"\b(stattdessen|nicht\s+so|hast\s+du\s+(das\s+)?falsch|falsch\s+verstanden|anders\s+machen|lieber\s+anders|korrektur|korrigier\w*|du\s+solltest|besser\s+w[äa]re|nicht\s+gefragt|wiederholst?)\b"
        + @"|\binstead|you\s+kept\s+asking|don'?t\s+do\s+that\s+again",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex QuestionOnly = new(
        @"^[^\p{L}]*[\p{L}][^\?!]*\?\s*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public sealed record UserMemoryIntent(
        bool WorthReviewing,
        bool ExplicitPermanence,
        bool ForgetRequest,
        bool AsksForStoredProfile,
        bool ProjectOnly,
        bool TemporaryOnly,
        bool RepeatedCorrection,
        string Reason)
    {
        public bool ExplicitGlobalScope { get; init; }

        /// <summary>
        /// When the user states a standing rule, asks for the profile or wants something forgotten,
        /// the result has to be in effect for this very answer. Anything else can be reviewed later.
        /// </summary>
        public bool RequiresImmediateReview => ExplicitPermanence || ForgetRequest || AsksForStoredProfile || ProjectOnly
            || ExplicitGlobalScope;
    }

    public static UserMemoryIntent Scan(string? userText)
    {
        var text = (userText ?? string.Empty).Trim();
        if (text.Length < MinimumCharacters)
        {
            return new UserMemoryIntent(false, false, false, false, false, false, false, "too short");
        }

        if (text.Length > MaximumCharacters)
        {
            return new UserMemoryIntent(false, false, false, false, false, false, false, "message too long");
        }

        var permanence = Remember.IsMatch(text) || PersonalPreference.IsMatch(text)
            || (Permanence.IsMatch(text) && Directive.IsMatch(text));
        var forget = Forget.IsMatch(text);
        var asks = AsksForProfile.IsMatch(text);
        var projectOnly = ProjectOnly.IsMatch(text);
        var explicitlyGlobal = GlobalScope.IsMatch(text);
        var temporary = TemporaryOnly.IsMatch(text);
        var correction = Correction.IsMatch(text);

        // A bare question that is not about the profile is the current task, not a lasting rule.
        if (!permanence && !forget && !asks && !projectOnly && !correction
            && QuestionOnly.IsMatch(text) && !temporary)
        {
            return new UserMemoryIntent(false, false, false, false, false, temporary, false, "question only");
        }

        var worth = permanence || forget || asks || projectOnly || correction;
        var reason = string.Join("+", new[]
        {
            permanence ? "permanence" : null,
            forget ? "forget" : null,
            asks ? "profile-question" : null,
            projectOnly ? "project-scope" : null,
            correction ? "correction" : null,
        }.Where(static value => value is not null));

        return new UserMemoryIntent(worth, permanence, forget, asks, projectOnly, temporary, correction,
            reason.Length == 0 ? "no signal" : reason) { ExplicitGlobalScope = explicitlyGlobal };
    }
}
