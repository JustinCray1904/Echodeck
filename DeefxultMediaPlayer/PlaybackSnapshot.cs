namespace DeefxultMediaPlayer;

/// <summary>The normal video's state at the moment the vault was opened.</summary>
internal sealed record PlaybackSnapshot(string Mrl, long TimeMs);
