namespace WorldEcon.Engine;

internal static class EngineGuards
{
    /// <summary>
    /// Throws if a domain operation returning <see cref="ErrorOr.ErrorOr{TValue}"/> of
    /// <see cref="ErrorOr.Success"/> failed. Used at engine call sites that are pre-guarded so a
    /// failure should be impossible in normal operation; a throw surfaces an otherwise-silent
    /// conservation leak instead of discarding the error.
    /// </summary>
    public static void OrThrow(this ErrorOr.ErrorOr<ErrorOr.Success> result, string context)
    {
        if (result.IsError)
            throw new InvalidOperationException($"{context}: {result.FirstError.Description}");
    }
}
