using System;

/// <summary>
/// Creates the credential write strategy that matches the requested model update mode.
/// </summary>
internal static class StructuredCredentialWriterFactory
{
  /// <summary>
  /// Resolves the configured model update mode to the matching TOM, TMSL, or TMDL writer.
  /// </summary>
  public static IStructuredCredentialWriter Create(string? mode)
  {
    string normalized = (mode ?? "TOM").Trim().ToUpperInvariant();

    return normalized switch {
      "TOM" => new TomStructuredCredentialWriter(),
      "TMSL" => new TmslStructuredCredentialWriter(),
      "TMDL" => new TmdlStructuredCredentialWriter(),
      _ => throw new InvalidOperationException(
        $"Unsupported MODEL_UPDATE_MODE '{mode}'. Use TOM, TMSL, or TMDL.")
    };
  }
}
