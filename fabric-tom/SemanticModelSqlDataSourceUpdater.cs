using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AnalysisServices.Tabular;

internal sealed record CompositeModelSqlOptions(
  string WorkspaceName,
  string TenantId,
  string AppId,
  string AppSecret,
  string TargetDatasetName,
  string DataSourceName,
  string SqlConnectionString,
  IReadOnlyList<SqlDirectQueryTableDefinition> DirectQueryTables);

internal sealed record SqlDirectQueryTableDefinition(
  string TableName,
  string PartitionName,
  string Query,
  IReadOnlyList<SqlDirectQueryColumnDefinition> Columns);

internal sealed record SqlDirectQueryColumnDefinition(
  string Name,
  DataType DataType,
  string SourceColumn);

internal sealed class SemanticModelSqlDataSourceUpdater
{
  public void Run(CompositeModelSqlOptions options)
  {
    string workspaceConnection = $"powerbi://api.powerbi.com/v1.0/myorg/{options.WorkspaceName}";
    Console.WriteLine(workspaceConnection);

    string connectStringApp =
      $"DataSource={workspaceConnection};User ID=app:{options.AppId}@{options.TenantId};Password={options.AppSecret};";

    using var server = new Server();
    server.Connect(connectStringApp);

    ListModels(server);
    ConfigureCompositeModel(server, options);
  }

  private static void ListModels(Server server)
  {
    foreach (Database database in server.Databases) {
      Console.WriteLine(database.Name);
      var model = database.Model;
      if (model is null) {
        continue;
      }

      Console.WriteLine($"  Model: {model.Name}");
      foreach (var table in model.Tables) {
        Console.WriteLine($"    Table: {table.Name}");
        foreach (var column in table.Columns) {
          Console.WriteLine($"      Column: {column.Name} ({column.DataType})");
        }
      }

      foreach (var dataSource in model.DataSources) {
        if (dataSource is ProviderDataSource providerDataSource) {
          Console.WriteLine($"    DataSource: {providerDataSource.Name} ({providerDataSource.ConnectionString})");
          continue;
        }

        Console.WriteLine($"    DataSource: {dataSource.Name} ({dataSource.Description})");
      }
    }
  }

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
        Console.WriteLine($"Warning: failed to refresh semantic model metadata. {ex.Message}");
      }
    }

    if (model is null) {
      Console.WriteLine($"Skip: semantic model '{options.TargetDatasetName}' is not TOM-accessible from the current connection.");
      Console.WriteLine("Check XMLA endpoint write settings, workspace capacity, and target dataset type.");
      return;
    }

    model.DiscourageImplicitMeasures = true;

    var modelDataSources = model.DataSources ?? throw new InvalidOperationException(
      $"Semantic model '{options.TargetDatasetName}' does not expose a data source collection.");

    if (modelDataSources.Count > 0) {
      foreach (var ds in modelDataSources) {
        if (ds is ProviderDataSource providerDataSource) {
          Console.WriteLine($"Existing data source: {providerDataSource.Name}, ConnectionString: {providerDataSource.ConnectionString}");
        }
        else {
          Console.WriteLine($"Existing data source: {ds.Name}, Description: {ds.Description}");
        }
      }
    }

    var providerDataSources = modelDataSources!
      .Cast<DataSource>()
      .OfType<ProviderDataSource>()
      .ToList();

    ProviderDataSource? sqlDataSource = providerDataSources
      .FirstOrDefault(ds => ds.Name.Equals(options.DataSourceName, StringComparison.OrdinalIgnoreCase));

    if (sqlDataSource is null && providerDataSources.Count == 1) {
      sqlDataSource = providerDataSources[0];
      Console.WriteLine($"Using the existing provider data source '{sqlDataSource.Name}' because it is the only provider data source in the model.");
    }

    if (sqlDataSource is null) {
      sqlDataSource = new ProviderDataSource {
        Name = options.DataSourceName,
        ConnectionString = options.SqlConnectionString
      };
      model.DataSources.Add(sqlDataSource);
    }
    else {
      sqlDataSource.Name = options.DataSourceName;
      sqlDataSource.ConnectionString = options.SqlConnectionString;
    }

    EnsureDirectQueryTables(model, sqlDataSource, options);

    model.SaveChanges();

    Console.WriteLine($"Composite model settings applied to '{options.TargetDatasetName}'.");
    Console.WriteLine($"SQL data source updated: '{options.DataSourceName}'.");
    Console.WriteLine($"DirectQuery tables ensured: {options.DirectQueryTables.Count}");
  }

  private static void EnsureDirectQueryTables(Model model, ProviderDataSource sqlDataSource, CompositeModelSqlOptions options)
  {
    foreach (var tableDefinition in options.DirectQueryTables) {
      EnsureDirectQueryTable(model, sqlDataSource, tableDefinition);
    }
  }

  private static void EnsureDirectQueryTable(Model model, ProviderDataSource sqlDataSource, SqlDirectQueryTableDefinition tableDefinition)
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
      Source = new QueryPartitionSource {
        DataSource = sqlDataSource,
        Query = tableDefinition.Query
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
}
