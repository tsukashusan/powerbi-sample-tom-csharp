using System;
using Microsoft.AnalysisServices.Tabular;
using Serilog;

/// <summary>
/// Applies structured data source credentials directly through TOM and saves the model.
/// </summary>
internal sealed class TomStructuredCredentialWriter : IStructuredCredentialWriter
{
  public string ModeName => "TOM";

  /// <summary>
  /// Writes the structured credential to the in-memory model and persists it with SaveChanges().
  /// </summary>
  public void Apply(
    Server server,
    Database database,
    Model model,
    StructuredDataSource structuredDataSource,
    CompositeModelSqlOptions options,
    SqlDataSourceCredentialOptions credentials)
  {
    if (options.ClearStructuredCredential) {
      // Clearing the credential is still a model edit, so persist the empty credential object.
      structuredDataSource.Credential = new Credential();
      model.SaveChanges();
      Log.Information("Structured credential was cleared via {ModeName} for '{DataSourceName}'.", ModeName, structuredDataSource.Name);
      return;
    }

    // Reuse the current credential object when present so existing settings stay intact unless explicitly overridden.
    var currentCredential = structuredDataSource.Credential ?? new Credential();
    currentCredential.AuthenticationKind = string.IsNullOrWhiteSpace(credentials.StructuredAuthenticationKind)
      ? AuthenticationKind.UsernamePassword
      : credentials.StructuredAuthenticationKind;
    currentCredential.PrivacySetting = string.IsNullOrWhiteSpace(credentials.StructuredPrivacySetting)
      ? PrivacyClass.Organizational
      : credentials.StructuredPrivacySetting;
    currentCredential.Username = credentials.Account;
    currentCredential.Password = credentials.Password;
    currentCredential.EncryptConnection = credentials.StructuredEncryptConnection;
    structuredDataSource.Credential = currentCredential;

    model.SaveChanges();

    Log.Information(
      "Credential settings applied to structured data source '{DataSourceName}' via {ModeName} (AuthenticationKind={AuthenticationKind}, PrivacySetting={PrivacySetting}, EncryptConnection={EncryptConnection}).",
      structuredDataSource.Name,
      ModeName,
      currentCredential.AuthenticationKind,
      currentCredential.PrivacySetting,
      currentCredential.EncryptConnection);
  }
}
