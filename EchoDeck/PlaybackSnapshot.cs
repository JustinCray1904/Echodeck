namespace EchoDeck;

/// <summary>The normal media's state at the moment Anonymous Mode was opened.</summary>
internal sealed record PlaybackSnapshot(string Mrl, long TimeMs, bool WasAudio, string Title);
