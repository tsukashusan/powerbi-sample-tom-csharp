using Microsoft.AnalysisServices.Tabular;

internal interface IStructuredCredentialWriter
{
  string ModeName { get; }

  void Apply(
    Server server,
    Database database,
    Model model,
    StructuredDataSource structuredDataSource,
    CompositeModelSqlOptions options,
    SqlDataSourceCredentialOptions credentials);
}
