using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DotNetEnv;
using Microsoft.AnalysisServices.Tabular;
using Serilog;
using Serilog.Events;

/// <summary>
/// Application entry point that reads configuration, sets up logging, and runs the semantic model update.
/// </summary>
internal static class Program
{
  /// <summary>
  /// Loads environment settings, configures logging, and executes the model update workflow.
  /// </summary>
  private static int Main()
  {
    // Load .env before reading any configuration keys.
    Env.TraversePath().Load();

    ConfigureLogger();

    try {
      string workspaceName = GetRequiredEnv("WORKSPACE_NAME");
      string tenantId = GetRequiredEnv("TENANT_ID");
      string appId = GetRequiredEnv("APP_ID");
      string appSecret = GetRequiredEnv("APP_SECRET");
      string targetDatasetName = GetRequiredEnv("TARGET_DATASET_NAME");
      string sqlServer = GetRequiredEnv("SQL_SERVER");
      string sqlDatabase = GetRequiredEnv("SQL_DATABASE");
      string sqlAuthMode = (Environment.GetEnvironmentVariable("SQL_AUTH_MODE") ?? "SqlPassword").Trim();
      string dataSourceName = Environment.GetEnvironmentVariable("SQL_DATA_SOURCE_NAME")
        ?? Environment.GetEnvironmentVariable("REMOTE_DATA_SOURCE_NAME")
        ?? "SqlDataSource";
      bool skipStructuredCredentialWrite = ParseBoolOrDefault(
        GetEnvWithLegacyFallback("SKIP_STRUCTURED_CREDENTIAL_WRITE", "SQL_SKIP_STRUCTURED_CREDENTIAL_WRITE"),
        fallbackValue: false);
      bool clearStructuredCredential = ParseBoolOrDefault(
        GetEnvWithLegacyFallback("CLEAR_STRUCTURED_CREDENTIAL", "SQL_CLEAR_STRUCTURED_CREDENTIAL"),
        fallbackValue: false);
      string structuredCredentialWriteMode =
        GetFirstNonEmptyEnv("MODEL_UPDATE_MODE")
        ?? "TOM";
      string tmdlFolderPath =
        GetEnvWithLegacyFallback("TMDL_FOLDER_PATH", "SQL_TMDL_FOLDER_PATH")
        ?? "tmdl";
      bool emitTmdlDiffDiagnostics = ParseBoolOrDefault(
        GetEnvWithLegacyFallback("TMDL_DIFF_DIAGNOSTICS", "SQL_TMDL_DIFF_DIAGNOSTICS"),
        fallbackValue: true);
      IReadOnlyList<SqlDirectQueryTableDefinition> directQueryTables = GetSqlDirectQueryTables();
      string sqlConnectionString = GetSqlConnectionString(sqlAuthMode);
      SqlDataSourceCredentialOptions? sqlDataSourceCredentials = GetSqlDataSourceCredentials(sqlAuthMode);

      var options = new CompositeModelSqlOptions(
        workspaceName,
        tenantId,
        appId,
        appSecret,
        targetDatasetName,
        sqlServer,
        sqlDatabase,
        dataSourceName,
        sqlConnectionString,
        sqlDataSourceCredentials,
        skipStructuredCredentialWrite,
        clearStructuredCredential,
        structuredCredentialWriteMode,
        tmdlFolderPath,
        emitTmdlDiffDiagnostics,
        directQueryTables);

      var updater = new SemanticModelSqlDataSourceUpdater();
      updater.Run(options);
      return 0;
    }
    catch (Exception ex) {
      Log.Fatal(ex, "Unhandled exception while updating semantic model.");
      return 1;
    }
    finally {
      Log.CloseAndFlush();
    }
  }

  /// <summary>
  /// Configures Serilog from environment variables and selects console or file output.
  /// </summary>
  private static void ConfigureLogger()
  {
    string target = (GetEnvWithLegacyFallback("LOG_TARGET", "SQL_LOG_TARGET") ?? "Console").Trim();
    string levelRaw = (GetEnvWithLegacyFallback("LOG_LEVEL", "SQL_LOG_LEVEL") ?? "Information").Trim();
    LogEventLevel level = ParseLogEventLevel(levelRaw);

    var configuration = new LoggerConfiguration()
      .MinimumLevel.Is(level)
      .WriteTo.Console(outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");

    if (target.Equals("File", StringComparison.OrdinalIgnoreCase)) {
      // Normalize the file sink target so relative paths stay within the current run directory.
      string configuredDirectory = GetEnvWithLegacyFallback("LOG_DIRECTORY", "SQL_LOG_DIRECTORY") ?? "logs";
      string logDirectory = Path.IsPathRooted(configuredDirectory)
        ? configuredDirectory
        : Path.Combine(Environment.CurrentDirectory, configuredDirectory);
      Directory.CreateDirectory(logDirectory);

      string logFilePath = Path.Combine(logDirectory, "fabric-tom-.log");
      configuration = new LoggerConfiguration()
        .MinimumLevel.Is(level)
        .WriteTo.File(
          path: logFilePath,
          rollingInterval: RollingInterval.Day,
          retainedFileCountLimit: 14,
          outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");
    }
    else if (!target.Equals("Console", StringComparison.OrdinalIgnoreCase)) {
      throw new InvalidOperationException("LOG_TARGET must be either 'Console' or 'File'.");
    }

    Log.Logger = configuration.CreateLogger();
    Log.Information("Logger initialized. Target={Target}, Level={Level}", target, level);
  }

  /// <summary>
  /// Parses a Serilog log level or throws when the configured value is invalid.
  /// </summary>
  private static LogEventLevel ParseLogEventLevel(string raw)
  {
    if (Enum.TryParse<LogEventLevel>(raw, ignoreCase: true, out LogEventLevel parsed)) {
      return parsed;
    }

    throw new InvalidOperationException(
      "LOG_LEVEL must be one of: Verbose, Debug, Information, Warning, Error, Fatal.");
  }

  /// <summary>
  /// Reads a new environment key first and falls back to a legacy key when needed.
  /// </summary>
  private static string? GetEnvWithLegacyFallback(string key, string legacyKey)
  {
    string? value = Environment.GetEnvironmentVariable(key);
    if (!string.IsNullOrWhiteSpace(value)) {
      return value;
    }

    return Environment.GetEnvironmentVariable(legacyKey);
  }

  /// <summary>
  /// Reads the first configured environment value from the provided keys.
  /// </summary>
  private static string? GetFirstNonEmptyEnv(params string[] keys)
  {
    foreach (string key in keys) {
      string? value = Environment.GetEnvironmentVariable(key);
      if (!string.IsNullOrWhiteSpace(value)) {
        return value;
      }
    }

    return null;
  }

  /// <summary>
  /// Reads a required environment value and fails fast when it is missing.
  /// </summary>
  private static string GetRequiredEnv(string key)
  {
    string? value = Environment.GetEnvironmentVariable(key);
    if (string.IsNullOrWhiteSpace(value)) {
      throw new InvalidOperationException($"Environment variable '{key}' is not set. Please define it in .env.");
    }

    return value;
  }

  /// <summary>
  /// Builds the Power BI XMLA connection string used by the XMLA/TOM client.
  /// </summary>
  private static string GetSqlConnectionString(string sqlAuthMode)
  {
    string? directConnectionString = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING");
    if (!string.IsNullOrWhiteSpace(directConnectionString)) {
      return EnsureEncryptedSqlConnectionString(directConnectionString);
    }

    string sqlServer = GetRequiredEnv("SQL_SERVER");
    string sqlDatabase = GetRequiredEnv("SQL_DATABASE");
    string sqlProvider = Environment.GetEnvironmentVariable("SQL_PROVIDER") ?? "MSOLEDBSQL";
    string authSegment = GetSqlAuthenticationSegment(sqlAuthMode);

    string baseConnectionString = $"Provider={sqlProvider};Data Source={sqlServer};Initial Catalog={sqlDatabase};{authSegment};Persist Security Info=True;";
    return EnsureEncryptedSqlConnectionString(baseConnectionString);
  }

  /// <summary>
  /// Builds the credentials object used when TOM, TMSL, or TMDL writes structured data source credentials.
  /// </summary>
  private static SqlDataSourceCredentialOptions? GetSqlDataSourceCredentials(string sqlAuthMode)
  {
    if (sqlAuthMode.Equals("EntraInteractiveMfa", StringComparison.OrdinalIgnoreCase)) {
      throw new InvalidOperationException(
        "SQL_AUTH_MODE=EntraInteractiveMfa cannot provision persistent credentials for Power BI Service semantic model data sources. "
        + "Use SqlPassword or EntraServicePrincipal, then save credentials once in Power BI Service settings if required.");
    }

    string? explicitAccount = Environment.GetEnvironmentVariable("SQL_DATA_SOURCE_ACCOUNT");
    string? explicitPassword = Environment.GetEnvironmentVariable("SQL_DATA_SOURCE_PASSWORD");
    string? explicitImpersonationMode = Environment.GetEnvironmentVariable("SQL_DATA_SOURCE_IMPERSONATION_MODE");
    string structuredAuthenticationKind =
      Environment.GetEnvironmentVariable("SQL_STRUCTURED_AUTH_KIND")
      ?? "UsernamePassword";
    string structuredPrivacySetting =
      Environment.GetEnvironmentVariable("SQL_STRUCTURED_PRIVACY_LEVEL")
      ?? PrivacyClass.Organizational;
    bool structuredEncryptConnection = ParseBoolOrDefault(
      Environment.GetEnvironmentVariable("SQL_STRUCTURED_ENCRYPT_CONNECTION"),
      fallbackValue: true);
    ImpersonationMode defaultProviderImpersonationMode = ImpersonationMode.ImpersonateServiceAccount;
    bool isServicePrincipalMode = sqlAuthMode.Equals("EntraServicePrincipal", StringComparison.OrdinalIgnoreCase);

    if (!isServicePrincipalMode
      && !string.IsNullOrWhiteSpace(explicitAccount)
      && !string.IsNullOrWhiteSpace(explicitPassword)) {
      return new SqlDataSourceCredentialOptions(
        explicitAccount,
        explicitPassword,
        ParseImpersonationModeOrDefault(explicitImpersonationMode, defaultProviderImpersonationMode),
        structuredAuthenticationKind,
        structuredPrivacySetting,
        structuredEncryptConnection);
    }

    if (sqlAuthMode.Equals("SqlPassword", StringComparison.OrdinalIgnoreCase)) {
      return new SqlDataSourceCredentialOptions(
        GetRequiredEnv("SQL_USER"),
        GetRequiredEnv("SQL_PASSWORD"),
        ParseImpersonationModeOrDefault(explicitImpersonationMode, defaultProviderImpersonationMode),
        structuredAuthenticationKind,
        structuredPrivacySetting,
        structuredEncryptConnection);
    }

    if (sqlAuthMode.Equals("EntraPassword", StringComparison.OrdinalIgnoreCase)) {
      return new SqlDataSourceCredentialOptions(
        GetRequiredEnv("SQL_ENTRA_USER"),
        GetRequiredEnv("SQL_ENTRA_PASSWORD"),
        ParseImpersonationModeOrDefault(explicitImpersonationMode, defaultProviderImpersonationMode),
        structuredAuthenticationKind,
        structuredPrivacySetting,
        structuredEncryptConnection);
    }

    if (isServicePrincipalMode) {
      string entraClientId = GetRequiredEnv("SQL_ENTRA_CLIENT_ID");
      string entraTenantId = Environment.GetEnvironmentVariable("SQL_ENTRA_TENANT_ID")
        ?? GetRequiredEnv("TENANT_ID");

      return new SqlDataSourceCredentialOptions(
        $"{entraClientId}@{entraTenantId}",
        GetRequiredEnv("SQL_ENTRA_CLIENT_SECRET"),
        ParseImpersonationModeOrDefault(explicitImpersonationMode, defaultProviderImpersonationMode),
        structuredAuthenticationKind,
        structuredPrivacySetting,
        structuredEncryptConnection);
    }

    return null;
  }

  /// <summary>
  /// Converts a raw impersonation mode value into the TOM enum value.
  /// </summary>
  private static ImpersonationMode ParseImpersonationModeOrDefault(
    string? rawMode,
    ImpersonationMode fallbackMode)
  {
    if (!string.IsNullOrWhiteSpace(rawMode)
      && Enum.TryParse(rawMode.Trim(), ignoreCase: true, out ImpersonationMode mode)) {
      return mode;
    }

    return fallbackMode;
  }

  /// <summary>
  /// Converts a raw boolean environment value into a safe fallback value.
  /// </summary>
  private static bool ParseBoolOrDefault(string? rawValue, bool fallbackValue)
  {
    if (bool.TryParse(rawValue, out bool parsed)) {
      return parsed;
    }

    return fallbackValue;
  }

  /// <summary>
  /// Ensures the SQL connection string always uses encryption and does not trust the server certificate.
  /// </summary>
  private static string EnsureEncryptedSqlConnectionString(string connectionString)
  {
    string normalized = connectionString.Trim();
    if (!normalized.EndsWith(';')) {
      normalized += ";";
    }

    if (!ContainsConnectionStringKey(normalized, "Encrypt")) {
      normalized += "Encrypt=True;";
    }

    if (!ContainsConnectionStringKey(normalized, "TrustServerCertificate")) {
      normalized += "TrustServerCertificate=False;";
    }

    return normalized;
  }

  /// <summary>
  /// Checks whether a connection string already contains a specific key.
  /// </summary>
  private static bool ContainsConnectionStringKey(string connectionString, string key)
  {
    string[] segments = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    foreach (string segment in segments) {
      int equalIndex = segment.IndexOf('=');
      if (equalIndex <= 0) {
        continue;
      }

      string segmentKey = segment.Substring(0, equalIndex).Trim();
      if (segmentKey.Equals(key, StringComparison.OrdinalIgnoreCase)) {
        return true;
      }
    }

    return false;
  }

  /// <summary>
  /// Builds the provider-specific authentication segment for the SQL connection string.
  /// </summary>
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
      string entraTenantId = Environment.GetEnvironmentVariable("SQL_ENTRA_TENANT_ID")
        ?? GetRequiredEnv("TENANT_ID");
      return $"Authentication=ActiveDirectoryServicePrincipal;User ID={entraClientId};Password={entraClientSecret};Authority Id={entraTenantId}";
    }

    throw new InvalidOperationException(
      $"Unsupported SQL_AUTH_MODE '{sqlAuthMode}'. Use SqlPassword, EntraPassword, EntraInteractiveMfa, or EntraServicePrincipal.");
  }

  /// <summary>
  /// Parses one or more DirectQuery table definitions from the environment.
  /// </summary>
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

  /// <summary>
  /// Reads the single-table DirectQuery definition used when no indexed table count is specified.
  /// </summary>
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

  /// <summary>
  /// Reads one indexed DirectQuery table definition.
  /// </summary>
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

  /// <summary>
  /// Parses the column definition list for a DirectQuery table.
  /// </summary>
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

  /// <summary>
  /// Parses a single DirectQuery column definition entry.
  /// </summary>
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