using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AnalysisServices.Tabular;
using Serilog;

/// <summary>
/// All settings required to connect to Power BI and update the semantic model.
/// </summary>
internal sealed record CompositeModelSqlOptions(
  string WorkspaceName,
  string TenantId,
  string AppId,
  string AppSecret,
  string TargetDatasetName,
  string SqlServer,
  string SqlDatabase,
  string DataSourceName,
  string SqlConnectionString,
  SqlDataSourceCredentialOptions? SqlDataSourceCredentials,
  bool SkipStructuredCredentialWrite,
  bool ClearStructuredCredential,
  string StructuredCredentialWriteMode,
  string TmdlFolderPath,
  bool EmitTmdlDiffDiagnostics,
  IReadOnlyList<SqlDirectQueryTableDefinition> DirectQueryTables);

/// <summary>
/// Credentials and privacy settings used when writing a structured data source.
/// </summary>
internal sealed record SqlDataSourceCredentialOptions(
  string Account,
  string Password,
  ImpersonationMode ImpersonationMode,
  string StructuredAuthenticationKind,
  string StructuredPrivacySetting,
  bool StructuredEncryptConnection);

/// <summary>
/// Describes one local DirectQuery table to create or refresh.
/// </summary>
internal sealed record SqlDirectQueryTableDefinition(
  string TableName,
  string PartitionName,
  string Query,
  IReadOnlyList<SqlDirectQueryColumnDefinition> Columns);

/// <summary>
/// Describes one column within a generated DirectQuery table.
/// </summary>
internal sealed record SqlDirectQueryColumnDefinition(
  string Name,
  DataType DataType,
  string SourceColumn);

/// <summary>
/// Connects to the target semantic model and applies the structured data source and DirectQuery table changes.
/// </summary>
internal sealed class SemanticModelSqlDataSourceUpdater
{
  /// <summary>
  /// Runs the full update workflow against the configured workspace and semantic model.
  /// </summary>
  public void Run(CompositeModelSqlOptions options)
  {
    string workspaceConnection = $"powerbi://api.powerbi.com/v1.0/myorg/{options.WorkspaceName}";
    Log.Information("{WorkspaceConnection}", workspaceConnection);

    string connectStringApp =
      $"DataSource={workspaceConnection};User ID=app:{options.AppId}@{options.TenantId};Password={options.AppSecret};";

    using var server = new Server();
    server.Connect(connectStringApp);

    ListModels(server);
    ConfigureCompositeModel(server, options);
  }

  /// <summary>
  /// Lists the databases, tables, and data sources that are visible through the XMLA connection.
  /// </summary>
  private static void ListModels(Server server)
  {
    foreach (Database database in server.Databases) {
      Log.Information("{DatabaseName}", database.Name);
      var model = database.Model;
      if (model is null) {
        continue;
      }

      Log.Information("  Model: {ModelName}", model.Name);
      foreach (var table in model.Tables) {
        Log.Information("    Table: {TableName}", table.Name);
        foreach (var column in table.Columns) {
          Log.Information("      Column: {ColumnName} ({DataType})", column.Name, column.DataType);
        }
      }

      foreach (var dataSource in model.DataSources) {
        if (dataSource is ProviderDataSource providerDataSource) {
          Log.Information("    DataSource: {Name} ({ConnectionString})", providerDataSource.Name, providerDataSource.ConnectionString);
          Log.Information("      Provider auth: ImpersonationMode={ImpersonationMode}, Account={Account}, HasPassword={HasPassword}", providerDataSource.ImpersonationMode, string.IsNullOrWhiteSpace(providerDataSource.Account) ? "<empty>" : providerDataSource.Account, !string.IsNullOrWhiteSpace(providerDataSource.Password));
          continue;
        }

        if (dataSource is StructuredDataSource structuredDataSource) {
          Log.Information("    DataSource: {Name} (Structured)", structuredDataSource.Name);
          Log.Information("      ConnectionDetails: {ConnectionDetails}", FormatConnectionDetails(structuredDataSource.ConnectionDetails));
          Log.Information("      Structured auth: {StructuredAuth}", FormatStructuredCredential(structuredDataSource.Credential));
          continue;
        }

        Log.Information("    DataSource: {Name} ({Description})", dataSource.Name, dataSource.Description);
      }
    }
  }

  /// <summary>
  /// Applies the composite model changes and persists them back to the target dataset.
  /// </summary>
  private static void ConfigureCompositeModel(Server server, CompositeModelSqlOptions options)
  {
    var targetDatabase = server.Databases
      .Cast<Database>()
      .FirstOrDefault(db => db.Name.Equals(options.TargetDatasetName, StringComparison.OrdinalIgnoreCase));

    if (targetDatabase is null) {
      throw new InvalidOperationException($"Target semantic model '{options.TargetDatasetName}' was not found.");
    }

    Model? model = targetDatabase.Model;
    if (model is null) {
      try {
        targetDatabase.Refresh(true);
        model = targetDatabase.Model;
      }
      catch (Exception ex) {
        Log.Warning(ex, "Failed to refresh semantic model metadata.");
      }
    }

    if (model is null) {
      Log.Warning("Skip: semantic model '{TargetDatasetName}' is not TOM-accessible from the current connection.", options.TargetDatasetName);
      Log.Warning("Check XMLA endpoint write settings, workspace capacity, and target dataset type.");
      return;
    }

    model.DiscourageImplicitMeasures = true;

    var modelDataSources = model.DataSources ?? throw new InvalidOperationException(
      $"Semantic model '{options.TargetDatasetName}' does not expose a data source collection.");

    if (modelDataSources.Count > 0) {
      foreach (var ds in modelDataSources) {
        if (ds is ProviderDataSource providerDataSource) {
          Log.Information("Existing data source: {Name}, ConnectionString: {ConnectionString}", providerDataSource.Name, providerDataSource.ConnectionString);
        }
        else {
          Log.Information("Existing data source: {Name}, Description: {Description}", ds.Name, ds.Description);
        }
      }
    }

    var providerDataSources = modelDataSources!
      .Cast<DataSource>()
      .OfType<ProviderDataSource>()
      .ToList();

    var structuredDataSources = modelDataSources!
      .Cast<DataSource>()
      .OfType<StructuredDataSource>()
      .ToList();

    if (structuredDataSources.Count == 0) {
      Log.Information("Diagnostic: this semantic model has no StructuredDataSource.");
      Log.Information("Diagnostic: authentication method / encrypted connection / privacy level UI fields are backed by StructuredDataSource.Credential.");
      Log.Information("Diagnostic: with ProviderDataSource-only models, those UI fields can remain unset even after TOM updates.");
    }

    ProviderDataSource? providerDataSourceByName = providerDataSources
      .FirstOrDefault(ds => ds.Name.Equals(options.DataSourceName, StringComparison.OrdinalIgnoreCase));
    StructuredDataSource structuredSqlDataSource = GetOrCreateStructuredSqlDataSource(
      model,
      structuredDataSources,
      options);

    if (options.SqlDataSourceCredentials is null) {
      Log.Information("No SQL data source credentials were provided for this authentication mode. Existing credential settings are kept as-is.");
    }

    // DirectQuery tables are refreshed after the structured source is guaranteed to exist.
    EnsureDirectQueryTables(model, structuredSqlDataSource, options);

    if (providerDataSourceByName is not null && !IsProviderDataSourceReferenced(model, providerDataSourceByName)) {
      model.DataSources.Remove(providerDataSourceByName);
      Log.Information("Removed unused provider data source '{DataSourceName}' after switching to StructuredDataSource/M partitions.", providerDataSourceByName.Name);
    }

    if (options.SqlDataSourceCredentials is not null) {
      Log.Warning(
        "Initial credential setup may still require entering credentials in the Power BI Service UI and saving them manually; TOM, TMSL, and TMDL updates do not guarantee the service credential store is populated.");

      if (options.SkipStructuredCredentialWrite) {
        Log.Information("Structured credential write was skipped by SKIP_STRUCTURED_CREDENTIAL_WRITE=true.");
        model.SaveChanges();
      }
      else {
        // Strategy pattern keeps TOM, TMSL, and TMDL write behavior isolated.
        IStructuredCredentialWriter writer = StructuredCredentialWriterFactory.Create(options.StructuredCredentialWriteMode);
        writer.Apply(
          server,
          targetDatabase,
          model,
          structuredSqlDataSource,
          options,
          options.SqlDataSourceCredentials);
      }

      Log.Information("Diagnostic: if the service still reports missing credentials, open semantic model settings in Power BI Service and save credentials for this data source path.");
      Log.Information("Diagnostic: XMLA/TOM metadata can be updated, but the service credential store may still require an explicit save operation.");
    }
    else {
      model.SaveChanges();
    }

    Log.Information("Composite model settings applied to '{TargetDatasetName}'.", options.TargetDatasetName);
    Log.Information("SQL data source updated: '{DataSourceName}'.", options.DataSourceName);
    Log.Information("DirectQuery tables ensured: {TableCount}", options.DirectQueryTables.Count);
  }

  /// <summary>
  /// Ensures every configured DirectQuery table exists and points at the requested SQL query.
  /// </summary>
  private static void EnsureDirectQueryTables(Model model, StructuredDataSource structuredSqlDataSource, CompositeModelSqlOptions options)
  {
    foreach (var tableDefinition in options.DirectQueryTables) {
      EnsureDirectQueryTable(model, structuredSqlDataSource, tableDefinition, options);
    }
  }

  /// <summary>
  /// Creates or refreshes one DirectQuery table and wires its M partition.
  /// </summary>
  private static void EnsureDirectQueryTable(
    Model model,
    StructuredDataSource structuredSqlDataSource,
    SqlDirectQueryTableDefinition tableDefinition,
    CompositeModelSqlOptions options)
  {
    Table? directQueryTable = model.Tables.Find(tableDefinition.TableName);

    if (directQueryTable is null) {
      directQueryTable = new Table {
        Name = tableDefinition.TableName,
        Description = "Local DirectQuery table added to extend a Direct Lake semantic model."
      };
      model.Tables.Add(directQueryTable);
    }

    directQueryTable.Partitions.Clear();
    directQueryTable.Partitions.Add(new Partition {
      Name = tableDefinition.PartitionName,
      Mode = ModeType.DirectQuery,
      Source = new MPartitionSource {
        Expression = BuildNativeQueryMExpression(options.SqlServer, options.SqlDatabase, tableDefinition.Query)
      }
    });

    directQueryTable.Columns.Clear();
    foreach (var columnDefinition in tableDefinition.Columns) {
      directQueryTable.Columns.Add(new DataColumn {
        Name = columnDefinition.Name,
        DataType = columnDefinition.DataType,
        SourceColumn = columnDefinition.SourceColumn
      });
    }
  }

  /// <summary>
  /// Reuses an existing structured data source when it already targets the same SQL server and database.
  /// </summary>
  private static StructuredDataSource GetOrCreateStructuredSqlDataSource(
    Model model,
    IReadOnlyList<StructuredDataSource> structuredDataSources,
    CompositeModelSqlOptions options)
  {
    StructuredDataSource? structuredDataSource = structuredDataSources
      .FirstOrDefault(ds => ds.Name.Equals(options.DataSourceName, StringComparison.OrdinalIgnoreCase));

    if (structuredDataSource is null) {
      structuredDataSource = structuredDataSources.FirstOrDefault(ds =>
        IsSameSqlResourcePath(ds, options.SqlServer, options.SqlDatabase));

      if (structuredDataSource is not null) {
        Log.Information("Reusing existing structured data source '{DataSourceName}' because it targets the same SQL resource path.", structuredDataSource.Name);
      }
    }

    if (structuredDataSource is null) {
      structuredDataSource = new StructuredDataSource {
        Name = options.DataSourceName
      };
      model.DataSources.Add(structuredDataSource);
      Log.Information("Created structured data source '{DataSourceName}'.", structuredDataSource.Name);
    }

    if (!structuredDataSource.Name.Equals(options.DataSourceName, StringComparison.OrdinalIgnoreCase)) {
      Log.Information("Renaming structured data source '{CurrentName}' to '{TargetName}'.", structuredDataSource.Name, options.DataSourceName);
      structuredDataSource.Name = options.DataSourceName;
    }

    structuredDataSource.ConnectionDetails = BuildSqlConnectionDetails(options.SqlServer, options.SqlDatabase);

    return structuredDataSource;
  }

  /// <summary>
  /// Compares the structured source metadata to the requested SQL resource path.
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

  /// <summary>
  /// Builds TOM connection details for a SQL Server structured data source.
  /// </summary>
  private static ConnectionDetails BuildSqlConnectionDetails(string sqlServer, string sqlDatabase)
  {
    string escapedServer = EscapeJsonString(sqlServer);
    string escapedDatabase = EscapeJsonString(sqlDatabase);
    string json = $"{{\"protocol\":\"{DataSourceProtocol.Tds}\",\"address\":{{\"server\":\"{escapedServer}\",\"database\":\"{escapedDatabase}\"}}}}";

    return new ConnectionDetails(json);
  }

  /// <summary>
  /// Checks whether the old provider data source is still referenced by any partition.
  /// </summary>
  private static bool IsProviderDataSourceReferenced(Model model, ProviderDataSource providerDataSource)
  {
    foreach (var table in model.Tables) {
      foreach (var partition in table.Partitions) {
        if (partition.Source is QueryPartitionSource queryPartitionSource
          && queryPartitionSource.DataSource is ProviderDataSource source
          && source.Name.Equals(providerDataSource.Name, StringComparison.OrdinalIgnoreCase)) {
          return true;
        }
      }
    }

    return false;
  }

  /// <summary>
  /// Builds the M expression for a DirectQuery partition.
  /// </summary>
  private static string BuildNativeQueryMExpression(string sqlServer, string sqlDatabase, string query)
  {
    string escapedServer = EscapeMString(sqlServer);
    string escapedDatabase = EscapeMString(sqlDatabase);
    string escapedQuery = EscapeMString(query);

    return $@"let
    Source = Sql.Database(""{escapedServer}"", ""{escapedDatabase}"", [CreateNavigationProperties=false]),
    Query = Value.NativeQuery(Source, ""{escapedQuery}"", null, [EnableFolding=true])
in
    Query";
  }

  /// <summary>
  /// Escapes a string so it can safely appear inside an M string literal.
  /// </summary>
  private static string EscapeMString(string value)
  {
    return value.Replace("\"", "\"\"");
  }

  /// <summary>
  /// Escapes a string so it can safely appear inside JSON payload text.
  /// </summary>
  private static string EscapeJsonString(string value)
  {
    return value
      .Replace("\\", "\\\\")
      .Replace("\"", "\\\"");
  }

  /// <summary>
  /// Produces a readable diagnostic string for the connection details object.
  /// </summary>
  private static string FormatConnectionDetails(ConnectionDetails? connectionDetails)
  {
    if (connectionDetails is null || connectionDetails.IsEmpty) {
      return "<empty>";
    }

    try {
      return connectionDetails.ToJson();
    }
    catch {
      return $"Protocol={connectionDetails.Protocol}";
    }
  }

  /// <summary>
  /// Produces a readable diagnostic string for the structured credential object.
  /// </summary>
  private static string FormatStructuredCredential(Credential? credential)
  {
    if (credential is null || credential.IsEmpty) {
      return "<empty>";
    }

    string auth = string.IsNullOrWhiteSpace(credential.AuthenticationKind)
      ? "<empty>"
      : credential.AuthenticationKind;
    string username = string.IsNullOrWhiteSpace(credential.Username)
      ? "<empty>"
      : credential.Username;
    bool hasPassword = !string.IsNullOrWhiteSpace(credential.Password);
    string privacy = string.IsNullOrWhiteSpace(credential.PrivacySetting)
      ? "<empty>"
      : credential.PrivacySetting;

    return $"AuthenticationKind={auth}, Username={username}, HasPassword={hasPassword}, EncryptConnection={credential.EncryptConnection}, PrivacySetting={privacy}";
  }
}
