using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EchoDeck;

/// <summary>Decides whether a catalogue entry is the song the file holds.</summary>
internal static class SongMatcher
{
    /// <summary>The least score an entry needs to be accepted as this song.</summary>
    internal const double Accept = 75;

    /// <summary>
    /// Scores a catalogue entry against the file. False when it is not this song. The rules, all of which must hold:
    ///  1. every word of the entry's title is somewhere in what the file tells us (so "Love" is never taken for "Love Story");
    ///  2. the entry (title + artist) explains at least 70 % of the words of the tidied name (so "Flex" by someone else is not it);
    ///  3. the artist matches, when the file has one (the entry's artists, its featured artists, or any name in "A &amp; B");
    ///  4. with no artist known, the length must be within 10 s of the file's (or the entry must be the catalogue's first hit).
    /// The score then rewards word coverage, the artist, an exact title and a length close to the file's, and punishes karaoke, tribute,
    /// remix ... versions that the file is not.
    /// </summary>
    internal static bool TryScore(SongQuery q, SongCandidate c, out double score)
    {
        score = 0;
        if (c.Title.Length == 0 || q.Clean.Count == 0) return false;

        var titleWords = SongText.Tokens(SongText.StripBrackets(c.Title));
        if (titleWords.Count == 0) titleWords = SongText.Tokens(c.Title);
        if (titleWords.Count == 0) return false;

        foreach (var w in titleWords)
            if (!SongText.ContainsToken(q.Raw, w)) return false;

        var explained = SongText.Tokens(c.Title + " " + c.Artist);
        int hit = 0;
        foreach (var w in q.Clean)
            if (SongText.ContainsToken(explained, w)) hit++;
        double coverage = hit / (double)q.Clean.Count;
        if (coverage < 0.7) return false;

        bool artistKnown = q.Artist.Length > 0;
        bool artistOk = false;
        if (artistKnown)
        {
            foreach (var set in SongText.ArtistSets(c.Artist, c.Title))
            {
                bool all = true;
                foreach (var w in set)
                    if (!SongText.ContainsToken(q.Raw, w)) { all = false; break; }
                if (all) { artistOk = true; break; }
            }
            if (!artistOk) return false;
        }

        score = 50 + 30 * coverage;
        if (artistOk) score += 15;
        if (TextMatch.Norm(c.Title) == TextMatch.Norm(q.Title)) score += 10;

        if (q.Seconds > 0 && c.Seconds > 0)
        {
            double d = Math.Abs(c.Seconds - q.Seconds);
            score += d <= 3 ? 10 : d <= 8 ? 5 : d <= 15 ? 0 : d <= 30 ? -10 : -25;
            if (!artistKnown && d > 10) return false;
        }
        else if (!artistKnown && c.Rank > 0)
        {
            return false;
        }

        if (!q.JunkWanted && SongText.Junk.IsMatch(c.Title + " " + c.Album)) score -= 50;
        score -= c.Rank * 0.5;
        return score >= Accept;
    }
}
