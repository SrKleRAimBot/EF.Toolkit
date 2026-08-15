namespace EFToolkit.Query.Configuration;

/// <summary>Which page number identifies the first page.</summary>
public enum PageNumbering
{
    /// <summary>The first page is page 1. The usual choice for a public API.</summary>
    OneBased = 0,

    /// <summary>The first page is page 0.</summary>
    ZeroBased = 1,
}
