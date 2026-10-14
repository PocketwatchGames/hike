using System;
using System.Globalization;

// A language-understanding gate parsed out of a sheet's condition cell:
//
//   understands>=60%            the speaker's own tongue, at least 60% understood
//   understands<100%            ...not yet fully understood
//   understands:vyeshal>=50%    a named language (its LanguageData.id)
//
// Inline for the same reason npcvar: is: the whole content is (language, op,
// percent), and a .tres per threshold is authoring cost with nothing reusable in
// it. Only >= and < exist — the gate and its negation, which is every way the
// two sides of one threshold are used. The % is optional.
class UnderstandsRef
{
	// Null = the speaker's language (the condition's default).
	public ResRef Language;
	public float Threshold;
	public bool Below;
}

static class UnderstandsCell
{
	const string Prefix = "understands";

	public static bool IsUnderstandsToken(string token)
	{
		if (!token.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || token.Length == Prefix.Length)
		{
			return false;
		}
		char next = token[Prefix.Length];
		return next == ':' || next == '>' || next == '<' || next == '=' || next == '!';
	}

	public static UnderstandsRef Parse(string token, ResourceIndex index, string world, SheetRow row, Report report)
	{
		string body = token.Substring(Prefix.Length).Trim();
		int opAt = body.IndexOfAny(new[] { '>', '<', '=', '!' });
		if (opAt < 0)
		{
			report.Error(row, $"'{token}' has no threshold - say e.g. 'understands>=60%'");
			return null;
		}

		var reference = new UnderstandsRef();
		string languagePart = body.Substring(0, opAt).Trim();
		if (languagePart.Length > 0)
		{
			if (languagePart[0] != ':' || languagePart.Length == 1)
			{
				report.Error(row, $"'{token}' - name a language as 'understands:<id>>=60%'");
				return null;
			}
			string id = languagePart.Substring(1).Trim();
			reference.Language = index.Language(world, id);
			if (reference.Language == null)
			{
				report.Error(row, $"unknown language '{id}' - no LanguageData under resources/ declares it");
				return null;
			}
		}

		string rest = body.Substring(opAt);
		string op;
		if (rest.StartsWith(">="))
		{
			op = ">=";
		}
		else if (rest.StartsWith("<") && !rest.StartsWith("<="))
		{
			op = "<";
			reference.Below = true;
		}
		else
		{
			report.Error(row, $"'{token}' - an understanding gate is '>=' or '<' (the gate and its negation)");
			return null;
		}

		string value = rest.Substring(op.Length).Trim().TrimEnd('%').Trim();
		if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int percent) || percent < 0 || percent > 100)
		{
			report.Error(row, $"'{token}' compares against '{value}', which is not a whole percent from 0 to 100");
			return null;
		}
		reference.Threshold = percent / 100f;
		return reference;
	}
}
