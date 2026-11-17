namespace Blp.Eqs;

/// <summary>Where Bloomberg looks up an EQS screen by its name.</summary>
public enum EqsScreenType
{
    /// <summary>A screen Bloomberg publishes to every user.</summary>
    Global,

    /// <summary>A screen the logged-on user saved.</summary>
    Private,
}
