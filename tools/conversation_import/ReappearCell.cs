using System;

// `reappear:<place>` in an action cell: the speaker vanishes and reappears at
// the named place (ReappearAction). Not checked here - places are named on
// painter placements, which this tool never reads - so a misspelt place is an
// error when the action fires.
static class ReappearCell
{
	const string Prefix = "reappear:";

	public static bool IsReappearToken(string token)
	{
		return token.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
	}

	public static string Parse(string token, SheetRow row, Report report)
	{
		string place = token.Substring(Prefix.Length).Trim();
		if (place.Length == 0 || place.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0)
		{
			report.Error(row, $"'{token}' must name one place - a named painter placement, e.g. 'reappear:bear_body'");
			return null;
		}
		return place;
	}
}
