namespace EchoDeck;

/// <summary>Validation + saving shared by the onboarding, Privacy settings and Anonymous Mode settings pages.</summary>
internal static class PasscodeForm
{
    /// <summary>Returns null on success, otherwise a message for the user.</summary>
    public static string? TrySet(string? current, string next, string confirm)
    {
        if (PinStore.IsSet && !PinStore.Verify(current ?? ""))
            return "The current passcode isn't right.";

        if (!PinStore.IsValidFormat(next))
            return $"Use {PinStore.MinLength} to {PinStore.MaxLength} digits (numbers only).";

        if (next != confirm)
            return "The two passcodes don't match.";

        try
        {
            PinStore.Set(next);
            return null;
        }
        catch (Exception ex)
        {
            return "Couldn't save the passcode: " + ex.Message;
        }
    }
}
