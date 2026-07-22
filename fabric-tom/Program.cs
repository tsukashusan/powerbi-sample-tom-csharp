using System;
using System.Collections.Generic;
using System.Linq;
using DotNetEnv;

internal static class Program
{
  private static void Main()
  {
    Console.WriteLine("Hello, World!");

    // .env を読み込み
    Env.TraversePath().Load();

    string workspaceName = GetRequiredEnv("WORKSPACE_NAME");
    string tenantId = GetRequiredEnv("TENANT_ID");
    string appId = GetRequiredEnv("APP_ID");
    string appSecret = GetRequiredEnv("APP_SECRET");
    string targetDatasetName = GetRequiredEnv("TARGET_DATASET_NAME");
    string dataSourceName = Environment.GetEnvironmentVariable("SQL_DATA_SOURCE_NAME")
      ?? Environment.GetEnvironmentVariable("REMOTE_DATA_SOURCE_NAME")
      ?? "SqlDataSource";
    IReadOnlyList<SqlDirectQueryTableDefinition> directQueryTables = GetSqlDirectQueryTables();
    string sqlConnectionString = GetSqlConnectionString();

    var options = new CompositeModelSqlOptions(
      workspaceName,
      tenantId,
      appId,
      appSecret,
      targetDatasetName,
      dataSourceName,
      sqlConnectionString,
      directQueryTables);

    var updater = new SemanticModelSqlDataSourceUpdater();
    updater.Run(options);
  }

  private static string GetRequiredEnv(string key)
  {
    string? value = Environment.GetEnvironmentVariable(key);
    if (string.IsNullOrWhiteSpace(value)) {
      throw new InvalidOperationException($"Environment variable '{key}' is not set. Please define it in .env.");
    }

    return value;
  }

  private static string GetSqlConnectionString()
  {
    string? directConnectionString = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING");
    if (!string.IsNullOrWhiteSpace(directConnectionString)) {
      return directConnectionString;
    }

    string sqlServer = GetRequiredEnv("SQL_SERVER");
    string sqlDatabase = GetRequiredEnv("SQL_DATABASE");
    string sqlProvider = Environment.GetEnvironmentVariable("SQL_PROVIDER") ?? "MSOLEDBSQL";
    string sqlAuthMode = (Environment.GetEnvironmentVariable("SQL_AUTH_MODE") ?? "SqlPassword").Trim();
    string authSegment = GetSqlAuthenticationSegment(sqlAuthMode);

    return $"Provider={sqlProvider};Data Source={sqlServer};Initial Catalog={sqlDatabase};{authSegment};Persist Security Info=True;";
  }

  private static string GetSqlAuthenticationSegment(string sqlAuthMode)
  {
    if (sqlAuthMode.Equals("SqlPassword", StringComparison.OrdinalIgnoreCase)) {
      string sqlUser = GetRequiredEnv("SQL_USER");
      string sqlPassword = GetRequiredEnv("SQL_PASSWORD");
      return $"User ID={sqlUser};Password={sqlPassword}";
    }

    if (sqlAuthMode.Equals("EntraPassword", StringComparison.OrdinalIgnoreCase)) {
      string entraUser = GetRequiredEnv("SQL_ENTRA_USER");
      string entraPassword = GetRequiredEnv("SQL_ENTRA_PASSWORD");
      return $"Authentication=ActiveDirectoryPassword;User ID={entraUser};Password={entraPassword}";
    }

    if (sqlAuthMode.Equals("EntraInteractiveMfa", StringComparison.OrdinalIgnoreCase)) {
      string? entraUser = Environment.GetEnvironmentVariable("SQL_ENTRA_USER");
      string userSegment = string.IsNullOrWhiteSpace(entraUser)
        ? string.Empty
        : $";User ID={entraUser}";
      return $"Authentication=ActiveDirectoryInteractive{userSegment}";
    }

    if (sqlAuthMode.Equals("EntraServicePrincipal", StringComparison.OrdinalIgnoreCase)) {
      string entraClientId = GetRequiredEnv("SQL_ENTRA_CLIENT_ID");
      string entraClientSecret = GetRequiredEnv("SQL_ENTRA_CLIENT_SECRET");
      return $"Authentication=ActiveDirectoryServicePrincipal;User ID={entraClientId};Password={entraClientSecret}";
    }

    throw new InvalidOperationException(
      $"Unsupported SQL_AUTH_MODE '{sqlAuthMode}'. Use SqlPassword, EntraPassword, EntraInteractiveMfa, or EntraServicePrincipal.");
  }

  private static IReadOnlyList<SqlDirectQueryTableDefinition> GetSqlDirectQueryTables()
  {
    string? rawCount = Environment.GetEnvironmentVariable("SQL_DIRECTQUERY_TABLE_COUNT");
    if (string.IsNullOrWhiteSpace(rawCount)) {
      return new[] { GetSingleSqlDirectQueryTableDefinition() };
    }

    if (!int.TryParse(rawCount, out int tableCount) || tableCount <= 0) {
      throw new InvalidOperationException(
        "Environment variable 'SQL_DIRECTQUERY_TABLE_COUNT' must be a positive integer.");
    }

    var tableDefinitions = new List<SqlDirectQueryTableDefinition>();
    for (int i = 1; i <= tableCount; i++) {
      tableDefinitions.Add(GetIndexedSqlDirectQueryTableDefinition(i));
    }

    return tableDefinitions;
  }

  private static SqlDirectQueryTableDefinition GetSingleSqlDirectQueryTableDefinition()
  {
    string tableName = Environment.GetEnvironmentVariable("SQL_DIRECTQUERY_TABLE_NAME")
      ?? "SqlCompositeTable";
    string partitionName = Environment.GetEnvironmentVariable("SQL_DIRECTQUERY_PARTITION_NAME")
      ?? "All Rows";
    string query = GetRequiredEnv("SQL_DIRECTQUERY_QUERY");
    IReadOnlyList<SqlDirectQueryColumnDefinition> columns = ParseSqlDirectQueryColumns(
      GetRequiredEnv("SQL_DIRECTQUERY_COLUMNS"),
      "SQL_DIRECTQUERY_COLUMNS");

    return new SqlDirectQueryTableDefinition(tableName, partitionName, query, columns);
  }

  private static SqlDirectQueryTableDefinition GetIndexedSqlDirectQueryTableDefinition(int index)
  {
    string tableNameKey = $"SQL_DIRECTQUERY_TABLE_{index}_NAME";
    string partitionNameKey = $"SQL_DIRECTQUERY_TABLE_{index}_PARTITION_NAME";
    string queryKey = $"SQL_DIRECTQUERY_TABLE_{index}_QUERY";
    string columnsKey = $"SQL_DIRECTQUERY_TABLE_{index}_COLUMNS";

    string tableName = GetRequiredEnv(tableNameKey);
    string partitionName = Environment.GetEnvironmentVariable(partitionNameKey)
      ?? "All Rows";
    string query = GetRequiredEnv(queryKey);
    IReadOnlyList<SqlDirectQueryColumnDefinition> columns = ParseSqlDirectQueryColumns(
      GetRequiredEnv(columnsKey),
      columnsKey);

    return new SqlDirectQueryTableDefinition(tableName, partitionName, query, columns);
  }

  private static IReadOnlyList<SqlDirectQueryColumnDefinition> ParseSqlDirectQueryColumns(string rawColumns, string keyName)
  {
    var columnDefinitions = rawColumns
      .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
      .Select(ParseSqlDirectQueryColumn)
      .ToList();

    if (columnDefinitions.Count == 0) {
      throw new InvalidOperationException(
        $"Environment variable '{keyName}' must define at least one column.");
    }

    return columnDefinitions;
  }

  private static SqlDirectQueryColumnDefinition ParseSqlDirectQueryColumn(string rawColumn)
  {
    string[] parts = rawColumn
      .Split(':', StringSplitOptions.TrimEntries);

    if (parts.Length < 2 || parts.Length > 3) {
      throw new InvalidOperationException(
        $"Invalid SQL_DIRECTQUERY_COLUMNS entry '{rawColumn}'. Use 'ColumnName:DataType[:SourceColumn]'.");
    }

    if (!Enum.TryParse(parts[1], ignoreCase: true, out Microsoft.AnalysisServices.Tabular.DataType dataType)) {
      throw new InvalidOperationException(
        $"Invalid data type '{parts[1]}' in SQL_DIRECTQUERY_COLUMNS entry '{rawColumn}'.");
    }

    string sourceColumn = parts.Length == 3 ? parts[2] : parts[0];

    return new SqlDirectQueryColumnDefinition(parts[0], dataType, sourceColumn);
  }
}