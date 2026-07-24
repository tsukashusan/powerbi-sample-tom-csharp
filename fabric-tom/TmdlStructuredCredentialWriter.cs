using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.AnalysisServices.Tabular;
using Serilog;

/// <summary>
/// Applies structured data source credentials by round-tripping the model through the TMDL folder API.
/// </summary>
internal sealed class TmdlStructuredCredentialWriter : IStructuredCredentialWriter
{
  public string ModeName => "TMDL";

  /// <summary>
  /// Serializes the model to a folder, edits the TMDL files, then copies the result back to the live model.
  /// </summary>
  public void Apply(
    Server server,
    Database database,
    Model model,
    StructuredDataSource structuredDataSource,
    CompositeModelSqlOptions options,
    SqlDataSourceCredentialOptions credentials)
  {
    string rootFolder = Path.IsPathRooted(options.TmdlFolderPath)
      ? options.TmdlFolderPath
      : Path.Combine(Environment.CurrentDirectory, options.TmdlFolderPath);
    string datasetFolder = Path.Combine(rootFolder, $"{database.Name}-tmdl");

    // Start from a clean output folder so stale TMDL files do not leak into the next run.
    if (Directory.Exists(datasetFolder)) {
      Directory.Delete(datasetFolder, recursive: true);
    }

    Directory.CreateDirectory(rootFolder);
    // Serialize the live database before touching the local TMDL copy.
    TmdlSerializer.SerializeDatabaseToFolder(database, datasetFolder);
    Log.Information("Serialized database to TMDL folder: {DatasetFolder}", datasetFolder);

    string dataSourcesPath = Path.Combine(datasetFolder, "dataSources.tmdl");
    string beforeDataSources = File.Exists(dataSourcesPath)
      ? File.ReadAllText(dataSourcesPath)
      : string.Empty;

    Model localModel = TmdlSerializer.DeserializeModelFromFolder(datasetFolder);

    StructuredDataSource? localStructuredDataSource = localModel.DataSources
      .OfType<StructuredDataSource>()
      .FirstOrDefault(ds => ds.Name.Equals(structuredDataSource.Name, StringComparison.OrdinalIgnoreCase));

    if (localStructuredDataSource is null) {
      localStructuredDataSource = localModel.DataSources
        .OfType<StructuredDataSource>()
        .FirstOrDefault(ds => IsSameSqlResourcePath(ds, options.SqlServer, options.SqlDatabase));
    }

    if (localStructuredDataSource is null) {
      throw new InvalidOperationException(
        $"Structured data source '{structuredDataSource.Name}' was not found in deserialized TMDL model.");
    }

    // Update only the credential block so the TMDL diff stays focused on the intended change.
    if (options.ClearStructuredCredential) {
      localStructuredDataSource.Credential = new Credential();
    }
    else {
      var updatedCredential = localStructuredDataSource.Credential ?? new Credential();
      updatedCredential.AuthenticationKind = string.IsNullOrWhiteSpace(credentials.StructuredAuthenticationKind)
        ? AuthenticationKind.UsernamePassword
        : credentials.StructuredAuthenticationKind;
      updatedCredential.PrivacySetting = string.IsNullOrWhiteSpace(credentials.StructuredPrivacySetting)
        ? PrivacyClass.Organizational
        : credentials.StructuredPrivacySetting;
      updatedCredential.Username = credentials.Account;
      updatedCredential.Password = credentials.Password;
      updatedCredential.EncryptConnection = credentials.StructuredEncryptConnection;
      localStructuredDataSource.Credential = updatedCredential;
    }

    // Serialize the edited local model back into the same folder structure, then inspect the diff.
    TmdlSerializer.SerializeModelToFolder(localModel, datasetFolder);
    Log.Information("Wrote updated credential settings to TMDL files in: {DatasetFolder}", datasetFolder);

    string afterDataSources = File.Exists(dataSourcesPath)
      ? File.ReadAllText(dataSourcesPath)
      : string.Empty;

    if (options.EmitTmdlDiffDiagnostics) {
      EmitDataSourcesDiff(beforeDataSources, afterDataSources, dataSourcesPath);
    }

    localModel.CopyTo(database.Model);
    database.Model.SaveChanges();
    Log.Information("Credential settings applied to structured data source '{DataSourceName}' via {ModeName}.", structuredDataSource.Name, ModeName);
  }

  /// <summary>
  /// Emits a line-oriented diff for dataSources.tmdl so the credential changes are easy to inspect.
  /// </summary>
  private static void EmitDataSourcesDiff(string before, string after, string path)
  {
    if (string.Equals(before, after, StringComparison.Ordinal)) {
      Log.Information("Diagnostic: no changes in {DataSourcesPath}.", path);
      return;
    }

    Log.Information("Diagnostic diff for {DataSourcesPath}:", path);

    string[] beforeLines = before.Replace("\r\n", "\n").Split('\n');
    string[] afterLines = after.Replace("\r\n", "\n").Split('\n');

    var lcs = BuildLcsTable(beforeLines, afterLines);
    PrintDiffFromLcs(beforeLines, afterLines, lcs, beforeLines.Length, afterLines.Length);
  }

  /// <summary>
  /// Builds the dynamic programming table used by the simple line diff algorithm.
  /// </summary>
  private static int[,] BuildLcsTable(string[] beforeLines, string[] afterLines)
  {
    int[,] lcs = new int[beforeLines.Length + 1, afterLines.Length + 1];

    for (int i = 1; i <= beforeLines.Length; i++) {
      for (int j = 1; j <= afterLines.Length; j++) {
        if (string.Equals(beforeLines[i - 1], afterLines[j - 1], StringComparison.Ordinal)) {
          lcs[i, j] = lcs[i - 1, j - 1] + 1;
        }
        else {
          lcs[i, j] = Math.Max(lcs[i - 1, j], lcs[i, j - 1]);
        }
      }
    }

    return lcs;
  }

  /// <summary>
  /// Reconstructs the diff output from the LCS table and prints the result.
  /// </summary>
  private static void PrintDiffFromLcs(
    string[] beforeLines,
    string[] afterLines,
    int[,] lcs,
    int i,
    int j)
  {
    var output = new List<string>();
    BuildDiffLines(beforeLines, afterLines, lcs, i, j, output);

    foreach (string line in output) {
      Log.Information("{DiffLine}", line);
    }
  }

  /// <summary>
  /// Walks the LCS matrix recursively to produce a minimal line diff.
  /// </summary>
  private static void BuildDiffLines(
    string[] beforeLines,
    string[] afterLines,
    int[,] lcs,
    int i,
    int j,
    List<string> output)
  {
    if (i > 0 && j > 0 && string.Equals(beforeLines[i - 1], afterLines[j - 1], StringComparison.Ordinal)) {
      BuildDiffLines(beforeLines, afterLines, lcs, i - 1, j - 1, output);
      output.Add($" {beforeLines[i - 1]}");
      return;
    }

    if (j > 0 && (i == 0 || lcs[i, j - 1] >= lcs[i - 1, j])) {
      BuildDiffLines(beforeLines, afterLines, lcs, i, j - 1, output);
      output.Add($"+{afterLines[j - 1]}");
      return;
    }

    if (i > 0 && (j == 0 || lcs[i, j - 1] < lcs[i - 1, j])) {
      BuildDiffLines(beforeLines, afterLines, lcs, i - 1, j, output);
      output.Add($"-{beforeLines[i - 1]}");
    }
  }

  /// <summary>
  /// Compares a structured data source against the requested SQL server and database.
  /// </summary>
  private static bool IsSameSqlResourcePath(StructuredDataSource dataSource, string sqlServer, string sqlDatabase)
  {
    ConnectionDetails? details = dataSource.ConnectionDetails;
    ConnectionAddress? address = details?.Address;
    if (details is null || address is null) {
      return false;
    }

    return string.Equals(details.Protocol, DataSourceProtocol.Tds, StringComparison.OrdinalIgnoreCase)
      && string.Equals(address.Server, sqlServer, StringComparison.OrdinalIgnoreCase)
      && string.Equals(address.Database, sqlDatabase, StringComparison.OrdinalIgnoreCase);
  }
}
