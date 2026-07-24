using System;
using System.Text.Json;
using Microsoft.AnalysisServices.Tabular;
using Serilog;

/// <summary>
/// Applies structured data source credentials by issuing a TMSL createOrReplace command.
/// </summary>
internal sealed class TmslStructuredCredentialWriter : IStructuredCredentialWriter
{
  public string ModeName => "TMSL";

  /// <summary>
  /// Keeps the local model coherent, then pushes the structured data source update through TMSL.
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
      Log.Information("{ModeName} clear mode is not implemented; using TOM clear fallback.", ModeName);
      new TomStructuredCredentialWriter().Apply(server, database, model, structuredDataSource, options, credentials);
      return;
    }

    // Keep the local model state valid before emitting the createOrReplace payload.
    var localCredential = structuredDataSource.Credential ?? new Credential();
    localCredential.AuthenticationKind = string.IsNullOrWhiteSpace(credentials.StructuredAuthenticationKind)
      ? AuthenticationKind.UsernamePassword
      : credentials.StructuredAuthenticationKind;
    localCredential.PrivacySetting = string.IsNullOrWhiteSpace(credentials.StructuredPrivacySetting)
      ? PrivacyClass.Organizational
      : credentials.StructuredPrivacySetting;
    localCredential.Username = credentials.Account;
    localCredential.Password = credentials.Password;
    localCredential.EncryptConnection = credentials.StructuredEncryptConnection;
    structuredDataSource.Credential = localCredential;

    model.SaveChanges();

    string tmsl = BuildStructuredDataSourceCreateOrReplaceTmsl(
      database,
      structuredDataSource.Name,
      options.SqlServer,
      options.SqlDatabase,
      credentials);

    server.Execute(tmsl);

    Log.Information("Credential settings applied to structured data source '{DataSourceName}' via {ModeName}.", structuredDataSource.Name, ModeName);
  }

  /// <summary>
  /// Builds the createOrReplace payload that TMSL expects for a structured data source.
  /// </summary>
  private static string BuildStructuredDataSourceCreateOrReplaceTmsl(
    Database database,
    string dataSourceName,
    string sqlServer,
    string sqlDatabase,
    SqlDataSourceCredentialOptions credentials)
  {
    var payload = new {
      createOrReplace = new {
        @object = new {
          database = database.ID,
          dataSource = dataSourceName
        },
        dataSource = new {
          name = dataSourceName,
          type = "structured",
          connectionDetails = new {
            protocol = DataSourceProtocol.Tds,
            address = new {
              server = sqlServer,
              database = sqlDatabase
            }
          },
          credential = new {
            AuthenticationKind = string.IsNullOrWhiteSpace(credentials.StructuredAuthenticationKind)
              ? AuthenticationKind.UsernamePassword
              : credentials.StructuredAuthenticationKind,
            Username = credentials.Account,
            Password = credentials.Password,
            EncryptConnection = credentials.StructuredEncryptConnection,
            PrivacySetting = string.IsNullOrWhiteSpace(credentials.StructuredPrivacySetting)
              ? PrivacyClass.Organizational
              : credentials.StructuredPrivacySetting
          }
        }
      }
    };

    return System.Text.Json.JsonSerializer.Serialize(payload);
  }
}
