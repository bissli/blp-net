namespace Blp.Internal;

/// <summary>
/// Turns the errors of a completed response into the exceptions a request asks for.
/// </summary>
internal static class ErrorRaising
{
    /// <summary>Throws for the collected errors the request is set to raise.</summary>
    /// <param name="raiseSecurityError">Throw when securityErrors is not empty.</param>
    /// <param name="raiseFieldError">Throw when fieldErrors is not empty.</param>
    /// <param name="securityErrors">Security errors of the whole response.</param>
    /// <param name="fieldErrors">Field errors of the whole response.</param>
    /// <exception cref="RequestSecurityException">
    /// raiseSecurityError is set and securityErrors is not empty. Checked first.
    /// </exception>
    /// <exception cref="RequestFieldException">
    /// raiseFieldError is set and fieldErrors is not empty.
    /// </exception>
    public static void ThrowRequested(
        bool raiseSecurityError,
        bool raiseFieldError,
        IReadOnlyList<SecurityError> securityErrors,
        IReadOnlyList<FieldError> fieldErrors)
    {
        if (raiseSecurityError && securityErrors.Count > 0)
        {
            throw new RequestSecurityException(securityErrors);
        }

        if (raiseFieldError && fieldErrors.Count > 0)
        {
            throw new RequestFieldException(fieldErrors);
        }
    }
}
